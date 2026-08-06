namespace WowEmu.Data.Client;

/// <summary>A row of <c>ChrRaces.dbc</c>.</summary>
/// <remarks>
/// The display ids are why this store is on the critical path for entering the world: they are the
/// model the client draws. A character whose race has no row renders as nothing at all.
/// </remarks>
public sealed record ChrRacesEntry(
    uint RaceId,
    uint Flags,
    uint FactionId,
    uint MaleDisplayId,
    uint FemaleDisplayId,
    uint TeamId,
    uint CinematicSequenceId,
    uint Alliance,
    string Name,
    uint Expansion)
{
    /// <summary>Alliance races report team 7; everything else is Horde. Upstream's encoding.</summary>
    public bool IsAlliance => TeamId == 7;
}


/// <summary>
/// A row of <c>FactionTemplate.dbc</c> — who a unit will and will not fight.
/// </summary>
/// <remarks>
/// Every creature and every player carries a faction <i>template</i> id, not a faction id. The
/// template is the relationship table: which factions it counts as enemies, which as friends, and
/// two bitmasks for everyone it has no specific opinion about.
/// </remarks>
/// <param name="Id">The template id, which is what <c>creature_template.faction</c> holds.</param>
/// <param name="Faction">The faction this template belongs to, for reputation.</param>
/// <param name="Flags">Template flags — contested guards and call-for-help live here.</param>
/// <param name="OurMask">Which broad groups this unit belongs to. <c>m_factionGroup</c>.</param>
/// <param name="FriendlyMask">Which broad groups it is friendly towards.</param>
/// <param name="HostileMask">Which broad groups it is hostile towards.</param>
/// <param name="EnemyFactions">Up to four specific factions it will always fight.</param>
/// <param name="FriendFactions">Up to four specific factions it will never fight.</param>
public sealed record FactionTemplateEntry(
    uint Id,
    uint Faction,
    uint Flags,
    uint OurMask,
    uint FriendlyMask,
    uint HostileMask,
    uint[] EnemyFactions,
    uint[] FriendFactions)
{
    /// <summary>How many specific relations a template can name in each direction.</summary>
    public const int MaxFactionRelations = 4;

    /// <summary>The player faction groups, from <c>FactionMasks</c>.</summary>
    public const uint MaskPlayer = 1;
    public const uint MaskAlliance = 2;
    public const uint MaskHorde = 4;
    public const uint MaskMonster = 8;

    /// <summary>
    /// Whether this unit will attack <paramref name="other"/> on sight.
    /// </summary>
    /// <remarks>
    /// <b>The specific lists win over the masks, and enemies are checked before friends.</b> A
    /// template can name a faction as an enemy while its mask says the whole group is fine, which is
    /// how a guard is hostile to one enemy city but not to neutral travellers. Checking the mask
    /// first would make every such exception disappear.
    /// <para>
    /// Note the asymmetry with <see cref="IsFriendlyTo"/>: hostility consults only
    /// <see cref="HostileMask"/> against the other's <see cref="OurMask"/>, in one direction, while
    /// friendliness checks both directions. Two units can therefore be neither hostile nor friendly
    /// — which is exactly what neutral means.
    /// </para>
    /// </remarks>
    public bool IsHostileTo(FactionTemplateEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Faction != 0)
        {
            if (Array.IndexOf(EnemyFactions, other.Faction) >= 0)
            {
                return true;
            }

            if (Array.IndexOf(FriendFactions, other.Faction) >= 0)
            {
                return false;
            }
        }

        return (HostileMask & other.OurMask) != 0;
    }

    /// <summary>Whether this unit counts <paramref name="other"/> as a friend.</summary>
    /// <remarks>Sharing a faction is always friendly, whatever the masks say.</remarks>
    public bool IsFriendlyTo(FactionTemplateEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Faction == other.Faction)
        {
            return true;
        }

        if (other.Faction != 0)
        {
            if (Array.IndexOf(EnemyFactions, other.Faction) >= 0)
            {
                return false;
            }

            if (Array.IndexOf(FriendFactions, other.Faction) >= 0)
            {
                return true;
            }
        }

        return (FriendlyMask & other.OurMask) != 0 || (OurMask & other.FriendlyMask) != 0;
    }

    /// <summary>Hostile to players of either side.</summary>
    public bool IsHostileToPlayers => (HostileMask & MaskPlayer) != 0;

    /// <summary>
    /// Picks a fight with nobody at all — critters, and most quest props.
    /// </summary>
    /// <remarks>
    /// Distinct from merely not being hostile to you: a neutral-to-all unit never initiates, which
    /// is what stops a field of rabbits mobbing anyone who walks past.
    /// </remarks>
    public bool IsNeutralToAll =>
        HostileMask == 0 && FriendlyMask == 0 && Array.TrueForAll(EnemyFactions, faction => faction == 0);
}

