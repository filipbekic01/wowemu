using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Wearing gear out, and paying to have it mended.
/// </summary>
/// <remarks>
/// Nothing wore anything out before this, so the durability items already carried never moved and
/// repair had nothing to repair. The pair belongs together — either alone is a system that does
/// nothing.
/// </remarks>
public sealed class DurabilityTests
{
    /// <summary>Dying takes a tenth off what is worn.</summary>
    [Fact]
    public void Dying_WearsEquipment()
    {
        Player player = InventoryFixture.Player(level: 20);
        Item worn = Equip(player, maxDurability: 100);

        Durability.LoseAll(player, Durability.DeathLoss);

        Assert.Equal(90u, worn.Durability);
    }

    /// <summary>
    /// And leaves what is carried alone.
    /// </summary>
    /// <remarks>
    /// Upstream passes <c>inventory: false</c> on death. The spirit healer's resurrect is the one
    /// that reaches the bags, and it is a separate and larger charge.
    /// </remarks>
    [Fact]
    public void Dying_LeavesCarriedItemsAlone()
    {
        Player player = InventoryFixture.Player(level: 20);

        Item carried = InventoryFixture.Place(
            player, ItemFixture.Build(entry: 50, maxDurability: 100), InventoryFixture.Backpack());

        Durability.LoseAll(player, Durability.DeathLoss, inventory: false);

        Assert.Equal(100u, carried.Durability);

        Durability.LoseAll(player, Durability.DeathLoss, inventory: true);

        Assert.Equal(90u, carried.Durability);
    }

    /// <summary>
    /// A tenth of a small maximum still costs a point.
    /// </summary>
    /// <remarks>
    /// Ten percent of nine rounds to zero, and without the floor those items would never wear out —
    /// which is most of what a level-one character is wearing.
    /// </remarks>
    [Fact]
    public void ASmallItem_StillLosesAPoint()
    {
        Player player = InventoryFixture.Player(level: 5);
        Item worn = Equip(player, maxDurability: 9);

        Durability.LoseAll(player, Durability.DeathLoss);

        Assert.Equal(8u, worn.Durability);
    }

    /// <summary>
    /// Wearing everything wears what is worn once, not twice.
    /// </summary>
    /// <remarks>
    /// The equipment pass and the inventory pass are separate loops, and the obvious enumeration of
    /// "everything the player holds" includes the equipment slots — so a worn item takes the loss
    /// twice and a spirit-healer resurrect costs nineteen percent of your armour rather than ten.
    /// Repair hides the same mistake, because the second visit finds the item already mended.
    /// </remarks>
    [Fact]
    public void WearingEverything_WearsWornItemsOnce()
    {
        Player player = InventoryFixture.Player(level: 20);
        Item worn = Equip(player, maxDurability: 100);

        Durability.LoseAll(player, Durability.DeathLoss, inventory: true);

        // Twice would be 100 → 90 → 81.
        Assert.Equal(90u, worn.Durability);
    }

    /// <summary>Wear stops at zero rather than wrapping.</summary>
    [Fact]
    public void Wear_StopsAtZero()
    {
        Item item = Loose(maxDurability: 10);
        item.Durability = 1;

        Durability.Lose(item, 0.9);

        Assert.Equal(0u, item.Durability);
        Assert.True(item.IsBroken);
    }

    /// <summary>Something with no durability of its own cannot wear out.</summary>
    /// <remarks>Bags and keys, which fall out by having a maximum of zero rather than by a filter.</remarks>
    [Fact]
    public void SomethingWithNoDurability_IsUnaffected()
    {
        Item bag = Loose(maxDurability: 0);

        Durability.Lose(bag, 0.5);

        Assert.Equal(0u, bag.Durability);
        Assert.False(bag.IsDamaged);
    }

    // ------------------------------------------------------------------ the bill

    /// <summary>An undamaged item costs nothing.</summary>
    [Fact]
    public void AnUndamagedItem_CostsNothing() =>
        Assert.Equal(0u, Durability.RepairCost(Loose(100), Costs(), Quality()));

