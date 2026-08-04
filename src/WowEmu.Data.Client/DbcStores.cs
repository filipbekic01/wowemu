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

    private DbcStores(DbcStore<ChrRacesEntry> races, DbcStore<ChrClassesEntry> classes, DbcStore<MapEntry> maps)
    {
        Races = races;
        Classes = classes;
        Maps = maps;
    }

    public DbcStore<ChrRacesEntry> Races { get; }

    public DbcStore<ChrClassesEntry> Classes { get; }

    public DbcStore<MapEntry> Maps { get; }

    /// <summary>Total rows loaded, for the startup log.</summary>
    public int TotalRows => Races.Count + Classes.Count + Maps.Count;

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
                    Expansion: record.GetUInt32(63))));
    }
}
