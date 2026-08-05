using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The death state machine: alive, corpse, gone, back.
/// </summary>
public sealed class CreatureDeathTests
{
    /// <summary>
    /// Dying lands on <see cref="DeathState.Corpse"/>, never on <see cref="DeathState.JustDied"/>.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>setDeathState(JUST_DIED)</c> ends by promoting to <c>CORPSE</c> in the same
    /// call — which is why it logs an error if it ever sees a creature <i>updating</i> in the
    /// just-died state. The moment exists so the things that must happen exactly once at death
    /// happen between the two, not so anything can be observed in it.
    /// </remarks>
    [Fact]
    public void Dying_LandsOnCorpseNotOnJustDied()
    {
        Creature creature = CreatureFixture.Build();

        creature.Kill();

        Assert.Equal(DeathState.Corpse, creature.DeathState);
        Assert.False(creature.IsAlive);
    }

    [Fact]
    public void Dying_EmptiesHealthAndPower()
    {
        Creature creature = CreatureFixture.Build();

        Assert.True(creature.Health > 0);

        creature.Kill();

        Assert.Equal(0u, creature.Health);
        Assert.Equal(0u, creature.Power);
    }

    /// <summary>
    /// A corpse offers no services.
    /// </summary>
    /// <remarks>
    /// The client draws the gossip and vendor icons straight from the npc flags. Leaving them up
    /// gives a dead innkeeper a usable icon over its corpse.
    /// </remarks>
    [Fact]
    public void ACorpse_HasNoNpcFlags()
    {
        Creature creature = CreatureFixture.Build();

        creature.NpcFlags = 3;   // gossip and quest giver
        creature.Kill();

        Assert.Equal(0u, creature.NpcFlags);
    }

    /// <summary>Dying drops the target and stops any attack.</summary>
    [Fact]
    public void Dying_StopsAttacking()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        attacker.Attack(victim);
        attacker.IsInCombat = true;

        attacker.Kill();

