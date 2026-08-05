using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Protocol;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>Builds a player with an empty inventory and a guid source for its items.</summary>
internal static class InventoryFixture
{
    private static uint _nextItemGuid;

    /// <summary>A level-1 human warrior, alive, with nothing.</summary>
    public static Player Player(byte level = 1, byte race = 1, byte characterClass = 1)
    {
        CharacterSummary summary = new(
            1, "Carrier", race, characterClass, 0, 0, 0, 0, 0, 0, level, 12, 0, 0f, 0f, 0f, 0, 0, 0);

        ChrRacesEntry races = new(race, 0, 1, 49, 50, 7, 0, 0, "Human", 0);
        ChrClassesEntry classes = new(characterClass, 1, "Warrior", 4, 0);
        PlayerBaseStats stats = new(100, 0, 23, 20, 22, 20, 20);

        Player player = Game.Player.Create(summary, races, classes, stats);

        player.MaxHealth = 100;
        player.Health = 100;

        return player;
    }

    /// <summary>A guid source that never repeats, across every test in the run.</summary>
    public static uint NextGuid() => Interlocked.Increment(ref _nextItemGuid);

    /// <summary>Gives a player an item directly, bypassing the rules.</summary>
    public static Item Place(Player player, ItemTemplate template, ItemPosition at, uint count = 1)
    {
        Item item = Item.Create(NextGuid(), template, player.Guid);
        item.Count = count;

        player.Inventory.Place(at, item);

        return item;
    }

    /// <summary>The first backpack slot, which is where most of these tests start.</summary>
    public static ItemPosition Backpack(byte index = 0) =>
        new(InventorySlots.Backpack, (byte)(InventorySlots.ItemStart + index));
}

/// <summary>
/// The slot map, which is the part that is easy to get subtly wrong.
/// </summary>
public sealed class InventorySlotTests
{
    /// <summary>
    /// Every slot maps to one flat guid array, whatever the field names suggest.
    /// </summary>
    /// <remarks>
    /// The client's headers name five separate ranges — <c>INV_SLOT_HEAD</c>, <c>PACK_SLOT_1</c>,
    /// <c>BANK_SLOT_1</c> and so on — and they are consecutive stretches of one 150-guid run.
    /// Adding a range's own base to a range-relative slot double-counts the offset and puts the
    /// backpack 46 words too far along.
    /// </remarks>
    [Theory]
    [InlineData(InventorySlots.Head, UpdateFields.PLAYER_FIELD_INV_SLOT_HEAD)]
    [InlineData(InventorySlots.BagStart, UpdateFields.PLAYER_FIELD_INV_SLOT_HEAD + (19 * 2))]
    [InlineData(InventorySlots.ItemStart, UpdateFields.PLAYER_FIELD_PACK_SLOT_1)]
    [InlineData(InventorySlots.BankItemStart, UpdateFields.PLAYER_FIELD_BANK_SLOT_1)]
    [InlineData(InventorySlots.KeyringStart, UpdateFields.PLAYER_FIELD_KEYRING_SLOT_1)]
    public void EverySlot_IsOneFlatArray(byte slot, int expectedField)
    {
        Player player = InventoryFixture.Player();
        Item item = InventoryFixture.Place(
            player, ItemFixture.Build(), new ItemPosition(InventorySlots.Backpack, slot));

        Assert.Equal(item.Guid, player.Fields.GetGuid(expectedField));
    }

    /// <summary>
    /// The backpack is container 255, not container 0.
    /// </summary>
    /// <remarks>
    /// Bag 0 is a real bag in the first bag slot. Reading a bag byte of zero as "the backpack"
    /// sends every query to whatever is worn there instead — and finds nothing, because it is
    /// usually empty.
    /// </remarks>
    [Fact]
    public void TheBackpack_IsContainer255()
    {
        Assert.Equal(255, InventorySlots.Backpack);
        Assert.True(new ItemPosition(InventorySlots.Backpack, 0).IsOnThePlayer);
        Assert.False(new ItemPosition(0, 0).IsOnThePlayer);
    }

    /// <summary>A packed position puts the bag in the high byte.</summary>
    [Fact]
    public void APackedPosition_PutsTheBagHigh()
    {
        ItemPosition position = new(19, 3);

        Assert.Equal(0x1303, position.Packed);
        Assert.Equal(position, ItemPosition.Unpack(position.Packed));
    }

