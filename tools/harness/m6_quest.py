#!/usr/bin/env python3
"""M6 gate: take a quest, hand it in, and be paid for it — without a WoW client.

Walks the whole quest loop over the real protocol against the real content: open a questgiver, read
the offer, accept it, walk to whoever takes it back, and collect the reward.

The quest is deliberately a real one from the human starting zone rather than a fixture. Quest 5261
"Eagan Peltskinner" is offered by Deputy Willem two yards from where every human spawns and handed
in to Eagan Peltskinner some ninety yards away, which exercises the part that is easy to get wrong:
the starter and the ender are *different NPCs*, and a server that conflates them passes every unit
test and fails here.

What this can and cannot prove
------------------------------
The same limit as the other gates. It proves the server's packets are well formed, in the right
order and carry the right numbers; it cannot prove a real client draws them the way they were
meant. Only a 3.3.5a client answers that.

    tools/harness/m6_quest.py
"""

import argparse
import socket
import struct
import sys

from m2_world import (
    WorldClient, fail, logon,
    CMSG_LOGOUT_REQUEST, SMSG_LOGOUT_COMPLETE, SMSG_LOGOUT_RESPONSE,
)
from m5_combat import (
    CombatClient, cleanup, connect, creature_guid, ensure_character, enter_world, walk_to,
)

# ---- the quest under test, and the two NPCs it runs between

QUEST_ID = 5261
QUEST_TITLE = "Eagan Peltskinner"

DEPUTY_WILLEM = 823                       # offers it, two yards from the human spawn point
EAGAN_PELTSKINNER = 196                   # takes it back, about ninety yards away

# ---- opcodes

CMSG_QUESTGIVER_STATUS_QUERY = 0x182
SMSG_QUESTGIVER_STATUS = 0x183
CMSG_QUESTGIVER_HELLO = 0x184
SMSG_QUESTGIVER_QUEST_LIST = 0x185
CMSG_QUESTGIVER_QUERY_QUEST = 0x186
SMSG_QUESTGIVER_QUEST_DETAILS = 0x188
CMSG_QUESTGIVER_ACCEPT_QUEST = 0x189
CMSG_QUESTGIVER_COMPLETE_QUEST = 0x18A
SMSG_QUESTGIVER_REQUEST_ITEMS = 0x18B
SMSG_QUESTGIVER_OFFER_REWARD = 0x18D
CMSG_QUESTGIVER_CHOOSE_REWARD = 0x18E
SMSG_QUESTGIVER_QUEST_COMPLETE = 0x191
SMSG_QUESTUPDATE_COMPLETE = 0x198
CMSG_QUEST_QUERY = 0x05C
SMSG_QUEST_QUERY_RESPONSE = 0x05D

SMSG_UPDATE_OBJECT = 0x0A9
SMSG_COMPRESSED_UPDATE_OBJECT = 0x1F6

# The exclamation-mark states, from QuestGiverStatus.
STATUS_AVAILABLE = 8
STATUS_REWARD = 10


def await_opcode(client, wanted, name, limit=512):
    """Reads until `wanted`, stepping over anything else.

    The world pushes creature movement and object updates continuously, so a request and its reply
    are almost never adjacent. Anything that is not what was asked for is noise here.
    """
    for _ in range(limit):
        opcode, payload = client.recv()

        if opcode == wanted:
            return payload

    fail(f"expected {name} (0x{wanted:03X}) and it never arrived")


def read_cstring(payload, cursor):
    end = payload.index(b"\x00", cursor)

    return payload[cursor:end].decode(errors="replace"), end + 1


def find_spawn(host, entry):
    """The spawn id of one creature entry near the human start, read from the world database.

    The gate needs a guid, and a guid needs a spawn id. Asking the database is honest: the server
    is what is under test, and having it tell the gate where to look would make the gate agree with
    the server by construction.
    """
    import subprocess

    query = (
        "SELECT guid, position_x, position_y, position_z FROM creature "
        f"WHERE id = {entry} AND map = 0 "
        "ORDER BY POW(position_x + 8950, 2) + POW(position_y + 132, 2) LIMIT 1;"
    )

    try:
        result = subprocess.run(
            ["docker", "exec", "-i", "wowemu-mysql", "mysql", "-uroot", "-pwowemu",
             "-N", "-B", "wowemu_world", "-e", query],
            capture_output=True, text=True, timeout=30, check=False)
    except (OSError, subprocess.SubprocessError) as error:
        fail(f"could not reach the world database to find creature {entry}: {error}")

    for line in result.stdout.splitlines():
        parts = line.split("\t")

        if len(parts) == 4:
            return int(parts[0]), (float(parts[1]), float(parts[2]), float(parts[3]))

    fail(f"no spawn of creature {entry} near the human start — is `creature` imported?")


