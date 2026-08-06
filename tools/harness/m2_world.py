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
import zlib

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
CMSG_PLAYER_LOGIN = 0x03D
SMSG_LOGIN_VERIFY_WORLD = 0x236
SMSG_ACCOUNT_DATA_TIMES = 0x209
SMSG_FEATURE_SYSTEM_STATUS = 0x3C9
SMSG_MOTD = 0x33D
SMSG_LEARNED_DANCE_MOVES = 0x455
SMSG_BINDPOINTUPDATE = 0x155
SMSG_INSTANCE_DIFFICULTY = 0x33B
SMSG_LOGIN_SETTIMESPEED = 0x042
SMSG_UPDATE_OBJECT = 0x0A9
SMSG_COMPRESSED_UPDATE_OBJECT = 0x1F6
SMSG_MONSTER_MOVE = 0x0DD
SMSG_TIME_SYNC_REQ = 0x390
SMSG_INITIAL_SPELLS = 0x12A
CMSG_ITEM_QUERY_SINGLE = 0x056
SMSG_ITEM_QUERY_SINGLE_RESPONSE = 0x058

# Every starting outfit in the game carries one, so it is the safest item to ask about.
HEARTHSTONE = 6948
CMSG_LOGOUT_REQUEST = 0x04B
SMSG_LOGOUT_RESPONSE = 0x04C
SMSG_LOGOUT_COMPLETE = 0x04D
MSG_MOVE_HEARTBEAT = 0x0EE
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

    def expect(self, wanted, name, skip_unsolicited=True):
        """Reads until the wanted opcode, stepping over packets the server pushes on its own.

        Once the world has creatures in it, entering it produces a create block per creature in
        range, and once they wander it produces a monster-move whenever one sets off. Both arrive
        whenever the map gets round to them -- including in the middle of a request and its reply. A
        real client handles them at any time, so the gate has to as well. Skipping is disabled when
        the wanted opcode is itself one of these, so the checks that read a create block still read
        the one they meant to.
        """
        skippable = {SMSG_UPDATE_OBJECT, SMSG_COMPRESSED_UPDATE_OBJECT, SMSG_MONSTER_MOVE} - {wanted}

        if not skip_unsolicited:
            skippable = set()

        # Bounded: a server that only ever sends updates is a failure, not something to wait out.
        # The bound is generous because the server currently sends one packet per object rather than
        # batching a tick's worth into one -- 131 creatures stand within sight of the human start.
        for _ in range(1024):
            opcode, payload = self.recv()

            if opcode == wanted:
                return payload

            if opcode not in skippable:
                fail(f"expected {name} (0x{wanted:03X}), got opcode 0x{opcode:03X}")

        fail(f"expected {name} (0x{wanted:03X}), got 64 unsolicited packets instead")


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

    created = result == CHAR_CREATE_SUCCESS

    if result == CHAR_CREATE_NAME_IN_USE:
        print(f"  '{name}' already exists — reusing it")
    elif result != CHAR_CREATE_SUCCESS:
        fail(f"character creation rejected with code 0x{result:02X}")
    else:
        print(f"  created '{name}' (human warrior)")

    client.send(CMSG_CHAR_ENUM)
    characters = client.expect(SMSG_CHAR_ENUM, "SMSG_CHAR_ENUM")

    roster = parse_char_enum(characters)
    if not roster:
        fail("the created character did not come back in the character list")

    mine = next((entry for entry in roster if entry["name"].lower() == name.lower()), None)
    if mine is None:
        listed = ", ".join(entry["name"] for entry in roster)
        fail(f"'{name}' is not in the character list (got: {listed})")

    guid, x, y, z = mine["guid"], mine["x"], mine["y"], mine["z"]

    print(f"  list now has {len(roster)}: {', '.join(entry['name'] for entry in roster)}")
    print(f"    '{mine['name']}' guid 0x{guid:016X} race {mine['race']} class {mine['class']} "
          f"level {mine['level']}")
    print(f"    starts on map {mine['map']} at ({x:.1f}, {y:.1f}, {z:.1f}) — from playercreateinfo")

    if (x, y, z) == (0.0, 0.0, 0.0):
        fail("start position is the origin — playercreateinfo was not applied")

    # ---- enter the world (M3)
    enter_world(client, guid, mine["name"], x, y, z)

    # ---- delete it again so the gate is repeatable
    client.send(CMSG_CHAR_DELETE, struct.pack("<Q", guid))
    deleted = client.expect(SMSG_CHAR_DELETE, "SMSG_CHAR_DELETE")[0]
    if deleted != CHAR_DELETE_SUCCESS:
        fail(f"character deletion rejected with code 0x{deleted:02X}")

    client.send(CMSG_CHAR_ENUM)
    characters = client.expect(SMSG_CHAR_ENUM, "SMSG_CHAR_ENUM")
    # If the character already existed it was part of the starting count, so deleting it leaves
    # one fewer than we began with.
    expected = starting_count if created else starting_count - 1

    if characters[0] != expected:
        fail(f"after deleting, expected {expected} characters, got {characters[0]}")
    print("  delete ok (list back to where it started)")

    client.sock.close()


