using Microsoft.Extensions.Logging;
using WowEmu.Data.Db;

namespace WowEmu.Game.Maps;

/// <summary>
/// Builds the gameobjects that spawn in a grid.
/// </summary>
/// <remarks>
/// The same shape as <see cref="CreatureGridLoader"/>, and for the same reasons — see there for why
/// the grid index is built per map on first use.
/// </remarks>
public sealed class GameObjectGridLoader(
    GameObjectSpawnStore spawns,
    GameObjectTemplateStore templates,
    ILogger<GameObjectGridLoader> logger) : IGridObjectLoader
{
    private readonly Dictionary<uint, Dictionary<GridCoord, List<GameObjectSpawn>>> _byMapAndGrid = [];

    /// <inheritdoc cref="CreatureGridLoader.Difficulty"/>
    public byte Difficulty { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<WorldObject> Load(uint mapId, GridCoord grid)
    {
        Dictionary<GridCoord, List<GameObjectSpawn>> index = IndexFor(mapId);

        if (!index.TryGetValue(grid, out List<GameObjectSpawn>? inGrid))
        {
            return [];
        }

        List<WorldObject> loaded = new(inGrid.Count);
        int skipped = 0;

        foreach (GameObjectSpawn spawn in inGrid)
        {
            if (!spawn.SpawnsAtDifficulty(Difficulty))
            {
                continue;
            }

            if (!templates.TryGet(spawn.Entry, out GameObjectTemplate? template) || template is null)
            {
                Log.GameObjectSpawnMissingTemplate(logger, spawn.SpawnId, spawn.Entry);
                skipped++;
                continue;
            }

            loaded.Add(GameObject.Create(spawn, template));
        }

        Log.GameObjectGridLoaded(logger, mapId, grid.X, grid.Y, loaded.Count, skipped);
        return loaded;
    }

    /// <summary>
    /// Builds every map's grid index up front, so no login tick pays for one. Returns the map count.
    /// </summary>
    /// <remarks>See <see cref="CreatureGridLoader.BuildIndexes"/> for the reasoning.</remarks>
    public int BuildIndexes()
    {
        foreach (uint mapId in spawns.Maps)
        {
            IndexFor(mapId);
        }

        return _byMapAndGrid.Count;
    }

    private Dictionary<GridCoord, List<GameObjectSpawn>> IndexFor(uint mapId)
    {
        if (_byMapAndGrid.TryGetValue(mapId, out Dictionary<GridCoord, List<GameObjectSpawn>>? index))
        {
            return index;
        }

        index = [];

        foreach (GameObjectSpawn spawn in spawns.ForMap(mapId))
        {
            GridCoord grid = MapCoordinates.GridFor(spawn.Position.X, spawn.Position.Y);

            if (!index.TryGetValue(grid, out List<GameObjectSpawn>? inGrid))
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

/// <summary>
/// Runs several grid loaders as one.
/// </summary>
/// <remarks>
/// A grid holds creatures and gameobjects, and will hold corpses and transports. Composing the
/// loaders keeps <see cref="Map"/> from knowing that more than one kind of thing exists — it asks
/// for a grid's objects and files whatever comes back.
/// </remarks>
public sealed class CompositeGridLoader(IEnumerable<IGridObjectLoader> loaders) : IGridObjectLoader
{
    private readonly IGridObjectLoader[] _loaders = [.. loaders];

    /// <inheritdoc/>
    public IReadOnlyList<WorldObject> Load(uint mapId, GridCoord grid)
    {
        List<WorldObject> loaded = [];

        foreach (IGridObjectLoader loader in _loaders)
        {
            loaded.AddRange(loader.Load(mapId, grid));
        }

        return loaded;
    }
}