    /// <summary>Equipment is visible to everyone; what is in the backpack is not.</summary>
    [Fact]
    public void OnlyEquipment_GetsAVisibleItemBlock()
    {
        Player player = InventoryFixture.Player();
        ItemTemplate sword = ItemFixture.Build(
            entry: 25, itemClass: ItemClass.Weapon, inventoryType: InventoryType.WeaponMainHand);

        InventoryFixture.Place(player, sword, new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        int mainHandField = UpdateFields.PLAYER_VISIBLE_ITEM_1_ENTRYID + (InventorySlots.MainHand * 2);

        Assert.Equal(25u, player.Fields.GetUInt32(mainHandField));

        InventoryFixture.Place(player, sword, InventoryFixture.Backpack());

        // Still 25 in the main hand — putting a second copy in the backpack changes nothing visible.
        Assert.Equal(25u, player.Fields.GetUInt32(mainHandField));
    }

    /// <summary>Unequipping clears the visible block rather than leaving the last item drawn.</summary>
    [Fact]
    public void Unequipping_ClearsTheVisibleBlock()
    {
        Player player = InventoryFixture.Player();
        ItemPosition mainHand = new(InventorySlots.Backpack, InventorySlots.MainHand);

        InventoryFixture.Place(
            player,
            ItemFixture.Build(entry: 25, itemClass: ItemClass.Weapon, inventoryType: InventoryType.WeaponMainHand),
            mainHand);

        player.Inventory.Take(mainHand);

        int mainHandField = UpdateFields.PLAYER_VISIBLE_ITEM_1_ENTRYID + (InventorySlots.MainHand * 2);

        Assert.Equal(0u, player.Fields.GetUInt32(mainHandField));
        Assert.Equal(ObjectGuid.Empty, player.Fields.GetGuid(UpdateFields.PLAYER_FIELD_INV_SLOT_HEAD + (15 * 2)));
    }
}

/// <summary>Storing, stacking and the free-slot search.</summary>
public sealed class InventoryStorageTests
{
    private static readonly ItemTemplate Cloth = ItemFixture.Build(entry: 2589, name: "Linen Cloth", stackable: 20);
    private static readonly ItemTemplate Unique = ItemFixture.Build(entry: 100, name: "Thing", stackable: 1);

    /// <summary>Sixteen backpack slots and nothing else, with no bags worn.</summary>
    [Fact]
    public void AnEmptyPlayer_HasSixteenFreeSlots()
    {
        Assert.Equal(16, InventoryFixture.Player().Inventory.FreeSlots);
    }

    /// <summary>A stackable item fills a partial stack before taking a new slot.</summary>
    /// <remarks>
    /// A player with fifteen full slots and a half stack of cloth can still pick up cloth. Refusing
    /// there is the difference between a full bag and an apparently broken one.
    /// </remarks>
    [Fact]
    public void Storing_FillsPartialStacksFirst()
    {
        Player player = InventoryFixture.Player();

        InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(), count: 15);

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Store(Cloth, 3, InventoryFixture.NextGuid, out IReadOnlyList<Item> affected));

        Assert.Single(affected);
        Assert.Equal(18u, affected[0].Count);
        Assert.Equal(15, player.Inventory.FreeSlots);
    }

    /// <summary>An overflow spills into the next slot rather than exceeding the stack size.</summary>
    [Fact]
    public void Storing_SpillsIntoANewStack()
    {
        Player player = InventoryFixture.Player();

        InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(), count: 18);

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Store(Cloth, 5, InventoryFixture.NextGuid, out IReadOnlyList<Item> affected));

        Assert.Equal(2, affected.Count);
        Assert.Equal(20u, affected[0].Count);
        Assert.Equal(3u, affected[1].Count);
        Assert.Equal(23u, player.Inventory.CountOf(Cloth.Entry));
    }

    /// <summary>
    /// A store that cannot fit everything places nothing at all.
    /// </summary>
    /// <remarks>
    /// Half of a loot drop landing and the rest vanishing is worse than none of it landing — the
    /// player has no way to know what they lost.
    /// </remarks>
    [Fact]
    public void AStoreThatCannotFit_PlacesNothing()
    {
        Player player = InventoryFixture.Player();

        for (byte i = 0; i < 16; i++)
        {
            InventoryFixture.Place(player, Unique, InventoryFixture.Backpack(i));
        }

        Assert.Equal(
            InventoryResult.InventoryFull,
            player.Inventory.Store(Cloth, 1, InventoryFixture.NextGuid, out IReadOnlyList<Item> affected));

        Assert.Empty(affected);
        Assert.Equal(0u, player.Inventory.CountOf(Cloth.Entry));
    }

    /// <summary>A worn bag adds its slots to what the player can carry.</summary>
    [Fact]
    public void AWornBag_AddsItsSlots()
    {
        Player player = InventoryFixture.Player();

        InventoryFixture.Place(
            player,
            ItemFixture.Build(
                entry: 4496, itemClass: ItemClass.Container,
                inventoryType: InventoryType.Bag, containerSlots: 6),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.BagStart));

        Assert.Equal(16 + 6, player.Inventory.FreeSlots);

        for (byte i = 0; i < 16; i++)
        {
            InventoryFixture.Place(player, Unique, InventoryFixture.Backpack(i));
        }

        // The backpack is full, so this can only have gone into the bag.
        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Store(Cloth, 1, InventoryFixture.NextGuid, out IReadOnlyList<Item> affected));

        ItemPosition? where = player.Inventory.PositionOf(affected[0]);

        Assert.NotNull(where);
        Assert.False(where.Value.IsOnThePlayer);
        Assert.Equal(InventorySlots.BagStart, where.Value.Bag);
    }

    /// <summary>A bag's contents are reachable through the bag, and counted with everything else.</summary>
    [Fact]
    public void ABagsContents_AreCountedAndAddressable()
    {
        Player player = InventoryFixture.Player();

        InventoryFixture.Place(
            player,
            ItemFixture.Build(
                entry: 4496, itemClass: ItemClass.Container,
                inventoryType: InventoryType.Bag, containerSlots: 6),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.BagStart));

        ItemPosition inside = new(InventorySlots.BagStart, 2);
        Item stored = InventoryFixture.Place(player, Cloth, inside, count: 7);

        Assert.Equal(stored, player.Inventory.Get(inside));
        Assert.Equal(7u, player.Inventory.CountOf(Cloth.Entry));
        Assert.Equal(inside, player.Inventory.PositionOf(stored));
    }
}

