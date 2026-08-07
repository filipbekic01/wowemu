using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Movement;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Patrol routes, and the walk home after a fight.
/// </summary>
/// <remarks>
/// The two generators that were missing. Between them they are the difference between a world where
/// 5,290 guards stand frozen at their posts and one where they walk their beats — and between a
/// creature that gives up on a fight and stays where it was kited to, and one that goes home.
/// </remarks>
public sealed class WaypointTests
{
    /// <summary>A route is walked in order, and then it starts again.</summary>
    /// <remarks>
    /// The wrap is the point. Upstream's creature paths all repeat, and one that stopped at the last
    /// point would leave a guard standing at the far end of its beat for the rest of the session.
    /// </remarks>
    [Fact]
    public void APath_IsWalkedInOrderAndRepeats()
    {
        WaypointMovementGenerator generator = new(Path(
            (10f, 0f), (20f, 0f), (30f, 0f)));

        Creature creature = CreatureFixture.Build();

        Assert.Equal(10f, Next(generator, creature).Destination.X);
        Assert.Equal(20f, Next(generator, creature).Destination.X);
        Assert.Equal(30f, Next(generator, creature).Destination.X);

        // Round again, from the top.
        Assert.Equal(10f, Next(generator, creature).Destination.X);
        Assert.Equal(20f, Next(generator, creature).Destination.X);
    }

    /// <summary>
    /// A point with a delay holds the creature there before it moves on.
    /// </summary>
    /// <remarks>
    /// 2,973 of the 112,797 stored points carry one — the guard who stops at the end of the bridge
    /// and looks around. Ignoring it turns every patrol into a creature pacing without pause.
    /// </remarks>
    [Fact]
    public void APointWithADelay_HoldsTheCreatureThere()
    {
        WaypointMovementGenerator generator = new(
        [
            new Waypoint(new Position(10f, 0f, 0f, 0f), DelayMs: 5000, MoveType: 0),
            new Waypoint(new Position(20f, 0f, 0f, 0f), DelayMs: 0, MoveType: 0),
        ]);

        Creature creature = CreatureFixture.Build();

        // Heads for the first point, which is where it will wait.
        Assert.Equal(10f, Next(generator, creature).Destination.X);

        // Arrived. Four seconds of waiting is not enough.
        Assert.False(generator.TryGetDestination(creature, 2000, out _));
        Assert.False(generator.TryGetDestination(creature, 2000, out _));

        // The fifth second releases it.
        Assert.True(generator.TryGetDestination(creature, 2000, out MovementDecision decision));
        Assert.Equal(20f, decision.Destination.X);
    }

    /// <summary>A run-flagged leg runs; a plain one walks.</summary>
    /// <remarks>
    /// 7,299 of the stored points are flagged to run. Walking them all would be a visible difference
    /// on every patrol that is meant to be hurrying.
    /// </remarks>
    [Fact]
    public void MoveType_DecidesWhetherTheLegIsRun()
    {
        WaypointMovementGenerator generator = new(
        [
            new Waypoint(new Position(10f, 0f, 0f, 0f), DelayMs: 0, MoveType: 0),
            new Waypoint(new Position(20f, 0f, 0f, 0f), DelayMs: 0, MoveType: 1),
        ]);

        Creature creature = CreatureFixture.Build();

        Assert.False(Next(generator, creature).Run);
        Assert.True(Next(generator, creature).Run);
    }

    /// <summary>An empty route asks the creature to go nowhere, rather than looping forever.</summary>
    /// <remarks>
    /// 35 patrolling spawns name a path that is not in <c>waypoint_data</c>. A generator that
    /// indexed into an empty list would throw on the first tick of the first zone containing one.
    /// </remarks>
    [Fact]
    public void AnEmptyPath_GoesNowhere()
    {
        WaypointMovementGenerator generator = new([]);

        Assert.False(generator.TryGetDestination(CreatureFixture.Build(), 1000, out _));
    }

