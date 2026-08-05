using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Movement;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>Interpolating a straight-line move.</summary>
public sealed class CreatureMoveTests
{
    [Fact]
    public void At_InterpolatesBetweenTheEndpoints()
    {
        CreatureMove move = new(new Position(0f, 0f, 0f, 0f), new Position(10f, 0f, 0f, 0f), 1000);

        Assert.Equal(0f, move.At(0).X, 0.001f);
        Assert.Equal(5f, move.At(500).X, 0.001f);
        Assert.Equal(10f, move.At(1000).X, 0.001f);
    }

    /// <summary>Past the end it stays at the destination rather than overshooting.</summary>
    [Fact]
    public void At_ClampsToTheDestination()
    {
        CreatureMove move = new(new Position(0f, 0f, 0f, 0f), new Position(10f, 0f, 0f, 0f), 1000);

        Assert.Equal(10f, move.At(5000).X, 0.001f);
        Assert.Equal(10f, move.At(uint.MaxValue).X, 0.001f);
    }

    /// <summary>
    /// The facing is computed from the endpoints, once.
    /// </summary>
    /// <remarks>
    /// Deriving it from successive positions instead produces jitter as the creature nears its
    /// destination and the two points it is being computed from converge.
    /// </remarks>
    [Fact]
    public void Facing_PointsAlongThePathAndDoesNotChange()
    {
        CreatureMove move = new(new Position(0f, 0f, 0f, 0f), new Position(0f, 10f, 0f, 0f), 1000);

        Assert.Equal(MathF.PI / 2f, move.Facing, 0.001f);
        Assert.Equal(move.At(1).Orientation, move.At(999).Orientation, 0.0001f);
    }

    /// <summary>Duration comes from distance over speed.</summary>
    [Fact]
    public void Create_DerivesDurationFromDistanceAndSpeed()
    {
        CreatureMove? move = CreatureMove.Create(
            new Position(0f, 0f, 0f, 0f), new Position(5f, 0f, 0f, 0f), speed: 2.5f);

        Assert.NotNull(move);
        Assert.Equal(2000u, move.Value.DurationMs);
    }

    /// <summary>
    /// A destination too close to be worth walking to produces no move at all.
    /// </summary>
    /// <remarks>
    /// A zero-duration move tells the client to arrive instantly, which reads as a twitch — and the
    /// random generator picks nearby points often enough for it to be constant.
    /// </remarks>
    [Fact]
    public void Create_RefusesAMoveTooShortToBeWorthMaking()
    {
        Assert.Null(CreatureMove.Create(
            new Position(0f, 0f, 0f, 0f), new Position(0.1f, 0f, 0f, 0f), speed: 2.5f));

        Assert.Null(CreatureMove.Create(
            new Position(0f, 0f, 0f, 0f), new Position(0f, 0f, 0f, 0f), speed: 2.5f));
    }

    [Fact]
    public void Create_RefusesAZeroSpeed()
    {
        Assert.Null(CreatureMove.Create(
            new Position(0f, 0f, 0f, 0f), new Position(10f, 0f, 0f, 0f), speed: 0f));
    }
}

/// <summary>Where a creature decides to go.</summary>
public sealed class MovementGeneratorTests
{
    [Fact]
    public void Idle_NeverHasAnywhereToGo()
    {
        Creature creature = CreatureFixture.Build();

        Assert.False(IdleMovementGenerator.Instance.TryGetDestination(creature, 1000, out _));
    }

