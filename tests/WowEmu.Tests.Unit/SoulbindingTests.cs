using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// When an item becomes yours for good.
/// </summary>
/// <remarks>
/// Nothing was ever bound before this, so every item in the game was tradeable forever. The
/// distinction between bind-on-pickup and bind-on-equip is the whole of item trading: a
/// bind-on-equip item is worth something on the market right up until somebody wears it.
/// </remarks>
public sealed class SoulbindingTests
{
    /// <summary>A bind-on-pickup item binds wherever it lands.</summary>
    [Fact]
    public void BindOnPickup_BindsInTheBag()
    {
        Player player = InventoryFixture.Player();

        Item item = InventoryFixture.Place(
            player, Template(ItemBonding.OnPickup), InventoryFixture.Backpack());

        Assert.True(item.IsSoulBound);
    }

    /// <summary>
    /// A bind-on-equip item does not, until it is worn.
    /// </summary>
    /// <remarks>
    /// The load-bearing half. Binding it on pickup instead is a one-word change that quietly
    /// destroys the entire market for gear.
    /// </remarks>
    [Fact]
    public void BindOnEquip_BindsOnlyWhenWorn()
    {
        Player player = InventoryFixture.Player();

        Item carried = InventoryFixture.Place(
            player, Template(ItemBonding.OnEquip), InventoryFixture.Backpack());

        Assert.False(carried.IsSoulBound);

        Item worn = InventoryFixture.Place(
            player,
            Template(ItemBonding.OnEquip, inventoryType: InventoryType.Chest),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.Chest));

        Assert.True(worn.IsSoulBound);
    }

    /// <summary>A quest item binds like a bind-on-pickup one.</summary>
    [Fact]
    public void AQuestItem_Binds()
    {
        Player player = InventoryFixture.Player();

        Item item = InventoryFixture.Place(
            player, Template(ItemBonding.QuestItem), InventoryFixture.Backpack());

        Assert.True(item.IsSoulBound);
    }

    /// <summary>Something with no bonding rule never binds.</summary>
    [Fact]
    public void SomethingUnbound_StaysUnbound()
    {
        Player player = InventoryFixture.Player();

        Item worn = InventoryFixture.Place(
            player,
            Template(ItemBonding.None, inventoryType: InventoryType.Chest),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.Chest));

        Assert.False(worn.IsSoulBound);
    }

    /// <summary>
    /// The bound flag is on the item's own field, where the client reads it.
    /// </summary>
    /// <remarks>
    /// Tracking it only in the server's head leaves the tooltip saying the item is tradeable, which
    /// is worse than not binding at all — it invites a trade that then fails.
    /// </remarks>
    [Fact]
    public void TheBoundFlag_IsOnTheItemsOwnField()
    {
        Player player = InventoryFixture.Player();

        Item item = InventoryFixture.Place(
            player, Template(ItemBonding.OnPickup), InventoryFixture.Backpack());

        Assert.Equal(1u, item.ItemFlags & 1u);
    }

    /// <summary>
    /// An item bound to somebody else is refused; one bound to you is not.
    /// </summary>
    /// <remarks>
    /// Three cases and only the third refuses: unbound is never someone else's, and bound-to-you is
    /// not either. Collapsing to "is it bound" makes your own gear unequippable.
    /// </remarks>
    [Fact]
    public void OnlySomebodyElsesBoundItem_IsRefused()
    {
        Player mine = InventoryFixture.Player();
        Player theirs = InventoryFixture.Player();

        Item unbound = Loose(ItemBonding.None);
        Assert.False(unbound.IsBoundToSomeoneElse(mine));

        Item ownBound = Loose(ItemBonding.OnPickup);
        ownBound.Owner = mine.Guid;
        ownBound.IsSoulBound = true;
        Assert.False(ownBound.IsBoundToSomeoneElse(mine));

        Item otherBound = Loose(ItemBonding.OnPickup);
        otherBound.Owner = theirs.Guid;
        otherBound.IsSoulBound = true;
        Assert.True(otherBound.IsBoundToSomeoneElse(mine));
    }

    private static ItemTemplate Template(byte bonding, byte inventoryType = InventoryType.NonEquip) =>
        ItemFixture.Build(entry: 100, inventoryType: inventoryType) with { Bonding = bonding };

    private static Item Loose(byte bonding) =>
        Item.Create(InventoryFixture.NextGuid(), Template(bonding));
}