def pack_guid(value):
    """Packs a guid the way the client does: a mask byte then only the non-zero bytes."""
    mask = 0
    parts = b""

    for i in range(8):
        byte = (value >> (i * 8)) & 0xFF
        if byte:
            mask |= 1 << i
            parts += bytes([byte])

    return bytes([mask]) + parts


def parse_char_enum(payload):
    """Decodes SMSG_CHAR_ENUM into a list of characters.

    Every record is a fixed shape with one variable-length field (the name), so the whole list has
    to be walked in order -- there are no offsets to jump by.
    """
    count = payload[0]
    cursor = 1
    roster = []

    for _ in range(count):
        guid = struct.unpack("<Q", payload[cursor:cursor + 8])[0]
        cursor += 8

        end = payload.index(b"\x00", cursor)
        name = payload[cursor:end].decode()
        cursor = end + 1

        race, char_class, gender = payload[cursor], payload[cursor + 1], payload[cursor + 2]
        cursor += 3 + 5                                   # race/class/gender, then appearance

        level = payload[cursor]
        cursor += 1

        zone, char_map = struct.unpack("<II", payload[cursor:cursor + 8])
        cursor += 8

        x, y, z = struct.unpack("<fff", payload[cursor:cursor + 12])
        cursor += 12

        cursor += 4 + 4 + 4 + 1                           # guild, char flags, customize flags, first login
        cursor += 12                                      # pet display, level, family

        # 23 slots of display id, inventory type and enchant. The last four are the bag slots,
        # which the selection screen reads and does not draw.
        equipment = []

        for _ in range(23):
            display_id, inventory_type = struct.unpack("<IB", payload[cursor:cursor + 5])
            cursor += 4 + 1 + 4
            equipment.append((display_id, inventory_type))

        roster.append({"guid": guid, "name": name, "race": race, "class": char_class,
                       "gender": gender, "level": level, "zone": zone, "map": char_map,
                       "x": x, "y": y, "z": z, "equipment": equipment})

    return roster