    /// <summary>A spawn with a route actually walks it, through the creature's own update.</summary>
    /// <remarks>
    /// The end-to-end check. Everything above tests the generator in isolation; this one proves the
    /// path reaches it through <c>Creature.Create</c> and that the creature acts on it.
    /// </remarks>
    [Fact]
    public void ASpawnWithARoute_StartsWalkingIt()
    {
        Creature creature = CreatureFixture.Build(
            movementType: 2,
            position: new Position(0f, 0f, 0f, 0f),
            path: Path((10f, 0f), (20f, 0f)));

        Assert.Equal(MovementGeneratorType.Waypoint, creature.Motion.CurrentType);

        CreatureMove? move = creature.Update(100);

        Assert.NotNull(move);
        Assert.Equal(10f, move!.Value.Destination.X, 0.001f);
    }

    /// <summary>A waypoint spawn with no route stands still rather than breaking.</summary>
    [Fact]
    public void ASpawnWithNoRoute_FallsBackToIdle()
    {
        Creature creature = CreatureFixture.Build(movementType: 2, path: null);

        Assert.Equal(MovementGeneratorType.Idle, creature.Motion.CurrentType);
        Assert.Null(creature.Update(10_000));
    }

    // ------------------------------------------------------------------ going home

    /// <summary>
    /// Evading sends the creature home and restores it.
    /// </summary>
    /// <remarks>
    /// The health reset is not cosmetic. Without it a player can pull a creature, run out of range,
    /// come back and find it still wounded — repeat, and anything in the game dies to someone with
    /// no combat ability at all.
    /// </remarks>
    [Fact]
    public void Evading_HealsTheCreatureAndSendsItHome()
    {
        Creature creature = CreatureFixture.Build(position: new Position(0f, 0f, 0f, 0f));

        creature.Position = new Position(60f, 0f, 0f, 0f);
        creature.Health = 1;

        CreatureAi.Evade(creature);

        Assert.Equal(creature.MaxHealth, creature.Health);
        Assert.Equal(MovementGeneratorType.Home, creature.Motion.CurrentType);

        CreatureMove? move = creature.Update(100);

        Assert.NotNull(move);
        Assert.Equal(creature.HomePosition.X, move!.Value.Destination.X, 0.001f);
        Assert.Equal(creature.HomePosition.Y, move.Value.Destination.Y, 0.001f);
    }

    /// <summary>
    /// Arriving home pops the generator, and whatever it interrupted resumes.
    /// </summary>
    /// <remarks>
    /// This is what the stack is for. A patrolling guard dragged into a fight and left alone walks
    /// back to its post and picks its route up again, with nothing having had to remember the route
    /// on its behalf.
    /// </remarks>
    [Fact]
    public void ArrivingHome_ResumesWhatTheCreatureWasDoing()
    {
        Creature creature = CreatureFixture.Build(
            movementType: 2,
            position: new Position(0f, 0f, 0f, 0f),
            path: Path((10f, 0f), (20f, 0f)));

        creature.Position = new Position(60f, 0f, 0f, 0f);

        CreatureAi.Evade(creature);
        Assert.Equal(MovementGeneratorType.Home, creature.Motion.CurrentType);

        // Start the walk home, then let it run to completion.
        Assert.NotNull(creature.Update(100));

        for (int i = 0; i < 500 && creature.Motion.CurrentType == MovementGeneratorType.Home; i++)
        {
            creature.Update(100);
        }

        Assert.Equal(MovementGeneratorType.Waypoint, creature.Motion.CurrentType);
        Assert.Equal(creature.HomePosition.X, creature.Position.X, 0.5f);
    }

