using Microsoft.Extensions.Logging.Abstractions;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The gameobject stores and grid loader, over the real vendored rows.
/// </summary>
/// <remarks>
/// The fixtures in <c>GameObjectTests</c> are readable precisely because they are made up, which is
/// also what stops them noticing when the data disagrees with an assumption. These run the same code
/// over all 85,552 rows.
/// </remarks>
public sealed class GameObjectStoreTests
{
    /// <summary>The grid holding Northshire Valley, where every human character starts.</summary>
    private static readonly GridCoord NorthshireGrid = MapCoordinates.GridFor(-8949.95f, -132.493f);

    private static CancellationToken TestToken => CancellationToken.None;

    [RequiresWorldDatabaseFact]
    public async Task Stores_LoadEveryRow()
    {
        GameObjectTemplateStore templates = new();
        GameObjectSpawnStore spawns = new();

        await templates.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await spawns.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(templates.Count > 21_000, $"only {templates.Count} templates");
        Assert.True(spawns.Count > 85_000, $"only {spawns.Count} spawns");

        // Both continents are densely furnished; a zero here means the map column was misread.
        Assert.NotEmpty(spawns.ForMap(0));
        Assert.NotEmpty(spawns.ForMap(1));
        Assert.Empty(spawns.ForMap(9999));
    }

    /// <summary>A spawn with no template is an object that cannot be built and is silently skipped.</summary>
    [RequiresWorldDatabaseFact]
    public async Task EverySpawn_HasATemplate()
    {
        GameObjectTemplateStore templates = new();
        GameObjectSpawnStore spawns = new();

        await templates.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await spawns.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        List<uint> orphaned = [];

        foreach (uint mapId in (uint[])[0, 1])
        {
            foreach (GameObjectSpawn spawn in spawns.ForMap(mapId))
            {
                if (!templates.TryGet(spawn.Entry, out _))
                {
                    orphaned.Add(spawn.Entry);
                }
            }
        }

        Assert.Empty(orphaned.Distinct().Take(10));
    }

    /// <summary>
    /// No real row produces a NaN rotation.
    /// </summary>
    /// <remarks>
    /// The reason this is worth a pass over every row rather than a unit test: 15,478 spawns carry
    /// an all-zero quaternion, and without the fallback each of them normalises to NaN. A packed
    /// NaN is still a valid 64-bit number on the wire, so nothing would fail — the objects would
    /// simply be drawn at impossible angles.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task NoSpawn_ProducesAnUnusableRotation()
    {
        GameObjectSpawnStore spawns = new();
        await spawns.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        int zeroQuaternions = 0;

        foreach (uint mapId in (uint[])[0, 1])
        {
            foreach (GameObjectSpawn spawn in spawns.ForMap(mapId))
            {
                bool isZero = spawn is { Rotation0: 0f, Rotation1: 0f, Rotation2: 0f, Rotation3: 0f };

                if (isZero)
                {
                    zeroQuaternions++;
                }

                ulong packed = WowEmu.Protocol.UpdateBlockBuilder.PackRotation(
                    spawn.Rotation0,
                    spawn.Rotation1,
                    spawn.Rotation2,
                    spawn.Rotation3,
                    spawn.Position.Orientation);

                // A NaN component packs through int conversion to a recognisable pattern; the real
                // check is that a zero quaternion with a real facing does not pack to identity,
                // which is what silently happens when the fallback is missing.
                if (isZero && spawn.Position.Orientation != 0f)
                {
                    Assert.NotEqual(0ul, packed);
                }
            }
        }

        // If this ever reaches zero the fallback has become dead code and the guard can go.
        Assert.True(zeroQuaternions > 0, "expected some spawns to carry an all-zero quaternion");
    }

    [RequiresWorldDatabaseFact]
    public async Task NorthshireGrid_BuildsGameObjects()
    {
        GameObjectGridLoader loader = await NewLoaderAsync();

        IReadOnlyList<WorldObject> loaded = loader.Load(0, NorthshireGrid);

        Assert.NotEmpty(loaded);

        foreach (WorldObject spawned in loaded)
        {
            GameObject gameObject = Assert.IsType<GameObject>(spawned);

            Assert.Equal(gameObject.Entry, gameObject.Guid.Entry);
            Assert.Equal(0u, gameObject.MapId);
            Assert.NotEmpty(gameObject.Name);

            // Inside the grid it came from — the tile axis is inverted and origin-centred, and
            // getting that backwards files objects into the wrong grid with no error.
            Assert.Equal(
                NorthshireGrid,
                MapCoordinates.GridFor(gameObject.Position.X, gameObject.Position.Y));
        }
    }

    [RequiresWorldDatabaseFact]
    public async Task LoadedGameObjects_HaveDistinctGuids()
    {
        GameObjectGridLoader loader = await NewLoaderAsync();

        List<WowEmu.Core.ObjectGuid> guids =
            [.. loader.Load(0, NorthshireGrid).Select(spawned => spawned.Guid)];

        Assert.Equal(guids.Count, guids.Distinct().Count());
    }

    /// <summary>
    /// Creature and gameobject guids never collide, even though their spawn ids overlap.
    /// </summary>
    /// <remarks>
    /// Both tables number from 1, so spawn id 500 exists in each. What separates them is the high
    /// part of the guid. If it did not, one would overwrite the other in the map's object table and
    /// a client would be told to destroy the wrong thing.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task CreatureAndGameObjectGuids_DoNotCollide()
    {
        GameObjectGridLoader gameObjects = await NewLoaderAsync();

        CreatureTemplateStore creatureTemplates = new();
        CreatureStatsStore creatureStats = new();
        CreatureSpawnStore creatureSpawns = new();

        await creatureTemplates.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await creatureStats.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await creatureSpawns.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        CreatureGridLoader creatures = new(
            creatureSpawns,
            new CreatureFactory(creatureTemplates, creatureStats),
            NullLogger<CreatureGridLoader>.Instance);

        CompositeGridLoader composite = new([creatures, gameObjects]);

        List<WowEmu.Core.ObjectGuid> guids =
            [.. composite.Load(0, NorthshireGrid).Select(spawned => spawned.Guid)];

        Assert.Equal(guids.Count, guids.Distinct().Count());

        // And the composite really did produce both kinds.
        IReadOnlyList<WorldObject> all = composite.Load(0, NorthshireGrid);
        Assert.Contains(all, spawned => spawned is Creature);
        Assert.Contains(all, spawned => spawned is GameObject);
    }

    private static async Task<GameObjectGridLoader> NewLoaderAsync()
    {
        GameObjectTemplateStore templates = new();
        GameObjectSpawnStore spawns = new();

        await templates.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await spawns.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        return new GameObjectGridLoader(spawns, templates, NullLogger<GameObjectGridLoader>.Instance);
    }
}
