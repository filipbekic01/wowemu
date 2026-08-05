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

    private DbcStores(
        DbcStore<ChrRacesEntry> races,
        DbcStore<ChrClassesEntry> classes,
        DbcStore<MapEntry> maps,
        DbcStore<FactionTemplateEntry> factionTemplates)
    {
        Races = races;
        Classes = classes;
        Maps = maps;
        FactionTemplates = factionTemplates;
    }

    public DbcStore<ChrRacesEntry> Races { get; }

    public DbcStore<ChrClassesEntry> Classes { get; }

    public DbcStore<MapEntry> Maps { get; }

    /// <summary>Who fights whom.</summary>
    public DbcStore<FactionTemplateEntry> FactionTemplates { get; }

    /// <summary>Total rows loaded, for the startup log.</summary>
    public int TotalRows => Races.Count + Classes.Count + Maps.Count + FactionTemplates.Count;

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
                    ])));
    }
}
