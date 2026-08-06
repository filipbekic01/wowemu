#!/usr/bin/env python3
"""M6 gate: the whole milestone in one session, across a logout.

The other gates each prove one system. This one proves they hold together, and that what they build
survives the player leaving:

    take two quests -> earn money at a vendor -> log out -> log back in
    -> the quests are still in the log, the money is still in the purse, the bags still match
    -> hand a quest in to a different NPC

The logout is the point. Every system in M6 writes rows on the way out and rebuilds itself on the
way in, and a save that quietly writes nothing looks exactly like one that works — right up until
the next login.

What this can and cannot prove
------------------------------
The same limit as the other gates. It proves the server's packets are well formed and its state
survives a round trip; only a real 3.3.5a client can say whether the packets are laid out the way
it reads them.

    tools/harness/m6_world.py
"""

import argparse
import socket
import struct
import sys

from m2_world import (
    fail, logon,
    CMSG_LOGOUT_REQUEST, SMSG_LOGOUT_COMPLETE, SMSG_LOGOUT_RESPONSE,
)
from m5_combat import cleanup, connect, creature_guid, ensure_character, walk_to
from m6_quest import (
    await_opcode, clear_prerequisite, find_spawn, open_quest_giver, parse_quest_details,
    parse_offer_reward, quest_giver_status, MARSHAL_MCBRIDE,
    CMSG_QUESTGIVER_ACCEPT_QUEST, CMSG_QUESTGIVER_CHOOSE_REWARD, CMSG_QUESTGIVER_COMPLETE_QUEST,
    CMSG_QUESTGIVER_QUERY_QUEST, SMSG_QUESTGIVER_OFFER_REWARD, SMSG_QUESTGIVER_QUEST_COMPLETE,
    SMSG_QUESTGIVER_QUEST_DETAILS, await_quest_slot, QUEST_STATE_COMPLETE,
    QUEST_FLAGS_AUTO_ACCEPT, CMSG_QUESTGIVER_HELLO,
    STATUS_REWARD,
)
from m6_vendor import (
    enter_world, equipped_item_guids, read_vendor_list,
    BROTHER_DANIL, CMSG_GOSSIP_HELLO, CMSG_SELL_ITEM, SMSG_LIST_INVENTORY,
    PLAYER_FIELD_COINAGE,
)

# ---- the content this walks through

DEPUTY_WILLEM = 823
EAGAN_PELTSKINNER = 196

# Offered by Willem two yards from the spawn point, handed in to Eagan. No objectives, so it is
# complete on acceptance — which makes it the one that can be finished after the relogin.
QUEST_ERRAND = 5261

# From Marshal McBride: kill eight Kobold Vermin. Taken and deliberately left unfinished, so the
# relogin has an incomplete quest to bring back as well as a complete one.
#
# Northshire is one long chain and nothing in it starts from nothing: both of these sit behind
# "A Threat Within" (783), which the gate clears first. The obvious second quest here used to be 33
# "Wolves Across the Border", but that one is chained behind QUEST_ERRAND itself — it cannot be held
# at the same time, which is exactly what this gate needs two quests for.
QUEST_KOBOLDS = 7

PLAYER_QUEST_LOG_1_1 = 158
QUEST_LOG_SLOT_WIDTH = 5
MAX_QUEST_LOG_SIZE = 25


def quest_log(values):
    """The quest ids sitting in the player's log slots, read out of its own field block."""
    log = {}

    for slot in range(MAX_QUEST_LOG_SIZE):
        field = PLAYER_QUEST_LOG_1_1 + (slot * QUEST_LOG_SLOT_WIDTH)
        quest_id = values.get(field, 0)

        if quest_id:
            log[quest_id] = slot

    return log


def accept_quest(client, npc_guid, quest_id, expect_complete):
    """Opens one quest and takes it, the way the client would.

    Branches on QUEST_FLAGS_AUTO_ACCEPT exactly as the client does: when it is set the quest is
    already in the log by the time the window arrives and no accept is sent. Sending one anyway
    would test a conversation that never happens, and hide a server that never adds the quest.
    """
    client.send(CMSG_QUESTGIVER_QUERY_QUEST, struct.pack("<QIB", npc_guid, quest_id, 0))

    details = parse_quest_details(
        await_opcode(client, SMSG_QUESTGIVER_QUEST_DETAILS, "SMSG_QUESTGIVER_QUEST_DETAILS"))

    if details["id"] != quest_id:
        fail(f"opened quest {details['id']}, expected {quest_id}")

    if not details["flags"] & QUEST_FLAGS_AUTO_ACCEPT:
        client.send(CMSG_QUESTGIVER_ACCEPT_QUEST, struct.pack("<QII", npc_guid, quest_id, 0))

    if expect_complete:
        # No packet announces this — the slot's state word is the whole notification.
        if await_quest_slot(client, quest_id).get(1) != QUEST_STATE_COMPLETE:
            fail(f"quest {quest_id} should be complete on acceptance, but its slot says otherwise")

    print(f"  accepted {quest_id} '{details['title']}'"
          + (" — complete already" if expect_complete else " — objectives outstanding"))

    return details


def log_out(client):
    """Leaves the world, which is what makes everything write itself down."""
    client.send(CMSG_LOGOUT_REQUEST)

    response = await_opcode(client, SMSG_LOGOUT_RESPONSE, "SMSG_LOGOUT_RESPONSE")

    if response[0] != 0:
        fail(f"logout refused with reason {response[0]}")

    await_opcode(client, SMSG_LOGOUT_COMPLETE, "SMSG_LOGOUT_COMPLETE")
    print("  logged out")


