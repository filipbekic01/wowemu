namespace WowEmu.Data.Client;

/// <summary>How many effects one spell can carry. <c>MAX_SPELL_EFFECTS</c>.</summary>
/// <remarks>
/// Exactly three, in every 3.3.5a spell. Not a limit that can be raised — the DBC has three of every
/// per-effect column, so the number is baked into the file layout.
/// </remarks>
public static class SpellConstants
{
    public const int MaxEffects = 3;
}

/// <summary>One of a spell's three effects.</summary>
/// <param name="Effect">What it does — <c>SpellEffects</c>. Zero means the slot is unused.</param>
/// <param name="DieSides">The spread added on top of <paramref name="BasePoints"/>.</param>
/// <param name="RealPointsPerLevel">How much the effect grows per level above the spell's base.</param>
/// <param name="BasePoints">
/// The effect's magnitude, <b>one less than the minimum</b>. See <see cref="SpellEffectEntry.MinValue"/>.
/// </param>
/// <param name="ImplicitTargetA">Primary target mode — <c>Targets</c>.</param>
/// <param name="ImplicitTargetB">Secondary target mode, usually paired with a radius.</param>
/// <param name="RadiusIndex">Row in <c>SpellRadius.dbc</c>, for area effects.</param>
/// <param name="ApplyAuraName">Which aura it applies, when the effect is an aura application.</param>
/// <param name="Amplitude">Milliseconds between ticks, for a periodic aura.</param>
/// <param name="ChainTarget">How many extra targets it jumps to.</param>
/// <param name="ItemType">The item it creates, if it creates one.</param>
/// <param name="MiscValue">Effect-specific. A school, a stat, a mechanic — depends entirely on <paramref name="Effect"/>.</param>
/// <param name="MiscValueB">As above, for effects that need two.</param>
/// <param name="TriggerSpell">A spell this effect casts in turn.</param>
public readonly record struct SpellEffectEntry(
    uint Effect,
    int DieSides,
    float RealPointsPerLevel,
    int BasePoints,
    uint ImplicitTargetA,
    uint ImplicitTargetB,
    uint RadiusIndex,
    uint ApplyAuraName,
    uint Amplitude,
    uint ChainTarget,
    uint ItemType,
    int MiscValue,
    int MiscValueB,
    uint TriggerSpell)
{
    /// <summary>Whether this slot does anything at all.</summary>
    public bool IsUsed => Effect != 0;

    /// <summary>
    /// The smallest value this effect can roll, before level scaling.
    /// </summary>
    /// <remarks>
    /// <b><c>BasePoints</c> is stored one less than the minimum.</b> The client rolls
    /// <c>basePoints + 1 … basePoints + dieSides</c>, so a spell that hits for 10-14 is stored as
    /// base 9 with 5 sides. Reading the column as the minimum makes every spell in the game hit for
    /// one less than it should — small, uniform, and invisible without a reference to compare to.
    /// </remarks>
    public int MinValue => BasePoints + 1;

    /// <summary>The largest value this effect can roll, before level scaling.</summary>
    public int MaxValue => BasePoints + Math.Max(DieSides, 1);
}

