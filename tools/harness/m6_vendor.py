#!/usr/bin/env python3
"""M6 gate: sell to a vendor and buy from one — without a WoW client.

Walks the shopkeeping half of the milestone over the real protocol against real content: open
Brother Danil in Northshire Abbey, read his stock, be refused a purchase with an empty purse, sell
the starting gear, and buy something with the proceeds.

The gate reads its own item guids out of the login update packet, the same way a real client does —
they are in the player's own inventory slot fields. That is more work than hard-coding a guess, and
it is the only honest way: a gate the server tells where to look agrees with the server by
construction.

    tools/harness/m6_vendor.py
"""

import argparse
import socket
import struct
import sys
import zlib

from m2_world import (
    fail, logon,
    SMSG_COMPRESSED_UPDATE_OBJECT, SMSG_UPDATE_OBJECT,
)
from m5_combat import cleanup, connect, creature_guid, ensure_character, walk_to
from m6_quest import await_opcode, find_spawn

# ---- the vendor under test

BROTHER_DANIL = 152                       # Northshire Abbey's provisioner, about 50 yards from spawn

# ---- opcodes

CMSG_GOSSIP_HELLO = 0x17B
CMSG_LIST_INVENTORY = 0x19E
SMSG_LIST_INVENTORY = 0x19F
CMSG_SELL_ITEM = 0x1A0
SMSG_SELL_ITEM = 0x1A1
CMSG_BUY_ITEM = 0x1A2
SMSG_BUY_ITEM = 0x1A4
SMSG_BUY_FAILED = 0x1A5
SMSG_ITEM_PUSH_RESULT = 0x166
SMSG_INITIAL_SPELLS = 0x12A

CMSG_PLAYER_LOGIN = 0x03D
SMSG_LOGIN_VERIFY_WORLD = 0x236
SMSG_TIME_SYNC_REQ = 0x390

# ---- update fields, from UpdateFields.g.cs

PLAYER_FIELD_INV_SLOT_HEAD = 324
PLAYER_FIELD_COINAGE = 1170

EQUIPMENT_SLOTS = 19

BUY_ERR_NOT_ENOUGH_MONEY = 2


def parse_update(payload):
    """Decodes SMSG_UPDATE_OBJECT into a list of blocks.

    Handles the two block shapes this server emits: a living create block, and a values block. It
    is what a real client does with the same bytes, and getting it wrong here means getting it
    wrong there.
    """
    count = struct.unpack("<I", payload[:4])[0]
    cursor = 4
    blocks = []

    for _ in range(count):
        update_type = payload[cursor]
        cursor += 1

        guid, cursor = read_packed_guid(payload, cursor)

        type_id = None

        # 0 is a values update, 2 and 3 are the two create forms.
        if update_type in (1, 2, 3):
            type_id = payload[cursor]
            cursor += 1

            flags = struct.unpack("<H", payload[cursor:cursor + 2])[0]
            cursor += 2

            if flags & 0x0020:                     # LIVING
                # flags, extra flags, time, x/y/z/o, fall time -- then the nine speeds.
                cursor += 4 + 2 + 4 + 16 + 4
                cursor += 9 * 4

            if flags & 0x0010:                     # LOWGUID
                cursor += 4

            if flags & 0x0004:                     # HAS_TARGET
                _target, cursor = read_packed_guid(payload, cursor)

        values, cursor = read_values(payload, cursor)
        blocks.append({"type": update_type, "typeId": type_id, "guid": guid, "values": values})

    return blocks


def read_packed_guid(payload, cursor):
    mask = payload[cursor]
    cursor += 1

    guid = 0

    for bit in range(8):
        if mask & (1 << bit):
            guid |= payload[cursor] << (bit * 8)
            cursor += 1

    return guid, cursor


def read_values(payload, cursor):
    """The field mask and the words behind it: a byte of length, that many mask words, then values."""
    block_count = payload[cursor]
    cursor += 1

    mask = []

    for _ in range(block_count):
        mask.append(struct.unpack("<I", payload[cursor:cursor + 4])[0])
        cursor += 4

    values = {}

    for word in range(block_count):
        for bit in range(32):
            if mask[word] & (1 << bit):
                values[word * 32 + bit] = struct.unpack("<I", payload[cursor:cursor + 4])[0]
                cursor += 4

    return values, cursor


