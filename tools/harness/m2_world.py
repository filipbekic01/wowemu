#!/usr/bin/env python3
"""M2 gate: log in, connect to the world server, and reach the character list.

Drives the whole chain the way a real client does — SRP6 logon against the auth server, realm list,
then the world handshake: SMSG_AUTH_CHALLENGE, CMSG_AUTH_SESSION with the SHA-1 digest, RC4 header
encryption switched on, and CMSG_CHAR_ENUM answered with an empty list.

    python3 tools/harness/m2_world.py [host] [--user TEST] [--password TEST]

Exit code 0 means the gate passes. Both servers must be running, and the account must have logged
in at least once so the auth database holds its session key -- this script does that itself.
"""
import argparse
import hashlib
import hmac
import os
import socket
import struct
import sys

AUTH_PORT = 3724
BUILD = 12340

N = int("894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7", 16)
G = 7
K_MULTIPLIER = 3

SERVER_ENCRYPT_SEED = bytes.fromhex("cc98ae04e897eaca12ddc09342915357")
SERVER_DECRYPT_SEED = bytes.fromhex("c2b3723cc6aed9b5343c53ee2f4367ce")

SMSG_AUTH_CHALLENGE = 0x1EC
CMSG_AUTH_SESSION = 0x1ED
SMSG_AUTH_RESPONSE = 0x1EE
CMSG_CHAR_CREATE = 0x036
CMSG_CHAR_ENUM = 0x037
CMSG_CHAR_DELETE = 0x038
SMSG_CHAR_CREATE = 0x03A
SMSG_CHAR_DELETE = 0x03C
SMSG_CHAR_ENUM = 0x03B
CMSG_PING = 0x1DC
SMSG_PONG = 0x1DD
SMSG_ADDON_INFO = 0x2EF
SMSG_CLIENTCACHE_VERSION = 0x4AB
SMSG_TUTORIAL_FLAGS = 0x0FD

AUTH_OK = 0x0C
CHAR_CREATE_SUCCESS = 0x2F
CHAR_CREATE_NAME_IN_USE = 0x32
CHAR_DELETE_SUCCESS = 0x47


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


class Arc4:
    """ARC4, used here only for packet headers."""

    def __init__(self, key):
        self.state = list(range(256))
        self.i = self.j = 0
        j = 0
        for i in range(256):
            j = (j + self.state[i] + key[i % len(key)]) & 0xFF
            self.state[i], self.state[j] = self.state[j], self.state[i]

    def process(self, data):
        out = bytearray(len(data))
        for index, byte in enumerate(data):
            self.i = (self.i + 1) & 0xFF
            self.j = (self.j + self.state[self.i]) & 0xFF
            self.state[self.i], self.state[self.j] = self.state[self.j], self.state[self.i]
            out[index] = byte ^ self.state[(self.state[self.i] + self.state[self.j]) & 0xFF]
        return bytes(out)

    def drop(self, count):
        self.process(bytes(count))


def recv_exactly(sock, count, what):
    buffer = b""
    while len(buffer) < count:
        chunk = sock.recv(count - len(buffer))
        if not chunk:
            fail(f"connection closed while reading {what} ({len(buffer)}/{count} bytes)")
        buffer += chunk
    return buffer


# ---------------------------------------------------------------- logon (M1, condensed)

