namespace WowEmu.Data.Client;

/// <summary>
/// The four skill tables, and the lookups that only make sense across them.
/// </summary>
/// <remarks>
/// A wrapper rather than four bare stores because none of the questions worth asking are answered
/// by an id lookup. "Which skills may a night elf rogue have" and "what skill does this spell
/// belong to" both need an index keyed by something other than the row id, and building those once
/// at startup is the difference between a lookup and a scan of eight thousand rows.
/// </remarks>
public sealed class SkillLines
{
    private readonly DbcStore<SkillLineEntry> _lines;
    private readonly DbcStore<SkillTiersEntry> _tiers;

    /// <summary>Race/class rows by the skill they describe. Several rows can cover one skill.</summary>
    private readonly Dictionary<uint, List<SkillRaceClassInfoEntry>> _raceClassBySkill = [];

    /// <summary>Ability rows by the spell that grants them.</summary>
    private readonly Dictionary<uint, List<SkillLineAbilityEntry>> _abilitiesBySpell = [];

    public SkillLines(
        DbcStore<SkillLineEntry> lines,
        DbcStore<SkillRaceClassInfoEntry> raceClassInfo,
        DbcStore<SkillTiersEntry> tiers,
        DbcStore<SkillLineAbilityEntry> abilities)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(raceClassInfo);
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(abilities);

        _lines = lines;
        _tiers = tiers;

        foreach (SkillRaceClassInfoEntry row in raceClassInfo.Entries)
        {
            if (!_raceClassBySkill.TryGetValue(row.SkillId, out List<SkillRaceClassInfoEntry>? rows))
            {
                _raceClassBySkill[row.SkillId] = rows = [];
            }

            rows.Add(row);
        }

        foreach (SkillLineAbilityEntry row in abilities.Entries)
        {
            if (!_abilitiesBySpell.TryGetValue(row.Spell, out List<SkillLineAbilityEntry>? rows))
            {
                _abilitiesBySpell[row.Spell] = rows = [];
            }

            rows.Add(row);
        }
    }

    /// <summary>Nothing loaded, for callers that must run without a client extracted.</summary>
    public static SkillLines Empty { get; } = new(
        DbcStore<SkillLineEntry>.Empty,
        DbcStore<SkillRaceClassInfoEntry>.Empty,
        DbcStore<SkillTiersEntry>.Empty,
        DbcStore<SkillLineAbilityEntry>.Empty);

    /// <summary>Total rows across all four, for the startup log.</summary>
    public int TotalRows { get; init; }

    /// <summary>A skill's own row, or null.</summary>
    public SkillLineEntry? Line(uint skillId) =>
        _lines.TryGet(skillId, out SkillLineEntry? line) ? line : null;

    /// <summary>
    /// The race/class row for a skill and a character, or null when they may not have it.
    /// </summary>
    /// <remarks>
    /// Port of <c>GetSkillRaceClassInfo</c>. Null is a real answer here and not a missing-data
    /// case: it is how the tables say a paladin has no Runeforging.
    /// </remarks>
    public SkillRaceClassInfoEntry? RaceClassInfo(uint skillId, byte race, byte characterClass)
    {
        if (!_raceClassBySkill.TryGetValue(skillId, out List<SkillRaceClassInfoEntry>? rows))
        {
            return null;
        }

        foreach (SkillRaceClassInfoEntry row in rows)
        {
            if (row.Covers(race, characterClass))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>The tier row a ranked skill steps through, or null.</summary>
    public SkillTiersEntry? Tier(uint tierId) =>
        _tiers.TryGet(tierId, out SkillTiersEntry? tier) ? tier : null;

    /// <summary>Every ability row a spell belongs to. Usually one, occasionally none.</summary>
    public IReadOnlyList<SkillLineAbilityEntry> AbilitiesOf(uint spellId) =>
        _abilitiesBySpell.TryGetValue(spellId, out List<SkillLineAbilityEntry>? rows)
            ? rows
            : [];

    /// <summary>
    /// How a skill's bar is scaled.
    /// </summary>
    /// <remarks>
    /// Port of <c>GetSkillRangeType</c>. The order of the checks is the whole of it, and it is not
    /// the order the categories suggest:
    /// <list type="bullet">
    /// <item>
    /// Having a tier row wins over everything, because that is what makes a profession a profession.
    /// A skill is <see cref="SkillRange.Rank"/> whatever category it claims.
    /// </item>
    /// <item>
    /// Runeforging is named outright. It is a class skill by category, which would make it a level
    /// bar climbing to five times the death knight's level — it is a proficiency you either have or
    /// do not, and upstream hard-codes it rather than fixing the table.
    /// </item>
    /// <item>
    /// Armour is <see cref="SkillRange.Mono"/> and languages are flat 300. Everything else — weapon
    /// skills, defence, the class lines — climbs with level.
    /// </item>
    /// </list>
    /// </remarks>
    public SkillRange RangeOf(SkillRaceClassInfoEntry raceClassInfo)
    {
        ArgumentNullException.ThrowIfNull(raceClassInfo);

        if (Line(raceClassInfo.SkillId) is not { } line)
        {
            return SkillRange.None;
        }

        if (Tier(raceClassInfo.SkillTierId) is not null)
        {
            return SkillRange.Rank;
        }

        if (raceClassInfo.SkillId == SkillType.Runeforging)
        {
            return SkillRange.Mono;
        }

        return line.CategoryId switch
        {
            SkillCategory.Armor => SkillRange.Mono,
            SkillCategory.Languages => SkillRange.Language,
            _ => SkillRange.Level,
        };
    }

    /// <summary>What a language skill is fixed at, learned and maxed in one step.</summary>
    public const ushort LanguageValue = 300;
}
