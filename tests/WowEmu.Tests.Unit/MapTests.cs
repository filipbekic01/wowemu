using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;
using WowEmu.Game.Movement;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>Grid and cell arithmetic, which is inverted and centred on the world origin.</summary>
public sealed class MapCoordinatesTests
{
    [Fact]
    public void Origin_SitsAtTheCentre()
    {
        Assert.Equal(new GridCoord(32, 32), MapCoordinates.GridFor(0f, 0f));
        Assert.Equal(new CellCoord(256, 256), MapCoordinates.CellFor(0f, 0f));
    }

    /// <summary>World coordinates grow the opposite way to indices; a sign error here mirrors the map.</summary>
    [Fact]
    public void Axis_IsInverted()
    {
        CellCoord positive = MapCoordinates.CellFor(MapCoordinates.CellSize * 10, 0f);
        CellCoord negative = MapCoordinates.CellFor(-MapCoordinates.CellSize * 10, 0f);

        Assert.Equal(246, positive.X);
        Assert.Equal(266, negative.X);
    }

    [Fact]
    public void CellsPerGrid_MultiplyOut()
    {
        Assert.Equal(8, MapCoordinates.CellsPerGrid);
        Assert.Equal(512, MapCoordinates.CellsPerMap);
        Assert.Equal(66.66666f, MapCoordinates.CellSize, 0.001f);
    }

    [Fact]
    public void GridOf_MapsCellsBackToTheirGrid()
    {
        Assert.Equal(new GridCoord(32, 32), MapCoordinates.GridOf(MapCoordinates.CellFor(0f, 0f)));
    }

    /// <summary>
    /// 100 yards of visibility spans 1.5 cells either side of 66.7-yard cells, so the bounding
    /// square is 4×4 from the origin — not 5×5, because the origin sits on a cell boundary rather
    /// than in the middle of one.
    /// </summary>
    [Fact]
    public void CellsInRange_CoversTheBoundingSquare()
    {
        List<CellCoord> cells = [.. MapCoordinates.CellsInRange(0f, 0f, MapCoordinates.DefaultVisibilityDistance)];

        Assert.Equal(16, cells.Count);
        Assert.Contains(new CellCoord(256, 256), cells);
        Assert.Equal(cells.Count, cells.Distinct().Count());

        // Every cell the circle touches has to be in there; a missing one makes an object
        // invisible from one direction only.
        Assert.Contains(MapCoordinates.CellFor(99f, 0f), cells);
        Assert.Contains(MapCoordinates.CellFor(-99f, 0f), cells);
        Assert.Contains(MapCoordinates.CellFor(0f, 99f), cells);
        Assert.Contains(MapCoordinates.CellFor(0f, -99f), cells);
    }

    [Fact]
    public void CoordinatesBeyondTheMap_AreClamped()
    {
        CellCoord far = MapCoordinates.CellFor(-1_000_000f, -1_000_000f);

        Assert.InRange(far.X, 0, MapCoordinates.CellsPerMap - 1);
        Assert.InRange(far.Y, 0, MapCoordinates.CellsPerMap - 1);
    }
}

/// <summary>
/// The map's visibility bookkeeping.
/// </summary>
/// <remarks>
/// What these pin down is that each client is told exactly once when something appears and exactly
/// once when it goes — a duplicate create makes a character flicker, and a missed destroy leaves a
/// ghost standing where a player used to be.
/// </remarks>
public sealed class MapVisibilityTests
{
    [Fact]
    public void PlayersInRange_SeeEachOtherOnArrival()
    {
        Map map = NewMap();

        (Player first, RecordingConnection firstLink) = NewPlayer(1, 0f, 0f);
        (Player second, RecordingConnection secondLink) = NewPlayer(2, 10f, 10f);

        map.Add(first);
        map.Add(second);

        Assert.Contains(second.Guid, first.VisibleObjects);
        Assert.Contains(first.Guid, second.VisibleObjects);

        Assert.Equal([second.Guid], firstLink.Created);
        Assert.Equal([first.Guid], secondLink.Created);
    }

    [Fact]
    public void PlayersOutOfRange_DoNotSeeEachOther()
    {
        Map map = NewMap();

        (Player first, RecordingConnection firstLink) = NewPlayer(1, 0f, 0f);
        (Player second, _) = NewPlayer(2, 500f, 500f);

        map.Add(first);
        map.Add(second);

        Assert.Empty(first.VisibleObjects);
        Assert.Empty(firstLink.Created);
    }