/// <summary>Equipping, and the slots an item is allowed in.</summary>
public sealed class InventoryEquipTests
{
    private static ItemTemplate Weapon(byte inventoryType, byte requiredLevel = 0) =>
        ItemFixture.Build(entry: 25, itemClass: ItemClass.Weapon, inventoryType: inventoryType)
            with { RequiredLevel = requiredLevel };

    private static ItemTemplate Ring() =>
        ItemFixture.Build(entry: 900, itemClass: ItemClass.Armor, inventoryType: InventoryType.Finger);

    /// <summary>A one-handed weapon goes in the main hand, and the off hand needs dual wield.</summary>
    /// <remarks>
    /// Dual wield is spell 674, which a player learns. Nobody has a spellbook yet, so nobody has it
    /// — the off-hand candidate is not offered at all.
    /// </remarks>
    [Fact]
    public void AOneHander_GoesMainHandUnlessTheOwnerCanDualWield()
    {
        Player player = InventoryFixture.Player();
        ItemTemplate sword = Weapon(InventoryType.Weapon);

        Assert.Equal(InventorySlots.MainHand, player.Inventory.FindEquipSlot(sword));

        InventoryFixture.Place(
            player, sword, new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        Assert.Equal(InventorySlots.None, player.Inventory.FindEquipSlot(sword));

        player.CanDualWield = true;

        Assert.Equal(InventorySlots.OffHand, player.Inventory.FindEquipSlot(sword));
    }

    /// <summary>A ring takes the second finger when the first is worn.</summary>
    [Fact]
    public void ARing_TakesTheSecondFingerWhenTheFirstIsWorn()
    {
        Player player = InventoryFixture.Player();
        ItemTemplate ring = Ring();

        Assert.Equal(InventorySlots.Finger1, player.Inventory.FindEquipSlot(ring));

        InventoryFixture.Place(player, ring, new ItemPosition(InventorySlots.Backpack, InventorySlots.Finger1));

        Assert.Equal(InventorySlots.Finger2, player.Inventory.FindEquipSlot(ring));
    }

    /// <summary>
    /// A two-handed weapon leaves the off hand empty and still occupied.
    /// </summary>
    /// <remarks>
    /// Checking only whether the slot holds something says it is free, and the player ends up with
    /// a greatsword and a shield.
    /// </remarks>
    [Fact]
    public void ATwoHander_MakesTheOffHandUnavailable()
    {
        Player player = InventoryFixture.Player();

        InventoryFixture.Place(
            player,
            Weapon(InventoryType.TwoHandWeapon),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        Assert.True(player.Inventory.IsTwoHandUsed);
        Assert.Null(player.Inventory.Equipped(InventorySlots.OffHand));

        ItemTemplate shield =
            ItemFixture.Build(entry: 2362, itemClass: ItemClass.Armor, inventoryType: InventoryType.Shield);

        Item held = InventoryFixture.Place(player, shield, InventoryFixture.Backpack());

        Assert.Equal(InventorySlots.None, player.Inventory.FindEquipSlot(shield));
        Assert.Equal(InventoryResult.CantEquipWithTwoHanded, player.Inventory.CanEquip(held, InventorySlots.OffHand));
    }

    /// <summary>Equipping a two-hander moves the off-hand item out, and refuses if there is no room.</summary>
    [Fact]
    public void ATwoHander_DisplacesTheOffHandOrIsRefused()
    {
        Player player = InventoryFixture.Player();

        Item shield = InventoryFixture.Place(
            player,
            ItemFixture.Build(entry: 2362, itemClass: ItemClass.Armor, inventoryType: InventoryType.Shield),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.OffHand));

        ItemPosition from = InventoryFixture.Backpack();
        InventoryFixture.Place(player, Weapon(InventoryType.TwoHandWeapon), from);

        Assert.Equal(InventoryResult.Ok, player.Inventory.Equip(from, InventorySlots.MainHand));
        Assert.Null(player.Inventory.Equipped(InventorySlots.OffHand));
        Assert.NotNull(player.Inventory.PositionOf(shield));
    }

    /// <summary>An item above the player's level is refused with the client's own message.</summary>
    [Fact]
    public void AnItemAboveTheOwnersLevel_IsRefused()
    {
        Player player = InventoryFixture.Player(level: 5);

        Item sword = InventoryFixture.Place(
            player, Weapon(InventoryType.WeaponMainHand, requiredLevel: 20), InventoryFixture.Backpack());

        Assert.Equal(InventoryResult.CantEquipLevel, player.Inventory.CanEquip(sword, InventorySlots.MainHand));
    }

    /// <summary>An item a class may never use is refused whatever its level.</summary>
    /// <remarks>
    /// The mask is by class <i>bit</i>, and classes are one-based: a warrior is class 1 and bit 0.
    /// Shifting by the class rather than by class minus one refuses every warrior item in the game.
    /// </remarks>
    [Fact]
    public void AnItemForAnotherClass_IsRefused()
    {
        Player warrior = InventoryFixture.Player(characterClass: 1);

        // Bit 3 is class 4, the rogue.
        ItemTemplate rogueOnly = Weapon(InventoryType.WeaponMainHand) with { AllowableClass = 1 << 3 };
        ItemTemplate warriorOk = Weapon(InventoryType.WeaponMainHand) with { AllowableClass = 1 << 0 };

        Item bad = InventoryFixture.Place(warrior, rogueOnly, InventoryFixture.Backpack(0));
        Item good = InventoryFixture.Place(warrior, warriorOk, InventoryFixture.Backpack(1));

        Assert.Equal(InventoryResult.YouCanNeverUseThatItem, warrior.Inventory.CanEquip(bad, InventorySlots.MainHand));
        Assert.Equal(InventoryResult.Ok, warrior.Inventory.CanEquip(good, InventorySlots.MainHand));
    }

    /// <summary>Dragging a weapon onto the head slot is refused, not silently accepted.</summary>
    [Fact]
    public void AnItemInTheWrongSlot_IsRefused()
    {
        Player player = InventoryFixture.Player();

        Item sword = InventoryFixture.Place(
            player, Weapon(InventoryType.WeaponMainHand), InventoryFixture.Backpack());

        Assert.Equal(InventoryResult.ItemDoesNotGoToSlot, player.Inventory.CanEquip(sword, InventorySlots.Head));
    }

    /// <summary>A dead player cannot change what they are wearing.</summary>
    [Fact]
    public void ADeadPlayer_CannotEquip()
    {
        Player player = InventoryFixture.Player();

        Item sword = InventoryFixture.Place(
            player, Weapon(InventoryType.WeaponMainHand), InventoryFixture.Backpack());

        // The death state, not the health: a corpse at zero health and a ghost at one are both
        // dead, and only the state says so.
        player.Health = 0;
        player.DeathState = DeathState.Corpse;

        Assert.Equal(InventoryResult.YouAreDead, player.Inventory.CanEquip(sword, InventorySlots.MainHand));
    }

    /// <summary>Equipping swaps, so what was worn ends up where the new item came from.</summary>
    [Fact]
    public void Equipping_SwapsWithWhatWasWorn()
    {
        Player player = InventoryFixture.Player();

        Item old = InventoryFixture.Place(
            player,
            Weapon(InventoryType.WeaponMainHand),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        ItemPosition from = InventoryFixture.Backpack();
        Item replacement = InventoryFixture.Place(player, Weapon(InventoryType.WeaponMainHand), from);

        Assert.Equal(InventoryResult.Ok, player.Inventory.Equip(from, InventorySlots.MainHand));
        Assert.Equal(replacement, player.Inventory.Equipped(InventorySlots.MainHand));
        Assert.Equal(old, player.Inventory.Get(from));
    }

    /// <summary>A bag with something in it cannot be swapped out from under its contents.</summary>
    [Fact]
    public void ANonEmptyBag_CannotBeReplaced()
    {
        Player player = InventoryFixture.Player();
        ItemTemplate bagTemplate = ItemFixture.Build(
                entry: 4496, itemClass: ItemClass.Container,
                inventoryType: InventoryType.Bag, containerSlots: 6);

        InventoryFixture.Place(
            player, bagTemplate, new ItemPosition(InventorySlots.Backpack, InventorySlots.BagStart));

        InventoryFixture.Place(player, ItemFixture.Build(entry: 2589), new ItemPosition(InventorySlots.BagStart, 0));

        ItemPosition from = InventoryFixture.Backpack();
        Item second = InventoryFixture.Place(player, bagTemplate, from);

        Assert.Equal(
            InventoryResult.NonEmptyBagOverOtherBag,
            player.Inventory.CanEquip(second, InventorySlots.BagStart));
    }
}

/// <summary>Swapping, splitting and destroying.</summary>
public sealed class InventoryMoveTests
{
    private static readonly ItemTemplate Cloth = ItemFixture.Build(entry: 2589, stackable: 20);

    [Fact]
    public void Swapping_ExchangesTwoSlots()
    {
        Player player = InventoryFixture.Player();

        Item first = InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(0), count: 3);
        Item second = InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(1), count: 7);

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Swap(InventoryFixture.Backpack(0), InventoryFixture.Backpack(1)));

