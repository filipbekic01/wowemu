using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Protocol;

namespace WowEmu.Game.Maps;

/// <summary>
/// How a player's connection is reached from the map layer.
/// </summary>
/// <remarks>
/// Defined here rather than in the network layer so <c>WowEmu.Game</c> stays free of sockets: the
/// map knows that someone became visible, not how a packet is framed.
/// </remarks>
public interface IPlayerConnection
{
    /// <summary>Tells this player about an object that has come into view.</summary>
    Task SendCreateAsync(WorldObject other, CancellationToken cancellationToken);

    /// <summary>Tells this player an object has left view, so the client destroys its copy.</summary>
    Task SendDestroyAsync(ObjectGuid objectGuid, CancellationToken cancellationToken);

    /// <summary>Relays another player's movement.</summary>
    Task SendMovementAsync(Opcode opcode, ObjectGuid mover, MovementInfo movement, CancellationToken cancellationToken);
}

/// <summary>
/// Supplies the objects that live in a grid.
/// </summary>
/// <remarks>
/// PLAN.md §6 keeps grid <i>creation</i> — the terrain tile — and grid <i>object loading</i> — the
/// database spawns — as two separate steps, because this fork does. The terrain is loaded by
/// <see cref="TerrainMap"/> on demand; this is the other half, and being an interface is what lets
/// a map be tested without a database behind it.
/// </remarks>
public interface IGridObjectLoader
{
    /// <summary>Builds every object that spawns in one grid. Called at most once per grid.</summary>
    IReadOnlyList<WorldObject> Load(uint mapId, GridCoord grid);
}

/// <summary>
/// One map instance: the objects on it, and who can see whom.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Map</c> that M4 needs. Objects live in cells so that a visibility query
/// visits a 5×5 block rather than every object on the continent.
/// <para>
/// <b>Threading is interim.</b> PLAN.md §4.2 gives each map a worker task and forbids touching a
/// <c>WorldObject</c> from anywhere else — that is what makes upstream's mutex-free entity code
/// safe. There is no tick loop yet, so sessions call in from their own tasks and a single lock
/// stands in for that guarantee. The <c>TickScheduler</c> the real design needs already exists and
/// is unused; this lock is what it replaces.
/// </para>
/// </remarks>
public sealed class Map(uint mapId, TerrainMap terrain, IGridObjectLoader? gridObjects = null)
{
    private readonly Dictionary<CellCoord, List<WorldObject>> _cells = [];
    private readonly Dictionary<ObjectGuid, WorldObject> _objects = [];
    private readonly Dictionary<ObjectGuid, Player> _players = [];
    private readonly HashSet<GridCoord> _loadedGrids = [];
    private readonly Lock _lock = new();

    public uint MapId { get; } = mapId;

    /// <summary>The terrain under this map.</summary>
    public TerrainMap Terrain { get; } = terrain;

    /// <summary>How far players can see here.</summary>
    public float VisibilityDistance { get; init; } = MapCoordinates.DefaultVisibilityDistance;

    /// <summary>How many players are on this map.</summary>
    public int PlayerCount
    {
        get
        {
            lock (_lock)
            {
                return _players.Count;
            }
        }
    }

    /// <summary>How many objects of every kind are on this map.</summary>
    public int ObjectCount
    {
        get
        {
            lock (_lock)
            {
                return _objects.Count;
            }
        }
    }

    /// <summary>How many grids have had their spawns loaded.</summary>
    public int LoadedGridCount
    {
        get
        {
            lock (_lock)
            {
                return _loadedGrids.Count;
            }
        }
    }

    /// <summary>Every player currently on the map.</summary>
    public IReadOnlyList<Player> Players
    {
        get
        {
            lock (_lock)
            {
                return [.. _players.Values];
            }
        }
    }

    /// <summary>Finds an object by guid.</summary>
    public WorldObject? Find(ObjectGuid objectGuid)
    {
        lock (_lock)
        {
            return _objects.GetValueOrDefault(objectGuid);
        }
    }

    /// <summary>Finds a player by guid.</summary>
    public Player? FindPlayer(ObjectGuid objectGuid)
    {
        lock (_lock)
        {
            return _players.GetValueOrDefault(objectGuid);
        }
    }

    /// <summary>
    /// Puts a player on the map and exchanges create blocks with everything already in range.
    /// </summary>
    public async Task AddAsync(Player player, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);

