#!/usr/bin/env python3
"""M5 gate: kill something and gain experience, without a WoW client.

Drives the whole milestone over the real protocol — log in, walk to a creature, auto-attack it,
watch the swings land, and check the kill pays experience.

What this can and cannot prove
------------------------------
It proves the *server* does the right thing: the packets it sends are well formed, in the right
order, and carry the right numbers. It cannot prove the *client* likes them — a packet can be
perfectly self-consistent and still be laid out in a way the client reads differently. Only a real
3.3.5a client can answer that, so this gate is the half that can be automated, not the whole test.

    tools/harness/m5_combat.py
"""

import argparse
import math
import socket
import struct
import sys
import time

from m2_world import (
    Arc4, WorldClient, fail, logon, pack_guid, parse_char_enum, sha1,
    CMSG_CHAR_DELETE, CMSG_LOGOUT_REQUEST, SMSG_CHAR_DELETE, SMSG_LOGOUT_COMPLETE,
    SMSG_LOGOUT_RESPONSE,
    AUTH_OK, BUILD, CHAR_CREATE_NAME_IN_USE, CHAR_CREATE_SUCCESS,
    CMSG_AUTH_SESSION, CMSG_CHAR_CREATE, CMSG_CHAR_ENUM, CMSG_PING, CMSG_PLAYER_LOGIN,
    MSG_MOVE_HEARTBEAT,
    SMSG_ACCOUNT_DATA_TIMES, SMSG_ADDON_INFO, SMSG_AUTH_CHALLENGE, SMSG_AUTH_RESPONSE,
    SMSG_BINDPOINTUPDATE, SMSG_CHAR_CREATE, SMSG_CHAR_ENUM, SMSG_CLIENTCACHE_VERSION,
    SMSG_COMPRESSED_UPDATE_OBJECT, SMSG_FEATURE_SYSTEM_STATUS, SMSG_INSTANCE_DIFFICULTY,
    SMSG_LEARNED_DANCE_MOVES, SMSG_LOGIN_SETTIMESPEED, SMSG_LOGIN_VERIFY_WORLD, SMSG_MONSTER_MOVE,
    SMSG_MOTD, SMSG_PONG, SMSG_TIME_SYNC_REQ, SMSG_TUTORIAL_FLAGS, SMSG_UPDATE_OBJECT,
)

CMSG_ATTACKSWING = 0x141
CMSG_ATTACKSTOP = 0x142
SMSG_ATTACKSTART = 0x143
SMSG_ATTACKSTOP = 0x144
SMSG_ATTACKSWING_NOTINRANGE = 0x145
SMSG_ATTACKSWING_BADFACING = 0x146
SMSG_ATTACKERSTATEUPDATE = 0x14A
SMSG_LOG_XPGAIN = 0x1D0
SMSG_LEVELUP_INFO = 0x1D4
SMSG_DESTROY_OBJECT = 0x0AA

HIGHGUID_UNIT = 0xF130

# Where a human warrior starts, from playercreateinfo.
START_X, START_Y, START_Z = -8949.95, -132.493, 83.5

# Diseased Young Wolves in Northshire: level 1, hostile, and the closest things to the start point
# that will actually fight. Taken from the `creature` table rather than parsed out of an update
# block — reimplementing the update-block parser in the harness would be testing the harness.
#
# Several of them, tried in turn, because a wolf killed by the previous run stays dead for four
# minutes: sixty seconds of corpse and three more of respawn. A gate that depends on one spawn can
# only be run once every four minutes, which is not a gate anyone will run.
WOLF_ENTRY = 299
WOLF_SPAWNS = [
    (79937, -8952.1, -83.9, 88.2),
    (79936, -8970.2, -87.7, 87.0),
    (79945, -8993.9, -153.9, 81.1),
    (79938, -9002.5, -130.0, 84.3),
    (80157, -8987.2, -179.0, 77.0),
    (79957, -8918.7, -73.9, 88.4),
    (79946, -9023.1, -141.8, 83.7),
]

# Movement is validated against a server-measured interval, so the walk is taken in steps with a
# pause between them. A single jump of fifty yards reads as a teleport and is rejected.
WALK_STEP_YARDS = 8.0
WALK_PAUSE_SECONDS = 0.35

# Stand this far short of the spawn point rather than on it. Standing exactly on it leaves no
# direction to face — the angle to a point zero yards away is undefined, and atan2(0, 0) is due
# east — so every swing is refused for facing while the wolf, which stops at melee range rather
# than walking into us, hits back from wherever it happens to be. The server says so exactly once,
# because a repeated failure is only reported on its first occurrence, which makes it look silent.
APPROACH_YARDS = 4.0

