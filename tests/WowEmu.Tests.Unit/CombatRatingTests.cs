using WowEmu.Data.Client;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Turning a combat rating into the percentage it is worth.
/// </summary>
/// <remarks>
/// A rating is not a percentage. 45.9 crit rating is one percent of crit at level 80 and roughly
/// two percent at 70 — the whole point of the table is that gear gets weaker as you out-level it
/// without the numbers printed on it changing.
/// </remarks>
public sealed class CombatRatingTests
{
    /// <summary>
    /// The divisors are the published 3.3.5 constants.
    /// </summary>
    /// <remarks>
    /// These numbers are widely documented, which makes them a real check on the table layout
    /// rather than a restatement of it: the <c>gt*</c> files carry no id column, so the row's
    /// ordinal is its index, and an off-by-one in that arithmetic would still produce plausible
    /// floats. Reproducing 45.91 for crit and 32.79 for hit at level 80 says the addressing is right.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheDivisors_AreThePublishedConstants()
    {
        CombatRatingTable table = Load();

        // 1 / multiplier is the rating needed for one percent.
        Assert.Equal(45.91f, RatingPerPercent(table, CombatRating.CritMelee, level: 80), 0.01f);
        Assert.Equal(32.79f, RatingPerPercent(table, CombatRating.HitMelee, level: 80), 0.01f);
        Assert.Equal(8.197f, RatingPerPercent(table, CombatRating.Expertise, level: 80), 0.01f);
        Assert.Equal(45.25f, RatingPerPercent(table, CombatRating.Dodge, level: 80), 0.01f);
    }

    /// <summary>
    /// The same rating is worth less at a higher level.
    /// </summary>
    /// <remarks>
    /// The mechanic itself. If this came out flat, the table would be being read at one level for
    /// every query and nothing would look wrong until someone compared two characters.
    /// </remarks>
    [RequiresClientDataFact]
    public void ARating_IsWorthLessAtAHigherLevel()
    {
        CombatRatingTable table = Load();

        float atSeventy = table.MultiplierFor(CombatRating.CritMelee, 70, Warrior);
        float atEighty = table.MultiplierFor(CombatRating.CritMelee, 80, Warrior);

        Assert.True(atSeventy > atEighty, $"{atSeventy} should exceed {atEighty}");
    }

    /// <summary>
    /// The per-class scalar is real, and it differs between classes.
    /// </summary>
    /// <remarks>
    /// Forty-six of the 352 rows are not 1.0, and they are not spread evenly: <b>every</b> class
    /// gets 1.1 on armour penetration, while melee haste is 1.3 for paladins, death knights and
    /// shamans alone. Skipping the scalar looks harmless — most combinations do read 1.0 — and
    /// quietly under-rewards exactly the ones that matter.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheClassScalar_DiffersBetweenClasses()
    {
        CombatRatingTable table = Load();

        float paladin = table.MultiplierFor(CombatRating.HasteMelee, 80, Paladin);
        float warrior = table.MultiplierFor(CombatRating.HasteMelee, 80, Warrior);

        Assert.Equal(1.3f, paladin / warrior, 0.001f);
    }

    /// <summary>
    /// And it is applied at all — armour penetration carries 1.1 for everyone.
    /// </summary>
    /// <remarks>
    /// A separate check from the one above, because a scalar that was read but never multiplied in
    /// would still make two classes differ if the divisor happened to. This compares a rating that
    /// carries a scalar against one that does not, for the same class.
    /// </remarks>
    [RequiresClientDataFact]
    public void TheClassScalar_IsActuallyMultipliedIn()
    {
        CombatRatingTable table = Load();

        // Rating ÷ multiplier is the raw divisor from the flat table. Armour penetration's carries a
        // 1.1 scalar on top, so the effective figure is 1.1 times what the divisor alone would give.
        float withScalar = 1f / table.MultiplierFor(CombatRating.ArmorPenetration, 80, Warrior);
        float rawDivisor = RawDivisor(CombatRating.ArmorPenetration, 80);

        Assert.Equal(rawDivisor / 1.1f, withScalar, 0.01f);
    }

    /// <summary>A rating is worth its amount times the multiplier.</summary>
    [RequiresClientDataFact]
    public void ABonus_IsTheAmountTimesTheMultiplier()
    {
        CombatRatingTable table = Load();

        // 45.91 crit rating is one percent at 80, so twice that is two.
        float percent = table.BonusFor(CombatRating.CritMelee, 92, 80, Warrior);

        Assert.Equal(2f, percent, 0.02f);
    }

    /// <summary>
    /// A level past the table's end is clamped rather than falling off it.
    /// </summary>
    /// <remarks>
    /// The tables stop at 100. Reading past the end lands in the next rating's block, so a level-101
    /// character would get a number from the wrong row rather than an obviously wrong one.
    /// </remarks>
    [RequiresClientDataFact]
    public void ALevelPastTheEnd_IsClamped()
    {
        CombatRatingTable table = Load();

        Assert.Equal(
            table.MultiplierFor(CombatRating.CritMelee, 100, Warrior),
            table.MultiplierFor(CombatRating.CritMelee, 255, Warrior));
    }