def parse_initial_spells(payload):
    """Decodes SMSG_INITIAL_SPELLS into the list of spell ids."""
    count = struct.unpack("<H", payload[1:3])[0]
    cursor = 3
    spells = []

    for _ in range(count):
        spells.append(struct.unpack("<I", payload[cursor:cursor + 4])[0])
        cursor += 6                                # the spell, then two bytes of not-a-slot-id

    return spells


def enter_world(client, guid):
    """Logs in and returns (position, the player's own field values).

    Also stashes the spellbook on the client, because it arrives in the same burst and the caller
    usually wants both.
    """
    client.send(CMSG_PLAYER_LOGIN, struct.pack("<Q", guid))

    payload = client.expect(SMSG_LOGIN_VERIFY_WORLD, "SMSG_LOGIN_VERIFY_WORLD")
    _map, x, y, z, _o = struct.unpack("<Iffff", payload)

    client.spells = []

    # The create burst. The player's own block carries its inventory slot guids.
    for _ in range(64):
        opcode, body = client.recv()

        if opcode == SMSG_INITIAL_SPELLS:
            client.spells = parse_initial_spells(body)
            continue

        if opcode in (SMSG_UPDATE_OBJECT, SMSG_COMPRESSED_UPDATE_OBJECT):
            if opcode == SMSG_COMPRESSED_UPDATE_OBJECT:
                body = zlib.decompress(body[4:])

            blocks = parse_update(body)

            for block in blocks:
                if block["typeId"] == 4:            # TYPEID_PLAYER
                    print(f"  in the world at ({x:.1f}, {y:.1f}, {z:.1f}), "
                          f"{len(blocks) - 1} item(s) alongside, "
                          f"{len(client.spells)} spell(s) known")

                    if not client.spells:
                        fail("SMSG_INITIAL_SPELLS never arrived — the spellbook would be empty")

                    return (x, y, z), block["values"]

    fail("the login update never carried a player block")


def equipped_item_guids(values):
    """The guids in the player's equipment slots, read out of its own field block."""
    guids = []

    for slot in range(EQUIPMENT_SLOTS):
        field = PLAYER_FIELD_INV_SLOT_HEAD + (slot * 2)

        low = values.get(field, 0)
        high = values.get(field + 1, 0)

        if low or high:
            guids.append((slot, low | (high << 32)))

    return guids


def read_vendor_list(payload):
    """Decodes SMSG_LIST_INVENTORY."""
    vendor, count = struct.unpack("<QB", payload[:9])

    if count == 0:
        return vendor, []

    cursor = 9
    items = []

    for _ in range(count):
        slot, item_id, _display, in_stock, price, _durability, buy_count, extended = struct.unpack(
            "<IIIiIIII", payload[cursor:cursor + 32])
        cursor += 32

        items.append({"slot": slot, "item": item_id, "stock": in_stock,
                      "price": price, "buyCount": buy_count, "extended": extended})

    return vendor, items