        Assert.Equal(second, player.Inventory.Get(InventoryFixture.Backpack(0)));
        Assert.Equal(first, player.Inventory.Get(InventoryFixture.Backpack(1)));
    }

    /// <summary>Moving into an empty slot works, and leaves the source empty.</summary>
    [Fact]
    public void MovingIntoAnEmptySlot_LeavesTheSourceEmpty()
    {
        Player player = InventoryFixture.Player();
        Item item = InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(0));

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Swap(InventoryFixture.Backpack(0), InventoryFixture.Backpack(5)));

        Assert.Null(player.Inventory.Get(InventoryFixture.Backpack(0)));
        Assert.Equal(item, player.Inventory.Get(InventoryFixture.Backpack(5)));
    }

    /// <summary>Swapping two empty slots is refused rather than quietly doing nothing.</summary>
    [Fact]
    public void SwappingTwoEmptySlots_IsRefused()
    {
        Player player = InventoryFixture.Player();

        Assert.Equal(
            InventoryResult.SlotIsEmpty,
            player.Inventory.Swap(InventoryFixture.Backpack(0), InventoryFixture.Backpack(1)));
    }

    /// <summary>A split takes part of a stack into an empty slot.</summary>
    [Fact]
    public void Splitting_MakesASecondStack()
    {
        Player player = InventoryFixture.Player();
        Item source = InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(0), count: 10);

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Split(
                InventoryFixture.Backpack(0), InventoryFixture.Backpack(1), 4, InventoryFixture.NextGuid));

        Assert.Equal(6u, source.Count);
        Assert.Equal(4u, player.Inventory.Get(InventoryFixture.Backpack(1))!.Count);
        Assert.Equal(10u, player.Inventory.CountOf(Cloth.Entry));
    }

    /// <summary>Splitting a whole stack, or more than there is, is refused.</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(10u)]
    [InlineData(11u)]
    public void SplittingTheWholeStack_IsRefused(uint count)
    {
        Player player = InventoryFixture.Player();
        InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(0), count: 10);

        Assert.Equal(
            InventoryResult.TriedToSplitMoreThanCount,
            player.Inventory.Split(
                InventoryFixture.Backpack(0), InventoryFixture.Backpack(1), count, InventoryFixture.NextGuid));
    }

    /// <summary>A split onto the same item merges instead of replacing.</summary>
    [Fact]
    public void SplittingOntoTheSameItem_Merges()
    {
        Player player = InventoryFixture.Player();

        InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(0), count: 10);
        InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(1), count: 2);

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Split(
                InventoryFixture.Backpack(0), InventoryFixture.Backpack(1), 4, InventoryFixture.NextGuid));

        Assert.Equal(6u, player.Inventory.Get(InventoryFixture.Backpack(0))!.Count);
        Assert.Equal(6u, player.Inventory.Get(InventoryFixture.Backpack(1))!.Count);
    }

    /// <summary>Destroying part of a stack leaves the rest.</summary>
    [Fact]
    public void DestroyingPartOfAStack_LeavesTheRest()
    {
        Player player = InventoryFixture.Player();
        InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(0), count: 10);

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Destroy(InventoryFixture.Backpack(0), 4, out Item? removed));

        Assert.Null(removed);
        Assert.Equal(6u, player.Inventory.CountOf(Cloth.Entry));
    }

    /// <summary>A count of zero destroys the whole stack, which is what the client sends.</summary>
    [Fact]
    public void DestroyingZero_DestroysEverything()
    {
        Player player = InventoryFixture.Player();
        InventoryFixture.Place(player, Cloth, InventoryFixture.Backpack(0), count: 10);

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.Destroy(InventoryFixture.Backpack(0), 0, out Item? removed));

        Assert.NotNull(removed);
        Assert.Equal(0u, player.Inventory.CountOf(Cloth.Entry));
        Assert.Null(player.Inventory.Get(InventoryFixture.Backpack(0)));
    }

    /// <summary>
    /// A bag with things in it cannot be destroyed.
    /// </summary>
    /// <remarks>
    /// Its contents have their own guids. Destroying the bag would strand them — still owned, in no
    /// slot, and written back out on the next save as rows pointing at a bag that no longer exists.
    /// </remarks>
    [Fact]
    public void ANonEmptyBag_CannotBeDestroyed()
    {
        Player player = InventoryFixture.Player();

        InventoryFixture.Place(
            player,
            ItemFixture.Build(
                entry: 4496, itemClass: ItemClass.Container,
                inventoryType: InventoryType.Bag, containerSlots: 6),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.BagStart));

        InventoryFixture.Place(player, Cloth, new ItemPosition(InventorySlots.BagStart, 0));

        Assert.Equal(
            InventoryResult.CanOnlyDoWithEmptyBags,
            player.Inventory.Destroy(
                new ItemPosition(InventorySlots.Backpack, InventorySlots.BagStart), 0, out _));
    }

    /// <summary>A bag does not go inside another bag.</summary>
    [Fact]
    public void ABag_DoesNotNest()
    {
        Player player = InventoryFixture.Player();
        ItemTemplate bagTemplate = ItemFixture.Build(
                entry: 4496, itemClass: ItemClass.Container,
                inventoryType: InventoryType.Bag, containerSlots: 6);

        InventoryFixture.Place(
            player, bagTemplate, new ItemPosition(InventorySlots.Backpack, InventorySlots.BagStart));

        ItemPosition from = InventoryFixture.Backpack();
        InventoryFixture.Place(player, bagTemplate, from);

        Assert.Equal(
            InventoryResult.ItemDoesNotGoIntoBag,
            player.Inventory.Swap(from, new ItemPosition(InventorySlots.BagStart, 0)));
    }
}

