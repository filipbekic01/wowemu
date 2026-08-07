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

/// <summary>
/// What agility and intellect are worth, before any gear ratings.
/// </summary>
/// <remarks>
/// Port of <c>Player::GetMeleeCritFromAgility</c>, <c>GetDodgeFromAgility</c> and
/// <c>GetSpellCritFromIntellect</c>. This is the half of a character's defences that comes from
/// their body rather than their gear — an ungeared level-80 rogue still dodges, and without this
/// the character sheet reads zero for everyone who has not been handed a rating.
/// </remarks>
public sealed class AttributeChanceTable
{
    private readonly DbcStore<GameTableFloat> _meleeCritBase;
    private readonly DbcStore<GameTableFloat> _meleeCritRatio;
    private readonly DbcStore<GameTableFloat> _spellCritBase;
    private readonly DbcStore<GameTableFloat> _spellCritRatio;

    public AttributeChanceTable(
        DbcStore<GameTableFloat> meleeCritBase,
        DbcStore<GameTableFloat> meleeCritRatio,
        DbcStore<GameTableFloat> spellCritBase,
        DbcStore<GameTableFloat> spellCritRatio)
    {
        ArgumentNullException.ThrowIfNull(meleeCritBase);
        ArgumentNullException.ThrowIfNull(meleeCritRatio);
        ArgumentNullException.ThrowIfNull(spellCritBase);
        ArgumentNullException.ThrowIfNull(spellCritRatio);

        _meleeCritBase = meleeCritBase;
        _meleeCritRatio = meleeCritRatio;
        _spellCritBase = spellCritBase;
        _spellCritRatio = spellCritRatio;
    }

    /// <summary>Nothing loaded, for callers that must run without a client extracted.</summary>
    public static AttributeChanceTable Empty { get; } = new(
        DbcStore<GameTableFloat>.Empty,
        DbcStore<GameTableFloat>.Empty,
        DbcStore<GameTableFloat>.Empty,
        DbcStore<GameTableFloat>.Empty);

    /// <summary>Melee crit chance from agility, as a percentage.</summary>
    public float MeleeCrit(byte characterClass, byte level, uint agility) =>
        FromAttribute(_meleeCritBase, _meleeCritRatio, characterClass, level, agility);

    /// <summary>Spell crit chance from intellect, as a percentage.</summary>
    public float SpellCrit(byte characterClass, byte level, uint intellect) =>
        FromAttribute(_spellCritBase, _spellCritRatio, characterClass, level, intellect);

    /// <summary>
    /// Dodge chance from agility, as a percentage.
    /// </summary>
    /// <remarks>
    /// <b>Dodge has no table of its own.</b> It reuses the melee crit ratio, scaled by a per-class
    /// constant — a rogue converts agility to dodge more than twice as well as a warrior. The 1.15
    /// divisor is patch 3.2.0 raising the agility needed across the board, and it is folded into the
    /// constants rather than applied separately, exactly as upstream writes them.
    /// <para>
    /// Upstream splits the result into a diminishing part (from gear agility) and a
    /// non-diminishing part (from base agility); we have no diminishing returns, so the whole of it
    /// is computed together. That overstates dodge for a heavily geared character, which is the
    /// direction to be aware of rather than a rounding difference.
    /// </para>
    /// </remarks>
    public float Dodge(byte characterClass, byte level, uint agility)
    {
        if (RatioFor(_meleeCritRatio, characterClass, level) is not { } ratio
            || ClassIndex(characterClass) is not { } index
            || index >= DodgeBase.Length)
        {
            return 0f;
        }

        return 100f * (DodgeBase[index] + (agility * ratio * CritToDodge[index]));
    }

    private static float FromAttribute(
        DbcStore<GameTableFloat> baseTable,
        DbcStore<GameTableFloat> ratioTable,
        byte characterClass,
        byte level,
        uint attribute)
    {
        if (ClassIndex(characterClass) is not { } index
            || !baseTable.TryGet((uint)index, out GameTableFloat? classBase) || classBase is null
            || RatioFor(ratioTable, characterClass, level) is not { } ratio)
        {
            return 0f;
        }

        return (classBase.Value + (attribute * ratio)) * 100f;
    }

    /// <summary>The per-level ratio for a class, or null when it is not in the table.</summary>
    private static float? RatioFor(DbcStore<GameTableFloat> table, byte characterClass, byte level)
    {
        if (ClassIndex(characterClass) is not { } index || level == 0)
        {
            return null;
        }

        uint capped = Math.Min(level, (byte)CombatRatingTable.MaxLevel);
        uint row = ((uint)index * CombatRatingTable.MaxLevel) + capped - 1;

        return table.TryGet(row, out GameTableFloat? entry) && entry is not null ? entry.Value : null;
    }

    private static int? ClassIndex(byte characterClass) =>
        characterClass is > 0 and <= Classes ? characterClass - 1 : null;

    /// <summary>How many class slots the tables carry. <c>MAX_CLASSES</c>.</summary>
    private const int Classes = 11;

    /// <summary>
    /// Flat dodge per class, before agility. Index 9 is the gap where no class exists.
    /// </summary>
    /// <remarks>
    /// A hunter's is <b>negative</b>. Clamping it to zero as an obvious tidy-up gives hunters four
    /// percent of dodge they should not have.
    /// </remarks>
    private static readonly float[] DodgeBase =
    [
        0.036640f,  // Warrior
        0.034943f,  // Paladin
        -0.040873f, // Hunter
        0.020957f,  // Rogue
        0.034178f,  // Priest
        0.036640f,  // Death knight
        0.021080f,  // Shaman
        0.036587f,  // Mage
        0.024211f,  // Warlock
        0.0f,       // no class 10
        0.056097f,  // Druid
    ];

    /// <summary>How well each class turns crit-per-agility into dodge-per-agility.</summary>
    private static readonly float[] CritToDodge =
    [
        0.85f / 1.15f,  // Warrior
        1.00f / 1.15f,  // Paladin
        1.11f / 1.15f,  // Hunter
        2.00f / 1.15f,  // Rogue
        1.00f / 1.15f,  // Priest
        0.85f / 1.15f,  // Death knight
        1.60f / 1.15f,  // Shaman
        1.00f / 1.15f,  // Mage
        0.97f / 1.15f,  // Warlock
        0.0f,           // no class 10
        2.00f / 1.15f,  // Druid
    ];
}

/// <summary>One bare float from a <c>gt*</c> table, addressed by its row's position.</summary>
public sealed record GameTableFloat(float Value);

/// <summary>One row of <c>gtOCTClassCombatRatingScalar.dbc</c>, which does have an id column.</summary>
public sealed record GameTableScalar(uint Id, float Value);