def logon(host, user, password):
    """Runs the SRP6 logon and returns the 40-byte session key both sides derived."""
    sock = socket.create_connection((host, AUTH_PORT), timeout=10)

    body = b"WoW\x00"[::-1] + bytes([3, 3, 5]) + struct.pack("<H", BUILD)
    body += b"x86\x00"[::-1] + b"Win\x00"[::-1] + b"enUS"[::-1]
    body += struct.pack("<I", 0) + bytes([127, 0, 0, 1])
    body += bytes([len(user)]) + user
    sock.sendall(bytes([0x00, 0x08]) + struct.pack("<H", len(body)) + body)

    head = recv_exactly(sock, 3, "challenge response")
    if head[2] != 0:
        fail(f"logon challenge rejected with code 0x{head[2]:02X}")

    rest = recv_exactly(sock, 116, "challenge body")
    B = le(rest[0:32])
    salt = rest[67:99]

    x = le(sha1(salt, sha1(user + b":" + password)))
    v = pow(G, x, N)
    a = le(os.urandom(19))
    A = pow(G, a, N)
    u = le(sha1(tole(A, 32), tole(B, 32)))
    S = tole(pow((B - K_MULTIPLIER * v) % N, a + u * x, N), 32)

    even = bytes(S[i * 2] for i in range(16))
    odd = bytes(S[i * 2 + 1] for i in range(16))
    offset = 0
    while offset < 32 and S[offset] == 0:
        offset += 1
    if offset & 1:
        offset += 1
    offset //= 2
    h0, h1 = sha1(even[offset:]), sha1(odd[offset:])
    session_key = bytes(h0[i // 2] if i % 2 == 0 else h1[i // 2] for i in range(40))

    ng = bytes(p ^ q for p, q in zip(sha1(tole(N, 32)), sha1(bytes([G]))))
    M1 = sha1(ng, sha1(user), salt, tole(A, 32), tole(B, 32), session_key)

    sock.sendall(bytes([0x01]) + tole(A, 32) + M1 + b"\x00" * 20 + bytes([0, 0]))
    proof = recv_exactly(sock, 32, "logon proof response")
    if proof[1] != 0:
        fail(f"login rejected with code 0x{proof[1]:02X}")
    if proof[2:22] != sha1(tole(A, 32), M1, session_key):
        fail("server proof M2 does not match")

    print("  logon ok (session key derived)")

    # Realm list, so we can find the world server the realm advertises.
    sock.sendall(bytes([0x10, 0, 0, 0, 0]))
    header = recv_exactly(sock, 3, "realm list header")
    payload = recv_exactly(sock, struct.unpack("<H", header[1:3])[0], "realm list body")

    count = struct.unpack("<H", payload[4:6])[0]
    if count == 0:
        fail("realm list is empty")

    cursor = 6 + 3
    end = payload.index(b"\x00", cursor)
    name = payload[cursor:end].decode()
    cursor = end + 1
    end = payload.index(b"\x00", cursor)
    address = payload[cursor:end].decode()

    sock.close()
    print(f"  realm list ok: {name} @ {address}")
    return session_key, address


# ---------------------------------------------------------------- world session

class WorldClient:
    """Speaks the world protocol: mixed-endian headers, RC4 header encryption after auth."""

    def __init__(self, host, port):
        self.sock = socket.create_connection((host, port), timeout=10)
        self.encrypt = None
        self.decrypt = None

    def enable_encryption(self, session_key):
        # The client's decrypt stream is keyed with the server's *encrypt* seed, and vice versa.
        self.decrypt = Arc4(hmac.new(SERVER_ENCRYPT_SEED, session_key, hashlib.sha1).digest())
        self.encrypt = Arc4(hmac.new(SERVER_DECRYPT_SEED, session_key, hashlib.sha1).digest())
        self.decrypt.drop(1024)
        self.encrypt.drop(1024)

    def send(self, opcode, body=b""):
        # Client header: big-endian size covering the 4-byte opcode, then little-endian opcode.
        header = struct.pack(">H", len(body) + 4) + struct.pack("<I", opcode)
        if self.encrypt:
            header = self.encrypt.process(header)
        self.sock.sendall(header + body)

    def recv(self):
        """Reads one server packet. Returns (opcode, payload)."""
        header = recv_exactly(self.sock, 4, "server header")
        if self.decrypt:
            header = self.decrypt.process(header)

        if header[0] & 0x80:
            # Three-byte size form: one more byte, which also needs decrypting in stream order.
            extra = recv_exactly(self.sock, 1, "large header byte")
            if self.decrypt:
                extra = self.decrypt.process(extra)
            size = ((header[0] & 0x7F) << 16) | (header[1] << 8) | header[2]
            opcode = header[3] | (extra[0] << 8)
        else:
            size = (header[0] << 8) | header[1]
            opcode = struct.unpack("<H", header[2:4])[0]

        payload = recv_exactly(self.sock, size - 2, "packet body") if size > 2 else b""
        return opcode, payload

    def expect(self, wanted, name):
        opcode, payload = self.recv()
        if opcode != wanted:
            fail(f"expected {name} (0x{wanted:03X}), got opcode 0x{opcode:03X}")
        return payload


def world_session(host, port, user, session_key):
    client = WorldClient(host, port)

    payload = client.expect(SMSG_AUTH_CHALLENGE, "SMSG_AUTH_CHALLENGE")
    if len(payload) != 40:
        fail(f"SMSG_AUTH_CHALLENGE should be 40 bytes, got {len(payload)}")

    server_seed = payload[4:8]
    client_seed = os.urandom(4)
    print("  world challenge ok (40 bytes)")

    digest = sha1(user, b"\x00\x00\x00\x00", client_seed, server_seed, session_key)

    body = struct.pack("<I", BUILD)
    body += struct.pack("<I", 0)              # login server id
    body += user + b"\x00"
    body += struct.pack("<I", 0)              # login server type
    body += client_seed
    body += struct.pack("<I", 0)              # region id
    body += struct.pack("<I", 0)              # battlegroup id
    body += struct.pack("<I", 1)              # realm id
    body += struct.pack("<Q", 0)              # DoS response
    body += digest
    body += struct.pack("<I", 0)              # addon manifest: zero uncompressed size

    # Sent in the clear: the server only switches its crypt on while handling this packet.
    client.send(CMSG_AUTH_SESSION, body)
    client.enable_encryption(session_key)

    payload = client.expect(SMSG_AUTH_RESPONSE, "SMSG_AUTH_RESPONSE")
    if payload[0] != AUTH_OK:
        fail(f"world auth rejected with code 0x{payload[0]:02X}")
    print(f"  world auth ok (encrypted headers, expansion {payload[10]})")

    # Everything from here on has an encrypted header; a desynchronised keystream shows up as a
    # nonsense opcode rather than a decryption error, so each step is checked by name.
    client.expect(SMSG_ADDON_INFO, "SMSG_ADDON_INFO")
    client.expect(SMSG_CLIENTCACHE_VERSION, "SMSG_CLIENTCACHE_VERSION")
    client.expect(SMSG_TUTORIAL_FLAGS, "SMSG_TUTORIAL_FLAGS")
    print("  addon info, cache version and tutorial flags ok")

    client.send(CMSG_PING, struct.pack("<II", 0xDEADBEEF, 0))
    pong = client.expect(SMSG_PONG, "SMSG_PONG")
    if struct.unpack("<I", pong)[0] != 0xDEADBEEF:
        fail("SMSG_PONG did not echo the ping sequence")
    print("  ping ok (RC4 streams still in step)")

    client.send(CMSG_CHAR_ENUM)
    characters = client.expect(SMSG_CHAR_ENUM, "SMSG_CHAR_ENUM")
    starting_count = characters[0]
    print(f"  character list ok ({starting_count} characters — the client shows the character screen here)")

    # ---- create a character, then prove it comes back in the list
    name = "Harnessbot"

    body = name.encode() + b"\x00"
    body += bytes([1, 1, 0])          # human warrior, male
    body += bytes([0, 0, 0, 0, 0])    # skin, face, hair style, hair colour, facial hair
    body += bytes([0])                # outfit id
    client.send(CMSG_CHAR_CREATE, body)

    result = client.expect(SMSG_CHAR_CREATE, "SMSG_CHAR_CREATE")[0]

    if result == CHAR_CREATE_NAME_IN_USE:
        print(f"  '{name}' already exists — reusing it")
    elif result != CHAR_CREATE_SUCCESS:
        fail(f"character creation rejected with code 0x{result:02X}")
    else:
        print(f"  created '{name}' (human warrior)")

    client.send(CMSG_CHAR_ENUM)
    characters = client.expect(SMSG_CHAR_ENUM, "SMSG_CHAR_ENUM")

    count = characters[0]
    if count < 1:
        fail("the created character did not come back in the character list")

    # guid(8) then a NUL-terminated name.
    guid = struct.unpack("<Q", characters[1:9])[0]
    end = characters.index(b"\x00", 9)
    listed = characters[9:end].decode()

    cursor = end + 1
    race, char_class, gender = characters[cursor], characters[cursor + 1], characters[cursor + 2]
    cursor += 3 + 5 + 1                                   # appearance, level
    zone, char_map = struct.unpack("<II", characters[cursor:cursor + 8])
    cursor += 8
    x, y, z = struct.unpack("<fff", characters[cursor:cursor + 12])

    print(f"  list now has {count}: '{listed}' guid 0x{guid:016X} race {race} class {char_class} gender {gender}")
    print(f"    starts on map {char_map} at ({x:.1f}, {y:.1f}, {z:.1f}) — from playercreateinfo")

    if listed.lower() != name.lower():
        fail(f"expected '{name}' in the list, got '{listed}'")
    if (x, y, z) == (0.0, 0.0, 0.0):
        fail("start position is the origin — playercreateinfo was not applied")

    # ---- delete it again so the gate is repeatable
    client.send(CMSG_CHAR_DELETE, struct.pack("<Q", guid))
    deleted = client.expect(SMSG_CHAR_DELETE, "SMSG_CHAR_DELETE")[0]
    if deleted != CHAR_DELETE_SUCCESS:
        fail(f"character deletion rejected with code 0x{deleted:02X}")

    client.send(CMSG_CHAR_ENUM)
    characters = client.expect(SMSG_CHAR_ENUM, "SMSG_CHAR_ENUM")
    if characters[0] != starting_count:
        fail(f"after deleting, expected {starting_count} characters, got {characters[0]}")
    print("  delete ok (list back to where it started)")

    client.sock.close()


def main():
    parser = argparse.ArgumentParser(description="M2 gate: reach the character list without a WoW client.")
    parser.add_argument("host", nargs="?", default="127.0.0.1")
    parser.add_argument("--user", default="TEST")
    parser.add_argument("--password", default="TEST")
    parser.add_argument("--world-port", type=int, default=None,
                        help="override the port from the realm list")
    args = parser.parse_args()

    user = args.user.upper().encode()
    password = args.password.upper().encode()

    print(f"M2 gate against {args.host} as {user.decode()}")

    try:
        session_key, realm_address = logon(args.host, user, password)

        host, _, port = realm_address.partition(":")
        port = args.world_port or int(port or 8085)

        # The realm may advertise an address meant for a different machine (a VM, say); the gate
        # always talks to the host it was pointed at.
        world_session(args.host, port, user, session_key)
    except (ConnectionRefusedError, socket.timeout, OSError) as error:
        fail(f"{error} — are both servers running?")

    print("PASS")


if __name__ == "__main__":
    main()
