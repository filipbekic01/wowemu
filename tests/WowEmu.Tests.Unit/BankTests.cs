using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The bank: twenty-eight slots, plus whatever bags a character has paid for.
/// </summary>
/// <remarks>
/// The slots always existed in the array — nothing could reach them. They are part of the same flat
/// run as the bags, so persistence already covered them the moment something could put an item
/// there.
/// </remarks>
public sealed class BankTests
{
    /// <summary>Something in the bags goes to the bank and stops being in the bags.</summary>
    [Fact]
    public void AnItem_MovesToTheBank()
    {
        Player player = InventoryFixture.Player();

        ItemPosition from = InventoryFixture.Backpack();
        Item item = InventoryFixture.Place(player, ItemFixture.Build(entry: 1), from);

        Assert.Equal(InventoryResult.Ok, player.Inventory.Move(from, toBank: true));

        Assert.Null(player.Inventory.Get(from));
        Assert.Equal(BankSlot(0), player.Inventory.PositionOf(item));
    }

    /// <summary>And comes back again.</summary>
    [Fact]
    public void AnItem_ComesBackFromTheBank()
    {
        Player player = InventoryFixture.Player();

        Item item = InventoryFixture.Place(player, ItemFixture.Build(entry: 1), BankSlot(0));

        Assert.Equal(InventoryResult.Ok, player.Inventory.Move(BankSlot(0), toBank: false));

        Assert.Null(player.Inventory.Get(BankSlot(0)));
        Assert.Equal(InventoryFixture.Backpack(), player.Inventory.PositionOf(item));
    }

    /// <summary>
    /// A stack merges into a partial one already in the bank.
    /// </summary>
    /// <remarks>
    /// Partial stacks before empty slots, the same rule the bags use. Filling an empty slot first
    /// leaves two half stacks of the same thing and burns a slot for nothing.
    /// </remarks>
    [Fact]
    public void AStack_MergesIntoOneAlreadyBanked()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate cloth = ItemFixture.Build(entry: 2, stackable: 20);

        Item banked = InventoryFixture.Place(player, cloth, BankSlot(0), count: 5);
        ItemPosition from = InventoryFixture.Backpack();
        InventoryFixture.Place(player, cloth, from, count: 7);

        Assert.Equal(InventoryResult.Ok, player.Inventory.Move(from, toBank: true));