/// <summary>
/// One row of <c>Spell.dbc</c> — everything the server needs to cast something.
/// </summary>
/// <remarks>
/// A subset of the file's 234 columns. What is here is what casting a damage or heal spell reads;
/// reagents, totems, equipped-item requirements, stances and the reputation gates are all still in
/// the file and not yet in this record.
/// <para>
/// The indirections are deliberate and are upstream's: cast time, range and duration are <i>indices
/// into other DBCs</i>, not values. A spell that says <c>CastingTimeIndex = 1</c> is instant because
/// row 1 of <c>SpellCastTimes.dbc</c> says zero, not because 1 means instant.
/// </para>
/// </remarks>
public sealed record SpellEntry(
    uint Id,
    string Name,
    string Rank,
    uint[] Attributes,

    /// <summary>Row in <c>SpellCategory.dbc</c>. Shared cooldowns are keyed on it, not on the spell.</summary>
    uint Category,
    uint CastingTimeIndex,
    uint RecoveryTime,
    uint CategoryRecoveryTime,
    uint StartRecoveryCategory,
    uint StartRecoveryTime,
    uint InterruptFlags,
    uint Targets,
    uint PowerType,
    uint ManaCost,
    uint ManaCostPerLevel,
    uint ManaCostPercentage,
    uint RangeIndex,
    float Speed,
    uint DurationIndex,
    uint BaseLevel,
    uint SpellLevel,
    uint MaxLevel,
    uint SchoolMask,
    uint DmgClass,
    uint PreventionType,
    uint SpellFamilyName,
    uint MaxAffectedTargets,
    uint SpellIconId,
    uint SpellVisual,
    SpellEffectEntry[] Effects,

    /// <summary>
    /// How many times this aura may stack on one target. <c>m_cumulativeAura</c>.
    /// </summary>
    /// <remarks>
    /// <b>Zero means it does not stack</b>, not that it stacks zero times — most spells are zero,
    /// and reading it as a limit makes every aura in the game refuse to apply.
    /// <para>
    /// Defaulted, and last in the record, so the several test fixtures that build a spell
    /// positionally keep working. A parameter in the middle would have been a rename of everything
    /// after it.
    /// </para>
    /// </remarks>
    uint StackAmount = 0,

    /// <summary>
    /// How many times the aura fires before it is used up, or 0 for no limit.
    /// </summary>
    /// <remarks>
    /// Zero is unlimited here too, and for the same reason: the column is blank on everything that
    /// simply runs on a timer.
    /// </remarks>
    uint ProcCharges = 0)
{
    /// <summary>How many attribute words a spell carries. <c>Attributes</c> through <c>AttributesEx7</c>.</summary>
    public const int AttributeWords = 8;

    /// <summary>Whether a given attribute bit is set in one of the eight words.</summary>
    /// <param name="word">0 for <c>Attributes</c>, 1 for <c>AttributesEx</c>, and so on.</param>
    public bool HasAttribute(int word, uint bit) =>
        word >= 0 && word < Attributes.Length && (Attributes[word] & bit) != 0;

    /// <summary>The effect slots that do something.</summary>
    public IEnumerable<SpellEffectEntry> UsedEffects => Effects.Where(effect => effect.IsUsed);

    /// <summary>
    /// Whether the spell is a passive one, which is cast once and never shows a cast bar.
    /// </summary>
    /// <remarks><c>SPELL_ATTR0_PASSIVE</c>, bit 6 of the first attribute word.</remarks>
    public bool IsPassive => HasAttribute(0, 0x00000040);

    /// <summary>The name, with its rank if it has one — "Frostbolt (Rank 3)".</summary>
    public override string ToString() =>
        string.IsNullOrEmpty(Rank) ? Name : $"{Name} ({Rank})";
}

/// <summary>A row of <c>SpellCastTimes.dbc</c>.</summary>
/// <param name="Base">Milliseconds to cast. Zero is instant.</param>
public sealed record SpellCastTimesEntry(uint Id, int Base);

/// <summary>A row of <c>SpellRange.dbc</c>.</summary>
/// <remarks>
/// Four numbers, not two. Hostile and friendly ranges are separate, and so are the minimums — a
/// spell can be usable on an enemy at 30 yards and an ally at 40.
/// </remarks>
public sealed record SpellRangeEntry(uint Id, float MinRangeHostile, float MinRangeFriend, float MaxRangeHostile, float MaxRangeFriend);

/// <summary>A row of <c>SpellDuration.dbc</c>.</summary>
/// <param name="Base">Milliseconds at the spell's base level.</param>
/// <param name="PerLevel">Milliseconds added per level above it.</param>
/// <param name="Max">The ceiling, however high the caster's level.</param>
public sealed record SpellDurationEntry(uint Id, int Base, int PerLevel, int Max);

/// <summary>A row of <c>SpellRadius.dbc</c>.</summary>
public sealed record SpellRadiusEntry(uint Id, float RadiusMin, float RadiusPerLevel, float RadiusMax);

/// <summary>
/// <c>Spell.dbc</c> and the four small tables it points into.
/// </summary>
/// <remarks>
/// Loaded together because a spell is useless without them: its cast time, range and duration are
/// all indices into these, and resolving them one at a time at every call site is how a server ends
/// up passing a row id where it meant a number of milliseconds.
/// </remarks>
public sealed class SpellStores
{
    // Verbatim from src/server/shared/DataStores/DBCfmt.h.
    private const string SpellFormat =
        "niiiiiiiiiiiixixiiiiiiiiiiiiiiiiiiiiiiiiiiiiiiifxiiiiiiiiiiiiiiiiiiiiiiiiiiii"
        + "fffiiiiiiiiiiiiiiiiiiiiifffiiiiiiiiiiiiiiifffiiiiiiiiiiiiiissssssssssssssssx"
        + "ssssssssssssssssxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxiiiiiiiiiiixfffxxxiiiiixxfffxx";

