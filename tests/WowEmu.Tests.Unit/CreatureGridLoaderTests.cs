using Microsoft.Extensions.Logging.Abstractions;
using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The whole spawn path over real rows: table → factory → creature filed in a grid.
/// </summary>
/// <remarks>
/// The unit tests around each piece use fixtures, which is what makes them readable — and also what
/// stops them noticing that the real data disagrees with an assumption. These run the same code over
/// the vendored rows and check the result is something a client could draw.
/// </remarks>
public sealed class CreatureGridLoaderTests
{
    /// <summary>
    /// The grid holding Northshire Valley, where every human character starts.
    /// </summary>
    /// <remarks>
    /// Chosen because it is the most consequential grid in the game for us: it is the first one a
    /// new character's client ever asks to see, so a fault here is a fault on everybody's first
    /// login.
    /// </remarks>
    private static readonly GridCoord NorthshireGrid = MapCoordinates.GridFor(-8949.95f, -132.493f);

    [RequiresWorldDatabaseFact]
    public async Task NorthshireGrid_BuildsCreaturesAClientCouldDraw()
    {
        CreatureGridLoader loader = await NewLoaderAsync();

        IReadOnlyList<WorldObject> loaded = loader.Load(0, NorthshireGrid);

        Assert.NotEmpty(loaded);

        foreach (WorldObject spawned in loaded)
        {
            Creature creature = Assert.IsType<Creature>(spawned);

            // Each of these renders wrong, or not at all, if it is left at zero — and none of them
            // produces an error when it is.
            Assert.True(creature.DisplayId > 0, $"entry {creature.Entry} has no display id");
            Assert.True(creature.MaxHealth > 0, $"entry {creature.Entry} has no health");
            Assert.True(creature.Health > 0, $"entry {creature.Entry} spawned dead");
            Assert.True(creature.BoundingRadius > 0, $"entry {creature.Entry} has no bounding radius");
            Assert.True(creature.CombatReach > 0, $"entry {creature.Entry} has no combat reach");
            Assert.True(creature.Level > 0, $"entry {creature.Entry} is level 0");
            Assert.NotEmpty(creature.Name);

            Assert.True(creature.Guid.IsCreature);
            Assert.Equal(creature.Entry, creature.Guid.Entry);
            Assert.Equal(0u, creature.MapId);
        }
    }

    /// <summary>Guids have to be unique, or the client's second copy overwrites its first.</summary>
    [RequiresWorldDatabaseFact]
    public async Task LoadedCreatures_HaveDistinctGuids()
    {
        CreatureGridLoader loader = await NewLoaderAsync();

        List<ObjectGuid> guids = [.. loader.Load(0, NorthshireGrid).Select(spawned => spawned.Guid)];

        Assert.Equal(guids.Count, guids.Distinct().Count());
    }

    /// <summary>
    /// Every creature the grid produces really is inside it.
    /// </summary>
    /// <remarks>
    /// PLAN.md §5.1 records the trap this guards: the tile axis is inverted and origin-centred, and
    /// getting it backwards mirrors the world across the diagonal with no error anywhere. A spawn
    /// filed into the wrong grid is the same fault, one grid at a time.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task LoadedCreatures_AreAllInsideTheGridTheyCameFrom()
    {
        CreatureGridLoader loader = await NewLoaderAsync();

        foreach (WorldObject spawned in loader.Load(0, NorthshireGrid))
        {
            Assert.Equal(
                NorthshireGrid,
                MapCoordinates.GridFor(spawned.Position.X, spawned.Position.Y));
        }
    }

    /// <summary>A grid nothing spawns in answers empty rather than throwing.</summary>
    [RequiresWorldDatabaseFact]
    public async Task EmptyGrid_LoadsNothing()
    {
        CreatureGridLoader loader = await NewLoaderAsync();

        Assert.Empty(loader.Load(0, new GridCoord(0, 0)));
    }

    /// <summary>
    /// Spawns that exclude difficulty 0 stay out of the normal world.
    /// </summary>
    /// <remarks>
    /// 78 rows across the whole table carry such a mask, 54 of them in Icecrown Citadel — a small
    /// number, but ignoring the mask puts every one of them in the world anyway: visible,
    /// targetable and wrong. The loader is what has to honour it, so this checks the loader and not
    /// just the predicate.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task SpawnMask_KeepsOtherDifficultiesOut()
    {
        CreatureSpawnStore spawns = new();
        await spawns.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);

        // Map 631 is Icecrown Citadel, which carries the largest group of them.
        List<CreatureSpawn> excluded =
            [.. spawns.ForMap(631).Where(spawn => !spawn.SpawnsAtDifficulty(0))];

        Assert.NotEmpty(excluded);

        CreatureGridLoader loader = await NewLoaderAsync();
        GridCoord grid = MapCoordinates.GridFor(excluded[0].Position.X, excluded[0].Position.Y);

        HashSet<uint> built = [.. loader.Load(631, grid).OfType<Creature>().Select(creature => creature.SpawnId)];

        foreach (CreatureSpawn spawn in excluded)
        {
            Assert.DoesNotContain(spawn.SpawnId, built);
        }
    }

    private static async Task<CreatureGridLoader> NewLoaderAsync()
    {
        CreatureTemplateStore templates = new();
        CreatureStatsStore stats = new();
        CreatureSpawnStore spawns = new();

        await templates.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);
        await stats.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);
        await spawns.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);

        return new CreatureGridLoader(
            spawns,
            new CreatureFactory(templates, stats),
            NullLogger<CreatureGridLoader>.Instance);
    }
}
