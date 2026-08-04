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
    Task SendCreateAsync(Player other, CancellationToken cancellationToken);

    /// <summary>Tells this player an object has left view, so the client destroys its copy.</summary>
    Task SendDestroyAsync(ObjectGuid objectGuid, CancellationToken cancellationToken);

    /// <summary>Relays another player's movement.</summary>
    Task SendMovementAsync(Opcode opcode, ObjectGuid mover, MovementInfo movement, CancellationToken cancellationToken);
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
public sealed class Map(uint mapId, TerrainMap terrain)
{
    private readonly Dictionary<CellCoord, List<Player>> _cells = [];
    private readonly Dictionary<ObjectGuid, Player> _players = [];
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

    /// <summary>Finds a player by guid.</summary>
    public Player? Find(ObjectGuid objectGuid)
    {
        lock (_lock)
        {
            return _players.GetValueOrDefault(objectGuid);
        }
    }

    /// <summary>
    /// Puts a player on the map and exchanges create blocks with everyone already in range.
    /// </summary>
    public async Task AddAsync(Player player, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);

        List<Player> nearby;

        lock (_lock)
        {
            // The cell has to be computed before the player is filed under it — otherwise it
            // lands in whatever cell the field happened to hold and range queries never find it.
            player.Cell = MapCoordinates.CellFor(player.Position.X, player.Position.Y);

            _players[player.Guid] = player;
            CellFor(player).Add(player);

            nearby = FindInRangeLocked(player.Position, VisibilityDistance, player);
        }

        // Both directions: the arriving player has to learn about everyone, and everyone about it.
        foreach (Player other in nearby)
        {
            await MakeVisibleAsync(player, other, cancellationToken).ConfigureAwait(false);
            await MakeVisibleAsync(other, player, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Takes a player off the map and tells everyone who could see it.</summary>
    public async Task RemoveAsync(Player player, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);

        List<Player> watchers;

        lock (_lock)
        {
            _players.Remove(player.Guid);
            CellFor(player).Remove(player);

            watchers = FindInRangeLocked(player.Position, VisibilityDistance, player);
        }

        foreach (Player other in watchers)
        {
            if (other.VisibleObjects.Remove(player.Guid))
            {
                await SendDestroyAsync(other, player.Guid, cancellationToken).ConfigureAwait(false);
            }
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

        List<Player> nearby;

        lock (_lock)
        {
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

        foreach (Player other in nearby)
        {
            stillVisible.Add(other.Guid);

            await MakeVisibleAsync(player, other, cancellationToken).ConfigureAwait(false);
            await MakeVisibleAsync(other, player, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Players within <paramref name="radius"/> of a point, excluding <paramref name="exclude"/>.</summary>
    public IReadOnlyList<Player> FindInRange(Position position, float radius, Player? exclude = null)
    {
        lock (_lock)
        {
            return FindInRangeLocked(position, radius, exclude);
        }
    }

    private List<Player> FindInRangeLocked(Position position, float radius, Player? exclude)
    {
        List<Player> found = [];
        float radiusSquared = radius * radius;

        foreach (CellCoord cell in MapCoordinates.CellsInRange(position.X, position.Y, radius))
        {
            if (!_cells.TryGetValue(cell, out List<Player>? occupants))
            {
                continue;
            }

            foreach (Player candidate in occupants)
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

    private static async Task MakeVisibleAsync(Player viewer, Player target, CancellationToken cancellationToken)
    {
        // Already visible: nothing to send. Without this check every movement packet would re-send
        // a full create block for everyone in range.
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

    private List<Player> CellFor(Player player) => CellAt(player.Cell);

    private List<Player> CellAt(CellCoord cell)
    {
        if (!_cells.TryGetValue(cell, out List<Player>? occupants))
        {
            occupants = [];
            _cells[cell] = occupants;
        }

        return occupants;
    }
}

/// <summary>Maps, created on first use.</summary>
public sealed class MapManager(TerrainManager terrain)
{
    private readonly Dictionary<uint, Map> _maps = [];
    private readonly Lock _lock = new();

    public Map GetMap(uint mapId)
    {
        lock (_lock)
        {
            if (!_maps.TryGetValue(mapId, out Map? map))
            {
                map = new Map(mapId, terrain.GetMap(mapId));
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
