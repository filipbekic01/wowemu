using Microsoft.Extensions.Logging;

namespace WowEmu.Game.Maps;

/// <summary>
/// Source-generated log messages for the map layer.
/// </summary>
/// <remarks>
/// Same rationale as the two servers' own: the generator turns each of these into an allocation-free
/// call that costs nothing when the level is disabled, which matters for a message emitted once per
/// grid load.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Debug,
        Message = "Grid ({GridX}, {GridY}) on map {MapId}: {Loaded} creature(s) loaded, {Skipped} skipped in {ElapsedMs} ms")]
    public static partial void GridLoaded(
        ILogger logger, uint mapId, int gridX, int gridY, int loaded, int skipped, double elapsedMs);

    /// <summary>
    /// The one-off per-map index build, timed separately from the grid load that triggered it.
    /// </summary>
    /// <remarks>
    /// Debug rather than Information because these are now built for every map at startup, and a
    /// line each would be 109 of them. The summary that startup prints is the one worth reading;
    /// this is here for when one map is the problem.
    /// </remarks>
    [LoggerMessage(EventId = 3005, Level = LogLevel.Debug,
        Message = "Map {MapId}: indexed {Spawns} creature spawn(s) into {Grids} grid(s) in {ElapsedMs} ms")]
    public static partial void SpawnIndexBuilt(
        ILogger logger, uint mapId, int spawns, int grids, double elapsedMs);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug,
        Message = "Creature spawn {SpawnId} skipped: {Reason}")]
    public static partial void CreatureSpawnSkipped(ILogger logger, uint spawnId, string? reason);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Debug,
        Message = "Grid ({GridX}, {GridY}) on map {MapId}: {Loaded} gameobject(s) loaded, {Skipped} skipped")]
    public static partial void GameObjectGridLoaded(
        ILogger logger, uint mapId, int gridX, int gridY, int loaded, int skipped);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Debug,
        Message = "'{Name}' walking {Distance:F1} yd over {DurationMs} ms, seen by {Watchers}")]
    public static partial void CreatureMoveStarted(
        ILogger logger, string name, float distance, uint durationMs, int watchers);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug,
        Message = "Gameobject spawn {SpawnId} skipped: no gameobject_template row for entry {Entry}")]
    public static partial void GameObjectSpawnMissingTemplate(ILogger logger, uint spawnId, uint entry);
}