def run(client, character):
    """Two quests, some money, a logout, and a hand-in."""
    guid = character["guid"]

    start, values = enter_world(client, guid)

    willem_spawn, willem_at = find_spawn(client, DEPUTY_WILLEM)
    eagan_spawn, eagan_at = find_spawn(client, EAGAN_PELTSKINNER)
    danil_spawn, danil_at = find_spawn(client, BROTHER_DANIL)
    mcbride_spawn, mcbride_at = find_spawn(client, MARSHAL_MCBRIDE)

    willem = creature_guid(DEPUTY_WILLEM, willem_spawn)
    eagan = creature_guid(EAGAN_PELTSKINNER, eagan_spawn)
    danil = creature_guid(BROTHER_DANIL, danil_spawn)
    mcbride = creature_guid(MARSHAL_MCBRIDE, mcbride_spawn)

    # ---- both of the quests below are chained behind this one
    clear_prerequisite(client, character, start, willem)

    # ---- take the errand from Willem, two yards away
    accept_quest(client, willem, QUEST_ERRAND, expect_complete=True)

    # ---- and the kill quest from McBride
    walk_to(client, guid, start, mcbride_at)
    position = mcbride_at

    accept_quest(client, mcbride, QUEST_KOBOLDS, expect_complete=False)

    # ---- earn something, so there is money to check afterwards
    walk_to(client, guid, position, danil_at)
    position = danil_at

    client.send(CMSG_GOSSIP_HELLO, struct.pack("<Q", danil))
    await_opcode(client, SMSG_LIST_INVENTORY, "SMSG_LIST_INVENTORY")

    worn = equipped_item_guids(values)

    if not worn:
        fail("the character is wearing nothing to sell")

    for _slot, item_guid in worn:
        client.send(CMSG_SELL_ITEM, struct.pack("<QQB", danil, item_guid, 0))

    print(f"  sold {len(worn)} equipped item(s) to Brother Danil")

    # ---- out and back in. Everything above has to be written down and rebuilt.
    log_out(client)

    _again, after = enter_world(client, guid)

    money = after.get(PLAYER_FIELD_COINAGE, 0)
    log = quest_log(after)

    print(f"  back in the world with {money} copper and {len(log)} quest(s) in the log")

    if money == 0:
        fail("the money did not survive the logout")

    if QUEST_ERRAND not in log:
        fail(f"quest {QUEST_ERRAND} was not in the log after logging back in")

    if QUEST_KOBOLDS not in log:
        fail(f"quest {QUEST_KOBOLDS} was not in the log after logging back in")

    if equipped_item_guids(after):
        fail("the character is still wearing what it sold")

    print(f"  both quests came back: {QUEST_ERRAND} at slot {log[QUEST_ERRAND]}, "
          f"{QUEST_KOBOLDS} at slot {log[QUEST_KOBOLDS]}")

    # ---- the complete one should still be handed in
    walk_to(client, guid, position, eagan_at)

    if quest_giver_status(client, eagan) != STATUS_REWARD:
        fail("Eagan is not offering to take the finished quest back after the relogin")

    client.send(CMSG_QUESTGIVER_COMPLETE_QUEST, struct.pack("<QI", eagan, QUEST_ERRAND))

    reward = parse_offer_reward(
        await_opcode(client, SMSG_QUESTGIVER_OFFER_REWARD, "SMSG_QUESTGIVER_OFFER_REWARD"))

    if reward["id"] != QUEST_ERRAND:
        fail(f"offered a reward for quest {reward['id']}, not {QUEST_ERRAND}")

    client.send(CMSG_QUESTGIVER_CHOOSE_REWARD, struct.pack("<QII", eagan, QUEST_ERRAND, 0))

    payload = await_opcode(
        client, SMSG_QUESTGIVER_QUEST_COMPLETE, "SMSG_QUESTGIVER_QUEST_COMPLETE")

    quest_id, experience, _money = struct.unpack("<III", payload[:12])

    if quest_id != QUEST_ERRAND or experience == 0:
        fail(f"the hand-in paid {experience} experience for quest {quest_id}")

    print(f"  handed it in after the relogin: {experience} experience")

    # ---- and the incomplete one is still there, still incomplete
    log_out(client)
    _final, last = enter_world(client, guid)

    final_log = quest_log(last)

    if QUEST_ERRAND in final_log:
        fail("the finished quest is still in the log")

    if QUEST_KOBOLDS not in final_log:
        fail("the unfinished quest fell out of the log")

    print("  the finished quest left the log; the unfinished one stayed")


def main():
    parser = argparse.ArgumentParser(
        description="M6 gate: quests, money and bags across a logout.")
    parser.add_argument("host", nargs="?", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=0, help="world port; 0 takes it from the realm list")
    parser.add_argument("--user", default="test")
    parser.add_argument("--password", default="test")
    parser.add_argument("--character", default="Tourbot")

    args = parser.parse_args()

    user = args.user.upper().encode()
    password = args.password.upper().encode()

    print(f"M6 world gate against {args.host} as {user.decode()}")

    client = None
    character = None

    try:
        session_key, realm_address = logon(args.host, user, password)
        _, _, port = realm_address.partition(":")
        port = args.port or int(port or 8085)

        client = connect(args.host, port, user, session_key)
        character = ensure_character(client, args.character)

        run(client, character)

    except (ConnectionRefusedError, socket.timeout, TimeoutError, OSError) as error:
        fail(f"{error} — are both servers running?")
    finally:
        # Always. A character part-way through this leaves the next run starting from somewhere
        # else, with quests already taken and nothing left to sell.
        if client is not None and character is not None:
            cleanup(client, character["guid"])

    print("PASS")


if __name__ == "__main__":
    sys.exit(main())