def enter_world(client, guid, name, expected_x, expected_y, expected_z):
    """Sends CMSG_PLAYER_LOGIN and walks the burst the client waits on."""
    client.send(CMSG_PLAYER_LOGIN, struct.pack("<Q", guid))

    payload = client.expect(SMSG_LOGIN_VERIFY_WORLD, "SMSG_LOGIN_VERIFY_WORLD")
    world_map, wx, wy, wz, wo = struct.unpack("<Iffff", payload)
    print(f"  login verify ok: map {world_map} at ({wx:.1f}, {wy:.1f}, {wz:.1f})")

    if abs(wx - expected_x) > 0.1 or abs(wy - expected_y) > 0.1 or abs(wz - expected_z) > 0.1:
        fail("login position does not match the character list position")

    # Order matters -- the client drives its loading screen off this exact sequence.
    for opcode, label in [
        (SMSG_ACCOUNT_DATA_TIMES, "SMSG_ACCOUNT_DATA_TIMES"),
        (SMSG_FEATURE_SYSTEM_STATUS, "SMSG_FEATURE_SYSTEM_STATUS"),
        (SMSG_MOTD, "SMSG_MOTD"),
        (SMSG_LEARNED_DANCE_MOVES, "SMSG_LEARNED_DANCE_MOVES"),
        (SMSG_BINDPOINTUPDATE, "SMSG_BINDPOINTUPDATE"),
        (SMSG_INSTANCE_DIFFICULTY, "SMSG_INSTANCE_DIFFICULTY"),
        (SMSG_LOGIN_SETTIMESPEED, "SMSG_LOGIN_SETTIMESPEED"),

        # The spellbook, last of the burst and before the create block. The client builds its
        # spellbook and action bars from this and nothing else.
        (SMSG_INITIAL_SPELLS, "SMSG_INITIAL_SPELLS"),
    ]:
        client.expect(opcode, label)

    print("  login burst ok (8 packets in order)")

    opcode, payload = client.recv()

    if opcode == SMSG_COMPRESSED_UPDATE_OBJECT:
        uncompressed = struct.unpack("<I", payload[:4])[0]
        payload = zlib.decompress(payload[4:])
        if len(payload) != uncompressed:
            fail(f"compressed update claimed {uncompressed} bytes, inflated to {len(payload)}")
        print(f"  self create ok (compressed {uncompressed} -> wire, inflated cleanly)")
    elif opcode == SMSG_UPDATE_OBJECT:
        print(f"  self create ok (uncompressed, {len(payload)} bytes)")
    else:
        fail(f"expected an object update, got opcode 0x{opcode:03X}")

    # The player, then one create block per item it owns -- a new character is dressed, so this is
    # never just one. The items have to be in the same packet: their guids are already in the
    # player's slot fields, and a slot pointing at an object the client has never heard of draws an
    # empty square.
    blocks = struct.unpack("<I", payload[:4])[0]
    if blocks < 1:
        fail(f"expected at least one update block, got {blocks}")

    print(f"  update carries {blocks} block(s): the player and {blocks - 1} item(s)")

    update_type = payload[4]
    if update_type != 3:
        fail(f"a player must be created with CREATE_OBJECT2 (3), got {update_type}")

    # Packed guid: a mask byte then one byte per set bit.
    mask = payload[5]
    guid_bytes = bin(mask).count("1")
    cursor = 6 + guid_bytes

    type_id = payload[cursor]
    if type_id != 4:
        fail(f"expected TYPEID_PLAYER (4), got {type_id}")
    cursor += 1

    update_flags = struct.unpack("<H", payload[cursor:cursor + 2])[0]
    if not update_flags & 0x0001:
        fail("the player's own create block must carry UPDATEFLAG_SELF")
    if not update_flags & 0x0020:
        fail("a player create block must carry UPDATEFLAG_LIVING")

    print(f"  create block ok: CREATE_OBJECT2, TYPEID_PLAYER, flags 0x{update_flags:04X}")

    client.expect(SMSG_TIME_SYNC_REQ, "SMSG_TIME_SYNC_REQ")
    print(f"  time sync requested — '{name}' is in the world")

    # Ask about one of the items the character is holding. The client does this for anything it has
    # no cached tooltip for, and blocks the tooltip on the answer.
    client.send(CMSG_ITEM_QUERY_SINGLE, struct.pack("<I", HEARTHSTONE))
    response = client.expect(SMSG_ITEM_QUERY_SINGLE_RESPONSE, "SMSG_ITEM_QUERY_SINGLE_RESPONSE")

    entry = struct.unpack("<I", response[:4])[0]

    if entry & 0x80000000:
        fail(f"the server does not know item {HEARTHSTONE} — is item_template imported?")

    end = response.index(b"\x00", 16)
    item_name = response[16:end].decode()

    print(f"  item query ok: {HEARTHSTONE} is '{item_name}' ({len(response)} bytes)")

    # ---- walk somewhere, then log out, and prove the position survived
    moved_x, moved_y, moved_z = expected_x + 25.0, expected_y - 15.0, expected_z

    body = pack_guid(guid)
    body += struct.pack("<IHI", 0, 0, 1000)              # flags, extra flags, time
    body += struct.pack("<ffff", moved_x, moved_y, moved_z, 1.5)
    body += struct.pack("<I", 0)                          # fall time
    client.send(MSG_MOVE_HEARTBEAT, body)

    client.send(CMSG_LOGOUT_REQUEST)
    response = client.expect(SMSG_LOGOUT_RESPONSE, "SMSG_LOGOUT_RESPONSE")
    if response[0] != 0:
        fail(f"logout refused with reason {response[0]}")
    client.expect(SMSG_LOGOUT_COMPLETE, "SMSG_LOGOUT_COMPLETE")
    print(f"  logged out at ({moved_x:.1f}, {moved_y:.1f})")

    # Back at character selection, so the list should show the new position.
    client.send(CMSG_CHAR_ENUM)
    roster = parse_char_enum(client.expect(SMSG_CHAR_ENUM, "SMSG_CHAR_ENUM"))
    saved = next((entry for entry in roster if entry["guid"] == guid), None)

    if saved is None:
        fail("the character vanished from the list after logout")

    if abs(saved["x"] - moved_x) > 0.5 or abs(saved["y"] - moved_y) > 0.5:
        fail(f"position was not saved: expected ({moved_x:.1f}, {moved_y:.1f}), "
             f"list says ({saved['x']:.1f}, {saved['y']:.1f})")

    print(f"  position persisted: list now shows ({saved['x']:.1f}, {saved['y']:.1f})")

    # The selection screen draws each character wearing what it owns, so the equipment block is
    # what proves the inventory survived the logout.
    worn = [(slot, display) for slot, (display, _) in enumerate(saved["equipment"]) if display != 0]

    if not worn:
        fail("the character is naked in the list — starting gear was not saved")

    print(f"  equipment persisted: {len(worn)} visible slot(s) — "
          + ", ".join(f"slot {slot} display {display}" for slot, display in worn))


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