# How long to wait for a creature to notice us before going to it. One that wandered out of its
# aggro radius, or cannot see us, will never come — and waiting out the clock reports that as a
# combat failure rather than as the walk being too timid.
STALL_SECONDS = 8.0



def creature_guid(entry, spawn_id):
    """The guid the server builds for a creature — counter, entry in the middle bits, type on top."""
    return spawn_id | (entry << 24) | (HIGHGUID_UNIT << 48)


class CombatClient(WorldClient):
    """A world client that can wait for combat packets among the usual traffic."""

    # Everything the server pushes unasked once there is a world to stand in. A real client copes
    # with any of these at any moment, so the gate has to as well.
    BACKGROUND = {
        SMSG_UPDATE_OBJECT,
        SMSG_COMPRESSED_UPDATE_OBJECT,
        SMSG_MONSTER_MOVE,
        SMSG_DESTROY_OBJECT,
        SMSG_TIME_SYNC_REQ,
    }

    def drain(self, seconds, watch=()):
        """Reads for a while, counting the packets of interest.

        Returns {opcode: [payloads]} for everything in `watch`. Anything else is stepped over —
        this is the shape of a real client's main loop, not a request and its reply.
        """
        found = {opcode: [] for opcode in watch}
        deadline = time.monotonic() + seconds

        while time.monotonic() < deadline:
            remaining = deadline - time.monotonic()
            self.sock.settimeout(max(remaining, 0.01))

            try:
                opcode, payload = self.recv()
            except (socket.timeout, TimeoutError):
                break

            if opcode in found:
                found[opcode].append(payload)

        self.sock.settimeout(10)
        return found


def read_packed_guid(payload, cursor):
    """Reads a packed guid: a mask byte, then one byte per set bit, low to high."""
    mask = payload[cursor]
    cursor += 1

    value = 0
    for bit in range(8):
        if mask & (1 << bit):
            value |= payload[cursor] << (bit * 8)
            cursor += 1

    return value, cursor


def parse_attacker_state(payload):
    """Pulls the attacker, target and damage out of SMSG_ATTACKERSTATEUPDATE."""
    hit_info = struct.unpack("<I", payload[:4])[0]
    attacker, cursor = read_packed_guid(payload, 4)
    target, cursor = read_packed_guid(payload, cursor)

    damage = struct.unpack("<I", payload[cursor:cursor + 4])[0]

    return {"hit_info": hit_info, "attacker": attacker, "target": target, "damage": damage}


def parse_monster_move(payload):
    """Pulls the mover and the point it is moving *from* out of SMSG_MONSTER_MOVE.

    That start point is where the creature actually is at the moment the packet is sent, which is
    the only position the server volunteers about a creature after its create block.
    """
    mover, cursor = read_packed_guid(payload, 0)

    cursor += 1   # an always-zero byte upstream describes as a movement flag

    x, y, z = struct.unpack("<fff", payload[cursor:cursor + 12])

    return mover, (x, y, z)


def parse_xp_gain(payload):
    """Pulls the victim and amount out of SMSG_LOG_XPGAIN."""
    victim = struct.unpack("<Q", payload[:8])[0]
    total = struct.unpack("<I", payload[8:12])[0]
    kind = payload[12]

    return {"victim": victim, "total": total, "from_kill": kind == 0}


def connect(host, port, user, session_key):
    """Auths and reaches the character list. The same sequence M2 walks, without its assertions."""
    client = CombatClient(host, port)

    payload = client.expect(SMSG_AUTH_CHALLENGE, "SMSG_AUTH_CHALLENGE")
    server_seed = payload[4:8]
    client_seed = b"\x11\x22\x33\x44"

    digest = sha1(user, b"\x00\x00\x00\x00", client_seed, server_seed, session_key)

    body = struct.pack("<I", BUILD) + struct.pack("<I", 0) + user + b"\x00"
    body += struct.pack("<I", 0) + client_seed
    body += struct.pack("<III", 0, 0, 1) + struct.pack("<Q", 0) + digest + struct.pack("<I", 0)

    client.send(CMSG_AUTH_SESSION, body)
    client.enable_encryption(session_key)

    if client.expect(SMSG_AUTH_RESPONSE, "SMSG_AUTH_RESPONSE")[0] != AUTH_OK:
        fail("world auth rejected")

    for opcode, label in [
        (SMSG_ADDON_INFO, "SMSG_ADDON_INFO"),
        (SMSG_CLIENTCACHE_VERSION, "SMSG_CLIENTCACHE_VERSION"),
        (SMSG_TUTORIAL_FLAGS, "SMSG_TUTORIAL_FLAGS"),
    ]:
        client.expect(opcode, label)

    print("  logged in")
    return client