    /// <summary>
    /// The bill scales with the points lost.
    /// </summary>
    /// <remarks>
    /// Points, not a fraction: half of a hundred-durability item costs ten times half of a
    /// ten-durability one, because it is fifty points against five.
    /// </remarks>
    [Fact]
    public void TheBill_ScalesWithPointsLost()
    {
        Item lightlyWorn = Loose(100);
        lightlyWorn.Durability = 90;

        Item badlyWorn = Loose(100);
        badlyWorn.Durability = 50;

        uint light = Durability.RepairCost(lightlyWorn, Costs(), Quality());
        uint heavy = Durability.RepairCost(badlyWorn, Costs(), Quality());

        Assert.True(heavy > light, $"{heavy} should exceed {light}");
        Assert.Equal(5 * light, heavy);
    }

    /// <summary>
    /// The quality row is <c>(quality + 1) * 2</c>, not the quality.
    /// </summary>
    /// <remarks>
    /// The worst kind of off-by-one: indexing by the quality itself finds a row that exists and is
    /// wrong, so every bill comes out plausible and mispriced.
    /// </remarks>
    [Fact]
    public void TheQualityRow_IsNotTheQuality()
    {
        DbcStore<DurabilityQualityEntry> quality = Quality();

        Item common = Loose(100, quality: ItemQuality.Normal);
        common.Durability = 50;

        // Row 4 is (1 + 1) * 2. Row 1 exists too and carries a different modifier, which is what a
        // naive lookup would find.
        Assert.True(quality.TryGet(4, out DurabilityQualityEntry correct));
        Assert.True(quality.TryGet(1, out DurabilityQualityEntry wrong));
        Assert.NotEqual(correct.Modifier, wrong.Modifier);

        uint cost = Durability.RepairCost(common, Costs(), quality);

        Assert.Equal((uint)(50 * MultiplierForTest * (double)correct.Modifier), cost);
    }

    /// <summary>
    /// Armour picks its multiplier twenty-one slots along from a weapon's.
    /// </summary>
    /// <remarks>
    /// The whole of <c>ItemSubClassToDurabilityMultiplierId</c>, and easy to miss — without the
    /// offset a plate chestpiece is priced as a dagger.
    /// </remarks>
    [Fact]
    public void Armour_IsIndexedTwentyOneAlong()
    {
        Assert.Equal(3, DurabilityCostsEntry.MultiplierFor(DurabilityCostsEntry.ClassWeapon, 3));
        Assert.Equal(24, DurabilityCostsEntry.MultiplierFor(DurabilityCostsEntry.ClassArmor, 3));

        // Anything else falls to slot zero rather than off the end.
        Assert.Equal(0, DurabilityCostsEntry.MultiplierFor(itemClass: 9, subClass: 5));
    }

    /// <summary>A bill that works out to nothing is still a copper.</summary>
    /// <remarks>
    /// Upstream's own note calls this a fix for artifact quality, whose modifier is zero — without
    /// it those items repair for free.
    /// </remarks>
    [Fact]
    public void ABillOfNothing_IsStillACopper()
    {
        Item artifact = Loose(100, quality: ItemQuality.Artifact);
        artifact.Durability = 1;

        Assert.Equal(1u, Durability.RepairCost(artifact, Costs(), ZeroQuality()));
    }

    /// <summary>An item level the table has never heard of repairs for free rather than guessing.</summary>
    /// <remarks>
    /// The safe direction: the alternative to charging nothing is charging an invented amount.
    /// </remarks>
    [Fact]
    public void AnUnknownItemLevel_CostsNothing()
    {
        Item exotic = Loose(100, itemLevel: 9999);
        exotic.Durability = 1;

        Assert.Equal(0u, Durability.RepairCost(exotic, Costs(), Quality()));
    }

    // ------------------------------------------------------------------ paying

    /// <summary>Repairing takes the money and gives the durability back.</summary>
    [Fact]
    public void Repairing_ChargesAndMends()
    {
        Player player = InventoryFixture.Player(level: 20);
        Item worn = Equip(player, maxDurability: 100);
        worn.Durability = 50;

        player.Money = 100_000;

        uint? cost = Durability.Repair(player, worn, Costs(), Quality());

        Assert.NotNull(cost);
        Assert.Equal(100u, worn.Durability);
        Assert.Equal(100_000u - cost!.Value, player.Money);
    }