        Assert.Equal(12u, banked.Count);
        Assert.Null(player.Inventory.Get(from));
    }

    /// <summary>
    /// A unique item can be banked, rather than refused for already existing.
    /// </summary>
    /// <remarks>
    /// The item being moved has to be excluded from its own limit check. Otherwise putting your one
    /// unique trinket into the bank is refused on the grounds that you already have one — which is
    /// true, and is the item in your hand.
    /// </remarks>
    [Fact]
    public void AUniqueItem_CanBeBanked()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate unique = ItemFixture.Build(entry: 3) with { MaxCount = 1 };

        ItemPosition from = InventoryFixture.Backpack();
        InventoryFixture.Place(player, unique, from);

        Assert.Equal(InventoryResult.Ok, player.Inventory.Move(from, toBank: true));
    }

    /// <summary>A full bank refuses, and leaves the item where it was.</summary>
    /// <remarks>
    /// The item is taken out of its slot only after somewhere has been found for it. The obvious
    /// order — pick it up, then put it down — loses the item entirely when the destination is full.
    /// </remarks>
    [Fact]
    public void AFullBank_LeavesTheItemWhereItWas()
    {
        Player player = InventoryFixture.Player();

        for (byte slot = 0; slot < BankSlots; slot++)
        {
            InventoryFixture.Place(player, ItemFixture.Build(entry: (uint)(100 + slot)), BankSlot(slot));
        }

        ItemPosition from = InventoryFixture.Backpack();
        Item item = InventoryFixture.Place(player, ItemFixture.Build(entry: 1), from);

        Assert.Equal(InventoryResult.InventoryFull, player.Inventory.Move(from, toBank: true));
        Assert.Same(item, player.Inventory.Get(from));
    }

    /// <summary>Moving something already banked to the bank does nothing, and is not an error.</summary>
    /// <remarks>
    /// The client sends it on a double-click of something already in the bank.
    /// </remarks>
    [Fact]
    public void BankingSomethingAlreadyBanked_IsANoOp()
    {
        Player player = InventoryFixture.Player();

        Item item = InventoryFixture.Place(player, ItemFixture.Build(entry: 1), BankSlot(0));

        Assert.Equal(InventoryResult.Ok, player.Inventory.Move(BankSlot(0), toBank: true));
        Assert.Same(item, player.Inventory.Get(BankSlot(0)));
    }

    /// <summary>
    /// Bank bags are only usable in slots the character has bought.
    /// </summary>
    /// <remarks>
    /// The client draws exactly as many slots as the field says, and the packet naming a slot comes
    /// from the client — so this count is the only thing stopping a player using bank bags they
    /// never paid for.
    /// </remarks>
    [Fact]
    public void BankBags_AreOnlyUsableInBoughtSlots()
    {
        Player player = InventoryFixture.Player();

        // Fill the twenty-eight built-in slots so only a bag could take the item.
        for (byte slot = 0; slot < BankSlots; slot++)
        {
            InventoryFixture.Place(player, ItemFixture.Build(entry: (uint)(100 + slot)), BankSlot(slot));
        }

        InventoryFixture.Place(
            player,
            ItemFixture.Build(entry: 50, itemClass: ItemClass.Container, containerSlots: 8),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.BankBagStart));

        ItemPosition from = InventoryFixture.Backpack();
        InventoryFixture.Place(player, ItemFixture.Build(entry: 1), from);

        // The bag is there, but the slot has not been paid for.
        Assert.Equal(0, player.BankBagSlots);
        Assert.Equal(InventoryResult.InventoryFull, player.Inventory.Move(from, toBank: true));

        player.BankBagSlots = 1;

        Assert.Equal(InventoryResult.Ok, player.Inventory.Move(from, toBank: true));
    }

    /// <summary>The bank is separate from the bags — filling one does not fill the other.</summary>
    [Fact]
    public void TheBank_IsSeparateFromTheBags()
    {
        Player player = InventoryFixture.Player();

        for (byte slot = 0; slot < BankSlots; slot++)
        {
            InventoryFixture.Place(player, ItemFixture.Build(entry: (uint)(100 + slot)), BankSlot(slot));
        }

        // A full bank says nothing about whether the backpack has room.
        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.CanStore(ItemFixture.Build(entry: 1), 1, out _));
    }

    /// <summary>A key goes to the keyring, not into bag space.</summary>
    /// <remarks>
    /// The one item family with slots of its own, and the client draws them in their own panel.
    /// </remarks>
    [Fact]
    public void AKey_GoesToTheKeyring()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate key = ItemFixture.Build(entry: 60) with { BagFamily = KeyBagFamily };

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Store(key, 1, InventoryFixture.NextGuid, out IReadOnlyList<Item> stored));

        ItemPosition? where = player.Inventory.PositionOf(stored[0]);

        Assert.NotNull(where);
        Assert.InRange(where!.Value.Slot, InventorySlots.KeyringStart, InventorySlots.KeyringEnd - 1);
    }

    /// <summary>And anything else does not, however much keyring room there is.</summary>
    [Fact]
    public void SomethingThatIsNotAKey_StaysOutOfTheKeyring()
    {
        Player player = InventoryFixture.Player();

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Store(
                ItemFixture.Build(entry: 61), 1, InventoryFixture.NextGuid, out IReadOnlyList<Item> stored));

        ItemPosition where = Assert.IsType<ItemPosition>(player.Inventory.PositionOf(stored[0]));

        Assert.True(
            where.Slot < InventorySlots.KeyringStart,
            $"landed in slot {where.Slot}, which is keyring space");
    }

    /// <summary>
    /// A key overflows into bag space once the keyring is full.
    /// </summary>
    /// <remarks>
    /// Thirty-two slots against 156 keys in the content, so it genuinely fills. Upstream lets the
    /// overflow go into ordinary bag space rather than refusing the pick-up.
    /// </remarks>
    [Fact]
    public void AKey_OverflowsIntoBagSpace()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate key = ItemFixture.Build(entry: 62) with { BagFamily = KeyBagFamily };

        for (byte slot = InventorySlots.KeyringStart; slot < InventorySlots.KeyringEnd; slot++)
        {
            InventoryFixture.Place(
                player,
                ItemFixture.Build(entry: 900) with { BagFamily = KeyBagFamily },
                new ItemPosition(InventorySlots.Backpack, slot));
        }

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Store(key, 1, InventoryFixture.NextGuid, out IReadOnlyList<Item> stored));

        ItemPosition where = Assert.IsType<ItemPosition>(player.Inventory.PositionOf(stored[0]));

        Assert.InRange(where.Slot, InventorySlots.ItemStart, InventorySlots.ItemEnd - 1);
    }

    /// <summary>
    /// The price table has more rows than there are slots, and the extras are sentinels.
    /// </summary>
    /// <remarks>
    /// Twelve rows, seven slots. Rows 8 to 12 carry 999,999,999 copper — placeholders, not slots.
    /// Upstream sells them anyway (the client's money ceiling is above that price) and the slot then
    /// does not exist; we refuse past seven, and this pins the data that decision rests on.
    /// </remarks>
    [RequiresClientDataFact]
    public void ThePriceTable_HasMoreRowsThanSlots()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(12, stores.BankBagSlotPrices.Count);

        // The seven real slots get real prices, rising each time.
        uint previous = 0;

        for (uint slot = 1; slot <= RealBankBagSlots; slot++)
        {
            Assert.True(stores.BankBagSlotPrices.TryGet(slot, out BankBagSlotPriceEntry? entry));
            Assert.True(entry!.Price >= previous, $"slot {slot} costs less than slot {slot - 1}");
            Assert.True(entry.Price < Sentinel, $"slot {slot} is priced as a placeholder");

            previous = entry.Price;
        }

        // And the rest are placeholders.
        Assert.True(stores.BankBagSlotPrices.TryGet(RealBankBagSlots + 1, out BankBagSlotPriceEntry? beyond));
        Assert.Equal(Sentinel, beyond!.Price);
    }

    private const uint RealBankBagSlots = InventorySlots.BankBagEnd - InventorySlots.BankBagStart;
    private const uint Sentinel = 999_999_999;

    /// <summary><c>BAG_FAMILY_MASK_KEYS</c>.</summary>
    private const int KeyBagFamily = 0x00000100;

    private const byte BankSlots = InventorySlots.BankItemEnd - InventorySlots.BankItemStart;

    private static ItemPosition BankSlot(byte index) =>
        new(InventorySlots.Backpack, (byte)(InventorySlots.BankItemStart + index));
}