def ensure_character(client, name):
    """Creates the gate's character if it is not already there, and returns its guid."""
    body = name.encode() + b"\x00" + bytes([1, 1, 0]) + bytes([0, 0, 0, 0, 0]) + bytes([0])
    client.send(CMSG_CHAR_CREATE, body)

    result = client.expect(SMSG_CHAR_CREATE, "SMSG_CHAR_CREATE")[0]

    if result not in (CHAR_CREATE_SUCCESS, CHAR_CREATE_NAME_IN_USE):
        fail(f"character creation rejected with code 0x{result:02X}")

    client.send(CMSG_CHAR_ENUM)
    roster = parse_char_enum(client.expect(SMSG_CHAR_ENUM, "SMSG_CHAR_ENUM"))

    character = next((entry for entry in roster if entry["name"] == name), None)

    if character is None:
        fail(f"'{name}' is not in the character list after creating it")

    print(f"  '{name}' ready (guid 0x{character['guid']:016X}, level {character['level']})")
    return character


def enter_world(client, guid):
    """Sends CMSG_PLAYER_LOGIN and walks the burst, returning where the server put us."""
    client.send(CMSG_PLAYER_LOGIN, struct.pack("<Q", guid))

    payload = client.expect(SMSG_LOGIN_VERIFY_WORLD, "SMSG_LOGIN_VERIFY_WORLD")
    _, x, y, z, _ = struct.unpack("<Iffff", payload)

    for opcode, label in [
        (SMSG_ACCOUNT_DATA_TIMES, "SMSG_ACCOUNT_DATA_TIMES"),
        (SMSG_FEATURE_SYSTEM_STATUS, "SMSG_FEATURE_SYSTEM_STATUS"),
        (SMSG_MOTD, "SMSG_MOTD"),
        (SMSG_LEARNED_DANCE_MOVES, "SMSG_LEARNED_DANCE_MOVES"),
        (SMSG_BINDPOINTUPDATE, "SMSG_BINDPOINTUPDATE"),
        (SMSG_INSTANCE_DIFFICULTY, "SMSG_INSTANCE_DIFFICULTY"),
        (SMSG_LOGIN_SETTIMESPEED, "SMSG_LOGIN_SETTIMESPEED"),
    ]:
        client.expect(opcode, label)

    print(f"  in the world at ({x:.1f}, {y:.1f}, {z:.1f})")
    return x, y, z


def approach_point(start, target):
    """A point a few yards short of the target, on the line from where we are.

    Walking onto the target leaves nothing to face. When we are already standing on it — which
    happens whenever the previous run's position was saved there — the direction is taken from an
    arbitrary axis instead, because any direction is better than none.
    """
    from_x, from_y, _ = start
    to_x, to_y, to_z = target

    dx, dy = to_x - from_x, to_y - from_y
    distance = math.hypot(dx, dy)

    if distance < 0.5:
        return (to_x - APPROACH_YARDS, to_y, to_z)

    if distance <= APPROACH_YARDS:
        return start

    scale = (distance - APPROACH_YARDS) / distance
    return (from_x + dx * scale, from_y + dy * scale, to_z)


def face(client, guid, position, target_position):
    """Turns to look at a point, without moving.

    Swings are refused outside a 120-degree cone in front of the attacker, so a character that never
    turns lands nothing once its target circles it. A real client turns you when you attack; the
    harness has to do the same, and there is no separate facing opcode worth using here — a
    heartbeat carrying a new orientation does it.
    """
    x, y, z = position
    to_x, to_y, _ = target_position

    orientation = math.atan2(to_y - y, to_x - x)

    body = pack_guid(guid)
    body += struct.pack("<IHI", 0, 0, int(time.monotonic() * 1000) & 0xFFFFFFFF)
    body += struct.pack("<ffff", x, y, z, orientation)
    body += struct.pack("<I", 0)

    client.send(MSG_MOVE_HEARTBEAT, body)


