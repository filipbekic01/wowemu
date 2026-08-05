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

        Log.GridLoaded(logger, mapId, grid.X, grid.Y, loaded.Count, skipped);
        return loaded;
    }

    /// <summary>Groups a map's spawns by grid, once.</summary>
    private Dictionary<GridCoord, List<CreatureSpawn>> IndexFor(uint mapId)
    {
        if (_byMapAndGrid.TryGetValue(mapId, out Dictionary<GridCoord, List<CreatureSpawn>>? index))
        {
            return index;
        }

        index = [];

        foreach (CreatureSpawn spawn in spawns.ForMap(mapId))
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
        return index;
    }
}
