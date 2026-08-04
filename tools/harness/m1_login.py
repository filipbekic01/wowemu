#!/usr/bin/env python3
"""M1 gate: drive the logon protocol end to end without a WoW client.

Speaks the client half of the 3.3.5a handshake — logon challenge, SRP6 proof, realm list, then a
reconnect on a *second* connection — and fails loudly if any step deviates. This is the scripted
headless client from PLAN.md section 9.6; every milestone gate should end up with one of these.

    python3 tools/harness/m1_login.py [host] [--user TEST] [--password TEST]

Exit code 0 means the gate passes. The reconnect leg only passes if the session key was persisted,
so it doubles as a check that the auth database is wired up.
"""
import argparse
import hashlib
import os
import socket
import struct
import sys

PORT = 3724
BUILD = 12340
N = int("894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7", 16)
G = 7
K_MULTIPLIER = 3
VERSION_CHALLENGE = bytes.fromhex("baa31e99a00b2157fc373fb369cdd2f1")


def le(b):
    return int.from_bytes(b, "little")


def tole(n, size):
    return n.to_bytes(size, "little")


def sha1(*parts):
    h = hashlib.sha1()
    for part in parts:
        h.update(part)
    return h.digest()


def fail(message):
    print(f"FAIL: {message}")
    sys.exit(1)


def challenge_packet(command, user):
    body = b"WoW\x00"[::-1]
    body += bytes([3, 3, 5])
    body += struct.pack("<H", BUILD)
    body += b"x86\x00"[::-1]
    body += b"Win\x00"[::-1]
    body += b"enUS"[::-1]
    body += struct.pack("<I", 0)          # timezone bias
    body += bytes([127, 0, 0, 1])         # ip, informational only
    body += bytes([len(user)]) + user
    return bytes([command, 0x08]) + struct.pack("<H", len(body)) + body


def recv_exactly(sock, count, what):
    buffer = b""
    while len(buffer) < count:
        chunk = sock.recv(count - len(buffer))
        if not chunk:
            fail(f"connection closed while reading {what} ({len(buffer)}/{count} bytes)")
        buffer += chunk
    return buffer


