using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Combat ratings and magic resistances from what a character is wearing.
/// </summary>
/// <remarks>
/// Only the five attributes were applied before this — every rating and resistance on every item in
/// the game was read and thrown away, so the character sheet showed zeroes for gear that plainly
/// said otherwise.
/// <para>
/// These are the stored ratings. Turning a rating into a percentage needs <c>gtCombatRatings.dbc</c>
/// and its class scalar, which is a separate gap: a crit rating shows correctly and does not yet
/// change a roll.
/// </para>
/// </remarks>
public sealed class ItemRatingTests
{
    /// <summary>A rating on an item reaches the field the client reads.</summary>
    [Fact]
    public void ARating_ReachesItsField()
    {
        Player player = Wearing(Stat(ItemModDodgeRating, 40));

        Assert.Equal(40u, Rating(player, CrDodge));
    }

    /// <summary>
    /// A combined rating feeds all three schools, not one.
    /// </summary>
    /// <remarks>
    /// There is no single "hit rating" field. <c>ITEM_MOD_HIT_RATING</c> is melee, ranged and spell
    /// hit at once, so reading it as one rating loses two thirds of what the item gives — and the
    /// third that lands is plausible enough that nothing looks wrong.
    /// </remarks>
    [Fact]
    public void ACombinedRating_FeedsAllThreeSchools()
    {
        Player player = Wearing(Stat(ItemModHitRating, 30));

        Assert.Equal(30u, Rating(player, CrHitMelee));
        Assert.Equal(30u, Rating(player, CrHitRanged));
        Assert.Equal(30u, Rating(player, CrHitSpell));
    }

    /// <summary>
    /// Resilience and crit-taken are the same three ratings.
    /// </summary>
    /// <remarks>
    /// Upstream falls one case straight into the other, which reads like a missing <c>break</c>
    /// until you check — and "fixing" it gives resilience its own field, which does not exist.
    /// </remarks>
    [Fact]
    public void Resilience_IsCritTaken()
    {
        Player player = Wearing(Stat(ItemModResilienceRating, 25));

        Assert.Equal(25u, Rating(player, CrCritTakenMelee));
        Assert.Equal(25u, Rating(player, CrCritTakenRanged));
        Assert.Equal(25u, Rating(player, CrCritTakenSpell));
    }

    /// <summary>Ratings add up across everything worn.</summary>
    [Fact]
    public void Ratings_AddUpAcrossEquipment()
    {
        Player player = InventoryFixture.Player();

        Wear(player, InventorySlots.Chest, Stat(ItemModCritRating, 10));
        Wear(player, InventorySlots.Legs, Stat(ItemModCritRating, 15));

        PlayerCombatStats.Apply(player);

        Assert.Equal(25u, Rating(player, CrCritMelee));
    }

    /// <summary>Taking the gear off takes the rating with it.</summary>
    /// <remarks>
    /// Recomputed from what is worn rather than adjusted by a delta, for the same reason the
    /// attributes are: a delta is one missed call away from a permanent bonus.
    /// </remarks>
    [Fact]
    public void RemovingGear_RemovesTheRating()
    {
        Player player = Wearing(Stat(ItemModDodgeRating, 40));

        Assert.Equal(40u, Rating(player, CrDodge));

        player.Inventory.Place(new ItemPosition(InventorySlots.Backpack, InventorySlots.Chest), null);
        PlayerCombatStats.Apply(player);

        Assert.Equal(0u, Rating(player, CrDodge));
    }

    /// <summary>An attribute is not a rating, and does not leak into one.</summary>
    [Fact]
    public void AnAttribute_IsNotARating()
    {
        Player player = Wearing(Stat(ItemModAgility, 50));

        for (int rating = 0; rating < 25; rating++)
        {
            Assert.Equal(0u, Rating(player, rating));
        }
    }

    /// <summary>Only the declared stats count, not the leftovers past the count.</summary>
    /// <remarks>
    /// The ten stat columns are reused, so the ones past <c>StatsCount</c> hold whatever the last
    /// item to use them left behind.
    /// </remarks>
    [Fact]
    public void OnlyTheDeclaredStats_Count()
    {
        ItemStat[] stats = new ItemStat[ItemConstants.MaxStats];
        stats[0] = new ItemStat(ItemModDodgeRating, 10);
        stats[1] = new ItemStat(ItemModParryRating, 999);

        Player player = InventoryFixture.Player();

        InventoryFixture.Place(
            player,
            ItemFixture.Build(entry: 1, inventoryType: InventoryType.Chest, statsCount: 1, stats: stats),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.Chest));