/// <summary>The packets the client is sent about its bags.</summary>
public sealed class InventoryPacketTests
{
    private static readonly ObjectGuid Item = ObjectGuid.Create(HighGuid.Item, 7);
    private static readonly ObjectGuid Owner = ObjectGuid.Create(HighGuid.Player, 3);

    /// <summary>A success is one byte, and nothing follows it.</summary>
    /// <remarks>
    /// Writing the full body with a zero code leaves the client reading seventeen bytes it was not
    /// expecting.
    /// </remarks>
    [Fact]
    public void ASuccess_IsOneByte()
    {
        PacketWriter writer = new();
        InventoryChangeFailure.Write(writer, InventoryChangeFailure.Ok, Item);

        Assert.Equal(1, writer.WrittenSpan.Length);
    }

    /// <summary>A refusal carries two full guids, not packed ones.</summary>
    [Fact]
    public void ARefusal_CarriesTwoFullGuids()
    {
        PacketWriter writer = new();
        InventoryChangeFailure.Write(writer, (byte)InventoryResult.ItemDoesNotGoToSlot, Item);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt8(out byte code));
        Assert.Equal((byte)InventoryResult.ItemDoesNotGoToSlot, code);

        Assert.True(reader.TryReadUInt64(out ulong first));
        Assert.Equal(Item.Value, first);