        Assert.Null(attacker.Victim);
        Assert.False(attacker.IsMeleeAttacking);
        Assert.True(attacker.Target.IsEmpty);
        Assert.False(attacker.IsInCombat);
    }

    /// <summary>
    /// A corpse stops where it fell rather than sliding on.
    /// </summary>
    /// <remarks>
    /// The client keeps interpolating whatever spline it was last given. Without cancelling the
    /// move, a creature killed mid-walk leaves its corpse drifting to where it was headed.
    /// </remarks>
    [Fact]
    public void ACorpse_StopsWhereItFell()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 10f, movementType: 1);

        // Get it walking.
        while (!creature.IsMoving)
        {
            creature.Update(WowEmu.Game.Movement.RandomMovementGenerator.MaxWaitMs + 1);
        }

        Position whereItDied = creature.Position;

        creature.Kill();

        Assert.False(creature.IsMoving);

        // Ticking a corpse must not move it.
        creature.UpdateDeath(5000);

        Assert.Equal(whereItDied.X, creature.Position.X, 0.001f);
        Assert.Equal(whereItDied.Y, creature.Position.Y, 0.001f);
    }

    [Fact]
    public void KillingSomethingAlreadyDead_ChangesNothing()
    {
        Creature creature = CreatureFixture.Build();

        creature.Kill();
        creature.UpdateDeath(30_000);

        uint remaining = creature.CorpseRemainingMs;

        creature.Kill();

        Assert.Equal(remaining, creature.CorpseRemainingMs);
    }

    // ------------------------------------------------------------------ the timers

    /// <summary>
    /// The respawn clock includes the corpse delay, so a creature is not back the moment it fades.
    /// </summary>
    /// <remarks>
    /// Upstream sets <c>respawnTime = now + respawnDelay + corpseDelay</c>. Measuring the respawn
    /// from the corpse's removal instead would be a plausible reading and would make everything come
    /// back a minute early.
    /// </remarks>
    [Fact]
    public void TheRespawnClock_IncludesTheCorpseDelay()
    {
        Creature creature = CreatureFixture.Build();

        creature.Kill();

        Assert.Equal(creature.CorpseDelayMs, creature.CorpseRemainingMs);
        Assert.Equal(creature.RespawnDelayMs + creature.CorpseDelayMs, creature.RespawnRemainingMs);
    }

    /// <summary>The corpse disappears when its delay runs out, and the creature is not yet back.</summary>
    [Fact]
    public void WhenTheCorpseDelayExpires_TheCorpseGoesButNothingRespawns()
    {
        Creature creature = CreatureFixture.Build();

        creature.Kill();

        // One tick short.
        Assert.Equal(
            Creature.DeathTransition.None,
            creature.UpdateDeath(creature.CorpseDelayMs - 1));

        Assert.Equal(DeathState.Corpse, creature.DeathState);

        Assert.Equal(Creature.DeathTransition.CorpseRemoved, creature.UpdateDeath(1));

        Assert.Equal(DeathState.Dead, creature.DeathState);
        Assert.True(creature.IsDespawned);
        Assert.True(creature.RespawnRemainingMs > 0, "the creature came back with its corpse");
    }

    [Fact]
    public void WhenTheRespawnDelayExpires_TheCreatureComesBack()
    {
        Creature creature = CreatureFixture.Build();

        creature.Kill();
        creature.UpdateDeath(creature.CorpseDelayMs);

        Assert.Equal(
            Creature.DeathTransition.Respawned,
            creature.UpdateDeath(creature.RespawnRemainingMs));

        Assert.True(creature.IsAlive);
        Assert.Equal(creature.MaxHealth, creature.Health);
    }

    /// <summary>Each transition is reported once, not on every tick after it.</summary>
    [Fact]
    public void EachTransition_IsReportedOnce()
    {
        Creature creature = CreatureFixture.Build();

        creature.Kill();

        int corpseRemovals = 0, respawns = 0;

        // Ten minutes at 100 ms a tick, well past both delays.
        for (int i = 0; i < 6000; i++)
        {
            switch (creature.UpdateDeath(100))
            {
                case Creature.DeathTransition.CorpseRemoved:
                    corpseRemovals++;
                    break;

                case Creature.DeathTransition.Respawned:
                    respawns++;
                    break;

                default:
                    break;
            }
        }

        Assert.Equal(1, corpseRemovals);
        Assert.Equal(1, respawns);
    }

    /// <summary>A zero diff advances nothing, on a corpse as much as on a living creature.</summary>
    [Fact]
    public void AZeroDiff_DoesNotDecay()
    {
        Creature creature = CreatureFixture.Build();

        creature.Kill();

        uint before = creature.CorpseRemainingMs;

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(Creature.DeathTransition.None, creature.UpdateDeath(0));
        }

        Assert.Equal(before, creature.CorpseRemainingMs);
    }

    /// <summary>Ticking a living creature's death timers does nothing.</summary>
    [Fact]
    public void ALivingCreature_HasNothingToDecay()
    {
        Creature creature = CreatureFixture.Build();

        Assert.Equal(Creature.DeathTransition.None, creature.UpdateDeath(60_000));
        Assert.True(creature.IsAlive);
    }

    // ------------------------------------------------------------------ coming back

    /// <summary>
    /// A creature respawns where it spawned, not where it died.
    /// </summary>
    /// <remarks>
    /// A creature pulled across the zone and killed there must not reappear at the far end. The
    /// reset happens when the corpse goes rather than at respawn, so the corpse itself still lies
    /// where it fell.
    /// </remarks>
    [Fact]
    public void ARespawn_HappensAtHomeNotWhereItDied()
    {
        Creature creature = CreatureFixture.Build();

        Position home = creature.HomePosition;
        creature.Position = new Position(home.X + 200f, home.Y + 200f, home.Z, 0f);

        creature.Kill();

        // The corpse stays where it fell.
        Assert.Equal(home.X + 200f, creature.Position.X, 0.001f);

        creature.UpdateDeath(creature.CorpseDelayMs);

        // And is reclaimed the moment it fades.
        Assert.Equal(home.X, creature.Position.X, 0.001f);
        Assert.Equal(home.Y, creature.Position.Y, 0.001f);
    }

    /// <summary>
    /// A respawn restores the flags the spawn row asked for.
    /// </summary>
    /// <remarks>
    /// Death clears the npc flags. Restoring whatever the creature was carrying at the time would
    /// bring back a vendor with nothing to sell — permanently, after its first death.
    /// </remarks>
    [Fact]
    public void ARespawn_RestoresTheSpawnsFlags()
    {
        Creature creature = CreatureFixture.Build(npcFlags: 3);

        Assert.Equal(3u, creature.NpcFlags);

        creature.Kill();
        Assert.Equal(0u, creature.NpcFlags);

        creature.Respawn();

        Assert.Equal(3u, creature.NpcFlags);
    }

    [Fact]
    public void ARespawn_ClearsTheTimers()
    {
        Creature creature = CreatureFixture.Build();

        creature.Kill();
        creature.Respawn();

        Assert.Equal(0u, creature.CorpseRemainingMs);
        Assert.Equal(0u, creature.RespawnRemainingMs);
        Assert.False(creature.IsDespawned);
    }

    /// <summary>A dead creature does not wander.</summary>
    [Fact]
    public void ACorpse_DoesNotWander()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 10f, movementType: 1);

        creature.Kill();

        Position whereItDied = creature.Position;

        for (int i = 0; i < 100; i++)
        {
            Assert.Null(creature.Update(WowEmu.Game.Movement.RandomMovementGenerator.MaxWaitMs + 1));
        }

        Assert.Equal(whereItDied, creature.Position);
    }

    // ------------------------------------------------------------------ corpse delay by rank

    /// <summary>
    /// A rare or elite corpse lasts five times as long as a common one.
    /// </summary>
    /// <remarks>
    /// Long enough for a group to loot something they fought for. A world boss outside an instance
    /// gets ten minutes rather than the hour it gets inside one — upstream shortens it deliberately,
    /// so an open-world boss corpse does not block its own spawn point.
    /// </remarks>
    [Theory]
    [InlineData(Creature.RankNormal, 60_000u)]
    [InlineData(Creature.RankElite, 300_000u)]
    [InlineData(Creature.RankRareElite, 300_000u)]
    [InlineData(Creature.RankRare, 300_000u)]
    [InlineData(Creature.RankWorldBoss, 600_000u)]
    public void TheCorpseDelay_ComesFromTheRank(byte rank, uint expected) =>
        Assert.Equal(expected, Creature.CorpseDelayMsFor(rank));

    [Fact]
    public void TheRespawnDelay_ComesFromTheSpawnRow()
    {
        // The fixture's spawn row says 120 seconds.
        Creature creature = CreatureFixture.Build();

        Assert.Equal(120_000u, creature.RespawnDelayMs);
    }
}

