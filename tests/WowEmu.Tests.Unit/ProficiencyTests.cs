using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Whether a character knows how to hold the thing they are trying to wear.
/// </summary>
/// <remarks>
/// Three separate rules that all sound like one. The item's own class and subclass imply a
/// proficiency; the template can name a required skill and a rank; and it can require a spell.
/// They produce two different refusals, and the client prints different text for each.
/// <para>
/// All of this passed silently before there were skills, so a level-1 warrior could wear a wand.
/// </para>
/// </remarks>
public sealed class ProficiencyTests
{
    /// <summary>A weapon whose proficiency the character lacks is refused.</summary>
    [Fact]
    public void AWeaponTheyCannotHold_IsRefused()
    {
        Player player = Untrained();
        Item wand = Carried(player, Wand);

        Assert.Equal(
            InventoryResult.NoRequiredProficiency,
            player.Inventory.CanEquip(wand, InventorySlots.Ranged));
    }

    /// <summary>And accepted once the proficiency is there.</summary>
    /// <remarks>
    /// The proficiency is a mono skill sitting at 1/1, so the test is only whether it is present —
    /// there is no rank to reach.
    /// </remarks>
    [Fact]
    public void AWeaponTheyCanHold_IsAccepted()
    {
        Player player = Untrained();
        player.Skills.Set(SkillType.Wands, 0, 1, 1);

        Item wand = Carried(player, Wand);

        Assert.Equal(InventoryResult.Ok, player.Inventory.CanEquip(wand, InventorySlots.Ranged));
    }

    /// <summary>Armour works the same way — plate needs the plate proficiency.</summary>
    [Fact]
    public void ArmourNeedsItsProficiencyToo()
    {
        Player player = Untrained();
        Item plate = Carried(player, PlateChest);

        Assert.Equal(
            InventoryResult.NoRequiredProficiency,
            player.Inventory.CanEquip(plate, InventorySlots.Chest));

        player.Skills.Set(SkillType.PlateMail, 0, 1, 1);

        Assert.Equal(InventoryResult.Ok, player.Inventory.CanEquip(plate, InventorySlots.Chest));
    }

    /// <summary>Something with no proficiency of its own needs none.</summary>
    /// <remarks>
    /// Rings, trinkets and cloaks map to no skill at all. Treating a zero as "some skill you do not
    /// have" would make every one of them unequippable.
    /// </remarks>
    [Fact]
    public void SomethingWithNoProficiency_NeedsNone()
    {
        Player player = Untrained();

        ItemTemplate ring = ItemFixture.Build(
            entry: 30, itemClass: ItemClass.Armor, subClass: 0, inventoryType: InventoryType.Finger);

        Item item = Carried(player, ring);

        Assert.Equal(InventoryResult.Ok, player.Inventory.CanEquip(item, InventorySlots.Finger1));
    }

    /// <summary>
    /// A required skill the character lacks entirely, and one they merely lack the rank for, are
    /// two different refusals.
    /// </summary>
    /// <remarks>
    /// The client shows different text for each. Collapsing them tells someone who needs a little
    /// more practice that they can never use the item, which sends them looking for a trainer they
    /// have already visited.
    /// </remarks>
    [Fact]
    public void MissingTheSkillAndMissingTheRank_AreDifferentRefusals()
    {
        ItemTemplate gated = ItemFixture.Build(
            entry: 40,
            itemClass: ItemClass.Armor,
            subClass: 0,
            inventoryType: InventoryType.Finger) with
        {
            RequiredSkill = (ushort)SkillType.Swords,
            RequiredSkillRank = 100,
        };

        Player without = Untrained();

        Assert.Equal(
            InventoryResult.NoRequiredProficiency,
            without.Inventory.CanEquip(Carried(without, gated), InventorySlots.Finger1));

        Player underRank = Untrained();
        underRank.Skills.Set(SkillType.Swords, 0, 99, 300);

        Assert.Equal(
            InventoryResult.CantEquipSkill,
            underRank.Inventory.CanEquip(Carried(underRank, gated), InventorySlots.Finger1));

        Player ready = Untrained();
        ready.Skills.Set(SkillType.Swords, 0, 100, 300);

        Assert.Equal(
            InventoryResult.Ok,
            ready.Inventory.CanEquip(Carried(ready, gated), InventorySlots.Finger1));
    }

    /// <summary>A required spell gates the same way.</summary>
    [Fact]
    public void ARequiredSpell_MustBeKnown()
    {
        ItemTemplate gated = ItemFixture.Build(
            entry: 50, itemClass: ItemClass.Armor, subClass: 0, inventoryType: InventoryType.Finger) with
        {
            RequiredSpell = 1234,
        };

        Player player = Untrained();

        Assert.Equal(
            InventoryResult.NoRequiredProficiency,
            player.Inventory.CanEquip(Carried(player, gated), InventorySlots.Finger1));

        player.Spells.Learn(1234);

        Assert.Equal(
            InventoryResult.Ok,
            player.Inventory.CanEquip(Carried(player, gated, slot: 1), InventorySlots.Finger1));
    }