/// <summary>
/// A row of <c>WorldSafeLocs.dbc</c> — a graveyard, or any other named safe point.
/// </summary>
/// <remarks>
/// <b>This is where graveyard coordinates live in our data.</b> Newer AzerothCore reads them from a
/// <c>game_graveyard</c> world table instead; the vendored dump predates that and carries only
/// <c>game_graveyard_zone</c>, which maps a zone to an id in <i>this</i> file. Same divergence as
/// <c>creature_template_model</c> — read the C++ for behaviour, but check the dump before trusting a
/// column name.
/// </remarks>
/// <summary>
/// One row of <c>QuestXP.dbc</c>: what a quest of this level pays, by difficulty band.
/// </summary>
/// <remarks>
/// The id <i>is</i> the quest level, which is why a quest's <c>RewardXPId</c> column is a column
/// index into this row rather than a row id. Looking the quest up by its own id finds nothing.
/// </remarks>
public sealed record QuestXpEntry(uint Level, uint[] ByDifficulty)
{
    /// <summary>How many difficulty columns each row has.</summary>
    public const int DifficultyCount = 10;

    /// <summary>The payout for one difficulty band. Out-of-range bands pay nothing.</summary>
    public uint For(byte difficulty) =>
        difficulty < ByDifficulty.Length ? ByDifficulty[difficulty] : 0;
}

/// <summary>
/// A row of <c>AreaTable.dbc</c>: one zone, or one subzone of a zone.
/// </summary>
/// <param name="Id">The area id, which is what terrain tiles store per chunk.</param>
/// <param name="MapId">Which map it is on.</param>
/// <param name="ParentZoneId">
/// <b>Zero means this row IS a zone.</b> Otherwise it names the zone this subzone belongs to.
/// Elwynn Forest is a zone and stores 0; Northshire Valley is a subzone of it and stores 12.
/// </param>
/// <param name="Flags">Sanctuary, capital city, and the rest. 312 for every city upstream notes.</param>
/// <param name="AreaLevel">The suggested level, or 0 where there is none.</param>
/// <param name="Name">The name the client shows.</param>
/// <param name="Team">Alliance, Horde or neither, for the areas that belong to one.</param>
/// <param name="LiquidTypeOverride">
/// Four entries, one per liquid sound bank, letting a zone substitute its own liquid — which is how
/// Naxxramas gets slime where the geometry says water. Zero means no override for that kind.
/// </param>
public sealed record AreaTableEntry(
    uint Id,
    uint MapId,
    uint ParentZoneId,
    uint Flags,
    int AreaLevel,
    string Name,
    uint Team,
    uint[] LiquidTypeOverride)
{
    /// <summary>How many liquid kinds a zone can override. One per sound bank.</summary>
    public const int LiquidOverrideCount = 4;

    /// <summary>Whether this row is a zone in its own right rather than part of one.</summary>
    public bool IsZone => ParentZoneId == 0;

    /// <summary>The override for one liquid kind, or 0.</summary>
    public uint OverrideFor(uint soundBank) =>
        soundBank < (uint)LiquidTypeOverride.Length ? LiquidTypeOverride[soundBank] : 0;
}