        Assert.True(reader.TryReadUInt64(out ulong second));
        Assert.Equal(0u, second);

        Assert.True(reader.TryReadUInt8(out byte bagSubclass));
        Assert.Equal(0, bagSubclass);

        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>Only the level codes carry a required level after the body.</summary>
    [Fact]
    public void OnlyTheLevelCodes_CarryALevel()
    {
        PacketWriter withLevel = new();
        InventoryChangeFailure.Write(withLevel, InventoryChangeFailure.CantEquipLevel, Item, requiredLevel: 20);

        PacketWriter without = new();
        InventoryChangeFailure.Write(without, (byte)InventoryResult.BagFull, Item);

        Assert.Equal(4, withLevel.WrittenSpan.Length - without.WrittenSpan.Length);
    }

    /// <summary>The push result's three booleans are full words, and the slot is signed.</summary>
    [Fact]
    public void APushResult_ReadsBackFieldByField()
    {
        PacketWriter writer = new();

        ItemPushResultPacket.Write(writer, new ItemPushResult(
            Player: Owner,
            FromNpc: false,
            Created: true,
            ShowInChat: true,
            Bag: InventorySlots.Backpack,
            Slot: ItemPushResultPacket.MergedIntoStack,
            Entry: 2589,
            Count: 3,
            TotalOfEntry: 8));

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt64(out ulong player));
        Assert.Equal(Owner.Value, player);

        Assert.True(reader.TryReadUInt32(out uint fromNpc));
        Assert.Equal(0u, fromNpc);

        Assert.True(reader.TryReadUInt32(out uint created));
        Assert.Equal(1u, created);

        Assert.True(reader.TryReadUInt32(out uint chat));
        Assert.Equal(1u, chat);

        Assert.True(reader.TryReadUInt8(out byte bag));
        Assert.Equal(255, bag);

        Assert.True(reader.TryReadUInt32(out uint slot));
        Assert.Equal(0xFFFFFFFFu, slot);

        Assert.True(reader.TryReadUInt32(out uint entry));
        Assert.Equal(2589u, entry);

        reader.Skip(4 + 4);

        Assert.True(reader.TryReadUInt32(out uint count));
        Assert.Equal(3u, count);

        Assert.True(reader.TryReadUInt32(out uint total));
        Assert.Equal(8u, total);

        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>An item's create block carries no movement at all.</summary>
    /// <remarks>
    /// An item has no position. A movement block here would shift the field mask by 60-odd bytes
    /// and the client would read the whole item as noise.
    /// </remarks>
    [Fact]
    public void AnItemCreateBlock_HasNoMovement()
    {
        Player player = InventoryFixture.Player();
        Item item = InventoryFixture.Place(player, ItemFixture.Build(entry: 2589), InventoryFixture.Backpack());

        byte[] block = UpdateBlockBuilder.BuildItemCreateBlock(item.Guid, item.TypeId, item.Fields);

        PacketReader reader = new(block);

        Assert.True(reader.TryReadUInt8(out byte updateType));
        Assert.Equal((byte)UpdateType.CreateObject, updateType);   // not CreateObject2

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid guid));
        Assert.Equal(item.Guid, guid);

        Assert.True(reader.TryReadUInt8(out byte typeId));
        Assert.Equal((byte)TypeId.Item, typeId);

        Assert.True(reader.TryReadUInt16(out ushort flags));
        Assert.Equal(0x0010, flags);          // LOWGUID and nothing else

        Assert.True(reader.TryReadUInt32(out uint lowGuid));
        Assert.Equal(item.Guid.Counter, lowGuid);
    }
}