def walk_to(client, guid, start, destination):
    """Walks in steps, because the server validates speed against a measured interval."""
    from_x, from_y, from_z = start
    to_x, to_y, to_z = destination

    distance = ((to_x - from_x) ** 2 + (to_y - from_y) ** 2) ** 0.5
    steps = max(int(distance / WALK_STEP_YARDS), 1)

    for step in range(1, steps + 1):
        fraction = step / steps

        x = from_x + (to_x - from_x) * fraction
        y = from_y + (to_y - from_y) * fraction
        z = from_z + (to_z - from_z) * fraction

        body = pack_guid(guid)
        body += struct.pack("<IHI", 0, 0, step * 1000)
        body += struct.pack("<ffff", x, y, z, 0.0)
        body += struct.pack("<I", 0)

        client.send(MSG_MOVE_HEARTBEAT, body)

        # The pause is what makes the step legal: the validator compares distance against elapsed
        # server time, so sending them back to back reads as impossible speed.
        time.sleep(WALK_PAUSE_SECONDS)

    print(f"  walked {distance:.0f} yards in {steps} steps to ({to_x:.1f}, {to_y:.1f})")


def cleanup(client, guid):
    """Logs out and deletes the gate's character, so the next run starts from the spawn point."""
    client.send(CMSG_LOGOUT_REQUEST)

    try:
        # Combat packets are still in flight — an attack-stop for the fight we just left, a last
        # swing — so the logout reply has to be picked out of them rather than demanded next.
        found = client.drain(3.0, watch=(SMSG_LOGOUT_RESPONSE, SMSG_LOGOUT_COMPLETE))

        if not found[SMSG_LOGOUT_COMPLETE]:
            raise TimeoutError("no logout completion")

        client.send(CMSG_CHAR_DELETE, struct.pack("<Q", guid))
        client.expect(SMSG_CHAR_DELETE, "SMSG_CHAR_DELETE")
        print("  cleaned up")
    except (SystemExit, OSError, socket.timeout, TimeoutError):
        # Cleanup failing is worth knowing about but is not the thing under test, and it must not
        # replace the real failure with one about tidying up.
        print("  note: cleanup did not complete — the next run may start beside a wolf")