/// <summary>A row of <c>LiquidType.dbc</c>.</summary>
/// <remarks>
/// Two columns of forty-five. <see cref="SoundBank"/> is what upstream calls <c>Type</c>, and it is
/// the only classification there is: it says whether a liquid is water, ocean, magma or slime, and
/// nothing else in the file does. A WMO stores only the row id, so without this table indoor water
/// and Undercity's slime are indistinguishable.
/// </remarks>
public sealed record LiquidTypeEntry(uint Id, uint SoundBank, uint SpellId)
{
    /// <summary>The <c>MAP_LIQUID_TYPE_*</c> bit this row's sound bank corresponds to.</summary>
    /// <remarks>
    /// The same mapping the map extractor makes when it bakes types into a terrain tile —
    /// <c>1 &lt;&lt; SoundBank</c>, spelled out rather than shifted so that an unexpected value
    /// becomes <see cref="LiquidTypeMask.None"/> instead of an out-of-range bit.
    /// </remarks>
    public LiquidTypeMask Type => SoundBank switch
    {
        0 => LiquidTypeMask.Water,
        1 => LiquidTypeMask.Ocean,
        2 => LiquidTypeMask.Magma,
        3 => LiquidTypeMask.Slime,
        _ => LiquidTypeMask.None,
    };
}

public sealed record WorldSafeLocsEntry(
    uint Id,
    uint MapId,
    float X,
    float Y,
    float Z,
    string Name);

/// <summary>A row of <c>ChrClasses.dbc</c>.</summary>
public sealed record ChrClassesEntry(
    uint ClassId,
    uint PowerType,
    string Name,
    uint SpellFamily,
    uint Expansion);

/// <summary>A row of <c>Map.dbc</c>.</summary>
public sealed record MapEntry(
    uint MapId,
    uint MapType,
    uint Flags,
    string Directory,
    string Name,
    uint LinkedZone,
    uint Expansion)
{
    /// <summary>Instances, raids, battlegrounds and arenas — anything that is not a continent.</summary>
    public bool IsInstance => MapType is 1 or 2 or 3 or 4;

    /// <summary>A world map: Azeroth, Kalimdor, Outland, Northrend.</summary>
    public bool IsContinent => MapType == 0;
}

/// <summary>
/// The DBC stores loaded at startup.
/// </summary>
/// <remarks>
/// Only the stores something reads are here. Upstream loads all 109 unconditionally, which costs
/// seconds of startup for tables no phase has touched yet; the rest arrive as they are needed.
/// <para>
/// The format strings are upstream's, verbatim from <c>DBCfmt.h</c>. They encode the exact column
/// layout of a 3.3.5a client's files — a single character out of place shifts every field after it
/// and produces values that are wrong but not obviously so.
/// </para>
/// </remarks>
public sealed class DbcStores
{
    // Verbatim from src/server/shared/DataStores/DBCfmt.h.
    private const string ChrRacesFormat = "niixiixixxxxiissssssssssssssssxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxi";
    private const string ChrClassesFormat = "nxixssssssssssssssssxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxixii";
    private const string MapFormat = "nxiixssssssssssssssssxixxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxixiffxixi";
    private const string FactionTemplateFormat = "niiiiiiiiiiiii";

    // Not in upstream's DBCfmt.h — the C++ stopped reading this file when graveyards moved into
    // the world database. Derived from the file itself: 22 fields, id + map + three floats, then
    // sixteen locale names and a flags word.
    private const string WorldSafeLocsFormat = "nifffssssssssssssssssx";

    /// <summary>An id and ten difficulty columns. <c>QuestXPfmt</c>.</summary>
    private const string QuestXpFormat = "niiiiiiiiii";

    /// <summary>Verbatim from <c>DBCfmt.h</c>: id, type and spell out of forty-five columns.</summary>
    private const string LiquidTypeFormat = "nxxixixxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    /// <summary>
    /// Verbatim from <c>DBCfmt.h</c>. Thirty-six columns, sixteen of them the localised name.
    /// </summary>
    private const string AreaTableFormat = "niiiixxxxxissssssssssssssssxiiiiixxx";

    private DbcStores(
        DbcStore<ChrRacesEntry> races,
        DbcStore<ChrClassesEntry> classes,
        DbcStore<MapEntry> maps,
        DbcStore<FactionTemplateEntry> factionTemplates,
        DbcStore<WorldSafeLocsEntry> worldSafeLocs,
        DbcStore<QuestXpEntry> questXp,
        DbcStore<LiquidTypeEntry> liquidTypes,
        DbcStore<AreaTableEntry> areas)
    {
        QuestXp = questXp;
        LiquidTypes = liquidTypes;
        Areas = areas;
        Races = races;
        Classes = classes;
        Maps = maps;
        FactionTemplates = factionTemplates;
        WorldSafeLocs = worldSafeLocs;
    }

