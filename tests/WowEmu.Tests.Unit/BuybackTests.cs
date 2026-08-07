using WowEmu.Game;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The rack of things a player has just sold.
/// </summary>
/// <remarks>
/// Twelve slots, and the client reads three fields for each — the item, what it paid out, and when.
/// The point of the feature is undoing a misclick, which is why the price stored is the refund
/// rather than the item's worth.
/// </remarks>
public sealed class BuybackTests
{
    /// <summary>A sold item lands on the rack at the price it fetched.</summary>
    /// <remarks>
    /// The refund, not the buy price. Charging the buy price would take several times the refund
    /// back, so undoing a misclick would cost more than the mistake did.
    /// </remarks>
    [Fact]
    public void ASoldItem_IsHeldAtWhatItFetched()
    {
        Player player = InventoryFixture.Player(level: 5);

        Item sold = Sold(entry: 1234);
        int slot = player.Buyback.Add(sold, paid: 250, now: 100);

        Assert.Equal(0, slot);
        Assert.Same(sold, player.Buyback.At(0));
        Assert.Equal(250u, player.Buyback.PriceAt(0));
    }

    /// <summary>
    /// The guid field is two words wide, so slots are two apart.
    /// </summary>
    /// <remarks>
    /// Indexing it by the slot alone writes every other entry over its neighbour's high half, which
    /// leaves the client with a rack of items whose guids are half one thing and half another.
    /// </remarks>
    [Fact]
    public void TheGuidField_IsTwoWordsPerSlot()
    {
        Player player = InventoryFixture.Player(level: 5);

        Item first = Sold(entry: 1);
        Item second = Sold(entry: 2);

        player.Buyback.Add(first, paid: 10, now: 1);
        player.Buyback.Add(second, paid: 20, now: 2);

        Assert.Equal(
            first.Guid,
            player.Fields.GetGuid(UpdateFields.PLAYER_FIELD_VENDORBUYBACK_SLOT_1));

        Assert.Equal(
            second.Guid,
            player.Fields.GetGuid(UpdateFields.PLAYER_FIELD_VENDORBUYBACK_SLOT_1 + 2));
    }

    /// <summary>Buying something back clears all three of its fields.</summary>
    /// <remarks>
    /// The client draws the tab from the price and timestamp as well as the guid. Clearing only the
    /// guid leaves a priced, dated, empty square.
    /// </remarks>
    [Fact]
    public void BuyingBack_ClearsEveryFieldOfTheSlot()
    {
        Player player = InventoryFixture.Player(level: 5);

        player.Buyback.Add(Sold(entry: 1), paid: 99, now: 5);

        Assert.NotNull(player.Buyback.Remove(0));

        Assert.Equal(0u, player.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_BUYBACK_PRICE_1));
        Assert.Equal(0u, player.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_BUYBACK_TIMESTAMP_1));
        Assert.Equal(ObjectGuidEmpty, player.Fields.GetGuid(UpdateFields.PLAYER_FIELD_VENDORBUYBACK_SLOT_1));
        Assert.Null(player.Buyback.At(0));
    }

    /// <summary>The rack fills before anything is displaced.</summary>
    [Fact]
    public void TheRack_FillsEmptySlotsFirst()
    {
        Player player = InventoryFixture.Player(level: 5);

        for (int i = 0; i < Buyback.Slots; i++)
        {
            Assert.Equal(i, player.Buyback.Add(Sold((uint)(i + 1)), 10, i));
        }

        Assert.Equal(Buyback.Slots, player.Buyback.Count);
    }

    /// <summary>
    /// A full rack displaces the oldest entry, not the next one round.
    /// </summary>
    /// <remarks>
    /// Which is why the timestamp is stored rather than a cursor. Selling thirteen things, buying
    /// one back and selling again refills out of order — a round-robin cursor then evicts something
    /// newer than what is sitting in the slot it just freed.
    /// </remarks>
    [Fact]
    public void AFullRack_DisplacesTheOldest()
    {
        Player player = InventoryFixture.Player(level: 5);

        // Filled newest-first, so slot 11 holds the oldest sale.
        for (int i = 0; i < Buyback.Slots; i++)
        {
            player.Buyback.Add(Sold((uint)(i + 1)), 10, now: Buyback.Slots - i);
        }

        Item thirteenth = Sold(entry: 99);
        int slot = player.Buyback.Add(thirteenth, paid: 10, now: 1000);

        Assert.Equal(Buyback.Slots - 1, slot);
        Assert.Same(thirteenth, player.Buyback.At(Buyback.Slots - 1));
    }

    /// <summary>An item can be found by its guid, which is how a purchase is matched.</summary>
    [Fact]
    public void AnItem_IsFoundByItsGuid()
    {
        Player player = InventoryFixture.Player(level: 5);

        Item sold = Sold(entry: 7);
        player.Buyback.Add(sold, paid: 1, now: 1);

        Assert.Equal(0, player.Buyback.SlotOf(sold.Guid));
        Assert.Equal(-1, player.Buyback.SlotOf(Sold(8).Guid));
    }

    /// <summary>A slot outside the rack is empty rather than an error.</summary>
    /// <remarks>
    /// The slot arrives from the client, offset by <c>BUYBACK_SLOT_START</c>. A forged or stale one
    /// subtracts to something outside the array, and the answer to that is "no such item".
    /// </remarks>
    [Theory]
    [InlineData(-1)]
    [InlineData(Buyback.Slots)]
    [InlineData(1000)]
    public void ASlotOutsideTheRack_IsEmpty(int slot)
    {
        Player player = InventoryFixture.Player(level: 5);
        player.Buyback.Add(Sold(1), 10, 1);

        Assert.Null(player.Buyback.At(slot));
        Assert.Equal(0u, player.Buyback.PriceAt(slot));
        Assert.Null(player.Buyback.Remove(slot));
    }

    /// <summary>The client's first buyback slot is 74, which is where the offset comes from.</summary>
    /// <remarks>
    /// <c>BUYBACK_SLOT_START</c>. The client counts these inside the same run as its bags, so the
    /// number on the wire is never a plain index — and the twelve slots run to 86.
    /// </remarks>
    [Fact]
    public void TheClientsSlotNumbering_StartsAtSeventyFour()
    {
        Assert.Equal(74, InventorySlots.BuybackStart);
        Assert.Equal(86, InventorySlots.BuybackEnd);
        Assert.Equal(12, Buyback.Slots);
    }

    private static readonly WowEmu.Core.ObjectGuid ObjectGuidEmpty = WowEmu.Core.ObjectGuid.Empty;

    /// <summary>An item that exists but is in nobody's bags — which is what a sold one is.</summary>
    private static Item Sold(uint entry, uint count = 1)
    {
        Item item = Item.Create(
            InventoryFixture.NextGuid(), ItemFixture.Build(entry: entry), WowEmu.Core.ObjectGuid.Empty);

        item.Count = count;

        return item;
    }
}