def run(client, character, start):
    """Open the vendor, be refused, sell, buy."""
    _guid, values = character["login"]

    danil_spawn, danil_at = find_spawn(client, BROTHER_DANIL)
    danil = creature_guid(BROTHER_DANIL, danil_spawn)

    print(f"  Brother Danil is spawn {danil_spawn} at ({danil_at[0]:.1f}, {danil_at[1]:.1f})")

    walk_to(client, character["guid"], start, danil_at)

    # ---- he has no gossip menu of his own, so saying hello opens the stock directly
    client.send(CMSG_GOSSIP_HELLO, struct.pack("<Q", danil))

    vendor, stock = read_vendor_list(
        await_opcode(client, SMSG_LIST_INVENTORY, "SMSG_LIST_INVENTORY"))

    if vendor != danil:
        fail(f"the list is for 0x{vendor:016X}, not Brother Danil")

    if not stock:
        fail("Brother Danil is selling nothing — is npc_vendor imported?")

    print(f"  he sells {len(stock)}: "
          + ", ".join(f"slot {i['slot']} item {i['item']} at {i['price']}c" for i in stock))

    if stock[0]["slot"] != 1:
        fail(f"the first slot is numbered {stock[0]['slot']}; the client counts vendor slots from 1")

    for item in stock:
        if item["stock"] != -1:
            fail(f"slot {item['slot']} reports {item['stock']} in stock, expected -1 for unlimited")

    # ---- the cheapest thing bought with money rather than tokens
    affordable = [item for item in stock if item["extended"] == 0 and item["price"] > 0]

    if not affordable:
        fail("nothing on the list is bought with money")

    target = min(affordable, key=lambda item: item["price"])

    # ---- with an empty purse, it has to be refused for the right reason
    client.send(CMSG_BUY_ITEM, struct.pack("<QIIBB", danil, target["item"], target["slot"], 1, 0))

    payload = await_opcode(client, SMSG_BUY_FAILED, "SMSG_BUY_FAILED")
    reason = payload[-1]

    if reason != BUY_ERR_NOT_ENOUGH_MONEY:
        fail(f"a broke player was refused with reason {reason}, expected {BUY_ERR_NOT_ENOUGH_MONEY}")

    print(f"  buying with no money is refused (reason {reason})")

    # ---- sell the starting gear
    worn = equipped_item_guids(values)

    if not worn:
        fail("the character is wearing nothing — the inventory slot fields did not arrive")

    for slot, item_guid in worn:
        client.send(CMSG_SELL_ITEM, struct.pack("<QQB", danil, item_guid, 0))

    print(f"  sold {len(worn)} equipped item(s)")

    # ---- and now the purchase should go through
    client.send(CMSG_BUY_ITEM, struct.pack("<QIIBB", danil, target["item"], target["slot"], 1, 0))

    for _ in range(512):
        opcode, payload = client.recv()

        if opcode == SMSG_BUY_FAILED:
            fail(f"the purchase was still refused with reason {payload[-1]} after selling")

        if opcode == SMSG_BUY_ITEM:
            bought_vendor, slot, _stock, count = struct.unpack("<QIiI", payload[:20])

            if bought_vendor != danil or slot != target["slot"]:
                fail("the purchase confirmation names the wrong vendor or slot")

            print(f"  bought slot {slot} (item {target['item']}) for {target['price']}c, count {count}")
            break
    else:
        fail("the purchase was neither confirmed nor refused")

    push = await_opcode(client, SMSG_ITEM_PUSH_RESULT, "SMSG_ITEM_PUSH_RESULT")

    # guid(8) three booleans as full words(12) bag(1) slot(4) -> the entry starts at 25, and the
    # suffix and random property sit between it and the count.
    pushed = struct.unpack("<I", push[25:29])[0]
    quantity, total = struct.unpack("<II", push[37:45])

    if pushed != target["item"]:
        fail(f"the toast says item {pushed}, expected {target['item']}")

    if quantity == 0:
        fail("the toast says a count of zero — nothing actually arrived")

    print(f"  it arrived in the bags: {quantity} x item {pushed}, {total} held in total")


def main():
    parser = argparse.ArgumentParser(description="M6 gate: sell to a vendor and buy from one.")
    parser.add_argument("host", nargs="?", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=0, help="world port; 0 takes it from the realm list")
    parser.add_argument("--user", default="test")
    parser.add_argument("--password", default="test")
    parser.add_argument("--character", default="Shopbot")

    args = parser.parse_args()

    user = args.user.upper().encode()
    password = args.password.upper().encode()

    print(f"M6 vendor gate against {args.host} as {user.decode()}")

    client = None
    character = None

    try:
        session_key, realm_address = logon(args.host, user, password)
        _, _, port = realm_address.partition(":")
        port = args.port or int(port or 8085)

        client = connect(args.host, port, user, session_key)
        character = ensure_character(client, args.character)

        start, values = enter_world(client, character["guid"])
        character["login"] = (character["guid"], values)

        run(client, character, start)

    except (ConnectionRefusedError, socket.timeout, TimeoutError, OSError) as error:
        fail(f"{error} — are both servers running?")
    finally:
        # Always. A character that has already sold its gear and walked to the abbey makes the next
        # run start somewhere else with nothing to sell.
        if client is not None and character is not None:
            cleanup(client, character["guid"])

    print("PASS")


if __name__ == "__main__":
    sys.exit(main())
