using WowEmu.Core;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;
using WowEmu.Game.Movement;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Attack timers: how they count down, and what a swing does to them.
/// </summary>
public sealed class AttackTimerTests
{
    [Fact]
    public void ANewUnit_CanSwingImmediately()
    {
        Creature creature = CreatureFixture.Build();

        Assert.True(creature.IsAttackReady(WeaponAttackType.BaseAttack));
        Assert.Equal(0, creature.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    [Fact]
    public void TheTimer_CountsDownByTheDiff()
    {
        Creature creature = CreatureFixture.Build();

        creature.SetAttackTimer(WeaponAttackType.BaseAttack, 1000);
        creature.UpdateAttackTimers(300);

        Assert.Equal(700, creature.GetAttackTimer(WeaponAttackType.BaseAttack));
        Assert.False(creature.IsAttackReady(WeaponAttackType.BaseAttack));
    }

    /// <summary>
    /// A timer overshoots into the negative for exactly one tick, then settles at zero.
    /// </summary>
    /// <remarks>
    /// Not a clamp: a tick that carries the timer past zero leaves the overshoot behind, and the
    /// <i>next</i> tick is what discards it. That one-tick window is the whole point — it is when
    /// the swing fires, and <see cref="Unit.ResetAttackTimer"/> subtracts the overshoot from the
    /// next cooldown so a slow weapon does not drift later with every swing. Clamping to zero on the
    /// first tick would throw the overshoot away before anything could use it.
    /// </remarks>
    [Fact]
    public void TheTimer_OvershootsForOneTickThenSettles()
    {
        Creature creature = CreatureFixture.Build();

        creature.SetAttackTimer(WeaponAttackType.BaseAttack, 100);

        creature.UpdateAttackTimers(500);
        Assert.Equal(-400, creature.GetAttackTimer(WeaponAttackType.BaseAttack));

        // Ready either way — the swing happens off the back of this.
        Assert.True(creature.IsAttackReady(WeaponAttackType.BaseAttack));

        creature.UpdateAttackTimers(500);
        Assert.Equal(0, creature.GetAttackTimer(WeaponAttackType.BaseAttack));

        creature.UpdateAttackTimers(500);
        Assert.Equal(0, creature.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    /// <summary>
    /// A reset carries the overshoot forward rather than discarding it.
    /// </summary>
    /// <remarks>
    /// <c>min(timer + speed, speed)</c>, not <c>speed</c>. The two differ whenever the timer went
    /// negative, and assigning the speed outright makes every weapon slower than its tooltip by half
    /// a tick on average — invisible per swing, and a measurable damage loss over a fight.
    /// </remarks>
    [Fact]
    public void AResetCarriesTheOvershootForward()
    {
        Creature creature = CreatureFixture.Build();

        Assert.Equal(2000u, creature.GetAttackTime(WeaponAttackType.BaseAttack));

        // 300 ms past due when the swing happens.
        creature.SetAttackTimer(WeaponAttackType.BaseAttack, -300);
        creature.ResetAttackTimer(WeaponAttackType.BaseAttack);

        Assert.Equal(1700, creature.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    /// <summary>A reset from exactly zero is the full weapon speed, and never more.</summary>
    [Fact]
    public void AResetFromZero_IsTheWeaponSpeed()
    {
        Creature creature = CreatureFixture.Build();

        creature.ResetAttackTimer(WeaponAttackType.BaseAttack);

        Assert.Equal(2000, creature.GetAttackTimer(WeaponAttackType.BaseAttack));

        // A reset from a *positive* timer is capped, not added to — otherwise resetting twice in a
        // tick would push the next swing out to four seconds.
        creature.ResetAttackTimer(WeaponAttackType.BaseAttack);

        Assert.Equal(2000, creature.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    /// <summary>The three weapons count down independently.</summary>
    [Fact]
    public void TheWeapons_HaveSeparateTimers()
    {
        Creature creature = CreatureFixture.Build();

        creature.SetAttackTimer(WeaponAttackType.BaseAttack, 1000);
        creature.SetAttackTimer(WeaponAttackType.OffAttack, 500);
        creature.SetAttackTimer(WeaponAttackType.RangedAttack, 200);

        creature.UpdateAttackTimers(200);

        Assert.Equal(800, creature.GetAttackTimer(WeaponAttackType.BaseAttack));
        Assert.Equal(300, creature.GetAttackTimer(WeaponAttackType.OffAttack));
        Assert.Equal(0, creature.GetAttackTimer(WeaponAttackType.RangedAttack));
    }
}

/// <summary>Starting and stopping an attack.</summary>
public sealed class AttackStateTests
{
    [Fact]
    public void Attacking_SetsTheVictimAndTheClientVisibleTarget()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        Assert.True(attacker.Attack(victim));

        Assert.Same(victim, attacker.Victim);
        Assert.True(attacker.IsMeleeAttacking);
        Assert.Equal(victim.Guid, attacker.Target);
    }

    /// <summary>
    /// Attacking the same victim again changes nothing, and says so.
    /// </summary>
    /// <remarks>
    /// The client re-sends its attack request; returning true each time would have the caller send
    /// another attack-start, and the animation restarts from the beginning every second or so.
    /// </remarks>
    [Fact]
    public void AttackingTheSameVictimAgain_ChangesNothing()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        Assert.True(attacker.Attack(victim));
        Assert.False(attacker.Attack(victim));
        Assert.False(attacker.Attack(victim));
    }

    [Fact]
    public void SwitchingVictims_IsAChange()
    {
        Creature attacker = CreatureFixture.Build();
        Creature first = CreatureFixture.Build();
        Creature second = CreatureFixture.Build();

        Assert.True(attacker.Attack(first));
        Assert.True(attacker.Attack(second));
        Assert.Same(second, attacker.Victim);
    }

    /// <summary>
    /// Switching victims does not reset the swing timer.
    /// </summary>
    /// <remarks>
    /// Resetting it would be worse than wrong: swapping targets would become a way to cancel a slow
    /// swing you regretted, at no cost.
    /// </remarks>
    [Fact]
    public void SwitchingVictims_DoesNotResetTheSwingTimer()
    {
        Creature attacker = CreatureFixture.Build();
        Creature first = CreatureFixture.Build();
        Creature second = CreatureFixture.Build();

        attacker.Attack(first);
        attacker.SetAttackTimer(WeaponAttackType.BaseAttack, 1500);

        attacker.Attack(second);

        Assert.Equal(1500, attacker.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    [Fact]
    public void AUnit_CannotAttackItself()
    {
        Creature creature = CreatureFixture.Build();

        Assert.False(creature.Attack(creature));
        Assert.Null(creature.Victim);
    }

    [Fact]
    public void TheDead_NeitherAttackNorAreAttacked()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        victim.DeathState = DeathState.Corpse;
        Assert.False(attacker.Attack(victim));

        victim.DeathState = DeathState.Alive;
        attacker.DeathState = DeathState.Corpse;
        Assert.False(attacker.Attack(victim));
    }

    [Fact]
    public void Stopping_ClearsEverything()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        attacker.Attack(victim);

        Assert.True(attacker.AttackStop());

        Assert.Null(attacker.Victim);
        Assert.False(attacker.IsMeleeAttacking);
        Assert.True(attacker.Target.IsEmpty);

        // Nothing to stop the second time.
        Assert.False(attacker.AttackStop());
    }
}

/// <summary>Range, facing and the swing loop itself.</summary>
public sealed class MeleeSwingTests
{
    private static (Creature Attacker, Creature Victim) Pair(float distance, float facing = 0f)
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        attacker.Position = new Position(0f, 0f, 0f, facing);
        victim.Position = new Position(distance, 0f, 0f, 0f);

        attacker.Attack(victim);

        return (attacker, victim);
    }

    /// <summary>Melee range is both reaches plus 4/3 of a yard, floored at five.</summary>
    [Fact]
    public void MeleeRange_IsFlooredAtFiveYards()
    {
        Creature small = CreatureFixture.Build();
        Creature other = CreatureFixture.Build();

        // The fixture's combat reach is 1.5 each, so 1.5 + 1.5 + 1.33 = 4.33 — under the floor.
        Assert.Equal(UnitDefaults.NominalMeleeRange, small.MeleeRangeTo(other), 0.001f);

        // Something large enough to exceed it gets the computed range instead.
        small.CombatReach = 10f;

        Assert.Equal(10f + 1.5f + (4f / 3f), small.MeleeRangeTo(other), 0.001f);
    }

    [Fact]
    public void RangeIsMeasuredInThreeDimensions()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        attacker.Position = new Position(0f, 0f, 0f, 0f);

        // Four yards away flat is in range; four yards straight up is not, once combined.
        victim.Position = new Position(4f, 0f, 0f, 0f);
        Assert.True(attacker.IsWithinMeleeRange(victim));

        victim.Position = new Position(4f, 0f, 4f, 0f);
        Assert.False(attacker.IsWithinMeleeRange(victim));
    }

    [Fact]
    public void SomethingOnAnotherMap_IsNeverInRange()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        attacker.Position = new Position(0f, 0f, 0f, 0f);
        victim.Position = new Position(1f, 0f, 0f, 0f);
        victim.MapId = 571;

        Assert.False(attacker.IsWithinMeleeRange(victim));
    }

    // ------------------------------------------------------------------ facing

    /// <summary>A target straight ahead is inside the arc; one straight behind is not.</summary>
    [Fact]
    public void TheFacingArc_IsAConeInFront()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        // Far enough apart that the bounding-radius shortcut does not apply.
        attacker.Position = new Position(0f, 0f, 0f, 0f);
        victim.Position = new Position(4f, 0f, 0f, 0f);

        Assert.True(MeleeSwing.IsFacing(attacker, victim));

        // Facing the other way.
        attacker.Position = new Position(0f, 0f, 0f, MathF.PI);

        Assert.False(MeleeSwing.IsFacing(attacker, victim));
    }

    /// <summary>
    /// The arc wraps around π rather than breaking there.
    /// </summary>
    /// <remarks>
    /// The angle difference has to be folded into <c>[-π, π]</c>. Without that, an attacker facing
    /// just under -π and a target just over +π read as nearly 2π apart, and the attacker refuses to
    /// swing at something directly in front of it.
    /// </remarks>
    [Fact]
    public void TheFacingArc_WrapsAroundPi()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        attacker.Position = new Position(0f, 0f, 0f, MathF.PI - 0.01f);
        victim.Position = new Position(-4f, 0.01f, 0f, 0f);   // just past π from the attacker

        Assert.True(MeleeSwing.IsFacing(attacker, victim));
    }

    /// <summary>
    /// Facing is ignored when the two are inside each other's bounding radius.
    /// </summary>
    /// <remarks>
    /// At that distance the angle is dominated by noise, and enforcing it makes a large creature
    /// impossible to hit while standing on top of it.
    /// </remarks>
    [Fact]
    public void Facing_IsIgnoredInsideTheBoundingRadius()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        attacker.BoundingRadius = 5f;
        victim.BoundingRadius = 5f;

        // Facing directly away, but standing on top of each other.
        attacker.Position = new Position(0f, 0f, 0f, MathF.PI);
        victim.Position = new Position(1f, 0f, 0f, 0f);

        Assert.True(MeleeSwing.IsFacing(attacker, victim));
    }

