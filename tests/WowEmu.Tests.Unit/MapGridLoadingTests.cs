using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Loading a grid's spawns, and how they reach a client.
/// </summary>
/// <remarks>
/// PLAN.md §6 keeps grid creation (terrain) and grid object loading (database spawns) as two
/// separate steps. These pin the second: a grid is loaded the first time a player can see into it,
/// exactly once, and what comes out of it is filed like any other object on the map.
/// </remarks>
public sealed class MapGridLoadingTests
{
    /// <summary>A player arriving must see the creatures already standing around it.</summary>
    [Fact]
    public void ArrivingPlayer_IsSentTheSpawnsAroundIt()
    {
        RecordingGridLoader loader = new();
        loader.PlaceAt(0f, 0f, count: 3);

        Map map = NewMap(loader);
        (Player player, RecordingConnection link) = NewPlayer(1, 0f, 0f);

        map.Add(player);

        Assert.Equal(3, link.Created.Count);
        Assert.Equal(3, player.VisibleObjects.Count);

        // The player itself plus the three spawns.
        Assert.Equal(4, map.ObjectCount);
        Assert.Equal(1, map.PlayerCount);
    }

    /// <summary>
    /// A spawn in a loaded grid but out of visibility range is filed without being sent.
    /// </summary>
    /// <remarks>
    /// A grid is 533 yards and visibility is 100, so most of a loaded grid is legitimately out of
    /// sight. Sending the whole grid would be a create block per creature on every zone the player
    /// walks past.
    /// </remarks>
    [Fact]
    public void SpawnsInRangeAreSent_ButNotTheRestOfTheGrid()
    {
        RecordingGridLoader loader = new();
        loader.PlaceAt(0f, 0f, count: 1);
        loader.PlaceAt(300f, 0f, count: 1);

        Map map = NewMap(loader);
        (Player player, RecordingConnection link) = NewPlayer(1, 0f, 0f);

        map.Add(player);

        Assert.Single(link.Created);
        Assert.Equal(3, map.ObjectCount);
    }

    /// <summary>
    /// A grid is asked for once. Reloading would build a second copy of every creature in it, each
    /// with the same guid, and the client would be told to create things it already has.
    /// </summary>
    [Fact]
    public void Grids_AreLoadedAtMostOnce()
    {
        RecordingGridLoader loader = new();
        loader.PlaceAt(0f, 0f, count: 1);

        Map map = NewMap(loader);
        (Player player, _) = NewPlayer(1, 0f, 0f);

        map.Add(player);

        int afterArrival = loader.Requested.Count;
        Assert.Equal(afterArrival, loader.Requested.Distinct().Count());

        // Walking about inside the same grids must not ask again.
        for (int step = 1; step <= 10; step++)
        {
            map.Relocate(player, new Position(step * 5f, 0f, 0f, 0f));
        }

        Assert.Equal(afterArrival, loader.Requested.Count);
        Assert.Equal(2, map.ObjectCount);
    }

    /// <summary>Walking towards an unvisited grid loads it and reveals what is in it.</summary>
    [Fact]
    public void WalkingIntoANewGrid_LoadsIt()
    {
        RecordingGridLoader loader = new();
        loader.PlaceAt(0f, 0f, count: 1);
        loader.PlaceAt(-2000f, 0f, count: 2);

        Map map = NewMap(loader);
        (Player player, RecordingConnection link) = NewPlayer(1, 0f, 0f);

        map.Add(player);
        Assert.Single(link.Created);

        int gridsBefore = map.LoadedGridCount;

        map.Relocate(player, new Position(-2000f, 0f, 0f, 0f));

        Assert.True(map.LoadedGridCount > gridsBefore);
        Assert.Equal(3, link.Created.Count);

        // The first grid's creature is out of range now, so it is destroyed rather than forgotten.
        Assert.Single(link.Destroyed);
    }

    /// <summary>
    /// A map built without a loader still works. That is what keeps the two steps separable — and
    /// what lets every other map test run without a database.
    /// </summary>
    [Fact]
    public void MapWithoutALoader_HasNoSpawns()
    {
        Map map = new(0, new TerrainMap(0, Path.GetTempPath()));
        (Player player, RecordingConnection link) = NewPlayer(1, 0f, 0f);

        map.Add(player);

        Assert.Empty(link.Created);
        Assert.Equal(0, map.LoadedGridCount);
        Assert.Equal(1, map.ObjectCount);
    }