    /// <summary>Walking into range creates; walking out destroys. Both exactly once.</summary>
    [Fact]
    public void WalkingIntoAndOutOfRange_CreatesThenDestroys()
    {
        Map map = NewMap();

        (Player walker, RecordingConnection walkerLink) = NewPlayer(1, 500f, 0f);
        (Player stationary, RecordingConnection stationaryLink) = NewPlayer(2, 0f, 0f);

        map.Add(stationary);
        map.Add(walker);

        Assert.Empty(walkerLink.Created);

        // Into range.
        map.Relocate(walker, new Position(20f, 0f, 0f, 0f));

        Assert.Equal([stationary.Guid], walkerLink.Created);
        Assert.Equal([walker.Guid], stationaryLink.Created);

        // Back out again.
        map.Relocate(walker, new Position(500f, 0f, 0f, 0f));

        Assert.Equal([stationary.Guid], walkerLink.Destroyed);
        Assert.Equal([walker.Guid], stationaryLink.Destroyed);
        Assert.Empty(walker.VisibleObjects);
    }

    /// <summary>
    /// Moving while already visible must not re-send a create — that is what would make every
    /// nearby character flicker on every movement packet.
    /// </summary>
    [Fact]
    public void MovingWhileVisible_DoesNotResendCreates()
    {
        Map map = NewMap();

        (Player mover, RecordingConnection moverLink) = NewPlayer(1, 0f, 0f);
        (Player other, RecordingConnection otherLink) = NewPlayer(2, 10f, 0f);

        map.Add(other);
        map.Add(mover);

        int createsBefore = moverLink.Created.Count;
        int otherCreatesBefore = otherLink.Created.Count;

        for (int step = 1; step <= 5; step++)
        {
            map.Relocate(mover, new Position(step, 0f, 0f, 0f));
        }

        Assert.Equal(createsBefore, moverLink.Created.Count);
        Assert.Equal(otherCreatesBefore, otherLink.Created.Count);
    }

    [Fact]
    public void LeavingTheMap_DestroysForEveryoneWhoCouldSee()
    {
        Map map = NewMap();

        (Player leaver, _) = NewPlayer(1, 0f, 0f);
        (Player watcher, RecordingConnection watcherLink) = NewPlayer(2, 5f, 5f);

        map.Add(watcher);
        map.Add(leaver);

        map.Remove(leaver);

        Assert.Equal([leaver.Guid], watcherLink.Destroyed);
        Assert.DoesNotContain(leaver.Guid, watcher.VisibleObjects);
        Assert.Equal(1, map.PlayerCount);
    }

    /// <summary>Movement goes to everyone who can see the mover, and never back to the mover.</summary>
    [Fact]
    public void MovementBroadcast_SkipsTheMover()
    {
        Map map = NewMap();

        (Player mover, RecordingConnection moverLink) = NewPlayer(1, 0f, 0f);
        (Player watcher, RecordingConnection watcherLink) = NewPlayer(2, 10f, 0f);

        map.Add(mover);
        map.Add(watcher);

        map.BroadcastMovement(mover, Opcode.MSG_MOVE_HEARTBEAT, mover.Movement);

        Assert.Equal([mover.Guid], watcherLink.Moved);
        Assert.Empty(moverLink.Moved);
    }

    [Fact]
    public void FindInRange_ExcludesTheSubject()
    {
        Map map = NewMap();

        (Player first, _) = NewPlayer(1, 0f, 0f);
        (Player second, _) = NewPlayer(2, 10f, 0f);

        map.Add(first);
        map.Add(second);

        IReadOnlyList<WorldObject> found = map.FindInRange(first.Position, 50f, first);

        Assert.Equal([second], found);
    }

    /// <summary>
    /// A swing reaches everyone who can see either end of it, and exactly once each.
    /// </summary>
    /// <remarks>
    /// The two watcher sets overlap heavily — anyone standing near a fight sees both fighters — so
    /// concatenating them instead of unioning them sends the common case twice, and the client draws
    /// two damage numbers for one hit.
    /// </remarks>
    [Fact]
    public void ASwing_ReachesBothSidesWatchersExactlyOnce()
    {
        Map map = NewMap();

        (Player attacker, RecordingConnection attackerLink) = NewPlayer(1, 0f, 0f);
        (Player victim, RecordingConnection victimLink) = NewPlayer(2, 5f, 0f);
        (Player bystander, RecordingConnection bystanderLink) = NewPlayer(3, 10f, 0f);

        map.Add(attacker);
        map.Add(victim);
        map.Add(bystander);

        MeleeDamageInfo info = MeleeDamage.Apply(MeleeHitOutcome.Normal, 42, 60, 60);

        map.BroadcastMeleeSwing(attacker, victim, info, victimHealthBeforeHit: 500);

        // The bystander can see both fighters, and gets one swing rather than two.
        Assert.Single(bystanderLink.Swings);
        Assert.Equal((attacker.Guid, victim.Guid, info), bystanderLink.Swings[0]);

        // Each fighter can see the other, so each is told too.
        Assert.Single(attackerLink.Swings);
        Assert.Single(victimLink.Swings);
    }