    /// <summary>Every destination lands inside the wander radius, measured from home.</summary>
    [Fact]
    public void Random_StaysWithinItsWanderDistanceOfHome()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 5f, movementType: 1);
        RandomMovementGenerator generator = new(5f);

        for (int i = 0; i < 200; i++)
        {
            // A diff past the longest possible wait, so every call produces a destination.
            Assert.True(generator.TryGetDestination(creature, RandomMovementGenerator.MaxWaitMs + 1, out Position destination));

            float distance = creature.HomePosition.GetExactDist2d(destination);
            Assert.True(distance <= 5f + 0.001f, $"wandered {distance} yards from home");
        }
    }

    /// <summary>
    /// The generator waits between wanders rather than walking continuously.
    /// </summary>
    /// <remarks>
    /// Without it a creature is always moving, which reads as driven rather than alive — and it
    /// means a monster-move packet per creature per arrival, forever.
    /// </remarks>
    [Fact]
    public void Random_WaitsBetweenDestinations()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 5f, movementType: 1);
        RandomMovementGenerator generator = new(5f);

        Assert.True(generator.TryGetDestination(creature, RandomMovementGenerator.MaxWaitMs + 1, out _));

        // A tick shorter than the shortest possible wait cannot produce another destination.
        Assert.False(generator.TryGetDestination(creature, RandomMovementGenerator.MinWaitMs - 1, out _));
    }

    /// <summary>A wander distance of zero leaves the creature where it is.</summary>
    [Fact]
    public void Random_WithNoWanderDistance_GoesNowhere()
    {
        Creature creature = CreatureFixture.Build();
        RandomMovementGenerator generator = new(0f);

        Assert.False(generator.TryGetDestination(creature, 60_000, out _));
    }

    /// <summary>
    /// A spawn that says "wander" but gives no radius falls back to standing still.
    /// </summary>
    /// <remarks>
    /// Upstream does the same in <c>InitEntry</c>. Without it the creature asks for a destination
    /// every tick, gets its own position, and produces nothing — a wasted draw per creature per tick
    /// forever.
    /// </remarks>
    [Fact]
    public void ARandomSpawnWithNoRadius_BecomesIdle()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 0f, movementType: 1);

        Assert.Equal(MovementGeneratorType.Idle, creature.Motion.CurrentType);
    }

    [Fact]
    public void MovementType_ComesFromTheSpawnRow()
    {
        Assert.Equal(
            MovementGeneratorType.Random,
            CreatureFixture.Build(wanderDistance: 5f, movementType: 1).Motion.CurrentType);

        Assert.Equal(
            MovementGeneratorType.Idle,
            CreatureFixture.Build(movementType: 0).Motion.CurrentType);

        // Waypoint is not implemented, and standing still beats moving wrongly.
        Assert.Equal(
            MovementGeneratorType.Idle,
            CreatureFixture.Build(movementType: 2).Motion.CurrentType);
    }
}