/// <summary>
/// How many of a thing a player may hold, and how many they may wear.
/// </summary>
/// <remarks>
/// Two separate caps that read the same in prose. <c>MaxCount</c> and a "have" category limit what
/// is carried; the unique-equipped flag and an "equip" category limit what is worn. Before this, a
/// player could hold as many of anything as fit in their bags.
/// </remarks>
public sealed class UniqueItemTests
{
    /// <summary>A unique item may be held once.</summary>
    [Fact]
    public void AUniqueItem_MayBeHeldOnce()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate unique = ItemFixture.Build(entry: 200) with { MaxCount = 1 };

        InventoryFixture.Place(player, unique, InventoryFixture.Backpack());

        Assert.Equal(
            InventoryResult.CantCarryMoreOfThis,
            player.Inventory.CanStore(unique, 1, out _));
    }

    /// <summary>
    /// A MaxCount of zero means no limit, not a limit of zero.
    /// </summary>
    /// <remarks>
    /// Most items leave the column at zero. Reading it literally makes almost everything in the game
    /// impossible to pick up, which is the kind of bug that is obvious the moment it ships and
    /// invisible until then.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void AMaxCountOfZeroOrLess_MeansNoLimit(int maxCount)
    {
        Player player = InventoryFixture.Player();

        ItemTemplate template = ItemFixture.Build(entry: 201) with { MaxCount = maxCount };

        for (int i = 0; i < 5; i++)
        {
            InventoryFixture.Place(player, template, InventoryFixture.Backpack((byte)i));
        }

        Assert.Equal(InventoryResult.Ok, player.Inventory.CanStore(template, 1, out _));
    }

    /// <summary>Below the limit, more may still be taken.</summary>
    [Fact]
    public void BelowTheLimit_MoreMayBeTaken()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate template = ItemFixture.Build(entry: 202) with { MaxCount = 3 };

        InventoryFixture.Place(player, template, InventoryFixture.Backpack());

        Assert.Equal(InventoryResult.Ok, player.Inventory.CanStore(template, 2, out _));
        Assert.Equal(
            InventoryResult.CantCarryMoreOfThis,
            player.Inventory.CanStore(template, 3, out _));
    }

    /// <summary>
    /// A "have" category caps a whole family, not each item separately.
    /// </summary>
    /// <remarks>
    /// The entire reason the category exists. Capping each item on its own lets a player carry one
    /// of every kind of mana gem, which is exactly what "one mana gem" was meant to prevent.
    /// </remarks>
    [Fact]
    public void AHaveCategory_CapsTheWholeFamily()
    {
        Player player = WithCategories(ItemLimitCategoryEntry.ModeHave, maxCount: 1);

        ItemTemplate first = ItemFixture.Build(entry: 300) with { ItemLimitCategory = Family };
        ItemTemplate second = ItemFixture.Build(entry: 301) with { ItemLimitCategory = Family };

        InventoryFixture.Place(player, first, InventoryFixture.Backpack());

        // A different item entirely, and still refused.
        Assert.Equal(
            InventoryResult.CantCarryMoreOfThis,
            player.Inventory.CanStore(second, 1, out _));
    }

    /// <summary>
    /// An "equip" category does not stop you carrying them.
    /// </summary>
    /// <remarks>
    /// The mode column is the whole difference. Refusing the pick-up for an equip-limited family
    /// stops a player looting a second trinket they are perfectly entitled to own.
    /// </remarks>
    [Fact]
    public void AnEquipCategory_DoesNotStopYouCarryingThem()
    {
        Player player = WithCategories(ItemLimitCategoryEntry.ModeEquip, maxCount: 1);

        ItemTemplate template = ItemFixture.Build(entry: 302) with { ItemLimitCategory = Family };

        InventoryFixture.Place(player, template, InventoryFixture.Backpack());

        Assert.Equal(InventoryResult.Ok, player.Inventory.CanStore(template, 1, out _));
    }

    /// <summary>But it does stop you wearing a second.</summary>
    [Fact]
    public void AnEquipCategory_StopsYouWearingASecond()
    {
        Player player = WithCategories(ItemLimitCategoryEntry.ModeEquip, maxCount: 1);

        ItemTemplate trinket = ItemFixture.Build(entry: 303, inventoryType: InventoryType.Trinket)
            with { ItemLimitCategory = Family };

        InventoryFixture.Place(
            player, trinket, new ItemPosition(InventorySlots.Backpack, InventorySlots.Trinket1));

        Assert.Equal(
            InventoryResult.ItemMaxLimitCategoryEquippedExceeded,
            player.Inventory.CanEquipUnique(trinket));
    }

    /// <summary>A unique-equipped item may be carried twice but worn once.</summary>
    [Fact]
    public void AUniqueEquippedItem_MayBeCarriedTwiceButWornOnce()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate ring = ItemFixture.Build(entry: 304, inventoryType: InventoryType.Finger)
            with { Flags = UniqueEquippable };

        // Carrying two is fine — the flag is about wearing.
        InventoryFixture.Place(player, ring, InventoryFixture.Backpack(0));
        Assert.Equal(InventoryResult.Ok, player.Inventory.CanStore(ring, 1, out _));

        InventoryFixture.Place(
            player, ring, new ItemPosition(InventorySlots.Backpack, InventorySlots.Finger1));

        Assert.Equal(InventoryResult.ItemUniqueEquippable, player.Inventory.CanEquipUnique(ring));
    }

    /// <summary>
    /// Replacing a unique item with another of its kind is allowed.
    /// </summary>
    /// <remarks>
    /// The excluded slot. Without it the one being taken off is still counted, so swapping a unique
    /// ring for the same ring refuses — and the player is stuck with whichever they equipped first.
    /// </remarks>
    [Fact]
    public void ReplacingAUniqueItem_IsAllowed()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate ring = ItemFixture.Build(entry: 305, inventoryType: InventoryType.Finger)
            with { Flags = UniqueEquippable };

        InventoryFixture.Place(
            player, ring, new ItemPosition(InventorySlots.Backpack, InventorySlots.Finger1));

        Assert.Null(player.Inventory.CanEquipUnique(ring, exceptSlot: InventorySlots.Finger1));
        Assert.NotNull(player.Inventory.CanEquipUnique(ring, exceptSlot: InventorySlots.Finger2));
    }

    /// <summary>
    /// A category the table has never heard of refuses rather than passing.
    /// </summary>
    /// <remarks>
    /// The safe direction: passing turns a data gap into an item with no limit at all.
    /// </remarks>
    [Fact]
    public void AnUnknownCategory_Refuses()
    {
        Player player = WithCategories(ItemLimitCategoryEntry.ModeHave, maxCount: 1);

        ItemTemplate exotic = ItemFixture.Build(entry: 306) with { ItemLimitCategory = 999 };

        Assert.Equal(
            InventoryResult.ItemCantBeEquipped,
            player.Inventory.CanStore(exotic, 1, out _));
    }

    /// <summary>With no table loaded the category limits simply pass.</summary>
    /// <remarks>
    /// So that everything which does not need a client extracted carries on working, rather than
    /// every item becoming unobtainable the moment a DBC is missing.
    /// </remarks>
    [Fact]
    public void WithNoTable_CategoryLimitsPass()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate template = ItemFixture.Build(entry: 307) with { ItemLimitCategory = Family };

        Assert.Equal(InventoryResult.Ok, player.Inventory.CanStore(template, 1, out _));
    }

    /// <summary>
    /// The real table loads, and carries both modes.
    /// </summary>
    /// <remarks>
    /// The mode column is what separates a carry limit from a wear limit, and a format string that
    /// picked up the wrong column would still load 83 plausible rows. Asserting both modes are
    /// present is what catches reading the count column twice.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheRealTable_Loads()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(83, stores.ItemLimitCategories.Count);

        Assert.Contains(stores.ItemLimitCategories.Entries, e => e.Mode == ItemLimitCategoryEntry.ModeHave);
        Assert.Contains(stores.ItemLimitCategories.Entries, e => e.Mode == ItemLimitCategoryEntry.ModeEquip);

        // Nothing is capped at zero, and nothing carries a mode outside the two.
        Assert.All(stores.ItemLimitCategories.Entries, e =>
        {
            Assert.True(e.MaxCount > 0, $"category {e.Id} caps at zero");
            Assert.InRange(e.Mode, ItemLimitCategoryEntry.ModeHave, ItemLimitCategoryEntry.ModeEquip);
        });
    }

    private const short Family = 42;
    private const uint UniqueEquippable = 0x00080000;

    private static Player WithCategories(uint mode, uint maxCount)
    {
        Player player = InventoryFixture.Player();

        player.Inventory.LimitCategories =
            DbcFixture.Store(e => e.Id, new ItemLimitCategoryEntry((uint)Family, maxCount, mode));

        return player;
    }
}
