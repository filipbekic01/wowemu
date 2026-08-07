using WowEmu.Core;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// What a player has sold and can still buy back.
/// </summary>
/// <remarks>
/// Port of <c>Player::AddItemToBuyBackSlot</c> and <c>RemoveItemFromBuyBackSlot</c>. Twelve slots,
/// and the client reads all three of its fields for each — the item, what it paid out, and when.
/// <para>
/// <b>The price stored is what the player was given, not what the item is worth.</b> Buying it back
/// costs exactly that, so selling and repurchasing is free rather than a slow drain. Storing the buy
/// price instead would charge a player several times over for undoing a misclick.
/// </para>
/// <para>
/// A full rack evicts the <i>oldest</i> entry, which is why the timestamp is stored rather than a
/// slot cursor: upstream picks the least recent by comparing them, and a round-robin cursor gets
/// this wrong the moment one slot is bought back and refilled out of order.
/// </para>
/// </remarks>
public sealed class Buyback(Player owner)
{
    /// <summary>How many things a player can have sold and still recover.</summary>
    public const int Slots = InventorySlots.BuybackEnd - InventorySlots.BuybackStart;

    /// <summary>The items themselves, parallel to the client's slots.</summary>
    private readonly Item?[] _items = new Item?[Slots];

    /// <summary>What is in a buyback slot, or null.</summary>
    public Item? At(int slot) => slot >= 0 && slot < Slots ? _items[slot] : null;

    /// <summary>What buying it back costs — exactly what was paid out for it.</summary>
    public uint PriceAt(int slot) =>
        slot >= 0 && slot < Slots
            ? owner.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_BUYBACK_PRICE_1 + slot)
            : 0;

    /// <summary>How many slots are holding something.</summary>
    public int Count
    {
        get
        {
            int count = 0;

            foreach (Item? item in _items)
            {
                if (item is not null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Puts a sold item on the rack, evicting the oldest if it is full.
    /// </summary>
    /// <param name="item">What was sold. The whole stack, however much of it was sold.</param>
    /// <param name="paid">What the player was given for it, which is what buying it back costs.</param>
    /// <param name="now">Unix seconds, so a test need not wait.</param>
    /// <returns>The slot it landed in.</returns>
    public int Add(Item item, uint paid, long now)
    {
        ArgumentNullException.ThrowIfNull(item);

        int slot = FirstEmpty() ?? Oldest();

        // Whatever was there is gone for good. Upstream destroys it at this point too — the rack is
        // the only thing holding it, and something has to give when a thirteenth item is sold.
        _items[slot] = item;

        Write(slot, item.Guid, paid, now);

        return slot;
    }

    /// <summary>
    /// Takes an item back off the rack.
    /// </summary>
    /// <remarks>
    /// Clears all three fields, not just the guid. The client draws the buyback tab from the price
    /// and timestamp as well, and leaving them behind shows a priced, dated, empty square.
    /// </remarks>
    public Item? Remove(int slot)
    {
        if (slot < 0 || slot >= Slots || _items[slot] is not { } item)
        {
            return null;
        }

        _items[slot] = null;
        Write(slot, ObjectGuid.Empty, 0, 0);

        return item;
    }

    /// <summary>Which slot holds a given item, or -1.</summary>
    public int SlotOf(ObjectGuid itemGuid)
    {
        for (int slot = 0; slot < Slots; slot++)
        {
            if (_items[slot] is { } item && item.Guid == itemGuid)
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Empties the rack. A character logging out does not keep it.</summary>
    /// <remarks>
    /// Upstream does not persist buyback either — it is a convenience for undoing a misclick within
    /// one session, not a second bank.
    /// </remarks>
    public void Clear()
    {
        for (int slot = 0; slot < Slots; slot++)
        {
            Remove(slot);
        }
    }

    private int? FirstEmpty()
    {
        for (int slot = 0; slot < Slots; slot++)
        {
            if (_items[slot] is null)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>The least recently sold slot, which is the one a thirteenth sale displaces.</summary>
    private int Oldest()
    {
        int oldest = 0;
        uint oldestTime = uint.MaxValue;

        for (int slot = 0; slot < Slots; slot++)
        {
            uint stamp = owner.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_BUYBACK_TIMESTAMP_1 + slot);

            if (stamp < oldestTime)
            {
                oldestTime = stamp;
                oldest = slot;
            }
        }

        return oldest;
    }

    private void Write(int slot, ObjectGuid itemGuid, uint price, long timestamp)
    {
        // The guid field is two words wide and the slots are two apart because of it — indexing it
        // by the slot alone writes every other entry over its neighbour's high half.
        owner.Fields.SetGuid(UpdateFields.PLAYER_FIELD_VENDORBUYBACK_SLOT_1 + (slot * 2), itemGuid);

        owner.Fields.SetUInt32(UpdateFields.PLAYER_FIELD_BUYBACK_PRICE_1 + slot, price);
        owner.Fields.SetUInt32(UpdateFields.PLAYER_FIELD_BUYBACK_TIMESTAMP_1 + slot, (uint)timestamp);
    }
}