    /// <summary>
    /// A creature that evades where it already stands is released immediately.
    /// </summary>
    /// <remarks>
    /// The nastiest case, because nothing about it looks wrong. The distance home is zero, so no
    /// move is issued — and an arrival is normally only reported when a move <i>finishes</i>. The
    /// home generator would then sit on top of the stack waiting for an arrival that no move can
    /// ever produce, and the creature would never wander or patrol again for the rest of the
    /// session. A zero-length journey has to count as arriving.
    /// </remarks>
    [Fact]
    public void EvadingAtHome_DoesNotStrandTheCreature()
    {
        Creature creature = CreatureFixture.Build(
            movementType: 1,
            wanderDistance: 5f,
            position: new Position(0f, 0f, 0f, 0f));

        CreatureAi.Evade(creature);

        // No move — it is already there — but the generator is finished all the same.
        Assert.Null(creature.Update(100));
        Assert.Equal(MovementGeneratorType.Random, creature.Motion.CurrentType);

        // And the wander underneath takes over, which is the thing that would never have happened.
        //
        // Over several attempts rather than one. A wander draws its radius as `distance * sqrt(u)`,
        // so roughly one draw in a hundred lands inside the half-yard minimum and produces no move
        // at all — which is correct behaviour and made this test fail about that often, depending on
        // what had consumed the shared generator before it.
        bool moved = false;

        for (int attempt = 0; attempt < 20 && !moved; attempt++)
        {
            moved = creature.Update(RandomMovementGenerator.MaxWaitMs + 1) is not null;
        }

        Assert.True(moved, "the wander never produced a move in twenty attempts");
    }

    // ------------------------------------------------------------------ standing on the ground

    /// <summary>
    /// A wander destination sits on the floor, not at the spawn's height.
    /// </summary>
    /// <remarks>
    /// The spawn's own Z was used for every point in the radius, so on any slope the creature ended
    /// up buried or floating by the full height difference. 1,946 wandering spawns roam more than
    /// twenty yards, which on a hillside is a long way vertically.
    /// </remarks>
    [Fact]
    public void AWanderDestination_SitsOnTheFloor()
    {
        Creature creature = CreatureFixture.Build(
            wanderDistance: 10f,
            movementType: 1,
            position: new Position(0f, 0f, 100f, 0f));

        // A floor that rises one yard per yard of x, so the answer is never the spawn height.
        creature.FloorAt = (x, y, z) => 100f + x;

        RandomMovementGenerator generator = new(10f);

        for (int i = 0; i < 50; i++)
        {
            Assert.True(generator.TryGetDestination(
                creature, RandomMovementGenerator.MaxWaitMs + 1, out MovementDecision decision));

            Assert.Equal(100f + decision.Destination.X, decision.Destination.Z, 0.001f);
        }
    }

    /// <summary>
    /// With nothing able to answer, the spawn height is used and the creature still wanders.
    /// </summary>
    /// <remarks>
    /// Deliberate, and the reverse of the obvious choice. Refusing to move without a height reads as
    /// safer until you notice that a fresh clone has no extracted client data at all — <c>data/</c>
    /// is three gigabytes and uncommitted — at which point every wandering creature in the world
    /// silently freezes. Degrading to the old behaviour is what the rest of the codebase does with
    /// missing client data.
    /// </remarks>
    [Fact]
    public void WithNoHeightAvailable_TheCreatureStillWanders()
    {
        Creature creature = CreatureFixture.Build(
            wanderDistance: 10f,
            movementType: 1,
            position: new Position(0f, 0f, 100f, 0f));

        creature.FloorAt = (x, y, z) => null;

        RandomMovementGenerator generator = new(10f);

        Assert.True(generator.TryGetDestination(
            creature, RandomMovementGenerator.MaxWaitMs + 1, out MovementDecision decision));

        Assert.Equal(100f, decision.Destination.Z, 0.001f);
    }

    /// <summary>Real terrain: a creature wandering in Elwynn ends up on the ground there.</summary>
    /// <remarks>
    /// The synthetic tests above prove the height is consulted; this proves the thing consulted is
    /// the map. A resolver wired to the wrong map, or a wander that never left the spawn point,
    /// would pass both of them.
    /// </remarks>
    [RequiresMapsFact]
    public void OnRealTerrain_DestinationsFollowTheGround()
    {
        // The human starting area, which is on a slope.
        Position spawn = new(-8949.95f, -132.493f, 83.5312f, 0f);

        TerrainMap terrain = new TerrainManager(ClientData.DataDirectory).GetMap(0);

        Creature creature = CreatureFixture.Build(
            wanderDistance: 20f, movementType: 1, position: spawn);

        creature.FloorAt = (x, y, z) => WorldHeight.GetFloor(terrain, null, x, y, z);

        RandomMovementGenerator generator = new(20f);
        int differed = 0;

        for (int i = 0; i < 50; i++)
        {
            Assert.True(generator.TryGetDestination(
                creature, RandomMovementGenerator.MaxWaitMs + 1, out MovementDecision decision));

            float ground = terrain.GetHeight(decision.Destination.X, decision.Destination.Y);

            Assert.Equal(ground, decision.Destination.Z, 0.01f);

            if (Math.Abs(decision.Destination.Z - spawn.Z) > 0.5f)
            {
                differed++;
            }
        }

        Assert.True(differed > 0, "every destination came back at the spawn height — is terrain being read?");
    }