    /// <summary>Someone too far away to see either fighter hears nothing.</summary>
    [Fact]
    public void ASwing_DoesNotReachSomeoneOutOfRange()
    {
        Map map = NewMap();

        (Player attacker, _) = NewPlayer(1, 0f, 0f);
        (Player victim, _) = NewPlayer(2, 5f, 0f);
        (Player distant, RecordingConnection distantLink) = NewPlayer(3, 500f, 500f);

        map.Add(attacker);
        map.Add(victim);
        map.Add(distant);

        map.BroadcastMeleeSwing(attacker, victim, MeleeDamage.Apply(MeleeHitOutcome.Normal, 42, 60, 60), 500);

        Assert.Empty(distantLink.Swings);
    }

    /// <summary>
    /// A change to one player reaches everyone who can see them.
    /// </summary>
    /// <remarks>
    /// The point of the whole per-observer filter. Before it, a values block was only ever sent to
    /// the object's own client, so a second player was a mannequin frozen at whatever its create
    /// block said — health, level and equipment all fixed for as long as it stayed in sight.
    /// </remarks>
    [Fact]
    public void AChangedPlayer_IsBroadcastToWhoeverCanSeeThem()
    {
        Map map = NewMap();

        (Player changed, RecordingConnection changedLink) = NewPlayer(1, 0f, 0f);
        (Player watcher, RecordingConnection watcherLink) = NewPlayer(2, 10f, 0f);

        map.Add(changed);
        map.Add(watcher);

        // A freshly built player is dirty in every field it was given. The real login path clears
        // that in SendSelfCreate, once the create block has carried it; without the same clearing
        // here every test below would be measuring the construction rather than the change.
        changed.Fields.ClearDirty();
        watcher.Fields.ClearDirty();

        changed.Health = 42;
        map.Update(1, 1);

        Assert.Equal([changed.Guid], watcherLink.ValuesUpdates);

        // Not to itself through this path: a player's own block is built by its own flush, and is
        // the unfiltered one. Coming through here as well would send it the public half twice.
        Assert.Empty(changedLink.ValuesUpdates);
    }

    /// <summary>Someone too far away to have been sent a create block is told nothing.</summary>
    /// <remarks>
    /// The gate is the visible set rather than distance, because that is what the client's own state
    /// is: a values block naming a guid it has no object for is dropped on the floor.
    /// </remarks>
    [Fact]
    public void AChangedPlayer_IsNotBroadcastToSomeoneOutOfRange()
    {
        Map map = NewMap();

        (Player changed, _) = NewPlayer(1, 0f, 0f);
        (Player distant, RecordingConnection distantLink) = NewPlayer(2, 500f, 500f);

        map.Add(changed);
        map.Add(distant);

        changed.Fields.ClearDirty();
        distant.Fields.ClearDirty();

        changed.Health = 42;
        map.Update(1, 1);

        Assert.Empty(distantLink.ValuesUpdates);
    }

    /// <summary>
    /// The broadcast pass leaves a player's own dirty mask alone.
    /// </summary>
    /// <remarks>
    /// This is the ordering the whole design rests on. The player's own flush runs a few lines later
    /// and needs the mask still set, because that flush is where the unfiltered block only it may
    /// have is built. Clearing here would send every observer the public half and the player itself
    /// nothing at all.
    /// </remarks>
    [Fact]
    public void Broadcasting_DoesNotClearThePlayersOwnDirtyMask()
    {
        Map map = NewMap();

        (Player changed, _) = NewPlayer(1, 0f, 0f);
        (Player watcher, _) = NewPlayer(2, 10f, 0f);

        map.Add(changed);
        map.Add(watcher);

        changed.Fields.ClearDirty();
        watcher.Fields.ClearDirty();

        changed.Health = 42;
        map.Update(1, 1);

        Assert.True(changed.Fields.IsDirty);
    }