def main():
    parser = argparse.ArgumentParser(description="M5 gate: kill something and gain experience.")
    parser.add_argument("host", nargs="?", default="127.0.0.1")
    parser.add_argument("--user", default="TEST")
    parser.add_argument("--password", default="TEST")
    parser.add_argument("--world-port", type=int, default=None)
    # Letters only: the server rejects digits in a character name, as the real one does.
    parser.add_argument("--name", default="Gatebot")
    parser.add_argument("--fight-seconds", type=float, default=45.0,
                        help="how long to keep swinging before giving up")
    args = parser.parse_args()

    user = args.user.upper().encode()
    password = args.password.upper().encode()

    print(f"M5 gate against {args.host} as {user.decode()}")

    client = None
    character = None

    try:
        session_key, realm_address = logon(args.host, user, password)
        _, _, port = realm_address.partition(":")
        port = args.world_port or int(port or 8085)

        client = connect(args.host, port, user, session_key)
        character = ensure_character(client, args.name)

        position = enter_world(client, character["guid"])

        # ---- find something that will actually fight, and fight it
        #
        # Two separate things can go wrong per candidate: it may be dead (no attack-start), or it may
        # be alive but never close the distance — wandered out of its aggro radius, or unable to see
        # us. Both are facts about the world rather than faults, so each candidate gets a turn.
        #
        # The second case is nearly silent from outside: swing errors are reported once per *run* of
        # the same error, so a fight that is out of range from start to finish produces exactly one
        # SMSG_ATTACKSWING_NOTINRANGE, which is easily missed. Not waiting for it is the point.
        def by_distance(spawn):
            _, x, y, _ = spawn
            return ((x - position[0]) ** 2 + (y - position[1]) ** 2) ** 0.5

        target = None
        swings = []
        xp_gains = []
        levels = 0
        not_in_range = bad_facing = stopped = 0

        for spawn_id, wolf_x, wolf_y, wolf_z in sorted(WOLF_SPAWNS, key=by_distance):
            candidate = creature_guid(WOLF_ENTRY, spawn_id)
            standing = approach_point(position, (wolf_x, wolf_y, wolf_z))

            walk_to(client, character["guid"], position, standing)
            position = standing

            face(client, character["guid"], position, (wolf_x, wolf_y, wolf_z))
            client.send(CMSG_ATTACKSWING, struct.pack("<Q", candidate))

            seen = client.drain(2.0, watch=(
                SMSG_ATTACKSTART, SMSG_ATTACKSWING_NOTINRANGE, SMSG_ATTACKSWING_BADFACING))

            if not seen[SMSG_ATTACKSTART]:
                print(f"  spawn {spawn_id} is not available — trying the next")
                continue

            print(f"  attacking spawn {spawn_id}")

            target = candidate
            target_at = (wolf_x, wolf_y, wolf_z)
            started = time.monotonic()
            deadline = started + args.fight_seconds

            while time.monotonic() < deadline and not xp_gains:
                found = client.drain(1.0, watch=(
                    SMSG_ATTACKERSTATEUPDATE, SMSG_LOG_XPGAIN, SMSG_LEVELUP_INFO,
                    SMSG_ATTACKSTOP, SMSG_MONSTER_MOVE,
                    SMSG_ATTACKSWING_NOTINRANGE, SMSG_ATTACKSWING_BADFACING))

                not_in_range += len(found[SMSG_ATTACKSWING_NOTINRANGE])
                bad_facing += len(found[SMSG_ATTACKSWING_BADFACING])
                stopped += len(found[SMSG_ATTACKSTOP])

                swings.extend(parse_attacker_state(p) for p in found[SMSG_ATTACKERSTATEUPDATE])
                xp_gains.extend(parse_xp_gain(p) for p in found[SMSG_LOG_XPGAIN])
                levels += len(found[SMSG_LEVELUP_INFO])

                # Follow it: every move tells us where it was when it set off.
                for payload in found[SMSG_MONSTER_MOVE]:
                    mover, at = parse_monster_move(payload)

                    if mover == target:
                        target_at = at

                if xp_gains:
                    break

                if not swings and time.monotonic() - started > STALL_SECONDS:
                    # It is not coming. Walk onto it once, then give up on it and try another.
                    if position != target_at:
                        walk_to(client, character["guid"], position, target_at)
                        position = target_at
                        started = time.monotonic()
                    else:
                        print(f"  spawn {spawn_id} never engaged — trying another")
                        break

                face(client, character["guid"], position, target_at)
                client.send(CMSG_ATTACKSWING, struct.pack("<Q", target))

            if xp_gains:
                break

            client.send(CMSG_ATTACKSTOP)

        if target is None:
            fail("none of the candidate wolves could be attacked — all dead, or the guids are wrong")

        if not swings:
            fail("no swings landed in the whole fight — nothing ever got into reach, or combat "
                 "is not running at all")

        ours = [s for s in swings if s["attacker"] == character["guid"]]
        against_us = [s for s in swings if s["target"] == character["guid"]]
        taken = sum(s["damage"] for s in against_us)

        if not ours:
            fail(f"swings were logged but none of them were ours — we took {taken} damage from "
                 f"{len(against_us)} swings without landing one. The server said: "
                 f"{not_in_range} out-of-range, {bad_facing} bad-facing, {stopped} attack-stop. "
                 f"Attackers: {sorted({hex(s['attacker']) for s in against_us})}, "
                 f"our target 0x{target:016X}, our guid 0x{character['guid']:016X}")

        damage = [s["damage"] for s in ours]

        print(f"  {len(ours)} swings from us, {len(against_us)} back — "
              f"damage {min(damage)}-{max(damage)}, {sum(damage)} dealt, {taken} taken")

        if max(damage) == 0:
            fail("every swing dealt zero damage — an unarmed player has no weapon damage")

        if not xp_gains:
            fail(f"the wolf survived {args.fight_seconds:.0f} seconds — no kill, so no experience")

        gain = xp_gains[0]

        if not gain["from_kill"]:
            fail("the experience was not flagged as coming from a kill")

        if gain["victim"] != target:
            fail(f"experience credited to guid 0x{gain['victim']:016X}, not the wolf")

        if gain["total"] == 0:
            fail("the kill paid zero experience")

        print(f"  killed it — {gain['total']} experience")

        if levels:
            print(f"  levelled up {levels} time(s)")

        client.send(CMSG_ATTACKSTOP)

    except (ConnectionRefusedError, socket.timeout, TimeoutError, OSError) as error:
        fail(f"{error} — are both servers running?")
    finally:
        # Always, including after a failure. A character logged out beside a wolf makes the next run
        # start somewhere else entirely — already adjacent, already hated — and a gate whose starting
        # conditions depend on whether the last run passed is not a gate.
        if client is not None and character is not None:
            cleanup(client, character["guid"])

    print("PASS")


if __name__ == "__main__":
    sys.exit(main())
