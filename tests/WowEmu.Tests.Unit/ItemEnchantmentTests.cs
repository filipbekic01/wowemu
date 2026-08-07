using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Random item properties — the "of the Bear" suffixes and the enchantments behind them.
/// </summary>
public sealed class ItemEnchantmentTests
{
    // ------------------------------------------------------------------ the DBC stores

    /// <summary>
    /// An enchantment row carries three effects, each with its own type.
    /// </summary>
    /// <remarks>
    /// One enchantment can add a stat and a proc at once. Reading only the first effect silently
    /// drops half of what many enchants do, and the half that is left still looks right.
    /// </remarks>
    [RequiresClientDataFact]
    public void AnEnchantment_CarriesThreeEffects()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.NotEmpty(stores.ItemEnchantments.Entries);
        Assert.All(
            stores.ItemEnchantments.Entries,
            entry =>
            {
                Assert.Equal(SpellItemEnchantmentEntry.Effects, entry.Types.Length);
                Assert.Equal(SpellItemEnchantmentEntry.Effects, entry.Amounts.Length);
                Assert.Equal(SpellItemEnchantmentEntry.Effects, entry.SpellIds.Length);
            });

        // At least one row uses more than its first effect, or the shape above proves nothing.
        Assert.Contains(
            stores.ItemEnchantments.Entries,
            entry => entry.Types[1] != EnchantmentEffect.None);
    }

    /// <summary>
    /// The spell ids are three columns past the amounts, not adjacent to them.
    /// </summary>
    /// <remarks>
    /// Columns 8 to 10 are the maximum amounts, which 3.3.5 never uses and the format string skips.
    /// Reading the spell ids at 8 gets those instead — small integers that look like plausible
    /// spell ids and are not.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheSpellIds_SkipTheUnusedMaximums()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        // A combat-spell or equip-spell enchant must name a real spell.
        SpellItemEnchantmentEntry withSpell = stores.ItemEnchantments.Entries.First(
            entry => entry.Types[0] is EnchantmentEffect.CombatSpell or EnchantmentEffect.EquipSpell);

        Assert.NotEqual(0u, withSpell.SpellIds[0]);
        Assert.True(withSpell.SpellIds[0] > 100, "A spell id, not an amount.");
    }

    /// <summary>The points table is keyed by item level and descends with the coefficient.</summary>
    /// <remarks>
    /// The struct's comments in DBCStructure.h count a hidden key that is not there, so its indices
    /// run one high — following them reads epic points as rare ones.
    /// </remarks>
    [RequiresClientDataFact]
    public void ThePointsTable_IsKeyedByItemLevel()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.True(stores.RandomPropertyPoints.TryGet(60, out RandomPropertyPointsEntry? row));
        Assert.NotNull(row);

        // Epic beats rare beats uncommon at the same slot, which is what says the three runs are
        // not transposed.
        Assert.True(row.Epic[0] > row.Rare[0]);
        Assert.True(row.Rare[0] > row.Uncommon[0]);

        // And the coefficients descend: a chest is worth more than a ranged weapon.
        Assert.True(row.Epic[0] > row.Epic[4]);
    }

    // ------------------------------------------------------------------ the roll

    /// <summary>
    /// A table whose chances fall short of 100 still hands out a suffix.
    /// </summary>
    /// <remarks>
    /// <b>Most of these tables do fall short.</b> Treating a short total as "nothing happened"
    /// leaves most items with no suffix at all, and the data looks perfectly reasonable while it
    /// does — which is why the second, scaled draw exists.
    /// </remarks>
    [Fact]
    public void AShortTable_StillRolls()
    {
        ItemEnchantmentStore store = Rolls((1, 42, 40f));

        // First draw at 90 falls past the 40% total; the second is scaled into it.
        Queue<float> draws = new([90f, 50f]);

        Assert.Equal(42u, store.Roll(1, draws.Dequeue));
    }

    /// <summary>The running sum picks the right outcome.</summary>
    [Fact]
    public void TheRunningSum_PicksTheRightOutcome()
    {
        ItemEnchantmentStore store = Rolls(
            (1, 10, 30f),
            (1, 20, 30f),
            (1, 30, 40f));

        Assert.Equal(10u, store.Roll(1, () => 0f));
        Assert.Equal(20u, store.Roll(1, () => 30f));
        Assert.Equal(30u, store.Roll(1, () => 60f));
    }

    /// <summary>An unknown entry rolls nothing.</summary>
    [Fact]
    public void AnUnknownEntry_RollsNothing() =>
        Assert.Equal(0u, Rolls((1, 10, 100f)).Roll(999, () => 0f));

    // ------------------------------------------------------------------ the sign

    /// <summary>
    /// A RandomProperty item gets a positive id; a RandomSuffix item a negative one.
    /// </summary>
    /// <remarks>
    /// <b>The sign is what picks the table.</b> Both tables have rows at low ids, so reading the
    /// sign wrong finds a real row and applies a completely different suffix.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheSign_PicksTheTable()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        uint propertyId = stores.ItemRandomProperties.Entries.First().Id;
        uint suffixId = stores.ItemRandomSuffixes.Entries.First().Id;

        ItemEnchantmentStore rolls = Rolls(
            (1, propertyId, 100f),
            (2, suffixId, 100f));

        int property = ItemRandomProperties.Generate(
            Template(randomProperty: 1), rolls,
            stores.ItemRandomProperties, stores.ItemRandomSuffixes, () => 0f);

        int suffix = ItemRandomProperties.Generate(
            Template(randomSuffix: 2), rolls,
            stores.ItemRandomProperties, stores.ItemRandomSuffixes, () => 0f);

        Assert.True(property > 0);
        Assert.True(suffix < 0);
    }

    /// <summary>An item with neither column gets nothing.</summary>
    [RequiresClientDataFact]
    public void AnItemWithNeitherColumn_GetsNothing()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            0,
            ItemRandomProperties.Generate(
                Template(), Rolls(), stores.ItemRandomProperties, stores.ItemRandomSuffixes, () => 0f));
    }

    /// <summary>
    /// An item setting both columns gets nothing, rather than one of them.
    /// </summary>
    /// <remarks>
    /// Malformed data. Upstream refuses it rather than picking, because there is no way to know
    /// which was meant — and quietly picking one bakes a guess into every such item.
    /// </remarks>
    [RequiresClientDataFact]
    public void AnItemSettingBoth_GetsNothing()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        Assert.Equal(
            0,
            ItemRandomProperties.Generate(
                Template(randomProperty: 1, randomSuffix: 2), Rolls(),
                stores.ItemRandomProperties, stores.ItemRandomSuffixes, () => 0f));
    }

    // ------------------------------------------------------------------ scaling

    /// <summary>
    /// A suffix allocation is in ten-thousandths.
    /// </summary>
    /// <remarks>
    /// A row holding 3,200 means 32% of the item's points. Using it raw gives ten thousand times
    /// too much of every stat — large enough to read as a different bug entirely.
    /// </remarks>
    [Fact]
    public void AnAllocation_IsInTenThousandths()
    {
        SpellItemEnchantmentEntry enchantment = new(
            Id: 1, Charges: 0, Types: [EnchantmentEffect.Stat, 0, 0], Amounts: [0, 0, 0],
            SpellIds: [0, 0, 0], AuraId: 0, Slot: 0, GemItemId: 0,
            RequiredSkill: 0, RequiredSkillValue: 0, RequiredLevel: 0, Name: "of the Bear");

        // 32% of 44 points is 14, not 140,800.
        Assert.Equal(14, Enchantments.AmountFor(enchantment, 0, suffixFactor: 44, [3200u]));
    }

    /// <summary>An enchantment outside a scaled suffix uses its own flat amount.</summary>
    [Fact]
    public void AFlatEnchantment_UsesItsOwnAmount()
    {
        SpellItemEnchantmentEntry enchantment = new(
            Id: 1, Charges: 0, Types: [EnchantmentEffect.Stat, 0, 0], Amounts: [7, 0, 0],
            SpellIds: [0, 0, 0], AuraId: 0, Slot: 0, GemItemId: 0,
            RequiredSkill: 0, RequiredSkillValue: 0, RequiredLevel: 0, Name: "Flat");

        Assert.Equal(7, Enchantments.AmountFor(enchantment, 0, suffixFactor: 0));
    }

    /// <summary>
    /// Bags, tabards and relics have no random properties at all.
    /// </summary>
    /// <remarks>
    /// They return -1 rather than 0, because 0 is a real coefficient — the one chests use. Falling
    /// through to it would give a tabard a chest's worth of stats.
    /// </remarks>
    [Theory]
    [InlineData(0)]   // non-equip
    [InlineData(18)]  // bag
    [InlineData(19)]  // tabard
    [InlineData(24)]  // ammo
    [InlineData(27)]  // quiver
    [InlineData(28)]  // relic
    public void TypesWithoutPoints_ReturnMinusOne(byte inventoryType) =>
        Assert.Equal(-1, ItemRandomProperties.CoefficientFor(inventoryType));

    /// <summary>A chest and a two-hander share a coefficient; a ring and a cloak share another.</summary>
    /// <remarks>
    /// The mapping is not the inventory type itself, which is the obvious reading and gives every
    /// slot the wrong column.
    /// </remarks>
    [Fact]
    public void UnrelatedSlots_ShareCoefficients()
    {
        Assert.Equal(
            ItemRandomProperties.CoefficientFor(5), ItemRandomProperties.CoefficientFor(17));
        Assert.Equal(
            ItemRandomProperties.CoefficientFor(11), ItemRandomProperties.CoefficientFor(16));
        Assert.NotEqual(
            ItemRandomProperties.CoefficientFor(5), ItemRandomProperties.CoefficientFor(11));
    }

    /// <summary>
    /// Only the arithmetic effects are resolvable without a spell system.
    /// </summary>
    /// <remarks>
    /// Reporting a proc as applied when nothing casts it makes an enchanted weapon look correct and
    /// do nothing, which is worse than saying so.
    /// </remarks>
    [Fact]
    public void OnlyTheArithmeticEffects_AreResolvable()
    {
        Assert.True(Enchantments.IsResolvable(EnchantmentEffect.Stat));
        Assert.True(Enchantments.IsResolvable(EnchantmentEffect.Resistance));
        Assert.True(Enchantments.IsResolvable(EnchantmentEffect.Damage));

        Assert.False(Enchantments.IsResolvable(EnchantmentEffect.CombatSpell));
        Assert.False(Enchantments.IsResolvable(EnchantmentEffect.EquipSpell));
        Assert.False(Enchantments.IsResolvable(EnchantmentEffect.UseSpell));
    }

    // ------------------------------------------------------------------ onto the item

    /// <summary>
    /// A rolled suffix lands in the property slots, not the enchant ones.
    /// </summary>
    /// <remarks>
    /// Slots 0 to 6 belong to enchanters and gems. Writing a suffix into slot 0 would be wiped by
    /// the first enchant applied — and would wipe an existing enchant on the way in.
    /// </remarks>
    [RequiresClientDataFact]
    public void ARolledSuffix_LandsInThePropertySlots()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        ItemRandomSuffixEntry suffix = stores.ItemRandomSuffixes.Entries
            .First(entry => entry.Enchantments[0] != 0);

        Item item = Item.Create(1, Template(randomSuffix: 2));

        RandomSuffixes.Restore(
            item, -(int)suffix.Id, stores.ItemRandomProperties, stores.ItemRandomSuffixes,
            stores.RandomPropertyPoints);

        Assert.Equal(suffix.Enchantments[0], item.GetEnchantment(EnchantmentSlot.Property0));
        Assert.Equal(0u, item.GetEnchantment(EnchantmentSlot.Permanent));
    }

    /// <summary>
    /// Each enchantment slot is three words wide.
    /// </summary>
    /// <remarks>
    /// The id, then a duration and a charge count. Writing one word per slot puts every enchant
    /// past the first into the previous one's duration field — which the client reads as a
    /// temporary enchant about to expire.
    /// </remarks>
    [Fact]
    public void EachSlot_IsThreeWordsWide()
    {
        Item item = Item.Create(1, Template());

        item.SetEnchantment(EnchantmentSlot.Permanent, 100);
        item.SetEnchantment(EnchantmentSlot.Temporary, 200);

        Assert.Equal(100u, item.GetEnchantment(EnchantmentSlot.Permanent));
        Assert.Equal(200u, item.GetEnchantment(EnchantmentSlot.Temporary));
    }

    /// <summary>
    /// The random-properties id survives as a negative number.
    /// </summary>
    /// <remarks>
    /// Stored in an unsigned field and read back signed. Reading it unsigned turns every scaled
    /// suffix into a four-billion-ish property id that resolves to nothing at all.
    /// </remarks>
    [Fact]
    public void ANegativeId_SurvivesTheField()
    {
        Item item = Item.Create(1, Template()) ;

        item.RandomPropertiesId = -42;

        Assert.Equal(-42, item.RandomPropertiesId);
    }

    /// <summary>
    /// A scaled suffix gets a factor; a fixed one does not.
    /// </summary>
    /// <remarks>
    /// Setting a factor on a fixed suffix has the client scale amounts that are already final.
    /// </remarks>
    [RequiresClientDataFact]
    public void OnlyAScaledSuffix_GetsAFactor()
    {
        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        ItemRandomSuffixEntry suffix = stores.ItemRandomSuffixes.Entries.First();
        ItemRandomPropertiesEntry property = stores.ItemRandomProperties.Entries.First();

        Item scaled = Item.Create(1, Template(randomSuffix: 2));
        Item fixedOne = Item.Create(2, Template(randomProperty: 1));

        RandomSuffixes.Restore(
            scaled, -(int)suffix.Id, stores.ItemRandomProperties, stores.ItemRandomSuffixes,
            stores.RandomPropertyPoints);
        RandomSuffixes.Restore(
            fixedOne, (int)property.Id, stores.ItemRandomProperties, stores.ItemRandomSuffixes,
            stores.RandomPropertyPoints);

        Assert.NotEqual(0u, scaled.SuffixFactor);
        Assert.Equal(0u, fixedOne.SuffixFactor);
    }

    // ------------------------------------------------------------------ helpers

    private static ItemEnchantmentStore Rolls(params (uint Entry, uint Ench, float Chance)[] rows)
    {
        ItemEnchantmentStore store = new();

        System.Reflection.FieldInfo field = typeof(ItemEnchantmentStore)
            .GetField("_byEntry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        Dictionary<uint, List<EnchantmentChance>> byEntry =
            (Dictionary<uint, List<EnchantmentChance>>)field.GetValue(store)!;

        foreach ((uint entry, uint ench, float chance) in rows)
        {
            if (!byEntry.TryGetValue(entry, out List<EnchantmentChance>? outcomes))
            {
                byEntry[entry] = outcomes = [];
            }

            outcomes.Add(new EnchantmentChance(ench, chance));
        }

        return store;
    }

    private static ItemTemplate Template(int randomProperty = 0, uint randomSuffix = 0) =>
        ItemFixture.Build(entry: 1, name: "Sword") with
        {
            RandomProperty = randomProperty,
            RandomSuffix = randomSuffix,
            InventoryType = 13,
            ItemLevel = 60,
            Quality = 3,
        };
}