        PlayerCombatStats.Apply(player);

        Assert.Equal(10u, Rating(player, CrDodge));
        Assert.Equal(0u, Rating(player, CrParry));
    }

    // ------------------------------------------------------------------ resistances

    /// <summary>Resistances come from the item's own columns and land in their schools.</summary>
    [Fact]
    public void Resistances_LandInTheirSchools()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate cloak = ItemFixture.Build(entry: 2, inventoryType: InventoryType.Chest) with
        {
            FireResistance = 20,
            FrostResistance = 15,
        };

        InventoryFixture.Place(
            player, cloak, new ItemPosition(InventorySlots.Backpack, InventorySlots.Chest));

        PlayerCombatStats.Apply(player);

        Assert.Equal(20u, Resistance(player, SchoolFire));
        Assert.Equal(15u, Resistance(player, SchoolFrost));
        Assert.Equal(0u, Resistance(player, SchoolShadow));
    }

    /// <summary>
    /// Writing resistances does not overwrite armour.
    /// </summary>
    /// <remarks>
    /// They share a field block and <b>slot 0 is armour</b>, the physical school. Starting the
    /// resistance loop at zero silently erases every point of mitigation the character has, and
    /// replaces it with a holy resistance nobody asked for.
    /// </remarks>
    [Fact]
    public void Resistances_DoNotOverwriteArmour()
    {
        Player player = InventoryFixture.Player();

        ItemTemplate plate = ItemFixture.Build(entry: 3, inventoryType: InventoryType.Chest) with
        {
            Armor = 500,
            HolyResistance = 10,
        };

        InventoryFixture.Place(
            player, plate, new ItemPosition(InventorySlots.Backpack, InventorySlots.Chest));

        PlayerCombatStats.Apply(player);

        Assert.Equal(500u, player.Armor);
        Assert.Equal(10u, Resistance(player, SchoolHoly));
    }

    // ------------------------------------------------------------------ fixtures

    private const byte ItemModAgility = 3;
    private const byte ItemModDodgeRating = 13;
    private const byte ItemModParryRating = 14;
    private const byte ItemModHitRating = 31;
    private const byte ItemModCritRating = 32;
    private const byte ItemModResilienceRating = 35;

    private const int CrDodge = 2;
    private const int CrParry = 3;
    private const int CrHitMelee = 5;
    private const int CrHitRanged = 6;
    private const int CrHitSpell = 7;
    private const int CrCritMelee = 8;
    private const int CrCritTakenMelee = 14;
    private const int CrCritTakenRanged = 15;
    private const int CrCritTakenSpell = 16;

    private const int SchoolHoly = 1;
    private const int SchoolFire = 2;
    private const int SchoolFrost = 4;
    private const int SchoolShadow = 5;

    private static ItemStat[] Stat(byte type, short value)
    {
        ItemStat[] stats = new ItemStat[ItemConstants.MaxStats];
        stats[0] = new ItemStat(type, value);

        return stats;
    }

    private static uint Rating(Player player, int rating) =>
        player.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_COMBAT_RATING_1 + rating);

    private static uint Resistance(Player player, int school) =>
        player.Fields.GetUInt32(UpdateFields.UNIT_FIELD_RESISTANCES + school);

    private static Player Wearing(ItemStat[] stats)
    {
        Player player = InventoryFixture.Player();

        Wear(player, InventorySlots.Chest, stats);
        PlayerCombatStats.Apply(player);

        return player;
    }

    private static void Wear(Player player, byte slot, ItemStat[] stats) =>
        InventoryFixture.Place(
            player,
            ItemFixture.Build(
                entry: (uint)(10 + slot),
                inventoryType: slot == InventorySlots.Chest ? InventoryType.Chest : InventoryType.Legs,
                statsCount: 1,
                stats: stats),
            new ItemPosition(InventorySlots.Backpack, slot));
}