/// <summary>Starting gear, over the real DBC and the real item rows.</summary>
public sealed class StartingOutfitTests(ITestOutputHelper output)
{
    /// <summary>The three identifying columns are single bytes, and the file says 126 rows.</summary>
    /// <remarks>
    /// Reading race, class and gender as words would put the first item id nine bytes late, and
    /// every row would look like an outfit for race 0 — which still loads, and produces 126 rows
    /// that all collide on one key.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheOutfits_LoadKeyedByRaceClassAndGender()
    {
        CharStartOutfitStore outfits = CharStartOutfitStore.Load(ClientData.DbcDirectory);

        Assert.Equal(126, outfits.Rows.Count);
        Assert.Equal(126, outfits.Count);

        output.WriteLine($"{outfits.Count} outfits");
    }

    /// <summary>A human male warrior starts with clothes, a weapon and a hearthstone.</summary>
    [RequiresClientDataFact]
    public void AHumanWarrior_StartsWithTheExpectedItems()
    {
        CharStartOutfitStore outfits = CharStartOutfitStore.Load(ClientData.DbcDirectory);

        uint[] items = [.. outfits.ItemsFor(race: 1, characterClass: 1, gender: 0)];

        Assert.NotEmpty(items);

        // The hearthstone, which every outfit in the game carries.
        Assert.Contains(6948u, items);

        output.WriteLine($"human male warrior: {string.Join(", ", items)}");
    }

    /// <summary>An unused item slot is -1, and is skipped rather than handed out.</summary>
    [RequiresClientDataFact]
    public void EmptySlots_AreMinusOneAndSkipped()
    {
        CharStartOutfitStore outfits = CharStartOutfitStore.Load(ClientData.DbcDirectory);

        Assert.True(outfits.TryGet(1, 1, 0, out CharStartOutfitEntry? outfit));
        Assert.NotNull(outfit);

        Assert.Contains(outfit.ItemIds, id => id == -1);
        Assert.DoesNotContain(outfits.ItemsFor(1, 1, 0), id => id == uint.MaxValue);
    }

    /// <summary>Every race and class that can be created has an outfit for both genders.</summary>
    /// <remarks>
    /// A missing one is a naked character with no error anywhere, which is the failure mode this
    /// whole file exists to prevent.
    /// </remarks>
    [RequiresClientDataFact]
    public void EveryPlayableCombination_HasAnOutfit()
    {
        CharStartOutfitStore outfits = CharStartOutfitStore.Load(ClientData.DbcDirectory);

        List<string> missing = [];

        foreach (CharStartOutfitEntry entry in outfits.Rows.Entries)
        {
            byte otherGender = (byte)(entry.Gender == 0 ? 1 : 0);

            if (!outfits.TryGet(entry.Race, entry.Class, otherGender, out _))
            {
                missing.Add($"race {entry.Race} class {entry.Class} gender {otherGender}");
            }
        }

        Assert.Empty(missing);
    }
}

/// <summary>What wearing something does to the character's numbers.</summary>
public sealed class EquipmentStatsTests
{
    private static ItemTemplate Sword(float min, float max, ushort delay, ushort armor = 0, ItemStat[]? stats = null) =>
        ItemFixture.Build(
            entry: 25,
            itemClass: ItemClass.Weapon,
            inventoryType: InventoryType.WeaponMainHand,
            delay: delay,
            damage: [new ItemDamage(min, max, 0), default],
            statsCount: (byte)(stats?.Length ?? 0),
            stats: Pad(stats)) with { Armor = armor };

    /// <summary>The stat array is always ten wide, whatever the count says.</summary>
    private static ItemStat[] Pad(ItemStat[]? stats)
    {
        ItemStat[] padded = new ItemStat[ItemConstants.MaxStats];

        for (int i = 0; stats is not null && i < stats.Length; i++)
        {
            padded[i] = stats[i];
        }

        return padded;
    }