    /// <summary>
    /// Spawns outlive the player who caused their grid to load.
    /// </summary>
    /// <remarks>
    /// A creature belongs to the grid, not to whoever walked past it. Removing it with the last
    /// player would mean the next arrival sees an empty zone, since the grid is already marked
    /// loaded and would never be asked for again.
    /// </remarks>
    [Fact]
    public void Spawns_StayOnTheMapAfterThePlayerLeaves()
    {
        RecordingGridLoader loader = new();
        loader.PlaceAt(0f, 0f, count: 2);

        Map map = NewMap(loader);
        (Player first, _) = NewPlayer(1, 0f, 0f);

        map.Add(first);
        map.Remove(first);

        Assert.Equal(0, map.PlayerCount);
        Assert.Equal(2, map.ObjectCount);

        // And the next arrival is told about them without the grid being loaded a second time.
        int requestsBefore = loader.Requested.Count;
        (Player second, RecordingConnection secondLink) = NewPlayer(2, 0f, 0f);

        map.Add(second);

        Assert.Equal(2, secondLink.Created.Count);
        Assert.Equal(requestsBefore, loader.Requested.Count);
        Assert.Equal(2, loader.Built.Count);
    }

    private static CancellationToken TestToken => CancellationToken.None;

    private static Map NewMap(IGridObjectLoader loader) =>
        new(0, new TerrainMap(0, Path.GetTempPath()), loader);

    private static (Player Player, RecordingConnection Connection) NewPlayer(uint id, float x, float y)
    {
        CharacterSummary summary = new(
            id, $"Player{id}", 1, 1, 0, 0, 0, 0, 0, 0, 1, 12, 0, x, y, 0f, 0, 0, 0);

        ChrRacesEntry race = new(1, 0, 1, 49, 50, 7, 0, 0, "Human", 0);
        ChrClassesEntry characterClass = new(1, 1, "Warrior", 4, 0);
        PlayerBaseStats stats = new(20, 0, 23, 20, 22, 20, 20);

        Player player = Player.Create(summary, race, characterClass, stats);

        RecordingConnection connection = new();
        player.Connection = connection;

        return (player, connection);
    }

    /// <summary>
    /// Stands in for the creature loader: remembers which grids were asked for, and hands back
    /// objects placed at chosen coordinates.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <see cref="Creature"/>. The map's contract is with
    /// <see cref="WorldObject"/> — that is the whole point of Phase 6's generalisation — and a stub
    /// proves the map never reaches past it. What a real creature contains is
    /// <c>CreatureTests</c>'s job.
    /// </remarks>
    private sealed class RecordingGridLoader : IGridObjectLoader
    {
        private readonly List<(Position Position, int Count)> _placements = [];
        private uint _nextCounter = 1;

        public List<GridCoord> Requested { get; } = [];

        public List<WorldObject> Built { get; } = [];

        public void PlaceAt(float x, float y, int count) =>
            _placements.Add((new Position(x, y, 0f, 0f), count));

        public IReadOnlyList<WorldObject> Load(uint mapId, GridCoord grid)
        {
            Requested.Add(grid);

            List<WorldObject> loaded = [];

            foreach ((Position position, int count) in _placements)
            {
                if (MapCoordinates.GridFor(position.X, position.Y) != grid)
                {
                    continue;
                }

                for (int i = 0; i < count; i++)
                {
                    StubWorldObject spawned = new(ObjectGuid.Create(HighGuid.Unit, 1, _nextCounter++))
                    {
                        MapId = mapId,
                        Position = position,
                    };

                    loaded.Add(spawned);
                    Built.Add(spawned);
                }
            }

            return loaded;
        }
    }

    /// <summary>The least an object can be and still be filed on a map.</summary>
    private sealed class StubWorldObject(ObjectGuid objectGuid)
        : WorldObject(objectGuid, TypeId.Unit, UpdateFields.UNIT_END, TypeMask.CreatureObject);

    /// <summary>Records what the map asked a client to be told, instead of sending it.</summary>
    private sealed class RecordingConnection : IPlayerConnection
    {
        public List<ObjectGuid> Created { get; } = [];

        public List<ObjectGuid> Destroyed { get; } = [];

        public List<ObjectGuid> Moved { get; } = [];

        /// <summary>How many times a tick's worth of updates was flushed.</summary>
        public int Flushes { get; private set; }

        public void QueueCreate(WorldObject other)
        {
            ArgumentNullException.ThrowIfNull(other);

            Created.Add(other.Guid);
        }

        public void QueueDestroy(ObjectGuid objectGuid) => Destroyed.Add(objectGuid);

        public void FlushUpdates() => Flushes++;

        public void DrainMapPackets(uint diff)
        {
        }

        public void SendMovement(Opcode opcode, ObjectGuid mover, MovementInfo movement) => Moved.Add(mover);
    }
}