    /// <summary>
    /// A creature's changes are broadcast, and its mask is cleared once they have been.
    /// </summary>
    /// <remarks>
    /// A creature has no flush of its own to clear the mask in — nothing else would ever clear it —
    /// so the same change would be rebroadcast to everyone in sight on every tick from then on. It
    /// is also the first time a creature's health has been transmitted at all: nothing built a
    /// values block for one before this existed, so a mob's health bar never moved.
    /// </remarks>
    [Fact]
    public void AChangedCreature_IsBroadcastOnceAndThenClean()
    {
        Creature creature = CreatureFixture.Build(position: new Position(5f, 0f, 0f, 0f));

        Map map = new(0, new TerrainMap(0, Path.GetTempPath()), new OneCreature(creature));

        (Player watcher, RecordingConnection watcherLink) = NewPlayer(1, 0f, 0f);
        map.Add(watcher);

        Assert.Contains(creature.Guid, watcher.VisibleObjects);

        // As above: both are dirty from construction, and the create blocks have already carried it.
        creature.Fields.ClearDirty();
        watcher.Fields.ClearDirty();

        creature.Health = 1;
        map.Update(1, 1);

        Assert.Equal([creature.Guid], watcherLink.ValuesUpdates);
        Assert.False(creature.Fields.IsDirty);

        // And a second tick with nothing new to say sends nothing.
        map.Update(1, 1);

        Assert.Single(watcherLink.ValuesUpdates);
    }

    /// <summary>
    /// Environmental damage goes through the same death path as anything else.
    /// </summary>
    /// <remarks>
    /// The map applies it rather than the environment code, for the same reason a spell's damage is
    /// applied there: the one place health changes is the one place death gets noticed. A drowning
    /// that bypassed it would leave a player at zero health, alive, and unable to release.
    /// </remarks>
    [Fact]
    public void EnvironmentalDamage_KillsAndIsBroadcast()
    {
        Map map = NewMap();

        (Player drowning, RecordingConnection drowningLink) = NewPlayer(1, 0f, 0f);
        (Player watcher, RecordingConnection watcherLink) = NewPlayer(2, 10f, 0f);

        map.Add(drowning);
        map.Add(watcher);

        drowning.MaxHealth = 100;
        drowning.Health = 30;

        map.ApplyEnvironmentalDamage(drowning, EnvironmentalDamageType.Drowning, 50);

        Assert.Equal(0u, drowning.Health);
        Assert.False(drowning.IsAlive);

        // The victim is told it died, and everyone watching sees where the damage came from.
        Assert.NotEmpty(drowningLink.Deaths);
        Assert.Equal((EnvironmentalDamageType.Drowning, 30u), Assert.Single(drowningLink.EnvironmentalDamage));
        Assert.Equal((EnvironmentalDamageType.Drowning, 30u), Assert.Single(watcherLink.EnvironmentalDamage));
    }

    /// <summary>The log reports what was actually taken, not what was asked for.</summary>
    /// <remarks>
    /// A player on 30 health hit for 50 loses 30. Reporting 50 shows a number larger than the health
    /// bar had in it, which reads as a bug to anyone watching the combat log.
    /// </remarks>
    [Fact]
    public void EnvironmentalDamage_IsClampedToWhatTheVictimHad()
    {
        Map map = NewMap();

        (Player player, RecordingConnection link) = NewPlayer(1, 0f, 0f);
        map.Add(player);

        player.MaxHealth = 100;
        player.Health = 30;

        map.ApplyEnvironmentalDamage(player, EnvironmentalDamageType.Fall, 999);

        Assert.Equal(30u, Assert.Single(link.EnvironmentalDamage).Amount);
    }

    /// <summary>Hands one creature to whichever grid the player arrives in.</summary>
    private sealed class OneCreature(Creature creature) : IGridObjectLoader
    {
        private bool _loaded;

        public IReadOnlyList<WorldObject> Load(uint mapId, GridCoord grid)
        {
            if (_loaded)
            {
                return [];
            }

            _loaded = true;
            return [creature];
        }
    }

    private static CancellationToken TestToken => CancellationToken.None;

    private static Map NewMap() => new(0, new TerrainMap(0, Path.GetTempPath()));

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

    /// <summary>Records what the map asked a client to be told, instead of sending it.</summary>
    private sealed class RecordingConnection : IPlayerConnection
    {
        public List<ObjectGuid> Created { get; } = [];

        public List<ObjectGuid> Destroyed { get; } = [];

        public List<ObjectGuid> Moved { get; } = [];

        /// <summary>Creature moves this client was told to start interpolating.</summary>
        public List<(ObjectGuid Mover, CreatureMove Move)> MonsterMoves { get; } = [];