        List<WorldObject> nearby;

        lock (_lock)
        {
            // Spawns first. A player added before the grids around it are loaded sees an empty
            // world until it happens to walk into a cell it has already been told about.
            EnsureGridsLoadedLocked(player.Position);

            FileLocked(player);
            _players[player.Guid] = player;

            nearby = FindInRangeLocked(player.Position, VisibilityDistance, player);
        }

        // Both directions: the arriving player has to learn about everything, and every player
        // already here about it.
        foreach (WorldObject other in nearby)
        {
            await MakeVisibleAsync(player, other, cancellationToken).ConfigureAwait(false);

            if (other is Player observer)
            {
                await MakeVisibleAsync(observer, player, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Takes a player off the map and tells everyone who could see it.</summary>
    public async Task RemoveAsync(Player player, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);

        lock (_lock)
        {
            _players.Remove(player.Guid);
            UnfileLocked(player);
        }

        foreach (Player other in PlayersWhoSee(player.Guid))
        {
            other.VisibleObjects.Remove(player.Guid);
            await SendDestroyAsync(other, player.Guid, cancellationToken).ConfigureAwait(false);
        }

        player.VisibleObjects.Clear();
    }

    /// <summary>
    /// Moves a player and updates what it and everyone around it can see.
    /// </summary>
    /// <remarks>
    /// Called on every movement packet, so the work is proportional to the cells in range rather
    /// than to the map's population. Objects that were visible and no longer are get a destroy;
    /// objects newly in range get a create. Everything else is left alone.
    /// </remarks>
    public async Task RelocateAsync(Player player, Position position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);

        List<WorldObject> nearby;

        lock (_lock)
        {
            EnsureGridsLoadedLocked(position);

            CellCoord cell = MapCoordinates.CellFor(position.X, position.Y);

            if (cell != player.Cell)
            {
                CellFor(player).Remove(player);
                player.Cell = cell;
                CellAt(cell).Add(player);
            }

            player.Position = position;
            nearby = FindInRangeLocked(position, VisibilityDistance, player);
        }

        HashSet<ObjectGuid> stillVisible = [];

        foreach (WorldObject other in nearby)
        {
            stillVisible.Add(other.Guid);

            await MakeVisibleAsync(player, other, cancellationToken).ConfigureAwait(false);

            if (other is Player observer)
            {
                await MakeVisibleAsync(observer, player, cancellationToken).ConfigureAwait(false);
            }
        }

        // Anything the player could see but no longer can.
        foreach (ObjectGuid gone in player.VisibleObjects.Where(guid => !stillVisible.Contains(guid)).ToList())
        {
            player.VisibleObjects.Remove(gone);
            await SendDestroyAsync(player, gone, cancellationToken).ConfigureAwait(false);
        }

        // And the mirror: players who could see this one but have been left behind.
        foreach (Player other in PlayersWhoSee(player.Guid))
        {
            if (!stillVisible.Contains(other.Guid))
            {
                other.VisibleObjects.Remove(player.Guid);
                await SendDestroyAsync(other, player.Guid, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Relays a movement packet to everyone who can see the mover, but not the mover itself.
    /// </summary>
    /// <remarks>
    /// The client that sent the movement has already applied it locally; echoing it back makes the
    /// character stutter.
    /// </remarks>
    public async Task BroadcastMovementAsync(
        Player mover,
        Opcode opcode,
        MovementInfo movement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mover);

        foreach (Player other in PlayersWhoSee(mover.Guid))
        {
            if (other.Connection is not null)
            {
                await other.Connection
                    .SendMovementAsync(opcode, mover.Guid, movement, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Objects within <paramref name="radius"/> of a point, excluding <paramref name="exclude"/>.</summary>
    public IReadOnlyList<WorldObject> FindInRange(Position position, float radius, WorldObject? exclude = null)
    {
        lock (_lock)
        {
            return FindInRangeLocked(position, radius, exclude);
        }
    }

    /// <summary>
    /// Loads the spawns of every grid a player at <paramref name="position"/> could see into.
    /// </summary>
    /// <remarks>
    /// Loading is one-way for now: a grid, once loaded, stays. Unloading needs to know that no
    /// player is left anywhere near it, and there is no tick to notice that on — see TODO.md.
    /// <para>
    /// The load runs under the map lock, which means building creatures blocks anyone else touching
    /// this map. That is the same trade the lock itself is: a stand-in for the per-map worker task
    /// PLAN.md §4.2 describes, where this would be ordinary in-line work.
    /// </para>
    /// </remarks>
    private void EnsureGridsLoadedLocked(Position position)
    {
        if (gridObjects is null)
        {
            return;
        }

        foreach (CellCoord cell in MapCoordinates.CellsInRange(position.X, position.Y, VisibilityDistance))
        {
            GridCoord grid = MapCoordinates.GridOf(cell);

            if (!_loadedGrids.Add(grid))
            {
                continue;
            }

            foreach (WorldObject spawned in gridObjects.Load(MapId, grid))
            {
                FileLocked(spawned);
            }
        }
    }

    /// <summary>Files an object into the cell its position falls in.</summary>
    /// <remarks>
    /// The cell is computed before the object is filed under it. Filing first would put it in
    /// whatever cell the field happened to hold — cell (0, 0) for a fresh object — and range queries
    /// would never find it again, with nothing to show for it but an invisible creature.
    /// </remarks>
    private void FileLocked(WorldObject worldObject)
    {
        worldObject.Cell = MapCoordinates.CellFor(worldObject.Position.X, worldObject.Position.Y);

        _objects[worldObject.Guid] = worldObject;
        CellAt(worldObject.Cell).Add(worldObject);
    }

    private void UnfileLocked(WorldObject worldObject)
    {
        _objects.Remove(worldObject.Guid);
        CellFor(worldObject).Remove(worldObject);
    }

    private List<WorldObject> FindInRangeLocked(Position position, float radius, WorldObject? exclude)
    {
        List<WorldObject> found = [];
        float radiusSquared = radius * radius;

        foreach (CellCoord cell in MapCoordinates.CellsInRange(position.X, position.Y, radius))
        {
            if (!_cells.TryGetValue(cell, out List<WorldObject>? occupants))
            {
                continue;
            }

            foreach (WorldObject candidate in occupants)
            {
                if (ReferenceEquals(candidate, exclude))
                {
                    continue;
                }

                // The cell sweep is a bounding square, so the circle still has to be checked.
                if (position.GetExactDist2dSq(candidate.Position) <= radiusSquared)
                {
                    found.Add(candidate);
                }
            }
        }

        return found;
    }

    private List<Player> PlayersWhoSee(ObjectGuid objectGuid)
    {
        lock (_lock)
        {
            return [.. _players.Values.Where(player => player.VisibleObjects.Contains(objectGuid))];
        }
    }

    private static async Task MakeVisibleAsync(Player viewer, WorldObject target, CancellationToken cancellationToken)
    {
        // Already visible: nothing to send. Without this check every movement packet would re-send
        // a full create block for everything in range.
        if (!viewer.VisibleObjects.Add(target.Guid))
        {
            return;
        }

        if (viewer.Connection is not null)
        {
            await viewer.Connection.SendCreateAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SendDestroyAsync(Player viewer, ObjectGuid objectGuid, CancellationToken cancellationToken)
    {
        if (viewer.Connection is not null)
        {
            await viewer.Connection.SendDestroyAsync(objectGuid, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<WorldObject> CellFor(WorldObject worldObject) => CellAt(worldObject.Cell);

    private List<WorldObject> CellAt(CellCoord cell)
    {
        if (!_cells.TryGetValue(cell, out List<WorldObject>? occupants))
        {
            occupants = [];
            _cells[cell] = occupants;
        }

        return occupants;
    }
}

/// <summary>Maps, created on first use.</summary>
public sealed class MapManager(TerrainManager terrain, IGridObjectLoader? gridObjects = null)
{
    private readonly Dictionary<uint, Map> _maps = [];
    private readonly Lock _lock = new();

    public Map GetMap(uint mapId)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out Map? map))
            {
                map = new Map(mapId, terrain.GetMap(mapId), gridObjects);
                _maps[mapId] = map;
            }

            return map;
        }
    }

    /// <summary>Every map that has been touched, for diagnostics.</summary>
    public IReadOnlyList<Map> ActiveMaps
    {
        get
        {
            lock (_lock)
            {
                return [.. _maps.Values];
            }
        }
    }
}