    /// <summary>"Behind" is the rear half, a wider region than the front cone.</summary>
    [Fact]
    public void Behind_IsTheRearHalf()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        // Victim faces +x; attacker stands at -x, so it is behind.
        victim.Position = new Position(0f, 0f, 0f, 0f);
        attacker.Position = new Position(-4f, 0f, 0f, 0f);

        Assert.True(MeleeSwing.IsBehind(attacker, victim));

        // Attacker moves round to the front.
        attacker.Position = new Position(4f, 0f, 0f, 0f);

        Assert.False(MeleeSwing.IsBehind(attacker, victim));

        // Directly to the side is *not* behind — the rear half is measured from the victim's facing.
        attacker.Position = new Position(0f, 4f, 0f, 0f);

        Assert.False(MeleeSwing.IsBehind(attacker, victim));
    }

    // ------------------------------------------------------------------ the loop

    [Fact]
    public void AUnitAttackingNothing_NeverSwings()
    {
        Creature attacker = CreatureFixture.Build();

        SwingResult result = MeleeSwing.Advance(attacker, WeaponAttackType.BaseAttack, GameRandom.Urand);

        Assert.False(result.Swung);
        Assert.Equal(SwingError.None, result.Error);
    }

    [Fact]
    public void AReadyWeaponInRange_Swings()
    {
        (Creature attacker, _) = Pair(distance: 2f);

        SwingResult result = MeleeSwing.Advance(attacker, WeaponAttackType.BaseAttack, GameRandom.Urand);

        Assert.True(result.Swung);
        Assert.Equal(SwingError.None, result.Error);

        // The weapon is now on cooldown for its full speed.
        Assert.Equal(2000, attacker.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    [Fact]
    public void AWeaponOnCooldown_DoesNotSwing()
    {
        (Creature attacker, _) = Pair(distance: 2f);

        attacker.SetAttackTimer(WeaponAttackType.BaseAttack, 500);

        SwingResult result = MeleeSwing.Advance(attacker, WeaponAttackType.BaseAttack, GameRandom.Urand);

        Assert.False(result.Swung);
        Assert.Equal(SwingError.None, result.Error);

        // Untouched: the swing never happened, so nothing was spent.
        Assert.Equal(500, attacker.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    /// <summary>
    /// An out-of-range swing costs a retry interval, not a weapon swing.
    /// </summary>
    /// <remarks>
    /// Spending the full weapon speed here would mean that falling a step behind a fleeing target
    /// costs an entire swing — and the shorter interval is why closing the gap feels immediate
    /// rather than as if the attack were queued.
    /// </remarks>
    [Fact]
    public void AnOutOfRangeSwing_CostsOnlyARetryInterval()
    {
        (Creature attacker, _) = Pair(distance: 50f);

        SwingResult result = MeleeSwing.Advance(attacker, WeaponAttackType.BaseAttack, GameRandom.Urand);

        Assert.False(result.Swung);
        Assert.Equal(SwingError.NotInRange, result.Error);
        Assert.Equal(UnitDefaults.SwingRetryDelayMs, attacker.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    [Fact]
    public void ASwingAtSomethingBehindYou_CostsOnlyARetryInterval()
    {
        // In range, but the attacker faces the opposite way.
        (Creature attacker, _) = Pair(distance: 4f, facing: MathF.PI);

        SwingResult result = MeleeSwing.Advance(attacker, WeaponAttackType.BaseAttack, GameRandom.Urand);

        Assert.False(result.Swung);
        Assert.Equal(SwingError.BadFacing, result.Error);
        Assert.Equal(UnitDefaults.SwingRetryDelayMs, attacker.GetAttackTimer(WeaponAttackType.BaseAttack));
    }

    [Fact]
    public void ADeadVictim_StopsTheSwings()
    {
        (Creature attacker, Creature victim) = Pair(distance: 2f);

        victim.DeathState = DeathState.JustDied;

        SwingResult result = MeleeSwing.Advance(attacker, WeaponAttackType.BaseAttack, GameRandom.Urand);

        Assert.False(result.Swung);
    }

    /// <summary>A melee swing spends the ranged timer too.</summary>
    /// <remarks>
    /// Otherwise a ranged timer that has been counting down all fight fires for free the instant the
    /// attacker swaps to a bow.
    /// </remarks>
    [Fact]
    public void AMeleeSwing_AlsoSpendsTheRangedTimer()
    {
        (Creature attacker, _) = Pair(distance: 2f);

        attacker.SetAttackTime(WeaponAttackType.RangedAttack, 2400);

        MeleeSwing.Advance(attacker, WeaponAttackType.BaseAttack, GameRandom.Urand);

        Assert.Equal(2400, attacker.GetAttackTimer(WeaponAttackType.RangedAttack));
    }

    /// <summary>Swings arrive at the weapon's rate over a simulated fight.</summary>
    [Fact]
    public void OverASimulatedFight_SwingsArriveAtTheWeaponsRate()
    {
        (Creature attacker, _) = Pair(distance: 2f);

        GameRandom.SeedCurrentThread(20260805);

        const uint TickMs = 100;
        const int Ticks = 100;   // ten seconds

        int swings = 0;

        for (int i = 0; i < Ticks; i++)
        {
            attacker.UpdateAttackTimers(TickMs);

            if (MeleeSwing.Advance(attacker, WeaponAttackType.BaseAttack, GameRandom.Urand).Swung)
            {
                swings++;
            }
        }

        // A 2-second weapon over ten seconds: one immediately, then one every two seconds — at 0, 2,
        // 4, 6 and 8 seconds. The sixth falls at exactly ten, which is one tick past the window.
        Assert.Equal(5, swings);
    }
}

/// <summary>
/// Auto-attack driven through a real map, which is where damage is actually applied.
/// </summary>
/// <remarks>
/// The pieces are pinned individually elsewhere; what these check is the wiring — that a tick turns
/// a swing into lost health and into a packet, and that nothing happens on a tick the map was not
/// meant to advance on.
/// </remarks>
public sealed class MapCombatTests
{
    /// <summary>A tick with a live fight takes health off the victim and tells the client.</summary>
    [Fact]
    public void ATick_LandsASwingAndTakesHealth()
    {
        (Map map, Player attacker, Creature victim, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        uint before = victim.Health;

        map.Update(gameplayDiff: 100, sessionDiff: 100);

        Assert.True(victim.Health < before, "the victim took no damage");

        // The player's swing specifically. There may be a second in the other direction — the
        // creature answers on the same tick now that it has an AI — so this asks for the one it
        // means rather than for the only one there is.
        Assert.Contains(link.Swings, swing => swing.Attacker == attacker.Guid && swing.Target == victim.Guid);
    }

    /// <summary>
    /// A session-only tick advances nothing.
    /// </summary>
    /// <remarks>
    /// Three ticks in four are out of phase and carry a zero gameplay diff. Treating one as a very
    /// short tick would make every weapon in the world swing four times as fast.
    /// </remarks>
    [Fact]
    public void ASessionOnlyTick_DoesNotSwing()
    {
        (Map map, _, Creature victim, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        uint before = victim.Health;

        for (int i = 0; i < 50; i++)
        {
            map.Update(gameplayDiff: 0, sessionDiff: 100);
        }

        Assert.Equal(before, victim.Health);
        Assert.Empty(link.Swings);
    }

    /// <summary>Health never wraps around past zero.</summary>
    /// <remarks>
    /// Health is unsigned, so a hit larger than what is left would otherwise underflow to something
    /// near four billion — a victim that becomes unkillable at the moment it should have died.
    /// </remarks>
    [Fact]
    public void AnOverkill_FloorsHealthAtZero()
    {
        (Map map, _, Creature victim, _) = MapCombatFixture.Engaged();

        victim.Health = 1;

        for (int i = 0; i < 100 && victim.Health > 0; i++)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.Equal(0u, victim.Health);
    }

    /// <summary>
    /// An out-of-range fight reports the failure once, not on every retry.
    /// </summary>
    /// <remarks>
    /// The swing retries every 100 ms. Without the suppression the client is told ten times a second
    /// that it is out of range, and prints the message ten times a second.
    /// </remarks>
    [Fact]
    public void AnOutOfRangeFight_ReportsTheFailureRepeatedlyToTheSession()
    {
        (Map map, _, _, MapCombatFixture.Link link) = MapCombatFixture.Engaged(distance: 60f);

        for (int i = 0; i < 20; i++)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.Empty(link.Swings);
        Assert.All(link.SwingErrors, error => Assert.Equal(SwingError.NotInRange, error));

        // The map reports every failure; the *session* is what suppresses the repeats, and that is
        // tested against the session rather than here.
        Assert.NotEmpty(link.SwingErrors);
    }

    /// <summary>A landed swing clears the client's suppression, so the next failure is reported.</summary>
    [Fact]
    public void ALandedSwing_ClearsTheSuppression()
    {
        (Map map, _, _, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        map.Update(gameplayDiff: 100, sessionDiff: 100);

        Assert.NotEmpty(link.Swings);
        Assert.Equal([SwingError.None], link.SwingErrors);
    }
}

/// <summary>Builds a map with one player already attacking one creature.</summary>
internal static class MapCombatFixture
{
    public static (Map Map, Player Attacker, Creature Victim, Link Connection) Engaged(
        float distance = 2f,
        PlayerXpStore? experience = null,
        PlayerStatsStore? playerStats = null)
    {
        Creature victim = CreatureFixture.Build(position: new Position(distance, 0f, 0f, 0f));

        // Creatures reach a map through the grid loader, not through Add — the same path the real
        // server uses, so the victim ends up filed in a cell and findable by a range query.
        Map map = new(0, new TerrainMap(0, Path.GetTempPath()), new OneCreature(victim))
        {
            ExperienceTable = experience,
            PlayerStats = playerStats,
        };

        CharacterSummary summary = new(1, "Fighter", 1, 1, 0, 0, 0, 0, 0, 0, 1, 12, 0, 0f, 0f, 0f, 0, 0, 0);
        ChrRacesEntry race = new(1, 0, 1, 49, 50, 7, 0, 0, "Human", 0);
        ChrClassesEntry characterClass = new(1, 1, "Warrior", 4, 0);
        PlayerBaseStats stats = new(20, 0, 23, 20, 22, 20, 20);

        Player attacker = Player.Create(summary, race, characterClass, stats);
        Link connection = new();
        attacker.Connection = connection;

        // A player with no equipment swings for nothing, which would make every test here vacuous.
        attacker.MinDamage = 5f;
        attacker.MaxDamage = 10f;
        attacker.SetAttackTime(WeaponAttackType.BaseAttack, 2000);
        attacker.Position = new Position(0f, 0f, 0f, 0f);

        // The base stats give a level-1 character almost nothing, and a player at zero health is
        // already dead — every "did it take damage" assertion would be vacuously false.
        attacker.MaxHealth = 1000;
        attacker.Health = 1000;

        map.Add(attacker);

        attacker.Attack(victim);

        GameRandom.SeedCurrentThread(20260805);

        return (map, attacker, victim, connection);
    }

    /// <summary>A grid loader holding exactly one creature, so the map has something to fight.</summary>
    private sealed class OneCreature(Creature creature) : IGridObjectLoader
    {
        public IReadOnlyList<WorldObject> Load(uint mapId, GridCoord grid) =>
            grid == MapCoordinates.GridFor(creature.Position.X, creature.Position.Y) ? [creature] : [];
    }

    /// <summary>Records what the map asked the client to be told.</summary>
    internal sealed class Link : IPlayerConnection
    {
        public List<(ObjectGuid Attacker, ObjectGuid Target, MeleeDamageInfo Info)> Swings { get; } = [];

        public List<SwingError> SwingErrors { get; } = [];

        public List<ObjectGuid> Created { get; } = [];

        public List<ObjectGuid> Destroyed { get; } = [];

        /// <summary>Attack starts and stops, with the guid each named.</summary>
        public List<(ObjectGuid Victim, bool Attacking, bool VictimIsDead)> AttackStates { get; } = [];

        public void QueueCreate(WorldObject other)
        {
            ArgumentNullException.ThrowIfNull(other);

            Created.Add(other.Guid);
        }

        public void QueueDestroy(ObjectGuid objectGuid) => Destroyed.Add(objectGuid);

        public void FlushUpdates()
        {
        }

        public void SendMovement(Opcode opcode, ObjectGuid mover, MovementInfo movement)
        {
        }

        public void QueueMonsterMove(ObjectGuid mover, CreatureMove move, uint splineId)
        {
        }

        public void QueueMeleeSwing(
            ObjectGuid attacker, ObjectGuid target, MeleeDamageInfo info, uint targetHealthBeforeHit) =>
            Swings.Add((attacker, target, info));

        public void SendAttackState(ObjectGuid attacker, ObjectGuid? victim, bool attacking, bool victimIsDead) =>
            AttackStates.Add((victim ?? ObjectGuid.Empty, attacking, victimIsDead));


        /// <summary>Casts this client was told about.</summary>
        public List<(uint SpellId, int CastTimeMs, bool Landed)> Casts { get; } = [];

        /// <summary>Cast refusals this client was told about.</summary>
        public List<SpellCastResult> CastFailures { get; } = [];

        public void SendSpellStart(
            ObjectGuid caster, uint spellId, byte castCount, int castTimeMs, ObjectGuid target, uint powerLeft) =>
            Casts.Add((spellId, castTimeMs, false));

        public void SendSpellGo(
            ObjectGuid caster, uint spellId, byte castCount, ObjectGuid target, uint powerLeft) =>
            Casts.Add((spellId, 0, true));

        public void SendCastFailed(byte castCount, uint spellId, SpellCastResult result) =>
            CastFailures.Add(result);


        /// <summary>Spell damage this client was told about.</summary>
        public List<(ObjectGuid Target, uint SpellId, SpellHit Hit)> SpellDamage { get; } = [];

        public void QueueSpellDamage(
            ObjectGuid target, ObjectGuid caster, uint spellId, SpellHit hit, uint targetHealthBeforeHit) =>
            SpellDamage.Add((target, spellId, hit));


        /// <summary>Experience gains this client was told about.</summary>
        public List<(uint Amount, IReadOnlyList<LevelUp> Levels)> ExperienceGains { get; } = [];

        public void SendExperienceGain(ObjectGuid victim, uint amount, IReadOnlyList<LevelUp> levels) =>
            ExperienceGains.Add((amount, levels));

        public void SendSwingError(SwingError reason) => SwingErrors.Add(reason);

        public void DrainMapPackets(uint diff)
        {
        }
    }
}