    public DbcStore<ChrRacesEntry> Races { get; }

    public DbcStore<ChrClassesEntry> Classes { get; }

    public DbcStore<MapEntry> Maps { get; }

    /// <summary>Who fights whom.</summary>
    public DbcStore<FactionTemplateEntry> FactionTemplates { get; }

    /// <summary>Graveyards and other named safe points.</summary>
    public DbcStore<WorldSafeLocsEntry> WorldSafeLocs { get; }

    /// <summary>
    /// How much experience a quest pays, by quest level and difficulty.
    /// </summary>
    /// <remarks>
    /// <b>Indexed by the quest's LEVEL, not by its id.</b> The row is the level and the column is
    /// the quest's <c>RewardXPId</c>, which is a difficulty band rather than an amount.
    /// </remarks>
    public DbcStore<QuestXpEntry> QuestXp { get; }

    /// <summary>What each liquid actually is. Without it a WMO's water has no type at all.</summary>
    public DbcStore<LiquidTypeEntry> LiquidTypes { get; }

    /// <summary>
    /// Zones and subzones. What turns the area id a terrain tile stores into a zone.
    /// </summary>
    /// <remarks>
    /// The distinction matters more than it looks. A terrain chunk stores the <i>area</i>, and
    /// everything keyed by zone — graveyards, the character list, the location display — wants the
    /// zone. Using one for the other works everywhere a zone has no subzones and fails silently
    /// everywhere it does.
    /// </remarks>
    public DbcStore<AreaTableEntry> Areas { get; }

    /// <summary>
    /// The zone an area belongs to, which is the area itself when it is already a zone.
    /// </summary>
    /// <remarks>
    /// Falls back to the area id when the row is missing rather than answering zero: an unknown area
    /// is better treated as its own zone than as no zone at all, which would silently disable
    /// everything keyed by one.
    /// </remarks>
    public uint ZoneFor(uint areaId) =>
        Areas.TryGet(areaId, out AreaTableEntry? area) && area is not null && area.ParentZoneId != 0
            ? area.ParentZoneId
            : areaId;

    /// <summary>Total rows loaded, for the startup log.</summary>
    public int TotalRows =>
        Races.Count + Classes.Count + Maps.Count + FactionTemplates.Count + WorldSafeLocs.Count
        + QuestXp.Count + LiquidTypes.Count + Areas.Count;