    /// <summary>
    /// The wrong slot is a wrong-slot mistake, whatever the character can hold.
    /// </summary>
    /// <remarks>
    /// Upstream resolves the slot with <c>FindEquipSlot</c> before it calls <c>CanUseItem</c>.
    /// Answering "you lack the proficiency" for a sword dragged onto the head slot sends the player
    /// off to a trainer for a problem a trainer cannot fix.
    /// </remarks>
    [Fact]
    public void TheWrongSlot_BeatsTheProficiency()
    {
        Player player = Untrained();
        Item wand = Carried(player, Wand);

        Assert.Equal(
            InventoryResult.ItemDoesNotGoToSlot,
            player.Inventory.CanEquip(wand, InventorySlots.Head));
    }

    /// <summary>
    /// A heirloom's armour bends for the two classes whose armour genuinely upgrades.
    /// </summary>
    /// <remarks>
    /// Heirloom shoulders are mail for a warrior until 40 and plate after, but the item's subclass
    /// says plate throughout — so the plain check refuses the level-1 warrior it was bought for.
    /// The exception is narrow on purpose: only the type each class upgrades <i>into</i>, and only
    /// for heirloom armour.
    /// </remarks>
    [Fact]
    public void AHeirloom_BendsForTheClassesThatUpgrade()
    {
        ItemTemplate heirloomPlate = PlateChest with { Quality = ItemQuality.Heirloom };

        Player warrior = Untrained(characterClass: Warrior);

        Assert.Equal(
            InventoryResult.Ok,
            warrior.Inventory.CanEquip(Carried(warrior, heirloomPlate), InventorySlots.Chest));

        // A rogue's armour never becomes plate, so nothing bends for them.
        Player rogue = Untrained(characterClass: Rogue);

        Assert.Equal(
            InventoryResult.NoRequiredProficiency,
            rogue.Inventory.CanEquip(Carried(rogue, heirloomPlate), InventorySlots.Chest));
    }

    /// <summary>And only for armour — a heirloom weapon still needs its proficiency.</summary>
    /// <remarks>
    /// Weapons do not change type as you level, so there is nothing for the exception to cover. It
    /// would only let a warrior swing a heirloom wand.
    /// </remarks>
    [Fact]
    public void AHeirloomWeapon_StillNeedsItsProficiency()
    {
        ItemTemplate heirloomWand = Wand with { Quality = ItemQuality.Heirloom };

        Player warrior = Untrained(characterClass: Warrior);

        Assert.Equal(
            InventoryResult.NoRequiredProficiency,
            warrior.Inventory.CanEquip(Carried(warrior, heirloomWand), InventorySlots.Ranged));
    }

    /// <summary>
    /// Handing someone gear puts it on only if they can hold it, and bags it otherwise.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the character-creation order load-bearing.</b> Starting items are
    /// handed over through <c>StoreInBestSlots</c>, which equips through the same rules as anything
    /// else — so skills have to be granted <i>before</i> the gear, or every new character is created
    /// holding their weapon in a bag. Nothing about that failure looks like a proficiency bug from
    /// the outside, which is why it is worth a test here rather than a comment there.
    /// </remarks>
    [Fact]
    public void GearIsOnlyWorn_IfItCanBeHeld()
    {
        Player untrained = Untrained();

        Assert.True(untrained.Inventory.StoreInBestSlots(Wand, 1, InventoryFixture.NextGuid, out _));
        Assert.Null(untrained.Inventory.Equipped(InventorySlots.Ranged));

        Player trained = Untrained();
        trained.Skills.Set(SkillType.Wands, 0, 1, 1);

        Assert.True(trained.Inventory.StoreInBestSlots(Wand, 1, InventoryFixture.NextGuid, out _));
        Assert.NotNull(trained.Inventory.Equipped(InventorySlots.Ranged));
    }

    private const byte Warrior = 1;
    private const byte Rogue = 4;

    private const byte WandSubClass = 19;
    private const byte PlateSubClass = 4;

    private static readonly ItemTemplate Wand = ItemFixture.Build(
        entry: 10,
        itemClass: ItemClass.Weapon,
        subClass: WandSubClass,
        inventoryType: InventoryType.RangedRight);

    private static readonly ItemTemplate PlateChest = ItemFixture.Build(
        entry: 20,
        itemClass: ItemClass.Armor,
        subClass: PlateSubClass,
        inventoryType: InventoryType.Chest);

    /// <summary>A character who has been taught nothing, which is what makes these tests mean something.</summary>
    private static Player Untrained(byte characterClass = Warrior) =>
        InventoryFixture.Player(level: 20, characterClass: characterClass, proficiencies: false);

    /// <summary>
    /// Puts a template in the backpack so it can be equipped from somewhere.
    /// </summary>
    /// <param name="slot">
    /// Distinct within a test that carries two things at once. A shared counter would do, and would
    /// be one static away from making these tests order-dependent.
    /// </param>
    private static Item Carried(Player player, ItemTemplate template, byte slot = 0) =>
        InventoryFixture.Place(player, template, InventoryFixture.Backpack(slot));
}