def quest_giver_status(client, guid):
    """Asks what mark is over an NPC's head."""
    client.send(CMSG_QUESTGIVER_STATUS_QUERY, struct.pack("<Q", guid))

    payload = await_opcode(client, SMSG_QUESTGIVER_STATUS, "SMSG_QUESTGIVER_STATUS")
    answered, status = struct.unpack("<QB", payload[:9])

    if answered != guid:
        fail(f"asked about 0x{guid:016X} and was told about 0x{answered:016X}")

    return status


def open_quest_giver(client, guid):
    """Says hello, and returns either the menu or the single quest's details."""
    client.send(CMSG_QUESTGIVER_HELLO, struct.pack("<Q", guid))

    for _ in range(512):
        opcode, payload = client.recv()

        if opcode == SMSG_QUESTGIVER_QUEST_LIST:
            return "list", parse_quest_list(payload)

        if opcode == SMSG_QUESTGIVER_QUEST_DETAILS:
            return "details", parse_quest_details(payload)

    fail("the questgiver said nothing at all")


def parse_quest_list(payload):
    """Decodes SMSG_QUESTGIVER_QUEST_LIST into (questid, icon, level, title) tuples."""
    cursor = 8

    _greeting, cursor = read_cstring(payload, cursor)
    cursor += 4 + 4                                # emote delay, emote

    count = payload[cursor]
    cursor += 1

    quests = []

    for _ in range(count):
        quest_id, icon, level, _flags = struct.unpack("<IIiI", payload[cursor:cursor + 16])
        cursor += 16 + 1                           # the repeatable byte

        title, cursor = read_cstring(payload, cursor)
        quests.append((quest_id, icon, level, title))

    return quests


def parse_quest_details(payload):
    """Decodes the head of SMSG_QUESTGIVER_QUEST_DETAILS — everything the gate checks."""
    cursor = 8 + 8                                 # npc guid, divider

    quest_id = struct.unpack("<I", payload[cursor:cursor + 4])[0]
    cursor += 4

    title, cursor = read_cstring(payload, cursor)
    description, cursor = read_cstring(payload, cursor)
    objectives, cursor = read_cstring(payload, cursor)

    return {"id": quest_id, "title": title, "description": description, "objectives": objectives}


def parse_offer_reward(payload):
    """Decodes the head of SMSG_QUESTGIVER_OFFER_REWARD."""
    cursor = 8

    quest_id = struct.unpack("<I", payload[cursor:cursor + 4])[0]
    cursor += 4

    title, cursor = read_cstring(payload, cursor)
    text, cursor = read_cstring(payload, cursor)

    return {"id": quest_id, "title": title, "text": text}