    // ------------------------------------------------------------------ the store

    /// <summary>A path is looked up by its own id, not by the guid of a creature walking it.</summary>
    /// <remarks>
    /// <c>waypoint_data.id</c> is commented "Creature GUID" upstream and is nothing of the sort —
    /// it is the path id that <c>creature_addon.path_id</c> names. For most routes the two numbers
    /// coincide, which is precisely why keying on the guid would pass a casual test.
    /// </remarks>
    [Fact]
    public void TheStore_KeysPathsByPathId()
    {
        WaypointStore store = new();
        store.Add(42, Path((1f, 2f)));

        Assert.Single(store.Path(42));
        Assert.Empty(store.Path(7));
        Assert.Equal(1, store.PathCount);
    }

    /// <summary>A spawn with no addon of any kind walks no route.</summary>
    [Fact]
    public void TheAddonStore_ReportsNoPathAsZero()
    {
        CreatureAddonStore store = new();
        store.Add(spawnId: 5, new CreatureAddon(PathId: 99, 0, 0, 0, 0));

        Assert.Equal(99u, store.PathFor(spawnId: 5, entry: 1));
        Assert.Equal(0u, store.PathFor(spawnId: 6, entry: 1));
    }

    /// <summary>
    /// A route named by the entry rather than by the spawn is still found.
    /// </summary>
    /// <remarks>
    /// 19 entries carry a path on their template addon instead of on each spawn. Reading only the
    /// per-spawn table — which is what this did until the template table was vendored — leaves those
    /// standing still with no indication anything is missing.
    /// </remarks>
    [Fact]
    public void ARouteOnTheTemplate_IsFoundThroughTheFallback()
    {
        CreatureAddonStore store = new();
        store.AddTemplate(entry: 299, new CreatureAddon(PathId: 7, 0, 0, 0, 0));

        Assert.Equal(7u, store.PathFor(spawnId: 5, entry: 299));
    }

    /// <summary>
    /// A spawn's own addon replaces its entry's outright rather than merging with it.
    /// </summary>
    /// <remarks>
    /// Upstream returns the first of the two that exists and never combines them, so a spawn row
    /// that sets only an emote also drops whatever its template said about sheath state. Merging
    /// field by field is the obvious improvement and is a different game.
    /// </remarks>
    [Fact]
    public void ASpawnsAddon_ReplacesItsTemplatesEntirely()
    {
        CreatureAddonStore store = new();

        store.AddTemplate(entry: 299, new CreatureAddon(PathId: 7, Mount: 100, 0, Bytes2: 1, Emote: 0));
        store.Add(spawnId: 5, new CreatureAddon(PathId: 0, Mount: 0, 0, Bytes2: 0, Emote: 13));

        CreatureAddon addon = store.For(spawnId: 5, entry: 299)!.Value;

        Assert.Equal(13u, addon.Emote);
        Assert.Equal(0u, addon.Mount);
        Assert.Equal(0u, addon.Bytes2);
        Assert.Equal(0u, store.PathFor(spawnId: 5, entry: 299));
    }

    // ------------------------------------------------------------------ helpers

    private static MovementDecision Next(WaypointMovementGenerator generator, Creature creature)
    {
        Assert.True(generator.TryGetDestination(creature, 0, out MovementDecision decision));
        return decision;
    }

    private static Waypoint[] Path(params (float X, float Y)[] points) =>
        [.. points.Select(p => new Waypoint(new Position(p.X, p.Y, 0f, 0f), DelayMs: 0, MoveType: 0))];
}
