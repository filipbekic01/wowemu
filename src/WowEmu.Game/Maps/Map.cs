using Microsoft.Extensions.Logging;
using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game.Combat;
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

    /// <summary>
    /// Notes that a creature has started walking somewhere, to go out at the next flush.
    /// </summary>
    /// <remarks>
    /// Queued rather than sent, so that it cannot overtake the create block for the same creature.
    /// A client told to move an object it has not been told about yet simply drops the message, and
    /// the creature then stands still until its next move — for up to ten seconds.
    /// </remarks>
    void QueueMonsterMove(ObjectGuid mover, Movement.CreatureMove move, uint splineId);

    /// <summary>
    /// Relays one melee swing, for the combat log and the floating damage number.
    /// </summary>
    /// <remarks>
    /// Queued rather than sent, for the same reason a monster move is: a swing that reaches the
    /// client before the create block for the attacker is dropped, and the fight starts invisibly.
    /// <para>
    /// The game layer hands over its own <see cref="Combat.MeleeDamageInfo"/> and lets the session
    /// encode it. Passing a wire-shaped record instead would put the packet's conditional layout —
    /// which trailer follows which flag — into the layer that decided the damage.
    /// </para>
    /// </remarks>
    /// <param name="targetHealthBeforeHit">
    /// The victim's health before the swing lands, which is what the overkill figure is measured
    /// against. Reading it after applying the damage reports every killing blow as pure overkill.
    /// </param>
    void QueueMeleeSwing(
        ObjectGuid attacker,
        ObjectGuid target,
        Combat.MeleeDamageInfo info,
        uint targetHealthBeforeHit);

    /// <summary>Tells this client to start or stop drawing an attack animation.</summary>
    /// <remarks>
    /// Immediate rather than queued. The animation is the client's own feedback that its attack
    /// command was heard, and holding it until the flush makes the click feel unacknowledged.
    /// </remarks>
    void SendAttackState(ObjectGuid attacker, ObjectGuid? victim, bool attacking, bool victimIsDead);

    /// <summary>
    /// Tells this client that a cast has started, so it draws a cast bar.
    /// </summary>
    /// <remarks>
    /// Immediate, like the attack animation and for the same reason: the bar is the client's own
    /// confirmation that its keypress was heard.
    /// </remarks>
    void SendSpellStart(
        ObjectGuid caster, uint spellId, byte castCount, int castTimeMs, ObjectGuid target, uint powerLeft);

    /// <summary>Tells this client that a cast landed, so it closes the bar and plays the impact.</summary>
    void SendSpellGo(ObjectGuid caster, uint spellId, byte castCount, ObjectGuid target, uint powerLeft);

    /// <summary>Tells this client its cast was refused, and why.</summary>
    void SendCastFailed(byte castCount, uint spellId, SpellCastResult result);

    /// <summary>
    /// Relays one spell's damage, for the combat log and the floating number.
    /// </summary>
    /// <remarks>
    /// Queued rather than sent, for the same reason a swing is: a damage log naming a caster the
    /// client has not been told about draws nothing.
    /// </remarks>
    void QueueSpellDamage(
        ObjectGuid target,
        ObjectGuid caster,
        uint spellId,
        Combat.SpellHit hit,
        uint targetHealthBeforeHit);

    /// <summary>
    /// Tells this client why its swing did not land.
    /// </summary>
    /// <remarks>
    /// Sent once per run of failures rather than per tick — the swing is retried every 100 ms, and a
    /// client told ten times a second that it is out of range prints the message ten times a second.
    /// </remarks>
    void SendSwingError(Combat.SwingError reason);

    /// <summary>
    /// Tells this client it gained experience, and about any levels that came with it.
    /// </summary>
    /// <remarks>
    /// One call rather than two, because the order matters: the experience log has to reach the
    /// client before the level-up banner or the banner appears with no reason for it.
    /// </remarks>
    void SendExperienceGain(ObjectGuid victim, uint amount, IReadOnlyList<Combat.LevelUp> levels);

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
public sealed class Map(
    uint mapId,
    TerrainMap terrain,
    IGridObjectLoader? gridObjects = null,
    ILogger? logger = null,
    StaticMapTree? collision = null)
{
    private readonly Dictionary<CellCoord, List<WorldObject>> _cells = [];
    private readonly Dictionary<ObjectGuid, WorldObject> _objects = [];
    private readonly Dictionary<ObjectGuid, Player> _players = [];
    private readonly HashSet<GridCoord> _loadedGrids = [];

    // Creatures that can move, kept separately so a tick does not walk every object on the
    // continent to find the few thousand that are going somewhere.
    private readonly List<Creature> _creatures = [];

    public uint MapId { get; } = mapId;

    /// <summary>Which phase of the round-robin updates this map. See <see cref="MapManager"/>.</summary>
    public MapKind Kind { get; init; } = MapKind.Continent;

    /// <summary>How many times <see cref="Update"/> has run with a non-zero gameplay diff.</summary>
    public long FullUpdates { get; private set; }

    /// <summary>How many times <see cref="Update"/> has run at all.</summary>
    public long TotalUpdates { get; private set; }

    /// <summary>The terrain under this map.</summary>
    public TerrainMap Terrain { get; } = terrain;

    /// <summary>Static collision — buildings, bridges, everything terrain does not know about.</summary>
    public StaticMapTree? Collision { get; } = collision;

    /// <summary>
    /// Who fights whom, from <c>FactionTemplate.dbc</c>.
    /// </summary>
    /// <remarks>
    /// Optional: a map built without it has creatures that never start fights. That is the safe
    /// direction to fail — the alternative would be a zone that attacks on sight, which reads as a
    /// game rule rather than as missing data.
    /// </remarks>
    public DbcStore<FactionTemplateEntry>? Factions { get; init; }

    /// <summary>How much experience each level costs. Without it nothing gains experience.</summary>
    public PlayerXpStore? ExperienceTable { get => _xpTable; init => _xpTable = value; }

    /// <summary>Base stats per race, class and level — what a level-up recomputes from.</summary>
    public PlayerStatsStore? PlayerStats { get => _playerStats; init => _playerStats = value; }

    /// <summary>The server's experience multiplier. 1.0 is Blizzard's own rate.</summary>
    public float ExperienceRate { get; init; } = 1.0f;

    private readonly PlayerXpStore? _xpTable;
    private readonly PlayerStatsStore? _playerStats;

    /// <summary>
    /// The surface under a point: the higher of terrain and any model standing there.
    /// </summary>
    /// <remarks>
    /// Null where neither knows — a hole in the terrain, an unloaded tile, or open sky. Callers must
    /// treat that as "no answer" rather than as a floor at zero.
    /// </remarks>
    public float? GetFloor(float x, float y, float z) => WorldHeight.GetFloor(Terrain, Collision, x, y, z);

    /// <summary>Whether one point on this map can see another.</summary>
    /// <remarks>Clear when there is no collision data: a missing file must not blind the world.</remarks>
    public bool IsInLineOfSight(Position from, Position to) =>
        Collision is null || Collision.IsInLineOfSight(from.X, from.Y, from.Z, to.X, to.Y, to.Z);

    /// <summary>How far players can see here.</summary>
    public float VisibilityDistance { get; init; } = MapCoordinates.DefaultVisibilityDistance;

    /// <summary>How many players are on this map.</summary>
    public int PlayerCount => _players.Count;

    /// <summary>How many objects of every kind are on this map.</summary>
    public int ObjectCount => _objects.Count;

    /// <summary>How many grids have had their spawns loaded.</summary>
    public int LoadedGridCount => _loadedGrids.Count;

    /// <summary>How many creatures are on this map.</summary>
    public int CreatureCount => _creatures.Count;

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

        UpdateCreatures(gameplayDiff);
        UpdateCombat(gameplayDiff);

        // Last, and unconditional. A player whose map is out of phase still had things happen to it
        // during the session pass, and holding those until the next full update would show as a
        // visible stutter every fourth tick.
        foreach (Player player in Players)
        {
            player.Connection?.FlushUpdates();
        }
    }

    /// <summary>
    /// Walks every creature that is going somewhere.
    /// </summary>
    /// <remarks>
    /// Does nothing on a session-only pass, because <paramref name="gameplayDiff"/> is zero there
    /// and a creature must not advance on a tick that was not meant to move the world.
    /// <para>
    /// A creature that moves has to be re-filed into its new cell, exactly as a player does — a
    /// creature that wanders out of the cell it is indexed under stays visible to people who have
    /// walked away from it and invisible to people who have walked up to it.
    /// </para>
    /// </remarks>
    private void UpdateCreatures(uint gameplayDiff)
    {
        if (gameplayDiff == 0 || _creatures.Count == 0)
        {
            return;
        }

        // Materialised: a respawn or a corpse removal files and unfiles cells, and a despawned
        // creature stays in this list so it has something to come back on.
        foreach (Creature creature in _creatures.ToList())
        {
            if (!creature.IsAlive)
            {
                UpdateDeadCreature(creature, gameplayDiff);
                continue;
            }

            CellCoord before = creature.Cell;
            Movement.CreatureMove? started = creature.Update(gameplayDiff);

            CellCoord after = MapCoordinates.CellFor(creature.Position.X, creature.Position.Y);

            if (after != before)
            {
                CellAt(before).Remove(creature);
                creature.Cell = after;
                CellAt(after).Add(creature);
            }

            if (started is null)
            {
                continue;
            }

            // Only the start of a move goes on the wire. The client interpolates the rest itself,
            // which is the entire point of sending a duration — a packet per tick would be both
            // enormous and jerkier than what the client already draws.
            List<Player> watchers = PlayersWhoSeeCore(creature.Guid);

            foreach (Player watcher in watchers)
            {
                watcher.Connection?.QueueMonsterMove(creature.Guid, started.Value, creature.SplineId);
            }

            if (logger is not null)
            {
                // Measured into a local: the analyzer objects to work inside a log call, and a
                // square root per creature per move is not free at this scale.
                float distance = started.Value.Start.GetExactDist2d(started.Value.Destination);

                Log.CreatureMoveStarted(
                    logger, creature.Name, distance, started.Value.DurationMs, watchers.Count);
            }
        }
    }

    /// <summary>
    /// Ticks a corpse down and brings the creature back when its time comes.
    /// </summary>
    /// <remarks>
    /// A despawned creature stays in <see cref="_creatures"/> but is taken out of its cell and out
    /// of the guid index, so nothing can see it, find it or attack it — while something still ticks
    /// it towards coming back. Removing it outright would be simpler and would mean it never
    /// respawns.
    /// </remarks>
    private void UpdateDeadCreature(Creature creature, uint gameplayDiff)
    {
        switch (creature.UpdateDeath(gameplayDiff))
        {
            case Creature.DeathTransition.CorpseRemoved:
                foreach (Player watcher in PlayersWhoSeeCore(creature.Guid))
                {
                    watcher.VisibleObjects.Remove(creature.Guid);
                    SendDestroy(watcher, creature.Guid);
                }

                Hide(creature);
                break;

            case Creature.DeathTransition.Respawned:
                Show(creature);

                // Everyone in range learns about it the ordinary way, which is what makes a respawn
                // indistinguishable from walking into view.
                foreach (WorldObject other in FindInRangeCore(creature.Position, VisibilityDistance, creature))
                {
                    if (other is Player observer)
                    {
                        MakeVisible(observer, creature);
                    }
                }

                break;

            default:
                break;
        }
    }

    /// <summary>Kills a creature and tells everyone watching.</summary>
    /// <remarks>
    /// The health field is already zero by the time this returns, and the update flush that follows
    /// is what the client actually plays the death animation from.
    /// </remarks>
    public void Kill(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);

        if (!creature.IsAlive)
        {
            return;
        }

        // Experience before the threat list is cleared: whoever the creature hated is who gets paid,
        // and Kill() forgets all of it.
        AwardExperience(creature);

        creature.Kill();

        // Everyone who was fighting it stops. Left alone they would keep swinging at a corpse,
        // which the swing loop would reject every tick for as long as the corpse lay there.
        foreach (Player player in Players)
        {
            if (ReferenceEquals(player.Victim, creature))
            {
                player.AttackStop();
                player.Connection?.SendAttackState(player.Guid, creature.Guid, attacking: false, victimIsDead: true);
            }
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

        if (worldObject is Creature creature)
        {
            _creatures.Add(creature);
        }
    }

    private void Unfile(WorldObject worldObject)
    {
        _objects.Remove(worldObject.Guid);
        CellFor(worldObject).Remove(worldObject);

        if (worldObject is Creature creature)
        {
            _creatures.Remove(creature);
        }
    }

    /// <summary>
    /// Takes a despawned creature out of the world without forgetting it.
    /// </summary>
    /// <remarks>
    /// Out of its cell so no range query finds it, and out of the guid index so nothing can target
    /// it — but still in <see cref="_creatures"/>, which is what keeps its respawn timer running.
    /// A full <see cref="Unfile"/> would take that away too, and the creature would never come back.
    /// </remarks>
    private void Hide(Creature creature)
    {
        _objects.Remove(creature.Guid);
        CellFor(creature).Remove(creature);
    }

    /// <summary>Puts a respawned creature back into the world, at wherever it now stands.</summary>
    private void Show(Creature creature)
    {
        creature.Cell = MapCoordinates.CellFor(creature.Position.X, creature.Position.Y);

        _objects[creature.Guid] = creature;
        CellAt(creature.Cell).Add(creature);
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
    /// <summary>
    /// Advances every attacking unit's swing timers and lands the swings that come due.
    /// </summary>
    /// <remarks>
    /// Does nothing on a session-only pass, for the same reason creatures do not move on one: a
    /// zero diff is a tick this map was not meant to advance on, and counting it as a very short one
    /// would make every weapon swing four times as fast.
    /// <para>
    /// Damage is applied here rather than inside the combat code, so that the one place a unit's
    /// health changes is the one place death can be noticed.
    /// </para>
    /// </remarks>
    private void UpdateCombat(uint gameplayDiff)
    {
        if (gameplayDiff == 0)
        {
            return;
        }

        // Materialised: a swing can kill something, and removing it mid-iteration would throw.
        foreach (Player player in Players)
        {
            player.UpdateAttackTimers(gameplayDiff);
            UpdateCasting(player, gameplayDiff);

            SwingResult swing = MeleeSwing.Advance(player, WeaponAttackType.BaseAttack, GameRandom.Urand);

            if (swing.Swung && player.Victim is { } victim)
            {
                ApplySwing(player, victim, swing.Damage);

                // Clears the client's suppression, so the next failure is reported again rather
                // than swallowed as a repeat of one from before the fight got going.
                player.Connection?.SendSwingError(SwingError.None);
            }
            else if (swing.Error != SwingError.None)
            {
                player.Connection?.SendSwingError(swing.Error);
            }
        }

        UpdateCreatureCombat(gameplayDiff);
    }

    /// <summary>
    /// Runs every living creature's AI: notice, chase, swing, or give up.
    /// </summary>
    /// <remarks>
    /// Aggro is scanned from the creature outwards rather than from the player, which is the wrong
    /// way round for a naive reading — but there are far more creatures than players, and starting
    /// from the creature means the aggro radius is the one already in hand.
    /// </remarks>
    private void UpdateCreatureCombat(uint gameplayDiff)
    {
        foreach (Creature creature in _creatures.ToList())
        {
            if (!creature.IsAlive)
            {
                continue;
            }

            creature.UpdateAttackTimers(gameplayDiff);

            if (creature.Victim is null && creature.Threat.IsEmpty)
            {
                TryAggro(creature);
            }

            AiDecision decision = CreatureAi.Update(creature);

            if (decision.Evaded)
            {
                SendCreatureMove(creature, creature.HomePosition);
                continue;
            }

            if (decision.Victim is null)
            {
                continue;
            }

            if (decision.Chase is { } destination)
            {
                Chase(creature, destination);
                continue;
            }

            // In reach and standing still — stop any chase that was in progress, or the client
            // keeps sliding it past the target it has already caught.
            SwingResult swing = MeleeSwing.Advance(creature, WeaponAttackType.BaseAttack, GameRandom.Urand);

            if (swing.Swung)
            {
                ApplySwing(creature, decision.Victim, swing.Damage);
            }
        }
    }

    /// <summary>Looks for a nearby player to pick a fight with.</summary>
    private void TryAggro(Creature creature)
    {
        if (creature.React != ReactState.Aggressive || Factions is null)
        {
            return;
        }

        foreach (Player candidate in Players)
        {
            bool hostile = CreatureAi.IsHostile(Factions, creature, candidate);

            if (CreatureAi.CanStartAttack(creature, candidate, hostile, () => CanSee(creature, candidate)))
            {
                // Zero threat, so the fight starts without pretending damage was dealt. The victim
                // selection that follows picks this up because being on the list is what counts.
                creature.Threat.AddThreat(candidate, 0f);
                return;
            }
        }
    }

    /// <summary>Walks a creature towards a point, telling everyone who can see it.</summary>
    /// <remarks>
    /// A fresh move every tick would be a packet per creature per tick. The move is only re-issued
    /// when the destination has meaningfully changed, which for a player standing still is never.
    /// </remarks>
    private void Chase(Creature creature, Position destination)
    {
        const float RestartThreshold = 2.0f;

        if (creature.IsMoving
            && creature.CurrentMove.Destination.GetExactDist2dSq(destination) < RestartThreshold * RestartThreshold)
        {
            return;
        }

        SendCreatureMove(creature, destination);
    }

    /// <summary>Starts a creature on a move and broadcasts it.</summary>
    private void SendCreatureMove(Creature creature, Position destination)
    {
        Movement.CreatureMove? move = creature.MoveTo(destination);

        if (move is null)
        {
            return;
        }

        foreach (Player watcher in PlayersWhoSeeCore(creature.Guid))
        {
            watcher.Connection?.QueueMonsterMove(creature.Guid, move.Value, creature.SplineId);
        }
    }

    /// <summary>Whether one unit can see another, for aggro purposes.</summary>
    private bool CanSee(WorldObject from, WorldObject to) =>
        IsInLineOfSight(
            from.Position with { Z = from.Position.Z + SightHeight },
            to.Position with { Z = to.Position.Z + SightHeight });

    /// <summary>How far above its feet a unit is considered to see from.</summary>
    /// <remarks>
    /// Without it every ray starts at ground level and is blocked by the ground itself, so nothing
    /// ever has line of sight to anything.
    /// </remarks>
    private const float SightHeight = 2.0f;

    /// <summary>
    /// Advances a caster's cast bar and cooldowns, completing a cast that has run out.
    /// </summary>
    /// <remarks>
    /// A cast that finishes here and one that was instant go through the same
    /// <see cref="CompleteCast"/>, so cooldowns and packets cannot drift apart between the two paths.
    /// </remarks>
    private void UpdateCasting(Player player, uint gameplayDiff)
    {
        if (player.Casting.Update(gameplayDiff) is not { } finished)
        {
            return;
        }

        // The target may have died or walked off during the cast. Completing against a dead target
        // would apply effects to a corpse; the cast still ends, it just lands on nothing.
        Unit? target = finished.Target is { IsAlive: true } alive && alive.MapId == player.MapId
            ? alive
            : null;

        CompleteCast(player, finished.Spell, target, finished.CastCount);
    }

    /// <summary>
    /// Finishes a cast: puts it on cooldown and tells everyone who can see it.
    /// </summary>
    /// <remarks>
    /// The one place a cast completes, whether it was instant or had a bar. Effects are not applied
    /// yet — that is the next task — so this is currently the visible half of a cast and nothing
    /// more.
    /// <para>
    /// <b>Broadcast, not sent to the caster alone.</b> <c>SMSG_SPELL_GO</c> is what plays the
    /// impact, so everyone in range needs it — sending it only to the caster leaves a fight where
    /// bystanders see damage numbers appear with nothing causing them.
    /// </para>
    /// </remarks>
    public void CompleteCast(Player caster, SpellEntry spell, Unit? target, byte castCount)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);

        if (spell.RecoveryTime > 0)
        {
            caster.Casting.StartCooldown(spell.Id, (int)spell.RecoveryTime);
        }

        ObjectGuid targetGuid = target?.Guid ?? ObjectGuid.Empty;

        // The caster first, then everyone watching. The caster is not in its own visible set, so a
        // loop over watchers alone would leave the person who cast it without the impact.
        caster.Connection?.SendSpellGo(caster.Guid, spell.Id, castCount, targetGuid, caster.Power);

        foreach (Player watcher in PlayersWhoSeeCore(caster.Guid))
        {
            watcher.Connection?.SendSpellGo(caster.Guid, spell.Id, castCount, targetGuid, caster.Power);
        }

        // Effects after the impact packet, not before. The client draws the spell landing from
        // SMSG_SPELL_GO, so a damage log that arrives first shows a number before its cause.
        ApplySpellEffects(caster, target ?? caster, spell);
    }

    /// <summary>
    /// Applies a spell's effects and tells everyone who can see it.
    /// </summary>
    /// <remarks>
    /// Damage and healing both route through here so that death is noticed in one place, the same
    /// way a melee swing does.
    /// </remarks>
    private void ApplySpellEffects(Player caster, Unit target, SpellEntry spell)
    {
        if (!target.IsAlive)
        {
            return;
        }

        SpellHit hit = SpellEffects.Apply(caster, target, spell, GameRandom.Urand);

        if (!hit.IsAnything)
        {
            return;
        }

        if (hit.Healing > 0)
        {
            // Healing cannot overfill. Clamping at the maximum rather than letting it run over is
            // what stops a heal on a full-health target showing as a gain.
            target.Health = Math.Min(target.Health + hit.Healing, target.MaxHealth);
        }

        if (hit.Damage == 0)
        {
            return;
        }

        uint healthBefore = target.Health;

        BroadcastSpellDamage(caster, target, spell, hit, healthBefore);

        target.Health = hit.Damage >= healthBefore ? 0 : healthBefore - hit.Damage;

        // Same as a swing: one point of threat per point of damage, and the kill is noticed where
        // the health changed.
        target.Threat.AddThreat(caster, hit.Damage);

        if (target.Health == 0 && target is Creature killed)
        {
            Kill(killed);
        }
    }

    /// <summary>Tells everyone who can see the fight about one spell's damage.</summary>
    /// <remarks>
    /// Both ends' watchers, unioned — the same reasoning as a melee swing. Someone watching only the
    /// victim still needs the number.
    /// </remarks>
    private void BroadcastSpellDamage(
        Unit caster, Unit target, SpellEntry spell, in SpellHit hit, uint targetHealthBeforeHit)
    {
        HashSet<ObjectGuid> notified = [];

        foreach (Player watcher in PlayersWhoSeeCore(caster.Guid).Concat(PlayersWhoSeeCore(target.Guid)))
        {
            if (notified.Add(watcher.Guid))
            {
                watcher.Connection?.QueueSpellDamage(
                    target.Guid, caster.Guid, spell.Id, hit, targetHealthBeforeHit);
            }
        }

        // The caster sees its own spell land even when nobody, itself included, has it in a
        // visible set — a player is not in its own.
        if (caster is Player self && notified.Add(self.Guid))
        {
            self.Connection?.QueueSpellDamage(
                target.Guid, caster.Guid, spell.Id, hit, targetHealthBeforeHit);
        }
    }

    /// <summary>
    /// Pays out experience for a kill.
    /// </summary>
    /// <remarks>
    /// Everyone on the creature's threat list is paid in full, not a share. Group splitting needs
    /// groups; until then paying each participant the whole amount is the honest simplification —
    /// it errs towards over-rewarding, which is visible, rather than silently paying nobody.
    /// <para>
    /// The content level is taken from the creature's template expansion rather than from the zone.
    /// Upstream uses the zone, so a classic creature standing in Outland pays classic rates here and
    /// Outland rates there — recorded in TODO.md rather than papered over.
    /// </para>
    /// </remarks>
    private void AwardExperience(Creature victim)
    {
        if (_xpTable is null || _playerStats is null)
        {
            return;
        }

        foreach (ThreatEntry entry in victim.Threat.Sorted)
        {
            if (entry.Target is not Player killer || !killer.IsAlive)
            {
                continue;
            }

            uint gain = ExperienceFormula.Gain(killer, victim, victim.Expansion, ExperienceRate);

            if (gain == 0)
            {
                continue;
            }

            IReadOnlyList<LevelUp> levels = Experience.Give(killer, gain, _xpTable, _playerStats);

            killer.Connection?.SendExperienceGain(victim.Guid, gain, levels);
        }
    }

    /// <summary>Applies one landed swing and tells everyone who can see it.</summary>
    /// <remarks>
    /// The victim's health is read <i>before</i> the damage is taken off, because that is what the
    /// packet's overkill figure is measured against — see <c>SMSG_ATTACKERSTATEUPDATE</c>.
    /// </remarks>
    private void ApplySwing(Unit attacker, Unit victim, Combat.MeleeDamageInfo damage)
    {
        uint healthBefore = victim.Health;

        BroadcastMeleeSwing(attacker, victim, damage, healthBefore);

        victim.Health = damage.Damage >= healthBefore ? 0 : healthBefore - damage.Damage;

        // One point of threat per point of damage — and the attacker goes on the list even when the
        // swing did nothing, which is what makes a creature fight back after being missed.
        victim.Threat.AddThreat(attacker, damage.Damage);

        // Death is noticed here, where health changed, rather than polled somewhere later. A hit
        // that takes the last point and a hit that takes ten times it are the same kill.
        if (victim.Health == 0 && victim is Creature killed)
        {
            Kill(killed);
        }
    }

    /// <summary>
    /// Tells everyone who can see the fight about one swing.
    /// </summary>
    /// <remarks>
    /// Both ends are broadcast to, not just the attacker's watchers: a player being hit by something
    /// they cannot see still needs the combat log entry, and someone watching only the victim still
    /// needs the damage number. The two sets are unioned rather than concatenated, or anyone who can
    /// see both — which is nearly everyone in the fight — gets the swing twice.
    /// </remarks>
    public void BroadcastMeleeSwing(
        Unit attacker,
        Unit victim,
        Combat.MeleeDamageInfo info,
        uint victimHealthBeforeHit)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);

        HashSet<ObjectGuid> notified = [];

        foreach (Player watcher in PlayersWhoSeeCore(attacker.Guid).Concat(PlayersWhoSeeCore(victim.Guid)))
        {
            if (notified.Add(watcher.Guid))
            {
                watcher.Connection?.QueueMeleeSwing(attacker.Guid, victim.Guid, info, victimHealthBeforeHit);
            }
        }
    }

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

        // A creature that is already walking when someone arrives needs the rest of its move, or it
        // stands frozen at the point the create block put it until it next decides to go somewhere.
        if (target is Creature { RemainingMove: { } remaining } moving)
        {
            viewer.Connection?.QueueMonsterMove(moving.Guid, remaining, moving.SplineId);
        }
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

