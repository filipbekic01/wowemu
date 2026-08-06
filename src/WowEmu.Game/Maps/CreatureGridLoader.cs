using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WowEmu.Data.Db;

namespace WowEmu.Game.Maps;

/// <summary>
/// Builds the creatures that spawn in a grid.
/// </summary>
/// <remarks>
/// The grid-object half of PLAN.md §6's two-step load: the terrain tile is one step, this is the
/// other. A map asks for a grid once, the first time a player can see into it.
/// <para>
/// The <c>creature</c> table has no grid column, so the index is built here on first use of each
/// map — one pass over that map's spawns, at most once per map for the life of the process.
/// Precomputing all 109 maps up front would cost the same work for maps nobody visits.
/// </para>
/// </remarks>
public sealed class CreatureGridLoader(
    CreatureSpawnStore spawns,
    CreatureFactory creatures,
    ILogger<CreatureGridLoader> logger) : IGridObjectLoader
{
    private readonly Dictionary<uint, Dictionary<GridCoord, List<CreatureSpawn>>> _byMapAndGrid = [];

    /// <summary>
    /// Which difficulty's spawns to build.
    /// </summary>
    /// <remarks>
    /// Zero is normal, and it is the only one reachable today — nothing enters an instance yet. It
    /// is a property rather than a constant because <c>spawnMask</c> is checked against it, and
    /// ignoring the mask would put the 78 spawns that exclude difficulty 0 into the normal world.
    /// </remarks>
    public byte Difficulty { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<WorldObject> Load(uint mapId, GridCoord grid)
    {
        long startedAt = Stopwatch.GetTimestamp();

        Dictionary<GridCoord, List<CreatureSpawn>> index = IndexFor(mapId);

        if (!index.TryGetValue(grid, out List<CreatureSpawn>? inGrid))
        {
            return [];
        }

        List<WorldObject> loaded = new(inGrid.Count);
        int skipped = 0;

        foreach (CreatureSpawn spawn in inGrid)
        {
            if (!spawn.SpawnsAtDifficulty(Difficulty))
            {
                continue;
            }

            if (creatures.TryCreate(spawn, out Creature? creature, out string? reason))
            {
                loaded.Add(creature);
            }
            else
            {
                // Logged at debug and counted at info: a broken spawn is a data problem, and one
                // line per spawn would bury the grid load in a zone with a bad template.
                Log.CreatureSpawnSkipped(logger, spawn.SpawnId, reason);
                skipped++;
            }
        }

        double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        Log.GridLoaded(logger, mapId, grid.X, grid.Y, loaded.Count, skipped, elapsedMs);

        return loaded;
    }

    /// <summary>
    /// Builds every map's grid index up front. Returns how many maps were indexed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the index for a map is built on whichever tick the first player arrives on it —
    /// measured at ~20 ms for Eastern Kingdoms, inside the login tick, paid by a player. Upstream
    /// precomputes the equivalent at startup for the same reason.
    /// </para>
    /// <para>
    /// This is the "same work for maps nobody visits" that the lazy build was avoiding. It is worth
    /// it now that it has been measured: the whole set is one pass over the spawns already in
    /// memory, and it moves the cost to the one place in the process where nobody is waiting on a
    /// 50 ms budget.
    /// </para>
    /// </remarks>
    public int BuildIndexes()
    {
        foreach (uint mapId in spawns.Maps)
        {
            IndexFor(mapId);
        }

        return _byMapAndGrid.Count;
    }

    /// <summary>Groups a map's spawns by grid, once.</summary>
    private Dictionary<GridCoord, List<CreatureSpawn>> IndexFor(uint mapId)
    {
        if (_byMapAndGrid.TryGetValue(mapId, out Dictionary<GridCoord, List<CreatureSpawn>>? index))
        {
            return index;
        }

        long startedAt = Stopwatch.GetTimestamp();
        index = [];

        IReadOnlyList<CreatureSpawn> onMap = spawns.ForMap(mapId);

        foreach (CreatureSpawn spawn in onMap)
        {
            GridCoord grid = MapCoordinates.GridFor(spawn.Position.X, spawn.Position.Y);

            if (!index.TryGetValue(grid, out List<CreatureSpawn>? inGrid))
            {
                inGrid = [];
                index[grid] = inGrid;
            }

            inGrid.Add(spawn);
        }

        _byMapAndGrid[mapId] = index;

        double elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        Log.SpawnIndexBuilt(logger, mapId, onMap.Count, index.Count, elapsedMs);

        return index;
    }
}