    private const string CastTimesFormat = "nixx";
    private const string DurationFormat = "niii";
    private const string RadiusFormat = "nfff";
    private const string RangeFormat = "nffffixxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    private SpellStores(
        DbcStore<SpellEntry> spells,
        DbcStore<SpellCastTimesEntry> castTimes,
        DbcStore<SpellRangeEntry> ranges,
        DbcStore<SpellDurationEntry> durations,
        DbcStore<SpellRadiusEntry> radii)
    {
        Spells = spells;
        CastTimes = castTimes;
        Ranges = ranges;
        Durations = durations;
        Radii = radii;
    }

    public DbcStore<SpellEntry> Spells { get; }

    public DbcStore<SpellCastTimesEntry> CastTimes { get; }

    public DbcStore<SpellRangeEntry> Ranges { get; }

    public DbcStore<SpellDurationEntry> Durations { get; }

    public DbcStore<SpellRadiusEntry> Radii { get; }

    /// <summary>Milliseconds to cast a spell. Zero for instant, and for an index that is not there.</summary>
    /// <remarks>
    /// A missing row means instant rather than an exception: <c>CastingTimeIndex</c> is 0 on a great
    /// many spells and there is no row 0.
    /// </remarks>
    public int CastTimeMs(SpellEntry spell)
    {
        ArgumentNullException.ThrowIfNull(spell);

        return CastTimes.TryGet(spell.CastingTimeIndex, out SpellCastTimesEntry entry) ? entry.Base : 0;
    }

    /// <summary>How far away a spell can be cast, in yards.</summary>
    /// <remarks>
    /// Hostile and friendly maxima differ, so the caller has to say which it wants. Taking the
    /// hostile one for a heal makes some spells shorter-ranged than they should be.
    /// </remarks>
    public float MaxRange(SpellEntry spell, bool friendly = false)
    {
        ArgumentNullException.ThrowIfNull(spell);

        if (!Ranges.TryGet(spell.RangeIndex, out SpellRangeEntry entry))
        {
            return 0f;
        }

        return friendly ? entry.MaxRangeFriend : entry.MaxRangeHostile;
    }

    /// <inheritdoc cref="MaxRange"/>
    public float MinRange(SpellEntry spell, bool friendly = false)
    {
        ArgumentNullException.ThrowIfNull(spell);

        if (!Ranges.TryGet(spell.RangeIndex, out SpellRangeEntry entry))
        {
            return 0f;
        }

        return friendly ? entry.MinRangeFriend : entry.MinRangeHostile;
    }

    /// <summary>
    /// How long a spell's effect lasts, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Port of <c>SpellInfo::GetDuration</c>: <c>-1</c> passes through as itself and everything else
    /// is taken as an absolute value.
    /// <list type="bullet">
    /// <item><b>−1 means "until dispelled"</b> and must not be clamped. Clamping it to zero turns
    /// every permanent aura into one that expires the instant it is applied, which reads as auras
    /// not working rather than as a sign error.</item>
    /// <item><b>The per-level column is not applied.</b> It is a vanilla remnant: Wrath reads only
    /// <c>Duration[0]</c>. Scaling by it looks reasonable and is measurably wrong — several rows
    /// carry a base of 100,000 seconds against a maximum of 15, so a scaled reading would clamp to
    /// the maximum from level 1 and never move.</item>
    /// </list>
    /// </remarks>
    public int DurationMs(SpellEntry spell)
    {
        ArgumentNullException.ThrowIfNull(spell);

        if (!Durations.TryGet(spell.DurationIndex, out SpellDurationEntry entry))
        {
            return 0;
        }

        return entry.Base == -1 ? -1 : Math.Abs(entry.Base);
    }

    /// <summary>The longest a spell's effect can last, in milliseconds.</summary>
    /// <inheritdoc cref="DurationMs" path="/remarks"/>
    public int MaxDurationMs(SpellEntry spell)
    {
        ArgumentNullException.ThrowIfNull(spell);

        if (!Durations.TryGet(spell.DurationIndex, out SpellDurationEntry entry))
        {
            return 0;
        }

        return entry.Max == -1 ? -1 : Math.Abs(entry.Max);
    }

    /// <summary>Loads every spell store from a directory of extracted <c>.dbc</c> files.</summary>
    public static SpellStores Load(string directory, int locale = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        return new SpellStores(
            DbcStore<SpellEntry>.Load(
                Path.Combine(directory, "Spell.dbc"),
                SpellFormat,
                idField: 0,
                (in DbcRecord record) => ReadSpell(record, locale)),

            DbcStore<SpellCastTimesEntry>.Load(
                Path.Combine(directory, "SpellCastTimes.dbc"),
                CastTimesFormat,
                idField: 0,
                (in DbcRecord record) => new SpellCastTimesEntry(record.GetUInt32(0), record.GetInt32(1))),

            DbcStore<SpellRangeEntry>.Load(
                Path.Combine(directory, "SpellRange.dbc"),
                RangeFormat,
                idField: 0,
                (in DbcRecord record) => new SpellRangeEntry(
                    record.GetUInt32(0),
                    record.GetFloat(1),
                    record.GetFloat(2),
                    record.GetFloat(3),
                    record.GetFloat(4))),

            DbcStore<SpellDurationEntry>.Load(
                Path.Combine(directory, "SpellDuration.dbc"),
                DurationFormat,
                idField: 0,
                (in DbcRecord record) => new SpellDurationEntry(
                    record.GetUInt32(0),
                    record.GetInt32(1),
                    record.GetInt32(2),
                    record.GetInt32(3))),

            DbcStore<SpellRadiusEntry>.Load(
                Path.Combine(directory, "SpellRadius.dbc"),
                RadiusFormat,
                idField: 0,
                (in DbcRecord record) => new SpellRadiusEntry(
                    record.GetUInt32(0),
                    record.GetFloat(1),
                    record.GetFloat(2),
                    record.GetFloat(3))));
    }

    /// <summary>
    /// Reads one spell.
    /// </summary>
    /// <remarks>
    /// The column numbers are the struct's, and the struct's comments are the authority — note that
    /// <c>Stances</c> is documented as column 12 but the next field is 14, because the format string
    /// marks 13 and 15 unused. Counting fields rather than reading the numbers off the comments is
    /// how every column after that ends up one out.
    /// </remarks>
    private static SpellEntry ReadSpell(in DbcRecord record, int locale)
    {
        uint[] attributes = new uint[SpellEntry.AttributeWords];

        for (int i = 0; i < attributes.Length; i++)
        {
            attributes[i] = record.GetUInt32(4 + i);
        }

        SpellEffectEntry[] effects = new SpellEffectEntry[SpellConstants.MaxEffects];

        for (int i = 0; i < effects.Length; i++)
        {
            // Every per-effect column is a block of three consecutive fields, so each is its own
            // base plus i — not one interleaved record per effect.
            effects[i] = new SpellEffectEntry(
                Effect: record.GetUInt32(71 + i),
                DieSides: record.GetInt32(74 + i),
                RealPointsPerLevel: record.GetFloat(77 + i),
                BasePoints: record.GetInt32(80 + i),
                ImplicitTargetA: record.GetUInt32(86 + i),
                ImplicitTargetB: record.GetUInt32(89 + i),
                RadiusIndex: record.GetUInt32(92 + i),
                ApplyAuraName: record.GetUInt32(95 + i),
                Amplitude: record.GetUInt32(98 + i),
                ChainTarget: record.GetUInt32(104 + i),
                ItemType: record.GetUInt32(107 + i),
                MiscValue: record.GetInt32(110 + i),
                MiscValueB: record.GetInt32(113 + i),
                TriggerSpell: record.GetUInt32(116 + i));
        }

        return new SpellEntry(
            Id: record.GetUInt32(0),
            Name: record.GetLocalizedString(136, locale),
            Rank: record.GetLocalizedString(153, locale),
            Attributes: attributes,
            Category: record.GetUInt32(1),
            CastingTimeIndex: record.GetUInt32(28),
            RecoveryTime: record.GetUInt32(29),
            CategoryRecoveryTime: record.GetUInt32(30),
            StartRecoveryCategory: record.GetUInt32(205),
            StartRecoveryTime: record.GetUInt32(206),
            InterruptFlags: record.GetUInt32(31),
            Targets: record.GetUInt32(16),
            PowerType: record.GetUInt32(41),
            ManaCost: record.GetUInt32(42),
            ManaCostPerLevel: record.GetUInt32(43),
            ManaCostPercentage: record.GetUInt32(204),
            RangeIndex: record.GetUInt32(46),
            Speed: record.GetFloat(47),
            DurationIndex: record.GetUInt32(40),
            BaseLevel: record.GetUInt32(38),
            SpellLevel: record.GetUInt32(39),
            MaxLevel: record.GetUInt32(37),
            SchoolMask: record.GetUInt32(225),
            DmgClass: record.GetUInt32(213),
            PreventionType: record.GetUInt32(214),
            SpellFamilyName: record.GetUInt32(208),
            MaxAffectedTargets: record.GetUInt32(212),
            StackAmount: record.GetUInt32(49),
            ProcCharges: record.GetUInt32(36),
            SpellIconId: record.GetUInt32(133),
            SpellVisual: record.GetUInt32(131),
            Effects: effects);
    }
}