/// <summary>A creature advancing along its move, tick by tick.</summary>
public sealed class CreatureUpdateTests
{
    /// <summary>
    /// A zero gameplay diff must do nothing at all.
    /// </summary>
    /// <remarks>
    /// Three ticks in four are a session-only pass with a diff of zero. Treating that as "a very
    /// short tick" would let creatures start moves on ticks the round-robin meant to skip, and the
    /// wander timers would run four times too fast.
    /// </remarks>
    [Fact]
    public void AZeroDiff_DoesNothing()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 5f, movementType: 1);
        Position before = creature.Position;

        for (int i = 0; i < 100; i++)
        {
            Assert.Null(creature.Update(0));
        }

        Assert.Equal(before, creature.Position);
        Assert.False(creature.IsMoving);
    }

    [Fact]
    public void AWanderingCreature_StartsMovingAndArrives()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 10f, movementType: 1);

        CreatureMove? started = null;

        for (int tick = 0; tick < 50 && started is null; tick++)
        {
            started = creature.Update(RandomMovementGenerator.MaxWaitMs + 1);
        }

        Assert.NotNull(started);
        Assert.True(creature.IsMoving);
        Assert.Equal(1u, creature.SplineId);

        // Walk it out. The move started from where the creature was, not from its home.
        Assert.Equal(creature.HomePosition, started.Value.Start);

        for (uint elapsed = 0; elapsed < started.Value.DurationMs; elapsed += 100)
        {
            creature.Update(100);
        }

        Assert.False(creature.IsMoving);
        Assert.Equal(started.Value.Destination.X, creature.Position.X, 0.01f);
        Assert.Equal(started.Value.Destination.Y, creature.Position.Y, 0.01f);
    }

    /// <summary>
    /// Successive moves start from where the creature is, not from where it began.
    /// </summary>
    /// <remarks>
    /// Upstream overwrites its path's first point with the unit's real position for this reason. A
    /// move that starts from a stale point makes the creature snap there before walking on.
    /// </remarks>
    [Fact]
    public void EachMove_StartsFromWhereTheCreatureActuallyIs()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 10f, movementType: 1);

        CreatureMove? first = RunUntilMove(creature);
        Assert.NotNull(first);

        // Finish it. The same tick may also start the next move: the wander wait is advanced by the
        // same diff, and a long walk covers it. That is not a special case to guard against — it is
        // what happens whenever a creature walks for longer than it then waits.
        CreatureMove? second = creature.Update(first.Value.DurationMs);

        Assert.Equal(first.Value.Destination.X, creature.Position.X, 0.001f);
        Assert.Equal(first.Value.Destination.Y, creature.Position.Y, 0.001f);

        Position arrived = creature.Position;

        second ??= RunUntilMove(creature);
        Assert.NotNull(second);

        Assert.Equal(arrived.X, second.Value.Start.X, 0.001f);
        Assert.Equal(arrived.Y, second.Value.Start.Y, 0.001f);
        Assert.Equal(2u, creature.SplineId);
    }

    /// <summary>
    /// Wandering is measured from home, so a creature does not drift away over time.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is slow and invisible: if each destination were drawn around
    /// the creature's current position instead, it would random-walk, and over an hour a bear ends
    /// up in the next zone.
    /// </remarks>
    [Fact]
    public void OverManyMoves_ACreatureStaysNearItsHome()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 5f, movementType: 1);

        for (int move = 0; move < 100; move++)
        {
            CreatureMove? started = RunUntilMove(creature);

            if (started is null)
            {
                continue;
            }

            creature.Update(started.Value.DurationMs);
        }

        float drift = creature.HomePosition.GetExactDist2d(creature.Position);
        Assert.True(drift <= 5f + 0.001f, $"drifted {drift} yards from home over 100 moves");
    }

    /// <summary>What a late arrival is sent: the remainder, from where the creature is now.</summary>
    [Fact]
    public void RemainingMove_StartsFromThePresentPosition()
    {
        Creature creature = CreatureFixture.Build(wanderDistance: 10f, movementType: 1);

        CreatureMove? started = RunUntilMove(creature);
        Assert.NotNull(started);

        creature.Update(started.Value.DurationMs / 2);

        CreatureMove? remaining = creature.RemainingMove;
        Assert.NotNull(remaining);

        Assert.Equal(creature.Position.X, remaining.Value.Start.X, 0.001f);
        Assert.Equal(started.Value.Destination.X, remaining.Value.Destination.X, 0.001f);
        Assert.True(remaining.Value.DurationMs < started.Value.DurationMs);
    }

    [Fact]
    public void AStationaryCreature_HasNoRemainingMove()
    {
        Assert.Null(CreatureFixture.Build().RemainingMove);
    }

    private static CreatureMove? RunUntilMove(Creature creature)
    {
        for (int tick = 0; tick < 50; tick++)
        {
            CreatureMove? started = creature.Update(RandomMovementGenerator.MaxWaitMs + 1);

            if (started is not null)
            {
                return started;
            }
        }

        return null;
    }
}

