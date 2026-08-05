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
    /// <summary>Notes that an object has come into view, to go out at the next flush.</summary>
    void QueueCreate(WorldObject other);

    /// <summary>Notes that an object has left view, so the client destroys its copy.</summary>
    void QueueDestroy(ObjectGuid objectGuid);

    /// <summary>
    /// Emits everything queued since the last flush as one packet.
    /// </summary>
    /// <remarks>
    /// Called once per map update per player. Batching is not an optimisation here so much as a
    /// correction: 131 creatures stand within sight of the human starting point, and sending a
    /// packet each meant 131 packets and 131 headers where upstream sends one.
    /// </remarks>
    void FlushUpdates();

    /// <summary>Relays another player's movement. Immediate — movement is not batched.</summary>
    void SendMovement(Opcode opcode, ObjectGuid mover, MovementInfo movement);

    /// <summary>Runs the packets this session queued for its map's worker.</summary>
    void DrainMapPackets(uint diff);
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
/// <b>There is no lock here, and there must not be one.</b> PLAN.md §4.2 rule 1 is that a
/// <c>WorldObject</c> is only ever touched on its map's worker, and that is what makes upstream's
/// mutex-free entity code safe. What enforces it is the <i>ordering of a tick</i>, not a mutex:
/// <list type="number">
/// <item>the world loop drains its own sessions — that is when a player is added or removed;</item>
/// <item>then, and only then, the map workers run.</item>
/// </list>
/// The two never overlap, so a login touching a map from the world thread is safe for the same
/// reason it is safe upstream. Adding a lock here would not make anything safer; it would hide the
/// day that ordering breaks.
/// </remarks>
public sealed class Map(uint mapId, TerrainMap terrain, IGridObjectLoader? gridObjects = null)
{
    private readonly Dictionary<CellCoord, List<WorldObject>> _cells = [];
    private readonly Dictionary<ObjectGuid, WorldObject> _objects = [];
    private readonly Dictionary<ObjectGuid, Player> _players = [];
    private readonly HashSet<GridCoord> _loadedGrids = [];

    public uint MapId { get; } = mapId;

    /// <summary>Which phase of the round-robin updates this map. See <see cref="MapManager"/>.</summary>
    public MapKind Kind { get; init; } = MapKind.Continent;

    /// <summary>How many times <see cref="Update"/> has run with a non-zero gameplay diff.</summary>
    public long FullUpdates { get; private set; }

    /// <summary>How many times <see cref="Update"/> has run at all.</summary>
    public long TotalUpdates { get; private set; }

    /// <summary>The terrain under this map.</summary>
    public TerrainMap Terrain { get; } = terrain;

    /// <summary>How far players can see here.</summary>
    public float VisibilityDistance { get; init; } = MapCoordinates.DefaultVisibilityDistance;

    /// <summary>How many players are on this map.</summary>
    public int PlayerCount => _players.Count;

    /// <summary>How many objects of every kind are on this map.</summary>
    public int ObjectCount => _objects.Count;

    /// <summary>How many grids have had their spawns loaded.</summary>
    public int LoadedGridCount => _loadedGrids.Count;

    /// <summary>Every player currently on the map.</summary>
    public IReadOnlyList<Player> Players => [.. _players.Values];

    /// <summary>Finds an object by guid.</summary>
    public WorldObject? Find(ObjectGuid objectGuid) => _objects.GetValueOrDefault(objectGuid);

    /// <summary>Finds a player by guid.</summary>
    public Player? FindPlayer(ObjectGuid objectGuid) => _players.GetValueOrDefault(objectGuid);

    /// <summary>
    /// Advances the map by one tick.
    /// </summary>
    /// <param name="gameplayDiff">
    /// Milliseconds of gameplay time. <b>Zero when this map is out of phase</b>, which happens three
    /// ticks in four and is not a skipped tick — see <see cref="MapManager.PhaseCount"/>.
    /// </param>
    /// <param name="sessionDiff">
    /// Milliseconds of real time. Always the true elapsed time, because sessions are serviced on
    /// every tick whatever the phase — a player must not wait up to four ticks to be heard.
    /// </param>
    /// <remarks>
    /// Port of <c>Map::Update</c>. Sessions first, so a player's own packets are applied before
    /// anything is decided about them, and the flush last, so everything a tick produced for a given
    /// client leaves as one packet.
    /// </remarks>
    public void Update(uint gameplayDiff, uint sessionDiff)
    {
        TotalUpdates++;

        if (gameplayDiff > 0)
        {
            FullUpdates++;
        }

        // Materialised: a session's packets can add or remove players, and iterating the dictionary
        // while that happens would throw.
        foreach (Player player in Players)
        {
            player.Connection?.DrainMapPackets(sessionDiff);
        }

        // Last, and unconditional. A player whose map is out of phase still had things happen to it
        // during the session pass, and holding those until the next full update would show as a
        // visible stutter every fourth tick.
        foreach (Player player in Players)
        {
            player.Connection?.FlushUpdates();
        }
    }

