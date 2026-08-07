namespace WowEmu.Data.Client;

/// <summary>The twenty-five combat ratings, in field order. <c>CombatRating</c>.</summary>
public static class CombatRating
{
    public const int WeaponSkill = 0;
    public const int DefenseSkill = 1;
    public const int Dodge = 2;
    public const int Parry = 3;
    public const int Block = 4;
    public const int HitMelee = 5;
    public const int HitRanged = 6;
    public const int HitSpell = 7;
    public const int CritMelee = 8;
    public const int CritRanged = 9;
    public const int CritSpell = 10;
    public const int HitTakenMelee = 11;
    public const int HitTakenRanged = 12;
    public const int HitTakenSpell = 13;
    public const int CritTakenMelee = 14;
    public const int CritTakenRanged = 15;
    public const int CritTakenSpell = 16;
    public const int HasteMelee = 17;
    public const int HasteRanged = 18;
    public const int HasteSpell = 19;
    public const int WeaponSkillMainHand = 20;
    public const int WeaponSkillOffHand = 21;
    public const int WeaponSkillRanged = 22;
    public const int Expertise = 23;
    public const int ArmorPenetration = 24;

    /// <summary>How many there are. <c>MAX_COMBAT_RATING</c>.</summary>
    public const int Count = 25;
}

/// <summary>
/// Turning a combat rating into the percentage it is worth.
/// </summary>
/// <remarks>
/// Port of <c>Player::GetRatingMultiplier</c> and <c>GetRatingBonusValue</c>. A rating is not a
/// percentage — 45.9 crit rating is one percent of crit at level 80 and roughly two percent at 70.
/// The whole point of the table is that gear gets weaker as you out-level it without the numbers on
/// it changing.
/// <para>
/// Two tables multiply together. <c>gtCombatRatings</c> gives the divisor for a rating at a level;
/// <c>gtOCTClassCombatRatingScalar</c> gives a per-class multiplier on top, which is 1.0 for most
/// combinations and <b>is not</b> for forty-six of them — warriors get 1.1× on armour penetration
/// and paladins 1.3× on melee haste. Skipping it looks harmless and quietly under-rewards those.
/// </para>
/// </remarks>
public sealed class CombatRatingTable
{
    private readonly DbcStore<GameTableFloat> _divisors;
    private readonly DbcStore<GameTableScalar> _classScalars;

    public CombatRatingTable(DbcStore<GameTableFloat> divisors, DbcStore<GameTableScalar> classScalars)
    {
        ArgumentNullException.ThrowIfNull(divisors);
        ArgumentNullException.ThrowIfNull(classScalars);

        _divisors = divisors;
        _classScalars = classScalars;
    }

    /// <summary>Nothing loaded, for callers that must run without a client extracted.</summary>
    public static CombatRatingTable Empty { get; } =
        new(DbcStore<GameTableFloat>.Empty, DbcStore<GameTableScalar>.Empty);

    /// <summary>The highest level the tables describe. <c>GT_MAX_LEVEL</c>.</summary>
    public const int MaxLevel = 100;

    /// <summary>How many rating columns each class block has. <c>GT_MAX_RATING</c>.</summary>
    /// <remarks>
    /// Thirty-two, not twenty-five. The tables leave room the game never used, and striding by 25
    /// walks into the next class's numbers a little further along every block.
    /// </remarks>
    public const int RatingStride = 32;

    /// <summary>
    /// How much one point of a rating is worth, as a percentage.
    /// </summary>
    /// <returns>Zero when the tables are not loaded, so nothing gets a silent bonus.</returns>
    /// <remarks>
    /// Upstream returns 1.0 when a row is missing, which is a hundred times too generous — one point
    /// of crit rating would be one percent of crit. Zero is the safer failure: gear stops helping
    /// rather than becoming absurd, and the difference is visible rather than plausible.
    /// </remarks>
    public float MultiplierFor(int rating, byte level, byte characterClass)
    {
        if (rating < 0 || rating >= CombatRating.Count || level == 0 || characterClass == 0)
        {
            return 0f;
        }

        uint capped = Math.Min(level, (byte)MaxLevel);

        // rating × 100 + level - 1, which is how the flat table is addressed.
        uint divisorIndex = ((uint)rating * MaxLevel) + capped - 1;

        if (!_divisors.TryGet(divisorIndex, out GameTableFloat? divisor)
            || divisor is null || divisor.Value == 0f)
        {
            return 0f;
        }

        // The scalar table has a real id column and it is 1-based, hence the + 1.
        uint scalarId = ((uint)(characterClass - 1) * RatingStride) + (uint)rating + 1;

        if (!_classScalars.TryGet(scalarId, out GameTableScalar? scalar) || scalar is null)
        {
            return 0f;
        }

        return scalar.Value / divisor.Value;
    }

    /// <summary>What a stored rating is worth, as a percentage.</summary>
    public float BonusFor(int rating, uint amount, byte level, byte characterClass) =>
        amount * MultiplierFor(rating, level, characterClass);
}

/// <summary>One bare float from a <c>gt*</c> table, addressed by its row's position.</summary>
public sealed record GameTableFloat(float Value);

/// <summary>One row of <c>gtOCTClassCombatRatingScalar.dbc</c>, which does have an id column.</summary>
public sealed record GameTableScalar(uint Id, float Value);