    /// <summary>
    /// Loads every store from a directory of extracted <c>.dbc</c> files.
    /// </summary>
    /// <param name="directory">Usually <c>data/dbc</c>.</param>
    /// <param name="locale">Preferred locale slot, 0-15. Ignored when a store has one locale filled.</param>
    public static DbcStores Load(string directory, int locale = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"No DBC directory at '{directory}'. Extract a 3.3.5a client into data/dbc — see data/README.md.");
        }

        return new DbcStores(
            DbcStore<ChrRacesEntry>.Load(
                Path.Combine(directory, "ChrRaces.dbc"),
                ChrRacesFormat,
                idField: 0,
                (in DbcRecord record) => new ChrRacesEntry(
                    RaceId: record.GetUInt32(0),
                    Flags: record.GetUInt32(1),
                    FactionId: record.GetUInt32(2),
                    MaleDisplayId: record.GetUInt32(4),
                    FemaleDisplayId: record.GetUInt32(5),
                    TeamId: record.GetUInt32(7),
                    CinematicSequenceId: record.GetUInt32(12),
                    Alliance: record.GetUInt32(13),
                    Name: record.GetLocalizedString(14, locale),
                    Expansion: record.GetUInt32(68))),

            DbcStore<ChrClassesEntry>.Load(
                Path.Combine(directory, "ChrClasses.dbc"),
                ChrClassesFormat,
                idField: 0,
                (in DbcRecord record) => new ChrClassesEntry(
                    ClassId: record.GetUInt32(0),
                    PowerType: record.GetUInt32(2),
                    // Column 4, not 5: the struct's comment in DBCStructure.h is off by one and
                    // the format string is what actually defines the byte offsets.
                    Name: record.GetLocalizedString(4, locale),
                    SpellFamily: record.GetUInt32(56),
                    Expansion: record.GetUInt32(59))),

            DbcStore<MapEntry>.Load(
                Path.Combine(directory, "Map.dbc"),
                MapFormat,
                idField: 0,
                (in DbcRecord record) => new MapEntry(
                    MapId: record.GetUInt32(0),
                    MapType: record.GetUInt32(2),
                    Flags: record.GetUInt32(3),
                    // Column 1 is the map's folder name. Upstream's format marks it unused, but it
                    // is a perfectly good string offset and it is what data/maps subdirectories are
                    // named after, so it is read here.
                    Directory: record.GetString(1),
                    Name: record.GetLocalizedString(5, locale),
                    LinkedZone: record.GetUInt32(22),
                    Expansion: record.GetUInt32(63))),

            DbcStore<FactionTemplateEntry>.Load(
                Path.Combine(directory, "FactionTemplate.dbc"),
                FactionTemplateFormat,
                idField: 0,
                (in DbcRecord record) => new FactionTemplateEntry(
                    Id: record.GetUInt32(0),
                    Faction: record.GetUInt32(1),
                    Flags: record.GetUInt32(2),
                    OurMask: record.GetUInt32(3),
                    FriendlyMask: record.GetUInt32(4),
                    HostileMask: record.GetUInt32(5),
                    // Four each, consecutive: enemies at 6-9, friends at 10-13. Reading them as one
                    // block of eight would compile and would make every friend an enemy.
                    EnemyFactions:
                    [
                        record.GetUInt32(6),
                        record.GetUInt32(7),
                        record.GetUInt32(8),
                        record.GetUInt32(9),
                    ],
                    FriendFactions:
                    [
                        record.GetUInt32(10),
                        record.GetUInt32(11),
                        record.GetUInt32(12),
                        record.GetUInt32(13),
                    ])),

            DbcStore<WorldSafeLocsEntry>.Load(
                Path.Combine(directory, "WorldSafeLocs.dbc"),
                WorldSafeLocsFormat,
                idField: 0,
                (in DbcRecord record) => new WorldSafeLocsEntry(
                    Id: record.GetUInt32(0),
                    MapId: record.GetUInt32(1),
                    X: record.GetFloat(2),
                    Y: record.GetFloat(3),
                    Z: record.GetFloat(4),
                    Name: record.GetLocalizedString(5, locale))),

            DbcStore<QuestXpEntry>.Load(
                Path.Combine(directory, "QuestXP.dbc"),
                QuestXpFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] byDifficulty = new uint[QuestXpEntry.DifficultyCount];

                    for (int i = 0; i < byDifficulty.Length; i++)
                    {
                        byDifficulty[i] = record.GetUInt32(1 + i);
                    }

                    return new QuestXpEntry(record.GetUInt32(0), byDifficulty);
                }),

            DbcStore<LiquidTypeEntry>.Load(
                Path.Combine(directory, "LiquidType.dbc"),
                LiquidTypeFormat,
                idField: 0,
                // Columns, not kept fields: an 'x' in the format still consumes an index, so the
                // type sits at 3 and the spell at 5 even though they are the second and third
                // things the format keeps. Reading them at 1 and 2 lands on the name's string
                // offset, which resolves to a plausible small number and quietly types every
                // liquid as nothing.
                (in DbcRecord record) => new LiquidTypeEntry(
                    Id: record.GetUInt32(0),
                    SoundBank: record.GetUInt32(3),
                    SpellId: record.GetUInt32(5))),

            DbcStore<AreaTableEntry>.Load(
                Path.Combine(directory, "AreaTable.dbc"),
                AreaTableFormat,
                idField: 0,
                (in DbcRecord record) =>
                {
                    uint[] overrides = new uint[AreaTableEntry.LiquidOverrideCount];

                    for (int i = 0; i < overrides.Length; i++)
                    {
                        overrides[i] = record.GetUInt32(29 + i);
                    }

                    return new AreaTableEntry(
                        Id: record.GetUInt32(0),
                        MapId: record.GetUInt32(1),
                        ParentZoneId: record.GetUInt32(2),
                        Flags: record.GetUInt32(4),
                        AreaLevel: record.GetInt32(10),
                        Name: record.GetLocalizedString(11, locale),
                        Team: record.GetUInt32(28),
                        LiquidTypeOverride: overrides);
                }));
    }
}
