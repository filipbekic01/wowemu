using Microsoft.Extensions.Logging;
using WowEmu.Data.Client;

namespace WowEmu.Game.Maps;

/// <summary>What kind of map this is, which decides which phase of the round-robin updates it.</summary>
/// <remarks>
/// Everything is a <see cref="Continent"/> today because nothing creates an instance yet. The other
/// two exist so the round-robin below is upstream's shape rather than a special case that would have
/// to be reworked the moment dungeons arrive.
/// </remarks>
public enum MapKind
{
    Continent,
    BattlegroundOrArena,
    Dungeon,
}

/// <summary>
/// Every live map, and the round-robin that updates them.
/// </summary>
/// <remarks>
/// Port of <c>MapMgr</c>. Touched only from the world tick — maps are created when a player logs in,
/// which is a world-queue packet, and enumerated by <see cref="Update"/>, which the same loop calls.
/// That is why there is no lock: it would be guarding against a caller that does not exist, and its
/// presence would suggest one did.
/// </remarks>
public sealed class MapManager(
    TerrainManager terrain,
    IGridObjectLoader? gridObjects = null,
    MapUpdater? updater = null,
    ILogger<Map>? logger = null) : IDisposable
{
    /// <summary>
    /// The four phases. Three update a category of map; the fourth is a pause.
    /// </summary>
    /// <remarks>
    /// PLAN.md §4.5 records the trap: a map that is out of phase is updated with
    /// <c>t_diff == 0</c>, which is <b>not</b> a skipped tick — its sessions and players are still
    /// updated, only its gameplay timers are not. Three ticks in four are a session-only pass, and
    /// reading that as "the map did not tick" leads to chasing a bug that is not there.
    /// </remarks>
    public const int PhaseCount = 4;

    private readonly Dictionary<uint, Map> _maps = [];
    private readonly uint[] _phaseAccumulator = new uint[PhaseCount];
    private readonly MapUpdater _updater = updater ?? new MapUpdater(0);

    private int _phase;

    /// <summary>Which phase the next <see cref="Update"/> will give a full diff to.</summary>
    public int CurrentPhase => _phase;

    /// <summary>Every map that has been touched.</summary>
    public IReadOnlyList<Map> ActiveMaps => [.. _maps.Values];

    public Map GetMap(uint mapId)
    {
        if (!_maps.TryGetValue(mapId, out Map? map))
        {
            map = new Map(mapId, terrain.GetMap(mapId), gridObjects, logger);
            _maps[mapId] = map;
        }

        return map;
    }

    /// <summary>
    /// Advances every map by one tick, giving a full gameplay diff to the maps in the current phase.
    /// </summary>
    /// <remarks>
    /// Port of <c>MapMgr::Update</c>. Every map is scheduled, then waited on, so no map is still
    /// running when the world loop moves on — which is what makes it safe for the world tick to
    /// touch map state at all.
    /// </remarks>
    public void Update(uint diff)
    {
        for (int phase = 0; phase < PhaseCount; phase++)
        {
            _phaseAccumulator[phase] += diff;
        }

        foreach (Map map in _maps.Values)
        {
            // The accumulated diff, not this tick's: a map updated once every four ticks must be
            // told about all the time that passed, or its timers run at a quarter speed.
            uint gameplayDiff = IsInPhase(map, _phase) ? _phaseAccumulator[_phase] : 0;

            _updater.Schedule(() => map.Update(gameplayDiff, diff));
        }

        _updater.Wait();

        if (_phase < PhaseCount - 1)
        {
            _phaseAccumulator[_phase] = 0;
        }

        _phase = (_phase + 1) % PhaseCount;
    }

    /// <summary>Whether a map gets a full gameplay diff in a given phase.</summary>
    /// <remarks>
    /// Phase 3 matches nothing. It is upstream's idle step, and it is what makes the interval
    /// between two full updates of the same map four ticks rather than three.
    /// </remarks>
    private static bool IsInPhase(Map map, int phase) => phase switch
    {
        0 => map.Kind == MapKind.Continent,
        1 => map.Kind == MapKind.BattlegroundOrArena,
        2 => map.Kind == MapKind.Dungeon,
        _ => false,
    };

    public void Dispose()
    {
        // Only when we made it. A caller that passed one in owns its lifetime.
        if (updater is null)
        {
            _updater.Dispose();
        }
    }
}