/// <summary>
/// The <c>SMSG_MONSTER_MOVE</c> wire format.
/// </summary>
/// <remarks>
/// Positional and unlengthed, like every other update packet: one field of the wrong width and the
/// client reads the rest as garbage.
/// </remarks>
public sealed class MonsterMoveTests
{
    [Fact]
    public void Move_HasEveryFieldInOrder()
    {
        PacketWriter writer = new();
        ObjectGuid mover = ObjectGuid.Create(HighGuid.Unit, 299, 42);

        Position start = new(-8913.2f, 554.6f, 93.7f, 0f);
        Position destination = new(-8900.0f, 560.0f, 93.5f, 0f);

        MonsterMove.Write(writer, mover, start, destination, splineId: 7, durationMs: 3200);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid readMover));
        Assert.Equal(mover, readMover);

        Assert.True(reader.TryReadUInt8(out byte unknown));
        Assert.Equal(0, unknown);

        Assert.Equal(start.X, ReadFloat(ref reader), 0.001f);
        Assert.Equal(start.Y, ReadFloat(ref reader), 0.001f);
        Assert.Equal(start.Z, ReadFloat(ref reader), 0.001f);

        Assert.True(reader.TryReadUInt32(out uint splineId));
        Assert.Equal(7u, splineId);

        Assert.True(reader.TryReadUInt8(out byte moveType));
        Assert.Equal((byte)MonsterMoveType.Normal, moveType);

        Assert.True(reader.TryReadUInt32(out uint flags));
        Assert.Equal(0u, flags);

        Assert.True(reader.TryReadUInt32(out uint duration));
        Assert.Equal(3200u, duration);

        // One, not two. See MonsterMove.WriteLinearPath — the count is derived from a padded
        // spline, and a two-point move reports one.
        Assert.True(reader.TryReadUInt32(out uint pointCount));
        Assert.Equal(1u, pointCount);

        Assert.Equal(destination.X, ReadFloat(ref reader), 0.001f);
        Assert.Equal(destination.Y, ReadFloat(ref reader), 0.001f);
        Assert.Equal(destination.Z, ReadFloat(ref reader), 0.001f);

        Assert.True(reader.Ok);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>The facing flags and animation ids are stripped before sending.</summary>
    [Fact]
    public void FacingAndAnimationFlags_NeverReachTheWire()
    {
        PacketWriter writer = new();

        MonsterMove.Write(
            writer,
            ObjectGuid.Create(HighGuid.Unit, 1, 1),
            new Position(0f, 0f, 0f, 0f),
            new Position(1f, 0f, 0f, 0f),
            splineId: 1,
            durationMs: 100,
            flags: MoveSplineFlag.FinalAngle | MoveSplineFlag.Done | MoveSplineFlag.Flying);

        PacketReader reader = new(writer.WrittenSpan);

        reader.TryReadPackedGuid(out ObjectGuid _);
        reader.TryReadUInt8(out _);
        ReadFloat(ref reader);
        ReadFloat(ref reader);
        ReadFloat(ref reader);
        reader.TryReadUInt32(out _);
        reader.TryReadUInt8(out _);

        Assert.True(reader.TryReadUInt32(out uint flags));

        // Flying survives; the facing and done bits do not.
        Assert.Equal((uint)MoveSplineFlag.Flying, flags);
    }

    /// <summary>A stop is shorter than a move and must not carry a body.</summary>
    [Fact]
    public void Stop_EndsAtTheTypeByte()
    {
        PacketWriter writer = new();
        ObjectGuid mover = ObjectGuid.Create(HighGuid.Unit, 299, 42);

        MonsterMove.WriteStop(writer, mover, new Position(1f, 2f, 3f, 0f), splineId: 9);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid _));
        Assert.True(reader.TryReadUInt8(out _));

        Assert.Equal(1f, ReadFloat(ref reader), 0.001f);
        Assert.Equal(2f, ReadFloat(ref reader), 0.001f);
        Assert.Equal(3f, ReadFloat(ref reader), 0.001f);

        Assert.True(reader.TryReadUInt32(out uint splineId));
        Assert.Equal(9u, splineId);

        Assert.True(reader.TryReadUInt8(out byte moveType));
        Assert.Equal((byte)MonsterMoveType.Stop, moveType);

        Assert.Equal(0, reader.Remaining);
    }

    private static float ReadFloat(ref PacketReader reader)
    {
        Assert.True(reader.TryReadUInt32(out uint bits));
        return BitConverter.UInt32BitsToSingle(bits);
    }
}