def interleave(shared_secret):
    """SHA1Interleave. The leading-zero strip is load-bearing; see the README."""
    even = bytes(shared_secret[i * 2] for i in range(16))
    odd = bytes(shared_secret[i * 2 + 1] for i in range(16))

    offset = 0
    while offset < 32 and shared_secret[offset] == 0:
        offset += 1
    if offset & 1:
        offset += 1
    offset //= 2

    hash_even, hash_odd = sha1(even[offset:]), sha1(odd[offset:])
    return bytes(hash_even[i // 2] if i % 2 == 0 else hash_odd[i // 2] for i in range(40))


def logon(host, user, password):
    """Full logon + realm list. Returns the session key both sides derived."""
    sock = socket.create_connection((host, PORT), timeout=10)

    sock.sendall(challenge_packet(0x00, user))
    head = recv_exactly(sock, 3, "challenge response")

    if head[0] != 0x00:
        fail(f"expected a logon challenge response, got command 0x{head[0]:02X}")
    if head[2] != 0x00:
        fail(f"server rejected the challenge with code 0x{head[2]:02X}")

    rest = recv_exactly(sock, 116, "challenge body")
    B = le(rest[0:32])
    if rest[32] != 1 or rest[33] != G:
        fail(f"unexpected generator block: len={rest[32]} g={rest[33]}")
    if rest[34] != 32 or le(rest[35:67]) != N:
        fail("server sent a different SRP6 modulus")
    salt = rest[67:99]
    if rest[99:115] != VERSION_CHALLENGE:
        fail("version challenge constant does not match")
    print(f"  challenge ok (119 bytes, security flags 0x{rest[115]:02X})")

    x = le(sha1(salt, sha1(user + b":" + password)))
    v = pow(G, x, N)
    a = le(os.urandom(19))
    A = pow(G, a, N)
    u = le(sha1(tole(A, 32), tole(B, 32)))
    S = pow((B - K_MULTIPLIER * v) % N, a + u * x, N)
    session_key = interleave(tole(S, 32))

    ng = bytes(x ^ y for x, y in zip(sha1(tole(N, 32)), sha1(bytes([G]))))
    M1 = sha1(ng, sha1(user), salt, tole(A, 32), tole(B, 32), session_key)

    sock.sendall(bytes([0x01]) + tole(A, 32) + M1 + b"\x00" * 20 + bytes([0, 0]))
    proof = recv_exactly(sock, 32, "logon proof response")

    if proof[1] != 0x00:
        fail(f"login rejected with code 0x{proof[1]:02X}")
    if proof[2:22] != sha1(tole(A, 32), M1, session_key):
        fail("server proof M2 does not match — the session keys disagree")
    print("  logon ok (M2 verified)")

    sock.sendall(bytes([0x10, 0, 0, 0, 0]))
    header = recv_exactly(sock, 3, "realm list header")
    if header[0] != 0x10:
        fail(f"expected a realm list, got command 0x{header[0]:02X}")

    payload = recv_exactly(sock, struct.unpack("<H", header[1:3])[0], "realm list body")
    count = struct.unpack("<H", payload[4:6])[0]
    if count == 0:
        fail("realm list is empty")

    names = []
    cursor = 6
    for _ in range(count):
        cursor += 3                                   # type, locked, flags
        end = payload.index(b"\x00", cursor)
        name = payload[cursor:end].decode("utf-8")
        cursor = end + 1
        end = payload.index(b"\x00", cursor)
        address = payload[cursor:end].decode("utf-8")
        cursor = end + 1 + 4 + 3                      # population, chars, timezone, id
        names.append(f"{name} @ {address}")

    print(f"  realm list ok ({count}): {', '.join(names)}")
    sock.close()
    return session_key


def reconnect(host, user, session_key):
    """Reconnect handshake on a fresh connection. Only works if the key was persisted."""
    sock = socket.create_connection((host, PORT), timeout=10)

    sock.sendall(challenge_packet(0x02, user))
    head = recv_exactly(sock, 2, "reconnect challenge response")

    if head[0] != 0x02:
        fail(f"expected a reconnect challenge response, got command 0x{head[0]:02X}")
    if head[1] != 0x00:
        fail(f"reconnect refused with code 0x{head[1]:02X} — was the session key persisted?")

    body = recv_exactly(sock, 32, "reconnect challenge body")
    server_challenge = body[0:16]

    r1 = os.urandom(16)
    r2 = sha1(user, r1, server_challenge, session_key)
    sock.sendall(bytes([0x03]) + r1 + r2 + b"\x00" * 20 + bytes([0]))

    result = recv_exactly(sock, 4, "reconnect proof response")
    if result[0] != 0x03 or result[1] != 0x00:
        fail(f"reconnect proof rejected: command 0x{result[0]:02X} code 0x{result[1]:02X}")

    print("  reconnect ok (session key survived the connection)")
    sock.close()


def main():
    parser = argparse.ArgumentParser(description="M1 gate: log in without a WoW client.")
    parser.add_argument("host", nargs="?", default="127.0.0.1")
    parser.add_argument("--user", default="TEST")
    parser.add_argument("--password", default="TEST")
    args = parser.parse_args()

    # Both are uppercased before the verifier is derived, exactly as the server does it.
    user = args.user.upper().encode("utf-8")
    password = args.password.upper().encode("utf-8")

    print(f"M1 gate against {args.host}:{PORT} as {user.decode()}")

    try:
        session_key = logon(args.host, user, password)
        reconnect(args.host, user, session_key)
    except (ConnectionRefusedError, socket.timeout, OSError) as error:
        fail(f"{error} — is the auth server running?")

    print("PASS")


if __name__ == "__main__":
    main()