/// <summary>Death driven through a real map, which is what tells clients about it.</summary>
public sealed class MapDeathTests
{
    /// <summary>A killed creature stops being attacked, and the client is told the fight is over.</summary>
    [Fact]
    public void AKill_StopsTheAttackAndTellsTheClient()
    {
        (Map map, Player attacker, Creature victim, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        victim.Health = 1;

        while (victim.IsAlive)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.Equal(DeathState.Corpse, victim.DeathState);
        Assert.Null(attacker.Victim);

        // An attack-stop naming the victim, flagged as a death.
        Assert.Contains(link.AttackStates, state => state is { Attacking: false });
    }

    /// <summary>
    /// The killing swing still reaches the client.
    /// </summary>
    /// <remarks>
    /// It is the packet that carries the overkill figure and the final damage number. Stopping the
    /// fight before broadcasting it would make the last hit of every fight invisible.
    /// </remarks>
    [Fact]
    public void TheKillingSwing_IsStillBroadcast()
    {
        (Map map, _, Creature victim, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        victim.Health = 1;

        while (victim.IsAlive)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.NotEmpty(link.Swings);
    }

    /// <summary>
    /// A corpse disappears from the client, and the creature is back later.
    /// </summary>
    /// <remarks>
    /// The whole cycle, driven by ticks: kill, corpse, destroy, respawn, create. What this catches
    /// that the unit tests cannot is the map bookkeeping — a creature taken out of its cell but left
    /// in the update list, so something is still there to bring it back.
    /// </remarks>
    [Fact]
    public void TheWholeCycle_RunsThroughToARespawn()
    {
        (Map map, _, Creature victim, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        victim.Health = 1;

        while (victim.IsAlive)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.Empty(link.Destroyed);

        // Past the corpse delay: the client is told to forget it.
        Tick(map, victim.CorpseDelayMs + 1000);

        Assert.True(victim.IsDespawned);
        Assert.Contains(victim.Guid, link.Destroyed);

        // It is out of the world entirely — nothing can find it or target it.
        Assert.Null(map.Find(victim.Guid));

        // And past the respawn delay, back again.
        Tick(map, victim.RespawnRemainingMs + 1000);

        Assert.True(victim.IsAlive);
        Assert.Equal(victim.MaxHealth, victim.Health);
        Assert.NotNull(map.Find(victim.Guid));
        Assert.Contains(victim.Guid, link.Created);
    }

    private static void Tick(Map map, uint totalMs)
    {
        for (uint elapsed = 0; elapsed < totalMs; elapsed += 500)
        {
            map.Update(gameplayDiff: 500, sessionDiff: 500);
        }
    }
}

/// <summary>Respawn timers read from the real spawn table.</summary>
public sealed class RespawnDataTests(ITestOutputHelper output)
{
    /// <summary>
    /// Every spawn carries a respawn delay, and they are not all the same.
    /// </summary>
    /// <remarks>
    /// A column that loaded as zeros everywhere would make every creature in the world respawn
    /// instantly — which reads as the corpse never appearing at all.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task TheRespawnDelays_AreLoadedAndVaried()
    {
        CreatureSpawnStore spawns = new();
        await spawns.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);

        Dictionary<uint, int> byDelay = [];

        foreach (CreatureSpawn spawn in spawns.ForMap(0))
        {
            byDelay[spawn.RespawnDelaySeconds] = byDelay.GetValueOrDefault(spawn.RespawnDelaySeconds) + 1;
        }

        Assert.True(byDelay.Count > 1, "every spawn on the map has the same respawn delay");
        Assert.True(byDelay.Keys.All(delay => delay > 0), "some spawns respawn instantly");

        foreach ((uint delay, int count) in byDelay.OrderByDescending(entry => entry.Value).Take(5))
        {
            output.WriteLine($"  {delay,6} s  {count,6} spawns");
        }
    }
}