/// <summary>Builds creatures for the movement tests without a database.</summary>
internal static class CreatureFixture
{
    /// <summary>
    /// Hands out a fresh spawn id per creature, so every fixture creature has a distinct guid.
    /// </summary>
    /// <remarks>
    /// Not a detail. A creature's guid is built from its entry and spawn id, so a fixed spawn id
    /// makes every fixture creature the <i>same</i> guid — and anything keyed by guid, a threat list
    /// or a visibility set, silently collapses three creatures into one. Interlocked because xunit
    /// runs classes in parallel.
    /// </remarks>
    private static int _nextSpawnId;

    public static Creature Build(
        float wanderDistance = 0f,
        byte movementType = 0,
        byte rank = 0,
        byte creatureType = 1,
        uint flagsExtra = 0,
        uint npcFlags = 0,
        Position? position = null,
        uint lootId = 0,
        uint minGold = 0,
        uint maxGold = 0,
        uint gossipMenuId = 0)
    {
        StubModels models = new();
        models.Add(new CreatureModelInfo(4481, 0.372f, 1.5f, 0, 0));

        CreatureSpawn spawn = new(
            SpawnId: (uint)System.Threading.Interlocked.Increment(ref _nextSpawnId),
            Entry: 299,
            MapId: 0,
            SpawnMask: 1,
            PhaseMask: 1,
            ModelId: 0,
            // Through the spawn row, so HomePosition follows. Assigning Position afterwards instead
            // leaves home where the fixture put it, and the creature evades the moment it has a
            // victim because it believes it has been dragged nine thousand yards.
            Position: position ?? new Position(-8913.2f, 554.6f, 93.7f, 0f),
            CurrentHealth: 1,
            CurrentMana: 0,
            NpcFlags: npcFlags,
            UnitFlags: 0,
            DynamicFlags: 0,
            WanderDistance: wanderDistance,
            MovementType: movementType,
            RespawnDelaySeconds: 120);

        CreatureTemplate template = new(
            Entry: 299,
            Name: "Diseased Young Wolf",
            SubName: string.Empty,
            ModelId1: 4481,
            ModelId2: 0,
            ModelId3: 0,
            ModelId4: 0,
            MinLevel: 5,
            MaxLevel: 5,
            Expansion: 0,
            Faction: 14,
            NpcFlags: 0,
            SpeedWalk: 1.0f,
            SpeedRun: 1.14286f,
            Scale: 1.0f,
            Rank: rank,
            UnitClass: 1,
            UnitFlags: 0,
            UnitFlags2: 2048,
            DynamicFlags: 0,
            CreatureType: creatureType,
            TypeFlags: 0,
            Family: 0,
            HealthModifier: 1.0f,
            ManaModifier: 1.0f,
            ArmorModifier: 1.0f,
            MovementType: movementType,
            RegeneratesHealth: true,
            MinDamage: 4f,
            MaxDamage: 6f,
            DamageModifier: 1f,
            BaseAttackTime: 2000,
            RangeAttackTime: 2000,
            AttackPower: 14,
            RangedAttackPower: 0,
            FlagsExtra: flagsExtra,
            LootId: lootId,
            MinGold: minGold,
            MaxGold: maxGold,
            GossipMenuId: gossipMenuId);

        CreatureBaseStats stats = new(100, 200, 300, 50, 60, 20, 5, 1.5f, 2f, 3f);

        Creature? creature = Creature.Create(
            spawn, template, models, stats, level: 5, useOppositeGenderModel: false, displayId: 4481);

        Assert.NotNull(creature);
        return creature;
    }

    private sealed class StubModels : ICreatureModelSource
    {
        private readonly Dictionary<uint, CreatureModelInfo> _models = [];

        public void Add(CreatureModelInfo model) => _models[model.DisplayId] = model;

        public bool TryGetModel(uint displayId, out CreatureModelInfo model) =>
            _models.TryGetValue(displayId, out model);
    }
}