    /// <summary>
    /// Equipping a weapon changes what a swing is worth.
    /// </summary>
    /// <remarks>
    /// Without this the player swings for its fists whatever it is holding — the sword is drawn,
    /// the tooltip is right, and the damage never changes. There is no error anywhere.
    /// </remarks>
    [Fact]
    public void AWeapon_ChangesTheSwing()
    {
        Player player = InventoryFixture.Player();

        float unarmedMin = player.MinDamage;

        InventoryFixture.Place(
            player,
            Sword(min: 20f, max: 30f, delay: 2600),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        Assert.True(player.MinDamage > unarmedMin, "the weapon did not change the swing");
        Assert.True(player.MinDamage >= 20f, $"expected at least the weapon's own minimum, got {player.MinDamage}");
        Assert.Equal(2600u, player.GetAttackTime(WeaponAttackType.BaseAttack));
    }

    /// <summary>Taking the weapon off puts the fists back.</summary>
    [Fact]
    public void Unequipping_RestoresTheUnarmedSwing()
    {
        Player player = InventoryFixture.Player();
        ItemPosition mainHand = new(InventorySlots.Backpack, InventorySlots.MainHand);

        InventoryFixture.Place(player, Sword(20f, 30f, 2600), mainHand);
        player.Inventory.Take(mainHand);

        Assert.Equal(PlayerCombatStats.UnarmedAttackTimeMs, player.GetAttackTime(WeaponAttackType.BaseAttack));
        Assert.True(player.MinDamage < 20f, $"still swinging for the weapon: {player.MinDamage}");
    }

    /// <summary>
    /// Attack power is scaled by the weapon's own speed, not by the unarmed two seconds.
    /// </summary>
    /// <remarks>
    /// Attack power is a damage-per-second figure. Scaling it by a fixed 2000 ms makes a slow
    /// weapon and a fast one hit for the same amount, which is the entire point of weapon speed.
    /// </remarks>
    [Fact]
    public void AttackPower_IsScaledByTheWeaponsSpeed()
    {
        Player fast = InventoryFixture.Player(level: 20);
        Player slow = InventoryFixture.Player(level: 20);

        InventoryFixture.Place(
            fast, Sword(10f, 10f, 1500), new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        InventoryFixture.Place(
            slow, Sword(10f, 10f, 3000), new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        Assert.Equal(fast.AttackPower, slow.AttackPower);
        Assert.True(slow.MinDamage > fast.MinDamage, "the slower weapon gained no extra from attack power");
    }

    /// <summary>Armour is the sum of what is worn, and drops back to zero when it comes off.</summary>
    [Fact]
    public void Armor_IsTheSumOfWhatIsWorn()
    {
        Player player = InventoryFixture.Player();
        ItemPosition chest = new(InventorySlots.Backpack, InventorySlots.Chest);

        Assert.Equal(0u, player.Armor);

        InventoryFixture.Place(
            player,
            ItemFixture.Build(entry: 6096, itemClass: ItemClass.Armor, inventoryType: InventoryType.Chest)
                with { Armor = 41 },
            chest);

        Assert.Equal(41u, player.Armor);

        player.Inventory.Take(chest);

        Assert.Equal(0u, player.Armor);
    }

    /// <summary>
    /// An item's attributes are added to the character's, and removed again.
    /// </summary>
    /// <remarks>
    /// Recomputed from the level's base each time rather than adjusted by a delta: a delta is one
    /// missed call away from a character who gains strength every time they take a belt off.
    /// </remarks>
    [Fact]
    public void ItemStats_AddToTheCharactersAndComeBackOff()
    {
        Player player = InventoryFixture.Player();
        uint baseStrength = player.GetStat(0);

        ItemPosition mainHand = new(InventorySlots.Backpack, InventorySlots.MainHand);

        // Type 4 is strength, type 7 is stamina.
        InventoryFixture.Place(
            player,
            Sword(5f, 6f, 2000, stats: [new ItemStat(4, 3), new ItemStat(7, 2)]),
            mainHand);

        Assert.Equal(baseStrength + 3, player.GetStat(0));
        Assert.Equal(2u, player.GetStat(2) - InventoryFixture.Player().GetStat(2));

        player.Inventory.Take(mainHand);

        Assert.Equal(baseStrength, player.GetStat(0));
    }

    /// <summary>
    /// A stat count of zero means no stats, whatever is in the columns.
    /// </summary>
    /// <remarks>
    /// The columns past the declared count hold leftovers from whatever the row was before, and
    /// reading all ten regardless hands out attributes no tooltip shows.
    /// </remarks>
    [Fact]
    public void StatsPastTheDeclaredCount_AreIgnored()
    {
        Player player = InventoryFixture.Player();
        uint baseStrength = player.GetStat(0);

        ItemTemplate sword = Sword(5f, 6f, 2000) with
        {
            StatsCount = 0,
            Stats = Pad([new ItemStat(4, 99)]),
        };

        InventoryFixture.Place(
            player, sword, new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        Assert.Equal(baseStrength, player.GetStat(0));
    }

    /// <summary>A broken weapon gives nothing — no damage, no stats, no armour.</summary>
    [Fact]
    public void ABrokenItem_GivesNothing()
    {
        Player player = InventoryFixture.Player();
        uint baseStrength = player.GetStat(0);

        ItemTemplate sword = Sword(20f, 30f, 2600, armor: 20, stats: [new ItemStat(4, 5)])
            with { MaxDurability = 55 };

        Item worn = InventoryFixture.Place(
            player, sword, new ItemPosition(InventorySlots.Backpack, InventorySlots.MainHand));

        Assert.Equal(baseStrength + 5, player.GetStat(0));

        worn.Durability = 0;
        PlayerCombatStats.Apply(player);

        Assert.True(worn.IsBroken);
        Assert.Equal(baseStrength, player.GetStat(0));
        Assert.Equal(0u, player.Armor);
        Assert.True(player.MinDamage < 20f, $"a broken weapon still swings for {player.MinDamage}");
    }
}