def run(client, character, start):
    """The whole loop: hello, accept, walk, hand in."""
    willem_spawn, willem_at = find_spawn(client, DEPUTY_WILLEM)
    eagan_spawn, eagan_at = find_spawn(client, EAGAN_PELTSKINNER)

    willem = creature_guid(DEPUTY_WILLEM, willem_spawn)
    eagan = creature_guid(EAGAN_PELTSKINNER, eagan_spawn)

    print(f"  Deputy Willem is spawn {willem_spawn} at ({willem_at[0]:.1f}, {willem_at[1]:.1f})")
    print(f"  Eagan Peltskinner is spawn {eagan_spawn} at ({eagan_at[0]:.1f}, {eagan_at[1]:.1f})")

    # ---- the mark over his head, before anything has been taken
    status = quest_giver_status(client, willem)

    if status != STATUS_AVAILABLE:
        fail(f"Deputy Willem shows status {status}, expected {STATUS_AVAILABLE} (available)")

    print(f"  Deputy Willem has something to offer (status {status})")

    # ---- open him
    kind, offer = open_quest_giver(client, willem)

    if kind == "list":
        titles = ", ".join(f"{q[0]} '{q[3]}'" for q in offer)
        print(f"  he offers {len(offer)}: {titles}")

        if not any(quest[0] == QUEST_ID for quest in offer):
            fail(f"quest {QUEST_ID} was not in the list")

        client.send(CMSG_QUESTGIVER_QUERY_QUEST, struct.pack("<QIB", willem, QUEST_ID, 0))
        details = parse_quest_details(
            await_opcode(client, SMSG_QUESTGIVER_QUEST_DETAILS, "SMSG_QUESTGIVER_QUEST_DETAILS"))
    else:
        details = offer

    if details["id"] != QUEST_ID:
        fail(f"opened quest {details['id']}, expected {QUEST_ID}")

    if details["title"] != QUEST_TITLE:
        fail(f"the quest is called '{details['title']}', expected '{QUEST_TITLE}'")

    if not details["description"]:
        fail("the quest has no description — the text columns did not load")

    print(f"  details ok: '{details['title']}' — {details['description'][:60]}...")

    # ---- accept it. This one has no objectives, so it completes on acceptance.
    client.send(CMSG_QUESTGIVER_ACCEPT_QUEST, struct.pack("<QII", willem, QUEST_ID, 0))

    completed = struct.unpack(
        "<I", await_opcode(client, SMSG_QUESTUPDATE_COMPLETE, "SMSG_QUESTUPDATE_COMPLETE")[:4])[0]

    if completed != QUEST_ID:
        fail(f"the server said quest {completed} completed, not {QUEST_ID}")

    print(f"  accepted, and it is complete already (no objectives)")

    # ---- what the client does next, and what the quest log is drawn from
    #
    # The details window is enough to accept a quest. The LOG entry is not: the client will not
    # draw a row for a quest whose structured data it has no copy of, and asks for anything missing
    # from its own cache. With no answer the quest is held server-side and invisible.
    client.send(CMSG_QUEST_QUERY, struct.pack("<I", QUEST_ID))

    payload = await_opcode(client, SMSG_QUEST_QUERY_RESPONSE, "SMSG_QUEST_QUERY_RESPONSE")

    queried = struct.unpack("<I", payload[:4])[0]

    if queried != QUEST_ID:
        fail(f"the query answered for quest {queried}, not {QUEST_ID}")

    # 26 scalar words, the reward and choice pairs, three reputation blocks of five, and four
    # point-of-interest words -- then the five strings.
    cursor = (26 + ((4 + 6) * 2) + (5 * 3) + 4) * 4

    queried_title, cursor = read_cstring(payload, cursor)

    if queried_title != QUEST_TITLE:
        fail(f"the query says the quest is called '{queried_title}', expected '{QUEST_TITLE}'")

    print(f"  the client can draw it: query returned '{queried_title}' ({len(payload)} bytes)")

    # ---- the ender should now be showing a question mark, and the starter should not
    if quest_giver_status(client, eagan) != STATUS_REWARD:
        fail("Eagan is not showing a hand-in mark for a quest that is ready")

    print("  Eagan is ready to take it back")

    # ---- walk over
    walk_to(client, character["guid"], start, eagan_at)

    # ---- hand it in
    client.send(CMSG_QUESTGIVER_COMPLETE_QUEST, struct.pack("<QI", eagan, QUEST_ID))

    reward = parse_offer_reward(
        await_opcode(client, SMSG_QUESTGIVER_OFFER_REWARD, "SMSG_QUESTGIVER_OFFER_REWARD"))

    if reward["id"] != QUEST_ID:
        fail(f"offered a reward for quest {reward['id']}, not {QUEST_ID}")

    print(f"  offer ok: {reward['text'][:60]}...")

    client.send(CMSG_QUESTGIVER_CHOOSE_REWARD, struct.pack("<QII", eagan, QUEST_ID, 0))

    payload = await_opcode(
        client, SMSG_QUESTGIVER_QUEST_COMPLETE, "SMSG_QUESTGIVER_QUEST_COMPLETE")

    quest_id, experience, money = struct.unpack("<III", payload[:12])

    if quest_id != QUEST_ID:
        fail(f"completed quest {quest_id}, not {QUEST_ID}")

    if experience == 0:
        fail("the quest paid no experience — is QuestXP.dbc being read by quest LEVEL?")

    print(f"  handed in: {experience} experience, {money} copper")

    # ---- and it must not be offered again
    if quest_giver_status(client, willem) == STATUS_AVAILABLE:
        remaining = open_quest_giver(client, willem)[1]

        if any(quest[0] == QUEST_ID for quest in remaining):
            fail("the quest is still on offer after being handed in")

    print("  it is no longer on offer")


def main():
    parser = argparse.ArgumentParser(description="M6 gate: take a quest and hand it in.")
    parser.add_argument("host", nargs="?", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=0, help="world port; 0 takes it from the realm list")
    parser.add_argument("--user", default="test")
    parser.add_argument("--password", default="test")
    parser.add_argument("--character", default="Questbot")

    args = parser.parse_args()

    user = args.user.upper().encode()
    password = args.password.upper().encode()

    print(f"M6 gate against {args.host} as {user.decode()}")

    client = None
    character = None

    try:
        session_key, realm_address = logon(args.host, user, password)
        _, _, port = realm_address.partition(":")
        port = args.port or int(port or 8085)

        client = connect(args.host, port, user, session_key)
        character = ensure_character(client, args.character)

        start = enter_world(client, character["guid"])

        run(client, character, start)

    except (ConnectionRefusedError, socket.timeout, TimeoutError, OSError) as error:
        fail(f"{error} — are both servers running?")
    finally:
        # Always, including after a failure. A character that has already handed the quest in makes
        # the next run start from a different place, and a gate whose result depends on whether the
        # last one passed is not a gate.
        if client is not None and character is not None:
            cleanup(client, character["guid"])

    print("PASS")


if __name__ == "__main__":
    sys.exit(main())