        /// <summary>Melee swings this client was told about.</summary>
        public List<(ObjectGuid Attacker, ObjectGuid Target, MeleeDamageInfo Info)> Swings { get; } = [];

        /// <summary>How many times a tick's worth of updates was flushed.</summary>
        public int Flushes { get; private set; }

        public void QueueCreate(WorldObject other)
        {
            ArgumentNullException.ThrowIfNull(other);

            Created.Add(other.Guid);
        }

        public void QueueDestroy(ObjectGuid objectGuid) => Destroyed.Add(objectGuid);

        /// <summary>Mirror-timer bars this client was told to draw, update or remove.</summary>
        public List<MirrorTimerUpdate> MirrorTimers { get; } = [];

        /// <summary>Environmental damage this client was told about.</summary>
        public List<(EnvironmentalDamageType Type, uint Amount)> EnvironmentalDamage { get; } = [];

        public void SendMirrorTimer(MirrorTimerUpdate timer) => MirrorTimers.Add(timer);

        public void QueueEnvironmentalDamage(ObjectGuid victim, EnvironmentalDamageType type, uint amount) =>
            EnvironmentalDamage.Add((type, amount));


        /// <summary>Objects this client was told had changed, one entry per block.</summary>
        public List<ObjectGuid> ValuesUpdates { get; } = [];

        public void QueueValues(WorldObject other)
        {
            ArgumentNullException.ThrowIfNull(other);

            ValuesUpdates.Add(other.Guid);
        }

        public void FlushUpdates() => Flushes++;

        public void QueueMonsterMove(ObjectGuid mover, CreatureMove move, uint splineId) =>
            MonsterMoves.Add((mover, move));

        public void QueueMeleeSwing(
            ObjectGuid attacker, ObjectGuid target, MeleeDamageInfo info, uint targetHealthBeforeHit) =>
            Swings.Add((attacker, target, info));

        /// <summary>Attack starts and stops this client was told about.</summary>
        public List<(ObjectGuid Victim, bool Attacking)> AttackStates { get; } = [];

        /// <summary>Swing failures this client was told about, including the clearing None.</summary>
        public List<SwingError> SwingErrors { get; } = [];

        public void SendAttackState(ObjectGuid attacker, ObjectGuid? victim, bool attacking, bool victimIsDead) =>
            AttackStates.Add((victim ?? ObjectGuid.Empty, attacking));


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


        /// <summary>Deaths this client was told about, as the reclaim delay it was given.</summary>
        public List<int> Deaths { get; } = [];

        /// <summary>Spirit-healer markers, and the clears (map id uint.MaxValue).</summary>
        public List<(uint MapId, Position At)> SpiritHealers { get; } = [];

        public void SendPlayerDied(int reclaimDelayMs) => Deaths.Add(reclaimDelayMs);

        public void SendSpiritHealerLocation(uint mapId, Position at) => SpiritHealers.Add((mapId, at));

        public void SendResurrected() => SendSpiritHealerLocation(uint.MaxValue, default);

        public void SendSwingError(SwingError reason) => SwingErrors.Add(reason);

        public void SendAuraApplied(
            ObjectGuid target,
            byte slot,
            uint spellId,
            byte flags,
            byte casterLevel,
            byte stackAmount,
            ObjectGuid caster,
            int maxDurationMs,
            int remainingMs)
        {
        }

        public void SendAuraRemoved(ObjectGuid target, byte slot)
        {
        }

        public void QueuePeriodicAuraLog(
            ObjectGuid target,
            ObjectGuid caster,
            uint spellId,
            uint auraType,
            uint amount,
            uint overflow,
            uint schoolMask)
        {
        }

        public void SendLootWindow(ObjectGuid target, byte lootType, uint gold, IReadOnlyList<LootSlot> slots)
        {
        }

        public void SendLootError(ObjectGuid target, LootError reason)
        {
        }

        public void SendLootRemoved(byte slot)
        {
        }

        public void SendLootMoneyTaken(uint copper)
        {
        }

        public void SendLootReleased(ObjectGuid target)
        {
        }

        public void SendItemPushed(in ItemPushResult push)
        {
        }

        public void SendQuestKillCredit(
            uint questId, uint wireEntry, uint current, uint required, ObjectGuid victim)
        {
        }

        public void SendQuestComplete(uint questId)
        {
        }

        public void DrainMapPackets(uint diff)
        {
        }

        public void SendMovement(Opcode opcode, ObjectGuid mover, MovementInfo movement) => Moved.Add(mover);
    }
}