    /// <summary>
    /// A player who cannot pay keeps their money and their broken item.
    /// </summary>
    /// <remarks>
    /// Both or neither. Deducting first and then failing would be a charge for nothing.
    /// </remarks>
    [Fact]
    public void APlayerWhoCannotPay_IsChargedNothing()
    {
        Player player = InventoryFixture.Player(level: 20);
        Item worn = Equip(player, maxDurability: 100);
        worn.Durability = 1;

        player.Money = 0;

        Assert.Null(Durability.Repair(player, worn, Costs(), Quality()));
        Assert.Equal(0u, player.Money);
        Assert.Equal(1u, worn.Durability);
    }

    /// <summary>
    /// A player short of the full bill gets back what they can pay for.
    /// </summary>
    /// <remarks>
    /// Item by item rather than as one bill. Charging the total and refusing would leave someone who
    /// is a copper short with nothing repaired at all, including their weapon.
    /// </remarks>
    [Fact]
    public void APlayerShortOfTheFullBill_GetsBackWhatTheyCanPayFor()
    {
        Player player = InventoryFixture.Player(level: 20);

        Item worn = Equip(player, maxDurability: 100);
        worn.Durability = 50;

        Item carried = InventoryFixture.Place(
            player,
            ItemFixture.Build(entry: 50, maxDurability: 100, itemLevel: TestItemLevel),
            InventoryFixture.Backpack());

        carried.Durability = 50;

        uint each = Durability.RepairCost(worn, Costs(), Quality());

        // Enough for one of the two, and not a copper more.
        player.Money = each;

        Assert.Equal(each, Durability.RepairAll(player, Costs(), Quality()));
        Assert.Equal(0u, player.Money);

        Assert.Equal(100u, worn.Durability);
        Assert.Equal(50u, carried.Durability);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>The multiplier every test row carries, so a bill can be predicted.</summary>
    private const uint MultiplierForTest = 7;

    /// <summary>The item level every test item and the one cost row share.</summary>
    private const ushort TestItemLevel = 10;

    private static Item Loose(
        ushort maxDurability, byte quality = ItemQuality.Normal, ushort itemLevel = TestItemLevel) =>
        Item.Create(
            InventoryFixture.NextGuid(),
            ItemFixture.Build(
                entry: 1, maxDurability: maxDurability, quality: quality, itemLevel: itemLevel));

    private static Item Equip(Player player, ushort maxDurability) =>
        InventoryFixture.Place(
            player,
            ItemFixture.Build(
                entry: 2,
                maxDurability: maxDurability,
                itemLevel: TestItemLevel,
                inventoryType: InventoryType.Chest),
            new ItemPosition(InventorySlots.Backpack, InventorySlots.Chest));

    /// <summary>One cost row at the test item level, every multiplier the same.</summary>
    private static DbcStore<DurabilityCostsEntry> Costs()
    {
        uint[] multipliers = new uint[DurabilityCostsEntry.MultiplierCount];
        Array.Fill(multipliers, MultiplierForTest);

        return Store(e => e.ItemLevel, new DurabilityCostsEntry(TestItemLevel, multipliers));
    }

    /// <summary>Quality rows whose modifiers differ, so a wrong row is detectable.</summary>
    private static DbcStore<DurabilityQualityEntry> Quality() =>
        Store(e => e.Id, new DurabilityQualityEntry(1, 0.5f), new DurabilityQualityEntry(4, 1.0f));

    /// <summary>An artifact-shaped table, whose modifier is zero.</summary>
    private static DbcStore<DurabilityQualityEntry> ZeroQuality() =>
        Store(e => e.Id, new DurabilityQualityEntry(ArtifactRow, 0f));

    /// <summary>The quality row an artifact reads — <c>(6 + 1) * 2</c>.</summary>
    private const uint ArtifactRow = (ItemQuality.Artifact + 1) * 2;

    /// <summary>
    /// A store made of literal rows, since <c>DbcStore</c> can only be loaded from a file.
    /// </summary>
    private static DbcStore<TEntry> Store<TEntry>(Func<TEntry, uint> id, params TEntry[] rows)
    {
        DbcStore<TEntry> store = (DbcStore<TEntry>)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(DbcStore<TEntry>));

        Dictionary<uint, TEntry> map = [];

        foreach (TEntry row in rows)
        {
            map[id(row)] = row;
        }

        typeof(DbcStore<TEntry>)
            .GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(store, map);

        return store;
    }
}
