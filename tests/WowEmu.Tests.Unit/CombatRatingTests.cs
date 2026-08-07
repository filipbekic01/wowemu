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