    /// <summary>
    /// With no tables loaded a rating is worth nothing, not everything.
    /// </summary>
    /// <remarks>
    /// Upstream returns 1.0 for a missing row, which is a hundred times too generous — one point of
    /// crit rating would be one percent of crit. Zero is the safer failure: gear stops helping
    /// rather than becoming absurd.
    /// </remarks>
    [Fact]
    public void WithNoTables_ARatingIsWorthNothing() =>
        Assert.Equal(0f, CombatRatingTable.Empty.MultiplierFor(CombatRating.CritMelee, 80, Warrior));

    /// <summary>
    /// Agility gives crit, at the documented per-class rate.
    /// </summary>
    /// <remarks>
    /// The published level-80 figures, and they differ per class: 62.5 agility per one percent of
    /// crit for a warrior, 83.33 for a rogue, 51.02 for a mage. They check the class-block
    /// addressing rather than restate it — reading the wrong class's block still yields a number
    /// that looks entirely believable.
    /// </remarks>
    [RequiresClientDataFact]
    public void Agility_GivesCritAtTheDocumentedRate()
    {
        AttributeChanceTable table = Attributes();

        Assert.Equal(62.5f, AgilityPerCritPercent(table, Warrior), 0.05f);
        Assert.Equal(83.33f, AgilityPerCritPercent(table, Rogue), 0.05f);
        Assert.Equal(51.02f, AgilityPerCritPercent(table, Mage), 0.05f);
    }

    /// <summary>
    /// And dodge, which a rogue converts far better than a warrior.
    /// </summary>
    /// <remarks>
    /// Dodge has no table of its own — it reuses the crit ratio scaled by a per-class constant.
    /// <b>Both terms differ per class</b>, so the ratio between two classes is not simply the ratio
    /// of their constants: a rogue's 2.00 against a warrior's 0.85 would suggest 2.35×, but the
    /// rogue's crit ratio is lower, and the real answer is 1.76×. Asserting the constants' ratio is
    /// the mistake this test exists to have already made.
    /// </remarks>
    [RequiresClientDataFact]
    public void Agility_GivesDodgeAtAClassSpecificRate()
    {
        AttributeChanceTable table = Attributes();

        float rogue = PerPointOfDodge(table, Rogue);
        float warrior = PerPointOfDodge(table, Warrior);

        // 0.020870 against 0.011826 per point of agility.
        Assert.Equal(0.020870f, rogue, 0.000001f);
        Assert.Equal(0.011826f, warrior, 0.000001f);
        Assert.True(rogue > warrior);
    }

    private static float PerPointOfDodge(AttributeChanceTable table, byte characterClass) =>
        (table.Dodge(characterClass, 80, 1000) - table.Dodge(characterClass, 80, 0)) / 1000f;

    /// <summary>
    /// A hunter's flat dodge is negative, and stays negative.
    /// </summary>
    /// <remarks>
    /// The obvious tidy-up — clamping a base value to zero — hands hunters roughly four percent of
    /// dodge they should not have.
    /// </remarks>
    [RequiresClientDataFact]
    public void AHuntersFlatDodge_IsNegative()
    {
        Assert.True(Attributes().Dodge(Hunter, 80, 0) < 0f);
    }

    /// <summary>Intellect gives spell crit, and it is not the same as the melee figure.</summary>
    [RequiresClientDataFact]
    public void Intellect_GivesSpellCrit()
    {
        AttributeChanceTable table = Attributes();

        float spell = table.SpellCrit(Mage, 80, 1000);
        float melee = table.MeleeCrit(Mage, 80, 1000);

        Assert.True(spell > 0f);
        Assert.NotEqual(melee, spell);
    }

    /// <summary>With no tables loaded, an attribute is worth nothing rather than something.</summary>
    [Fact]
    public void WithNoTables_AttributesAreWorthNothing()
    {
        Assert.Equal(0f, AttributeChanceTable.Empty.MeleeCrit(Warrior, 80, 1000));
        Assert.Equal(0f, AttributeChanceTable.Empty.Dodge(Warrior, 80, 1000));
    }

    private const byte Hunter = 3;
    private const byte Rogue = 4;
    private const byte Mage = 8;

    private static AttributeChanceTable Attributes() =>
        DbcStores.Load(ClientData.DbcDirectory).AttributeChances;

    /// <summary>How much agility buys one percent of crit — the figure players quote.</summary>
    private static float AgilityPerCritPercent(AttributeChanceTable table, byte characterClass)
    {
        float perPoint = table.MeleeCrit(characterClass, 80, 1000) - table.MeleeCrit(characterClass, 80, 0);

        return 1000f / perPoint;
    }

    private const byte Warrior = 1;
    private const byte Paladin = 2;

    /// <summary>The divisor straight out of the flat table, with no class scalar applied.</summary>
    private static float RawDivisor(int rating, byte level)
    {
        DbcStore<GameTableFloat> divisors = DbcStore<GameTableFloat>.LoadByOrdinal(
            Path.Combine(ClientData.DbcDirectory, "gtCombatRatings.dbc"),
            "f",
            (in DbcRecord record) => new GameTableFloat(record.GetFloat(0)));

        uint index = ((uint)rating * CombatRatingTable.MaxLevel) + level - 1;

        return divisors.Get(index).Value;
    }

    private static CombatRatingTable Load() => DbcStores.Load(ClientData.DbcDirectory).CombatRatings;

    private static float RatingPerPercent(CombatRatingTable table, int rating, byte level) =>
        1f / table.MultiplierFor(rating, level, Warrior);
}