    /// <summary>
    /// Puts a player on the map and exchanges create blocks with everything already in range.
    /// </summary>
    public void Add(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        // Spawns first. A player added before the grids around it are loaded sees an empty
        // world until it happens to walk into a cell it has already been told about.
        EnsureGridsLoaded(player.Position);

        File(player);
        _players[player.Guid] = player;

        // Both directions: the arriving player has to learn about everything, and every player
        // already here about it.
        foreach (WorldObject other in FindInRangeCore(player.Position, VisibilityDistance, player))
        {
            MakeVisible(player, other);

            if (other is Player observer)
            {
                MakeVisible(observer, player);
            }
        }
    }

    /// <summary>Takes a player off the map and tells everyone who could see it.</summary>
    public void Remove(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        _players.Remove(player.Guid);
        Unfile(player);

        foreach (Player other in PlayersWhoSeeCore(player.Guid))
        {
            other.VisibleObjects.Remove(player.Guid);
            SendDestroy(other, player.Guid);
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
    public void Relocate(Player player, Position position)
    {
        ArgumentNullException.ThrowIfNull(player);

        EnsureGridsLoaded(position);

        CellCoord cell = MapCoordinates.CellFor(position.X, position.Y);

        if (cell != player.Cell)
        {
            CellFor(player).Remove(player);
            player.Cell = cell;
            CellAt(cell).Add(player);
        }

        player.Position = position;

        HashSet<ObjectGuid> stillVisible = [];

        foreach (WorldObject other in FindInRangeCore(position, VisibilityDistance, player))
        {
            stillVisible.Add(other.Guid);

            MakeVisible(player, other);

            if (other is Player observer)
            {
                MakeVisible(observer, player);
            }
        }

        // Anything the player could see but no longer can.
        foreach (ObjectGuid gone in player.VisibleObjects.Where(guid => !stillVisible.Contains(guid)).ToList())
        {
            player.VisibleObjects.Remove(gone);
            SendDestroy(player, gone);
        }

        // And the mirror: players who could see this one but have been left behind.
        foreach (Player other in PlayersWhoSeeCore(player.Guid))
        {
            if (!stillVisible.Contains(other.Guid))
            {
                other.VisibleObjects.Remove(player.Guid);
                SendDestroy(other, player.Guid);
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
    public void BroadcastMovement(Player mover, Opcode opcode, MovementInfo movement)
    {
        ArgumentNullException.ThrowIfNull(mover);

        foreach (Player other in PlayersWhoSeeCore(mover.Guid))
        {
            other.Connection?.SendMovement(opcode, mover.Guid, movement);
        }
    }

    /// <summary>Objects within <paramref name="radius"/> of a point, excluding <paramref name="exclude"/>.</summary>
    public IReadOnlyList<WorldObject> FindInRange(Position position, float radius, WorldObject? exclude = null) =>
        FindInRangeCore(position, radius, exclude);

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
    private void EnsureGridsLoaded(Position position)
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
                File(spawned);
            }
        }
    }

    /// <summary>Files an object into the cell its position falls in.</summary>
    /// <remarks>
    /// The cell is computed before the object is filed under it. Filing first would put it in
    /// whatever cell the field happened to hold — cell (0, 0) for a fresh object — and range queries
    /// would never find it again, with nothing to show for it but an invisible creature.
    /// </remarks>
    private void File(WorldObject worldObject)
    {
        worldObject.Cell = MapCoordinates.CellFor(worldObject.Position.X, worldObject.Position.Y);

        _objects[worldObject.Guid] = worldObject;
        CellAt(worldObject.Cell).Add(worldObject);
    }

    private void Unfile(WorldObject worldObject)
    {
        _objects.Remove(worldObject.Guid);
        CellFor(worldObject).Remove(worldObject);
    }

    private List<WorldObject> FindInRangeCore(Position position, float radius, WorldObject? exclude)
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

    /// <summary>
    /// Players whose client has been told about <paramref name="objectGuid"/>.
    /// </summary>
    /// <remarks>
    /// Materialised rather than lazy: callers remove from the visible sets while iterating, and a
    /// deferred LINQ query over <c>_players</c> would be enumerating the collection it is mutating.
    /// </remarks>
    private List<Player> PlayersWhoSeeCore(ObjectGuid objectGuid) =>
        [.. _players.Values.Where(player => player.VisibleObjects.Contains(objectGuid))];

    private static void MakeVisible(Player viewer, WorldObject target)
    {
        // Already visible: nothing to send. Without this check every movement packet would re-send
        // a full create block for everything in range.
        if (!viewer.VisibleObjects.Add(target.Guid))
        {
            return;
        }

        viewer.Connection?.QueueCreate(target);
    }

    private static void SendDestroy(Player viewer, ObjectGuid objectGuid) =>
        viewer.Connection?.QueueDestroy(objectGuid);

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

