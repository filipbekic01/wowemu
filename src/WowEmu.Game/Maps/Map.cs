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
    /// Notes that an object this client can already see has changed, to go out at the next flush.
    /// </summary>
    /// <remarks>
    /// For <i>someone else's</i> object: the implementation filters the block down to what this
    /// observer is allowed to see. A player's own changes go out through its own flush, unfiltered,
    /// and must not come through here — the filter would strip its coinage and quest log from its
    /// own update.
    /// </remarks>
    void QueueValues(WorldObject other);

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

    /// <summary>Delivers one chat line. Immediate — a spoken line batched is a line delivered late.</summary>
    void SendChat(byte type, uint language, ObjectGuid sender, ObjectGuid receiver, string text);

    /// <summary>Tells this client a unit has changed between walking and running.</summary>
    void SendSplineMode(Opcode opcode, ObjectGuid unit);

    /// <summary>Tells this client its standing with one faction changed.</summary>
    void SendFactionStanding(uint reputationListId, int standing);


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

    /// <summary>
    /// Tells this client it has died, and how long before it can reclaim its corpse.
    /// </summary>
    /// <remarks>
    /// The delay is what the client counts down on the release button. Zero means "release now",
    /// which is not the same as not sending it — without the packet the button never appears.
    /// </remarks>
    void SendPlayerDied(int reclaimDelayMs);

    /// <summary>
    /// Tells this client where its spirit healer is, so the minimap shows it.
    /// </summary>
    /// <remarks>
    /// Cleared on resurrection by sending a map id of <c>-1</c>. That is the only way to remove the
    /// marker; there is no separate opcode.
    /// </remarks>
    void SendSpiritHealerLocation(uint mapId, Position at);

    /// <summary>Tells this client it is alive again.</summary>
    void SendResurrected();

    /// <summary>
    /// Tells this client an aura has landed on a unit, or that one already there has changed.
    /// </summary>
    /// <remarks>
    /// Immediate rather than queued, and knowingly so: an aura is applied by a cast whose
    /// <c>SMSG_SPELL_GO</c> has already gone out immediately, so queueing this behind it would let
    /// the impact reach the client before the buff icon.
    /// </remarks>
    /// <param name="flags">
    /// Decides the packet's own shape — which of the caster guid and the two durations follow. It is
    /// passed rather than derived because only the game layer knows whether the caster is the
    /// target.
    /// </param>
    void SendAuraApplied(
        ObjectGuid target,
        byte slot,
        uint spellId,
        byte flags,
        byte casterLevel,
        byte stackAmount,
        ObjectGuid caster,
        int maxDurationMs,
        int remainingMs);

    /// <summary>Tells this client an aura is gone, so the icon leaves the bar.</summary>
    void SendAuraRemoved(ObjectGuid target, byte slot);

    /// <summary>
    /// Tells this client everything currently on a unit it has just been shown.
    /// </summary>
    /// <remarks>
    /// 3.3.5a removed the aura update fields, so a create block carries no auras at all — a client
    /// learns them from packets alone. Without this a creature that spawned buffed shows nothing,
    /// and one already burning when you walk up to it looks untouched.
    /// </remarks>
    void SendAllAuras(Unit unit);

    /// <summary>
    /// Tells this client a unit now moves at a different speed.
    /// </summary>
    /// <param name="forced">
    /// Whether this client is the one steering the unit. It picks between two different opcodes with
    /// different payloads: the controller is <i>ordered</i> to a speed and acknowledges it, while
    /// everyone else is simply told, so that their copy interpolates at the right rate. Sending an
    /// onlooker the controller's packet leaves them waiting for an acknowledgement that will not come.
    /// </param>
    void SendSpeedChange(ObjectGuid unit, Combat.UnitMoveType type, float speed, bool forced);

    /// <summary>
    /// Relays one periodic aura tick, for the combat log and the floating number.
    /// </summary>
    /// <remarks>
    /// Queued rather than sent, for the same reason a swing is: a tick naming a caster the client
    /// has not been told about draws nothing.
    /// </remarks>
    void QueuePeriodicAuraLog(
        ObjectGuid target,
        ObjectGuid caster,
        uint spellId,
        uint auraType,
        uint amount,
        uint overflow,
        uint schoolMask);

    /// <summary>
    /// Tells this client what a corpse is holding, so it draws the loot window.
    /// </summary>
    /// <param name="slots">
    /// Only the slots still there. A taken slot is left out and keeps its number, so the count and
    /// the highest number disagree — that is correct.
    /// </param>
    void SendLootWindow(ObjectGuid target, byte lootType, uint gold, IReadOnlyList<LootSlot> slots);

    /// <summary>Tells this client the window could not be opened, and why.</summary>
    void SendLootError(ObjectGuid target, LootError reason);

    /// <summary>Tells this client one slot has been taken, by anyone.</summary>
    void SendLootRemoved(byte slot);

    /// <summary>Tells this client the money is gone from the window.</summary>
    void SendLootMoneyTaken(uint copper);

    /// <summary>Tells this client the window is closed.</summary>
    void SendLootReleased(ObjectGuid target);

    /// <summary>Tells this client an item has arrived in its bags.</summary>
    void SendItemPushed(in ItemPushResult push);

    /// <summary>
    /// Tells this client to draw, update or remove one of the bars under its portrait.
    /// </summary>
    /// <remarks>
    /// Immediate rather than queued. The bar is the player's warning that they are running out of
    /// air, and holding it until the flush is the one packet where a tick of delay is felt.
    /// </remarks>
    void SendMirrorTimer(MirrorTimerUpdate timer);

    /// <summary>
    /// Relays damage the world itself dealt — drowning, lava, a long fall.
    /// </summary>
    /// <remarks>
    /// Queued rather than sent, like the other combat-log lines: it names a victim, and one naming
    /// a player the client has not been told about draws nothing.
    /// </remarks>
    void QueueEnvironmentalDamage(ObjectGuid victim, EnvironmentalDamageType type, uint amount);

    /// <summary>Tells this client one quest objective has moved.</summary>
    /// <param name="wireEntry">
    /// The creature entry, or a gameobject's with the high bit set. Passed already encoded because
    /// only the quest layer knows which kind the objective was.
    /// </param>
    void SendQuestKillCredit(uint questId, uint wireEntry, uint current, uint required, ObjectGuid victim);

    /// <summary>Tells this client every objective on a quest is now met.</summary>
    void SendQuestComplete(uint questId);

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

    /// <summary>The body each dead player left behind, by owner guid.</summary>
    /// <remarks>
    /// Keyed by owner rather than held on the player, because a corpse outlives the session that
    /// made it — a player who logs out as a ghost leaves a body standing in the world.
    /// </remarks>
    private readonly Dictionary<ObjectGuid, Corpse> _corpses = [];

    /// <summary>Low guids for corpses, which have no spawn row to take one from.</summary>
    private uint _nextCorpseGuid;

    // Creatures that can move, kept separately so a tick does not walk every object on the
    // continent to find the few thousand that are going somewhere.
    private readonly List<Creature> _creatures = [];

    // Cells close enough to a player that what happens in them matters. Rebuilt once per gameplay
    // tick; see RefreshActiveCells.
    private readonly HashSet<CellCoord> _activeCells = [];

    // Reused by the per-tick creature passes. Those have to iterate a snapshot, because a tick can
    // kill or respawn a creature and that re-files cells underneath the enumerator — but allocating
    // a fresh list of every creature on the continent three times a tick is pure garbage.
    private readonly List<Creature> _creatureScratch = [];

    // Objects whose fields changed this tick. Reused for the same reason the creature scratch is:
    // the list is rebuilt every tick and is empty on most of them.
    private readonly List<WorldObject> _changedScratch = [];

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
    /// The navigation meshes, for routing creatures around the world rather than through it.
    /// </summary>
    /// <remarks>
    /// Optional, and its absence is a straight line rather than a refusal to move — see
    /// <see cref="SendCreatureMove"/>.
    /// </remarks>
    public NavMeshManager? NavMeshes { get; init; }

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

    /// <summary>Every spell, so an aura can resolve its own duration.</summary>
    public SpellStores? Spells { get => _spells; init => _spells = value; }

    /// <summary>What creatures drop, and the shared lists those point at.</summary>
    public LootStore? CreatureLoot { get => _creatureLoot; init => _creatureLoot = value; }

    /// <summary>What chests hold.</summary>
    public LootStore? GameObjectLoot { get; init; }

    /// <summary>
    /// What it takes to open a locked thing.
    /// </summary>
    /// <remarks>
    /// Named for the table rather than the concept, because <see cref="Game.Locks"/> is the rules
    /// and a property called <c>Locks</c> here shadows it inside this class.
    /// </remarks>
    public DbcStore<LockEntry>? LockTable { get; init; }

    /// <inheritdoc cref="CreatureLoot"/>
    public LootStore? LootReferences { get => _lootReferences; init => _lootReferences = value; }

    /// <summary>Every item, so a loot roll can resolve stack sizes and display ids.</summary>
    public ItemTemplateStore? Items { get => _items; init => _items = value; }

    /// <summary>Hands out guids for items the map creates — looted ones, and quest rewards.</summary>
    public Func<uint>? NextItemGuid { get => _itemGuids; init => _itemGuids = value; }

    /// <summary>Every quest, so a kill can be credited against the ones that want it.</summary>
    public QuestStore? Quests { get => _quests; init => _quests = value; }

    /// <summary>Which graveyards a zone offers. Without it a ghost stays where it fell.</summary>
    public GraveyardStore? Graveyards { get => _graveyards; init => _graveyards = value; }

    /// <summary>Graveyard coordinates, from <c>WorldSafeLocs.dbc</c>.</summary>
    public DbcStore<WorldSafeLocsEntry>? WorldSafeLocs { get => _worldSafeLocs; init => _worldSafeLocs = value; }

    /// <summary>The server's experience multiplier. 1.0 is Blizzard's own rate.</summary>
    public float ExperienceRate { get; init; } = 1.0f;

    private readonly PlayerXpStore? _xpTable;
    private readonly PlayerStatsStore? _playerStats;
    private readonly SpellStores? _spells;
    private readonly LootStore? _creatureLoot;
    private readonly LootStore? _lootReferences;
    private readonly ItemTemplateStore? _items;
    private readonly Func<uint>? _itemGuids;
    private readonly QuestStore? _quests;
    private readonly GraveyardStore? _graveyards;
    private readonly DbcStore<WorldSafeLocsEntry>? _worldSafeLocs;

    /// <summary>
    /// The surface under a point: the higher of terrain and any model standing there.
    /// </summary>
    /// <remarks>
    /// Null where neither knows — a hole in the terrain, an unloaded tile, or open sky. Callers must
    /// treat that as "no answer" rather than as a floor at zero.
    /// </remarks>
    public float? GetFloor(float x, float y, float z) => WorldHeight.GetFloor(Terrain, Collision, x, y, z);

    /// <summary>
    /// The liquid at a point, and how deep in it the asker is.
    /// </summary>
    /// <param name="collisionHeight">
    /// How tall the unit is — the depth at which it stops wading and is submerged. Upstream's
    /// default for a player is 2.0 yards.
    /// </param>
    /// <remarks>
    /// Terrain and models together, the same way <see cref="GetFloor"/> combines them — but by a
    /// different rule. See <see cref="WorldLiquid"/>: a model's water wins outright indoors, because
    /// a building standing in a lake must not have the lake running through its ground floor.
    /// </remarks>
    public LiquidData GetLiquid(float x, float y, float z, float collisionHeight) =>
        WorldLiquid.Get(Terrain, Collision, x, y, z, collisionHeight, LiquidTypes, Areas);

    /// <summary>
    /// <c>LiquidType.dbc</c>, which classifies the liquid inside a model.
    /// </summary>
    /// <remarks>
    /// Optional, and the failure without it is quiet rather than loud: indoor water still reports
    /// its depth and its entry id, but with no type — so nothing can tell Undercity's slime from
    /// Stormwind's canal, and any rule keyed on the type simply never fires.
    /// </remarks>
    public DbcStore<LiquidTypeEntry>? LiquidTypes { get; init; }

    /// <summary>
    /// <c>AreaTable.dbc</c>, for the zone liquid override and for area lookups.
    /// </summary>
    /// <remarks>
    /// Optional, and its absence is quiet: a zone that substitutes its own liquid is simply not
    /// noticed, and the geometry's own kind stands.
    /// </remarks>
    public DbcStore<AreaTableEntry>? Areas { get; init; }

    /// <summary>The skill tables, so a level-up raises what a character can practise to.</summary>
    public SkillLines? Skills { get; init; }

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

        // Once per tick, before anything reads it. Player positions do not change again until the
        // next session pass, so every creature pass below sees the same answer.
        if (gameplayDiff > 0)
        {
            RefreshActiveCells();
        }

        UpdateCreatures(gameplayDiff);
        UpdateCombat(gameplayDiff);
        UpdateEnvironment(gameplayDiff);
        UpdateItemDurations(gameplayDiff);

        // After everything that could change a field, before anything is flushed.
        BroadcastFieldChanges();

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
    /// <summary>
    /// Rebuilds the set of cells near enough to a player to be worth updating.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is upstream's grid-activity model, which we did not have: AzerothCore updates the cells
    /// around its players, not every object it has ever loaded. Without it the cost of a tick is the
    /// number of creatures the server has loaded since it started — so walking across a continent
    /// makes every later tick more expensive, permanently, and the map that is cheapest to update is
    /// the one nobody has visited.
    /// </para>
    /// <para>
    /// Whole cells, so the covered area is already larger than the visibility radius asked for: a
    /// cell is 66.6 yards and the radius is rounded outwards to cell boundaries. That slack is
    /// wanted — a creature that is one yard outside a player's view still has to be running when the
    /// player takes one step towards it.
    /// </para>
    /// </remarks>
    private void RefreshActiveCells()
    {
        _activeCells.Clear();

        foreach (Player player in _players.Values)
        {
            foreach (CellCoord cell in
                MapCoordinates.CellsInRange(player.Position.X, player.Position.Y, VisibilityDistance))
            {
                _activeCells.Add(cell);
            }
        }
    }

    /// <summary>
    /// Whether a creature is close enough to a player to need updating this tick.
    /// </summary>
    /// <remarks>
    /// A creature already in combat is always active, wherever it is standing. Freezing one that is
    /// mid-fight would leave it locked onto a victim who has walked away, and it would never reach
    /// the evade that sends it home — so it would be waiting, still angry, when the player came
    /// back. Combat is rare enough that keeping it exempt costs nothing.
    /// </remarks>
    private bool IsActive(Creature creature) =>
        _activeCells.Contains(creature.Cell) || creature.Victim is not null || !creature.Threat.IsEmpty;

    /// <summary>How many creatures the last gameplay tick actually updated.</summary>
    /// <remarks>
    /// Reported next to <see cref="CreatureCount"/> so the difference between "loaded" and "ticking"
    /// is visible rather than inferred.
    /// </remarks>
    public int ActiveCreatureCount { get; private set; }

    /// <summary>Copies the creature list into the reusable scratch buffer.</summary>
    private List<Creature> SnapshotCreatures()
    {
        _creatureScratch.Clear();
        _creatureScratch.AddRange(_creatures);
        return _creatureScratch;
    }

    private void UpdateCreatures(uint gameplayDiff)
    {
        if (gameplayDiff == 0 || _creatures.Count == 0)
        {
            return;
        }

        int active = 0;

        // Materialised: a respawn or a corpse removal files and unfiles cells, and a despawned
        // creature stays in this list so it has something to come back on.
        foreach (Creature creature in SnapshotCreatures())
        {
            if (!creature.IsAlive)
            {
                // Deliberately not gated on activity. This is a timer comparison, and a corpse that
                // only respawns once somebody walks up to it respawns in front of them.
                UpdateDeadCreature(creature, gameplayDiff);
                continue;
            }

            if (!IsActive(creature))
            {
                continue;
            }

            active++;

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

        ActiveCreatureCount = active;
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

    /// <summary>
    /// How tall a player is taken to be. <c>DEFAULT_COLLISION_HEIGHT</c>.
    /// </summary>
    /// <remarks>
    /// Upstream derives it per race, gender and model scale; this is the most common value in the
    /// DBC and stands in until those are loaded. It is the depth at which wading becomes swimming,
    /// so the error it carries is a few inches of waterline on a tauren, not a wrong answer.
    /// </remarks>
    public const float DefaultCollisionHeight = 2.03128f;

    /// <summary>
    /// Drowns, exhausts and burns the players standing in things that do that.
    /// </summary>
    /// <remarks>
    /// Runs after combat so that a player already at one hit point is killed by the water in the
    /// same tick rather than the next, and before the update broadcast so the health change goes out
    /// with everything else.
    /// <para>
    /// Nothing here is gated on activity: a player is always near a player. The liquid lookup is the
    /// only real cost and it is a loaded tile plus arithmetic.
    /// </para>
    /// </remarks>
    private void UpdateEnvironment(uint gameplayDiff)
    {
        if (gameplayDiff == 0)
        {
            return;
        }

        // Materialised: environmental damage can kill, and a death re-files cells.
        foreach (Player player in Players)
        {
            LiquidData liquid = GetLiquid(
                player.Position.X, player.Position.Y, player.Position.Z, DefaultCollisionHeight);

            player.Environment.Refresh(liquid, player.IsAlive);

            EnvironmentUpdate update = player.Environment.Update(
                gameplayDiff, player.MaxHealth, player.Level, player.IsAlive, GameRandom.Urand,
                player.Auras);

            foreach (MirrorTimerUpdate timer in update.Timers)
            {
                player.Connection?.SendMirrorTimer(timer);
            }

            foreach (EnvironmentalHit hit in update.Hits)
            {
                ApplyEnvironmentalDamage(player, hit.Type, hit.Amount);
            }
        }
    }

    /// <summary>
    /// Ticks down every timed item every player is carrying.
    /// </summary>
    /// <remarks>
    /// <b>Whole seconds only, with the remainder carried.</b> The duration field is in seconds and
    /// the tick is in milliseconds; dividing each tick and discarding the remainder means a 100 ms
    /// tick contributes nothing at all, and durations never move.
    /// </remarks>
    private void UpdateItemDurations(uint gameplayDiff)
    {
        if (gameplayDiff == 0)
        {
            return;
        }

        _durationRemainderMs += gameplayDiff;

        uint seconds = _durationRemainderMs / 1000;

        if (seconds == 0)
        {
            return;
        }

        _durationRemainderMs -= seconds * 1000;

        // No packet for this. Destroying the item clears its slot guid, and that field change is
        // what the client acts on — upstream sends nothing else either.
        foreach (Player player in Players)
        {
            ItemDuration.Tick(player, seconds);
        }
    }

    /// <summary>Milliseconds not yet worth a whole second of item duration.</summary>
    private uint _durationRemainderMs;

    /// <summary>
    /// Applies one helping of world damage and tells everyone who can see it.
    /// </summary>
    /// <remarks>
    /// The log goes out before the health changes, for the same reason a spell's does: the client
    /// draws the number from the log, and one that arrives after the death has been broadcast shows
    /// a corpse taking damage.
    /// </remarks>
    public void ApplyEnvironmentalDamage(Player player, EnvironmentalDamageType type, uint amount)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!player.IsAlive || amount == 0)
        {
            return;
        }

        uint healthBefore = player.Health;
        uint dealt = Math.Min(amount, healthBefore);

        foreach (Player watcher in WatchersOf(player))
        {
            watcher.Connection?.QueueEnvironmentalDamage(player.Guid, type, dealt);
        }

        player.Health = healthBefore - dealt;

        NoticeDeath(player);
    }

    /// <summary>
    /// Tells every observer about the objects near them that changed this tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes another player's health bar drop, their level change, and their new sword
    /// appear. Until it existed a values block was only ever sent to the object's own client, so a
    /// creature's health was never transmitted at all and a second player was a mannequin frozen at
    /// whatever its create block said.
    /// </para>
    /// <para>
    /// Ordered deliberately, and the ordering is the whole design: <b>after</b> everything that can
    /// dirty a field, and <b>before</b> the flush, because the flush is where a player builds its own
    /// unfiltered block and clears its own mask. Running this afterwards would find every mask
    /// already empty and send nothing, which is exactly the bug it replaces.
    /// </para>
    /// <para>
    /// Observer-major rather than object-major. The other way round costs a pass over every player on
    /// the map per changed object, and in a fight nearly every creature present is changing.
    /// </para>
    /// <para>
    /// Players and creatures only. Gameobjects are not scanned because nothing writes to their fields
    /// after the spawn builds them — when something does (a chest opening, a door swinging), it is
    /// this list they have to join, and there is no other place a change would leak out from.
    /// </para>
    /// </remarks>
    private void BroadcastFieldChanges()
    {
        _changedScratch.Clear();

        foreach (Player player in _players.Values)
        {
            if (player.Fields.IsDirty)
            {
                _changedScratch.Add(player);
            }
        }

        foreach (Creature creature in _creatures)
        {
            if (creature.Fields.IsDirty)
            {
                _changedScratch.Add(creature);
            }
        }

        if (_changedScratch.Count == 0)
        {
            return;
        }

        foreach (Player observer in _players.Values)
        {
            if (observer.Connection is not { } connection)
            {
                continue;
            }

            foreach (WorldObject changed in _changedScratch)
            {
                // A client that has not been sent a create block for the object drops the values
                // block silently, so the visible set is the gate — not proximity.
                if (observer.VisibleObjects.Contains(changed.Guid))
                {
                    connection.QueueValues(changed);
                }
            }
        }

        foreach (WorldObject changed in _changedScratch)
        {
            // A player's own mask is cleared by its own flush, a few lines later, once it has built
            // the unfiltered block only it is entitled to. Clearing it here would send every observer
            // the public half and the player itself nothing.
            //
            // Unless it has no connection to flush through — a test double, or a session that has
            // gone away — in which case nothing will ever clear it and the same stale change would be
            // rebroadcast on every tick from now on.
            if (changed is not Player { Connection: not null })
            {
                changed.Fields.ClearDirty();
            }
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
        // and Kill() forgets all of it. The loot's owner comes from the same list, so it is decided
        // here too.
        AwardExperience(creature);
        CreditQuestKills(creature);
        RollLoot(creature);

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

    /// <summary>
    /// Puts a corpse into the world and shows it to everyone nearby.
    /// </summary>
    /// <remarks>
    /// A separate overload rather than one taking <c>WorldObject</c>, because adding a player also
    /// registers it in the player list and teaches it what it can see — neither of which a corpse
    /// wants. What they share is the filing and the visibility sweep.
    /// </remarks>
    public void Add(Corpse corpse)
    {
        ArgumentNullException.ThrowIfNull(corpse);

        File(corpse);

        foreach (WorldObject other in FindInRangeCore(corpse.Position, VisibilityDistance, corpse))
        {
            if (other is Player observer)
            {
                MakeVisible(observer, corpse);
            }
        }
    }

    /// <summary>Takes a corpse out of the world and tells everyone who could see it.</summary>
    public void Remove(Corpse corpse)
    {
        ArgumentNullException.ThrowIfNull(corpse);

        Unfile(corpse);

        foreach (Player other in PlayersWhoSeeCore(corpse.Guid))
        {
            other.VisibleObjects.Remove(corpse.Guid);
            SendDestroy(other, corpse.Guid);
        }
    }

    /// <summary>
    /// Puts a gameobject into the world.
    /// </summary>
    /// <remarks>
    /// Most arrive through <c>IGridObjectLoader</c> when a grid loads. This is the same filing for
    /// one that does not — a scripted spawn, or a test placing exactly one thing.
    /// </remarks>
    public void Add(GameObject spawned)
    {
        ArgumentNullException.ThrowIfNull(spawned);

        File(spawned);

        foreach (WorldObject other in FindInRangeCore(spawned.Position, VisibilityDistance, spawned))
        {
            if (other is Player observer)
            {
                MakeVisible(observer, spawned);
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

            // Handed over here rather than at construction: the creature is built by a grid loader
            // that knows nothing about which map it is destined for, and the height and the routes
            // it needs are *this* map's.
            creature.FloorAt = GetFloor;
            creature.RouteTo = RouteBetween;
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
        UpdateAuras(gameplayDiff);
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
        foreach (Creature creature in SnapshotCreatures())
        {
            if (!creature.IsAlive)
            {
                continue;
            }

            // The single most expensive thing a tick did: TryAggro below scans the creature's
            // surroundings, and running it for every creature on the continent meant a range query
            // per loaded creature per tick, almost all of them looking for players hundreds of
            // yards away.
            if (!IsActive(creature))
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
                // The walk home is not issued here. Evade pushed a HomeMovementGenerator, and the
                // creature's own update takes the destination from it on the next tick — which is
                // also what broadcasts the move and what pops the generator on arrival. Sending one
                // here as well would put two moves on the wire for one journey.
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

        // Mid-route counts as heading there: a path's next corner is rarely the destination, so
        // comparing against it would re-path on every tick and never finish a leg.
        Position heading = creature.IsFollowingPath ? creature.ChaseTarget : creature.CurrentMove.Destination;

        if (creature.IsMoving
            && heading.GetExactDist2dSq(destination) < RestartThreshold * RestartThreshold)
        {
            return;
        }

        SendCreatureMove(creature, destination);
    }

    /// <summary>
    /// Tells everyone watching that a creature has changed between walking and running.
    /// </summary>
    /// <remarks>
    /// The client picks the animation from this, not from the speed in the move packet — so without
    /// it a wandering creature sprints along its patrol on a run cycle at walking pace, which reads
    /// as the animation being broken rather than the flag being absent.
    /// <para>
    /// Sent only when the mode actually changes. A packet per leg would be one per waypoint for
    /// every creature on the continent, for nothing.
    /// </para>
    /// </remarks>
    private void BroadcastWalkMode(Creature creature)
    {
        Opcode opcode = creature.IsWalking
            ? Opcode.SMSG_SPLINE_MOVE_SET_WALK_MODE
            : Opcode.SMSG_SPLINE_MOVE_SET_RUN_MODE;

        foreach (Player watcher in PlayersWhoSeeCore(creature.Guid))
        {
            watcher.Connection?.SendSplineMode(opcode, creature.Guid);
        }
    }

    /// <summary>
    /// Starts a creature on a move and broadcasts it.
    /// </summary>
    /// <remarks>
    /// Routed around the world where there is a navmesh to route around it with, and straight there
    /// where there is not. <b>The straight line is the fallback and must stay one</b>: 98 maps of
    /// the client's several hundred have a navmesh, a tile can be missing from any of them, and a
    /// creature that refused to move without a route would simply stand still in all those places.
    /// </remarks>
    private void SendCreatureMove(Creature creature, Position destination)
    {
        creature.ChaseTarget = destination;

        // Every move the map issues is a run — a chase and a walk home are both urgent. The
        // generator-driven path sets its own mode from the waypoint, and this is the other entry
        // point; missing it means a creature that was ambling keeps the walk animation while it
        // sprints after someone.
        creature.WalkModeChanged |= creature.SetWalk(false);

        Movement.CreatureMove? move = FindRoute(creature, destination) is { HasPath: true } route
            ? creature.MoveAlong(route.Points)
            : creature.MoveTo(destination);

        if (move is null)
        {
            return;
        }

        // Before the move, so the client knows which animation the spline it is about to receive
        // should be played with.
        if (creature.WalkModeChanged)
        {
            creature.WalkModeChanged = false;
            BroadcastWalkMode(creature);
        }

        foreach (Player watcher in PlayersWhoSeeCore(creature.Guid))
        {
            watcher.Connection?.QueueMonsterMove(creature.Guid, move.Value, creature.SplineId);
        }
    }

    /// <summary>
    /// A route from where a creature is to where it is going, or none.
    /// </summary>
    /// <remarks>
    /// None is the ordinary answer in most of the world, and the caller walks a straight line on it.
    /// A path is only worth asking for over any distance at all — a creature adjusting its footing
    /// next to its victim would otherwise pay a mesh query per tick to be told to go where it
    /// already is.
    /// </remarks>
    private NavPath? FindRoute(Creature creature, Position destination) =>
        RouteBetween(creature.Position, destination) is { } points
            ? new NavPath(PathResult.Complete, points)
            : null;

    /// <summary>
    /// A route between two points on this map, or null when there is none worth having.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary answer over most of the world and the caller walks a straight line on
    /// it. Short journeys are refused outright: a creature adjusting its footing next to its victim
    /// would otherwise pay a mesh query per tick to be told to go where it already is, and 68% of
    /// wandering spawns have a radius under the threshold.
    /// </remarks>
    private IReadOnlyList<Position>? RouteBetween(Position from, Position to)
    {
        const float ShortestWorthPathing = 5.0f;

        if (NavMeshes is not { } navmeshes
            || from.GetExactDist2dSq(to) < ShortestWorthPathing * ShortestWorthPathing)
        {
            return null;
        }

        navmeshes.EnsureLoaded(MapId, from.X, from.Y);
        navmeshes.EnsureLoaded(MapId, to.X, to.Y);

        if (navmeshes.For(MapId) is not { } generator)
        {
            return null;
        }

        NavPath path = generator.Find(from, to);

        return path.HasPath ? path.Points : null;
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

        ApplyAuras(caster, target, spell);

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

        NoticeDeath(target);
    }

    /// <summary>
    /// Puts a spell's auras on a target and tells the client.
    /// </summary>
    /// <remarks>
    /// Separate from the damage: a spell can do both, and Fireball does — direct damage from effect
    /// 0 and a burn from effect 1. Nothing here depends on whether the damage landed.
    /// </remarks>
    private void ApplyAuras(Unit caster, Unit target, SpellEntry spell)
    {
        if (_spells is null || !target.IsAlive)
        {
            return;
        }

        int duration = _spells.DurationMs(spell);

        Aura? aura = target.Auras.Apply(
            spell,
            caster.Guid,
            caster.Level,
            duration,
            effect => SpellEffects.CalculateValue(
                spell, effect, caster.Level, (min, max) => (int)GameRandom.Urand((uint)min, (uint)max)));

        if (aura is not null)
        {
            BroadcastAuraApplied(target, aura);

            // After the icon, not before: the speed packet is what actually slows the target, and a
            // client that sees itself slow down before it is told why has nothing to blame.
            RefreshSpeeds(target);
            RefreshStats(target);
        }
    }

    /// <summary>
    /// Advances every unit's auras and applies what came due.
    /// </summary>
    /// <remarks>
    /// Creatures as well as players: a burn on a wolf has to tick, and it is the only thing that
    /// kills something after its attacker has walked away.
    /// </remarks>
    private void UpdateAuras(uint gameplayDiff)
    {
        // Materialised: a tick can kill, and a death takes the victim out of the cell it was filed
        // in — enumerating the live collections while that happens throws.
        //
        // Only units that actually carry an aura are copied. The old form built a list of every
        // player and creature on the map each tick and then discarded almost all of it on the very
        // next line, which on a loaded continent is tens of thousands of references a second to
        // find the handful of things that are burning. Not gated on activity: an aura is what kills
        // something after its attacker has walked away, and a burn that stops ticking out of sight
        // is a mob that survives by being ignored.
        List<Unit>? afflicted = null;

        foreach (Player player in _players.Values)
        {
            if (player.Auras.Count > 0)
            {
                (afflicted ??= []).Add(player);
            }
        }

        foreach (Creature creature in _creatures)
        {
            if (creature.Auras.Count > 0)
            {
                (afflicted ??= []).Add(creature);
            }
        }

        if (afflicted is null)
        {
            return;
        }

        foreach (Unit unit in afflicted)
        {
            (IReadOnlyList<AuraTick> ticks, IReadOnlyList<Aura> expired) = unit.Auras.Update(gameplayDiff);

            foreach (AuraTick tick in ticks)
            {
                ApplyAuraTick(unit, tick);
            }

            if (expired.Count > 0)
            {
                foreach (Aura aura in expired)
                {
                    BroadcastAuraRemoved(unit, aura);
                }

                RefreshSpeeds(unit);
                RefreshStats(unit);
            }
        }
    }

    /// <summary>
    /// Applies what one aura effect owed this update, one tick at a time.
    /// </summary>
    /// <remarks>
    /// A tick is one combat-log line, so an update that owes three sends three rather than one for
    /// the total — an out-of-phase map owes several at once routinely, and summing them would show
    /// a burn hitting for triple every fourth tick.
    /// <para>
    /// The caster is looked up rather than held, because it may have died, logged out or walked off
    /// the map since the aura landed. A burn outlives the person who cast it.
    /// </para>
    /// </remarks>
    private void ApplyAuraTick(Unit target, in AuraTick tick)
    {
        if (!tick.Effect.IsHandled)
        {
            return;
        }

        uint amount = (uint)Math.Max(tick.Effect.Amount, 0);

        if (amount == 0)
        {
            return;
        }

        Unit? caster = Find(tick.Aura.CasterGuid) as Unit;

        for (int i = 0; i < tick.Ticks; i++)
        {
            if (!target.IsAlive)
            {
                return;
            }

            if (tick.Effect.Type == AuraType.PeriodicHeal)
            {
                uint before = target.Health;
                target.Health = Math.Min(target.Health + amount, target.MaxHealth);

                BroadcastAuraTick(target, tick, target.Health - before, amount - (target.Health - before));
                continue;
            }

            uint healthBefore = target.Health;
            uint overkill = amount > healthBefore ? amount - healthBefore : 0;

            BroadcastAuraTick(target, tick, amount, overkill);

            target.Health = amount >= healthBefore ? 0 : healthBefore - amount;

            // A tick generates threat for whoever cast it, so a damage-over-time keeps its caster on
            // the list rather than letting the target wander off to someone else.
            if (caster is not null)
            {
                target.Threat.AddThreat(caster, amount);
            }

            NoticeDeath(target);
        }
    }

    /// <summary>Takes every aura off a unit and tells the clients each one is gone.</summary>
    private void ClearAuras(Unit unit)
    {
        if (unit.Auras.Count == 0)
        {
            return;
        }

        foreach (Aura aura in unit.Auras.Auras.ToList())
        {
            BroadcastAuraRemoved(unit, aura);
        }

        unit.Auras.Clear();

        // Death takes every slow with it. Without this a creature that died slowed would respawn
        // slowed, because the speeds are stored and nothing else recomputes them.
        RefreshSpeeds(unit);
        RefreshStats(unit);
    }

    private void BroadcastAuraApplied(Unit target, Aura aura)
    {
        foreach (Player watcher in WatchersOf(target))
        {
            watcher.Connection?.SendAuraApplied(
                target.Guid, aura.Slot, aura.Spell.Id, (byte)aura.FlagsFor(target.Guid),
                aura.CasterLevel, aura.StackAmount, aura.CasterGuid,
                aura.MaxDurationMs, aura.RemainingMs);
        }
    }

    private void BroadcastAuraRemoved(Unit target, Aura aura)
    {
        foreach (Player watcher in WatchersOf(target))
        {
            watcher.Connection?.SendAuraRemoved(target.Guid, aura.Slot);
        }
    }

    /// <summary>
    /// Recomputes a unit's speeds after its auras changed, and tells whoever needs to know.
    /// </summary>
    /// <remarks>
    /// Called from every place the aura set moves — applied, expired, cleared on death. Speeds do
    /// not change on their own, so there is no per-tick equivalent and nothing to keep in step.
    /// <para>
    /// A player is told about its own speed on the controller opcode and everyone else on the
    /// observer one; a creature has no controller, so its watchers get the observer opcode and that
    /// is all.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Recomputes a player's attributes and everything derived from them.
    /// </summary>
    /// <remarks>
    /// Players only: a creature's attributes come from its class-level stats and nothing reads them
    /// afterwards, so a stat buff on one would change a number no calculation consults.
    /// <para>
    /// The whole recompute rather than a delta, for the same reason equipping does it that way — a
    /// delta is one missed call away from a character who gains strength every time a buff expires.
    /// The changed fields reach observers through the ordinary update broadcast.
    /// </para>
    /// </remarks>
    private static void RefreshStats(Unit unit)
    {
        if (unit is Player player)
        {
            PlayerCombatStats.Apply(player);
        }
    }

    private void RefreshSpeeds(Unit unit)
    {
        IReadOnlyList<UnitMoveType> changed = unit.RefreshSpeeds();

        if (changed.Count == 0)
        {
            return;
        }

        foreach (UnitMoveType type in changed)
        {
            float speed = UnitSpeed.Read(unit.Speeds, type);

            if (unit is Player self)
            {
                self.Connection?.SendSpeedChange(self.Guid, type, speed, forced: true);
            }

            foreach (Player watcher in PlayersWhoSeeCore(unit.Guid))
            {
                watcher.Connection?.SendSpeedChange(unit.Guid, type, speed, forced: false);
            }
        }
    }

    private void BroadcastAuraTick(Unit target, in AuraTick tick, uint amount, uint overflow)
    {
        foreach (Player watcher in WatchersOf(target))
        {
            watcher.Connection?.QueuePeriodicAuraLog(
                target.Guid, tick.Aura.CasterGuid, tick.Aura.Spell.Id,
                tick.Effect.Type, amount, overflow, tick.Aura.Spell.SchoolMask);
        }
    }

    /// <summary>
    /// Everyone who should be told about something happening to a unit, including the unit itself.
    /// </summary>
    /// <remarks>
    /// A player is not in its own visible set, so a loop over watchers alone leaves the person the
    /// aura is on as the only one who cannot see it.
    /// </remarks>
    private IEnumerable<Player> WatchersOf(Unit unit)
    {
        if (unit is Player self)
        {
            yield return self;
        }

        foreach (Player watcher in PlayersWhoSeeCore(unit.Guid))
        {
            yield return watcher;
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

    /// <summary>How close a player has to be to interact with something. <c>INTERACTION_DISTANCE</c>.</summary>
    public const float InteractionDistance = 5.5f;

    /// <summary>
    /// Opens a corpse's loot window for a player.
    /// </summary>
    /// <remarks>
    /// Port of <c>Player::SendLoot</c>, less the group permissions. Every refusal answers on the
    /// same opcode rather than going quiet: the client has already drawn the window frame and
    /// leaves it up, empty and unclosable, if nothing comes back.
    /// </remarks>
    public void OpenLoot(Player player, ObjectGuid target)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (Find(target) is not Creature creature)
        {
            player.Connection?.SendLootError(target, LootError.PlayerNotFound);
            return;
        }

        // Alive means it is being pickpocketed, which needs a rogue and a stealth check.
        if (creature.IsAlive || creature.Loot is null)
        {
            player.Connection?.SendLootError(target, LootError.NoLoot);
            return;
        }

        if (!creature.Loot.Owner.IsEmpty && creature.Loot.Owner != player.Guid)
        {
            player.Connection?.SendLootError(target, LootError.DidNotKill);
            return;
        }

        if (player.Position.GetExactDist2dSq(creature.Position)
            > InteractionDistance * InteractionDistance)
        {
            player.Connection?.SendLootError(target, LootError.TooFar);
            return;
        }

        player.LootTarget = target;

        player.Connection?.SendLootWindow(
            target, LootType.Corpse, creature.Loot.Gold, VisibleSlots(creature.Loot, player));
    }

    /// <summary>
    /// Opens a chest, if the player can get into it.
    /// </summary>
    /// <remarks>
    /// The loot is rolled the first time somebody opens it, not at spawn: a roll per chest on the
    /// continent up front is 38,594 rolls nobody has looked at. Once rolled it stays, so two players
    /// opening the same chest see the same contents rather than each getting their own.
    /// </remarks>
    /// <returns>False when it is not a chest, or is out of reach, or is locked against this player.</returns>
    public bool OpenChest(Player player, GameObject chest)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(chest);

        if (chest.Template.Type != GameObjectTemplate.TypeChest)
        {
            return false;
        }

        if (player.Position.GetExactDist2dSq(chest.Position)
            > InteractionDistance * InteractionDistance)
        {
            player.Connection?.SendLootError(chest.Guid, LootError.TooFar);
            return false;
        }

        if (Game.Locks.CanOpen(player, chest.Template.LockId, LockTable) != LockResult.Ok)
        {
            player.Connection?.SendLootError(chest.Guid, LootError.Locked);
            return false;
        }

        chest.Loot ??= RollChestLoot(chest);

        if (chest.Loot is not { } loot)
        {
            player.Connection?.SendLootError(chest.Guid, LootError.NoLoot);
            return false;
        }

        player.LootTarget = chest.Guid;

        player.Connection?.SendLootWindow(
            chest.Guid, LootType.Corpse, loot.Gold, VisibleSlots(loot, player));

        return true;
    }

    /// <summary>Rolls a chest's table, or null when there is nothing to roll.</summary>
    private Loot? RollChestLoot(GameObject chest)
    {
        uint lootId = chest.Template.LootId;

        if (lootId == 0 || GameObjectLoot is null || _lootReferences is null || _items is null
            || !GameObjectLoot.TryGet(lootId, out LootTemplate? template) || template is null)
        {
            return null;
        }

        // No owner: a chest belongs to whoever reaches it, unlike a corpse which belongs to whoever
        // killed it.
        Loot loot = new();

        LootRoll.Fill(
            loot,
            template,
            _lootReferences,
            _items,
            () => GameRandom.Urand(0, 9999) / 100f,
            count => (int)GameRandom.Urand(0, (uint)count - 1),
            GameRandom.Urand);

        return loot.IsEmpty ? null : loot;
    }

    /// <summary>
    /// Takes one slot out of the open loot window and puts it in the player's bags.
    /// </summary>
    /// <remarks>
    /// <b>The item is only marked taken once it is actually stored.</b> Marking first and storing
    /// second loses the item when the bags are full — the slot is gone from the window and nothing
    /// is holding it.
    /// </remarks>
    public void TakeLoot(Player player, byte slot)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (_items is null || _itemGuids is null || FindOpenLoot(player) is not (WorldObject holder, Loot loot))
        {
            return;
        }

        if (loot.At(slot) is not { IsLooted: false } entry)
        {
            player.Connection?.SendLootError(player.LootTarget, LootError.NoLoot);
            return;
        }

        // The slot number comes from the client, so a quest drop has to be refused here as well as
        // hidden from the window. Filtering only the window is exactly the kind of gap that reads as
        // working — nobody can click it, and anyone sending the packet by hand takes it anyway.
        if (entry.NeedsQuest && !NeedsForQuest(player, entry.ItemId))
        {
            player.Connection?.SendLootError(player.LootTarget, LootError.NoLoot);
            return;
        }

        if (!_items.TryGet(entry.ItemId, out ItemTemplate? template) || template is null)
        {
            return;
        }

        InventoryResult stored = player.Inventory.Store(
            template, entry.Count, _itemGuids, out IReadOnlyList<Item> affected);

        if (stored != InventoryResult.Ok)
        {
            player.Connection?.SendLootError(player.LootTarget, LootError.NoLoot);
            return;
        }

        loot.Take(slot);
        player.Connection?.SendLootRemoved(slot);

        // Recounted rather than incremented: an item can arrive by looting, trading, buying or
        // mail, and an increment at each is four chances to miss one.
        if (_quests is not null)
        {
            foreach (uint finished in player.Quests.RecountAllItems(_quests))
            {
                player.Connection?.SendQuestComplete(finished);
            }
        }

        foreach (Item item in affected)
        {
            ItemPosition? where = player.Inventory.PositionOf(item);

            player.Connection?.SendItemPushed(new ItemPushResult(
                Player: player.Guid,
                FromNpc: false,
                Created: false,
                ShowInChat: true,
                Bag: where?.Bag ?? InventorySlots.Backpack,

                // A stack that merged into an existing one reports -1 rather than a slot: the
                // client flashes the named square, and flashing the wrong one is worse than none.
                Slot: item.Count == entry.Count ? where?.Slot ?? 0 : ItemPushResultPacket.MergedIntoStack,
                Entry: entry.ItemId,
                Count: entry.Count,
                TotalOfEntry: player.Inventory.CountOf(entry.ItemId)));
        }

        ClearIfEmpty(holder, loot);
    }

    /// <summary>Takes the money out of the open loot window.</summary>
    public void TakeLootMoney(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (FindOpenLoot(player) is not (WorldObject holder, Loot loot) || loot.Gold == 0)
        {
            return;
        }

        uint copper = loot.Gold;
        loot.Gold = 0;

        player.Money += copper;
        player.Connection?.SendLootMoneyTaken(copper);

        ClearIfEmpty(holder, loot);
    }

    /// <summary>Closes the loot window.</summary>
    /// <remarks>
    /// The corpse stops sparkling if nothing is left. It is not despawned here — the corpse delay
    /// owns that, and a looted corpse still lies there for the rest of it.
    /// </remarks>
    public void ReleaseLoot(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        ObjectGuid target = player.LootTarget;

        player.LootTarget = ObjectGuid.Empty;
        player.Connection?.SendLootReleased(target);

        if (Find(target) is { } released)
        {
            Loot? loot = released switch
            {
                Creature creature => creature.Loot,
                GameObject chest => chest.Loot,
                _ => null,
            };

            if (loot is not null)
            {
                ClearIfEmpty(released, loot);
            }
        }
    }

    /// <summary>
    /// Whatever this player has a loot window open on, and what is in it.
    /// </summary>
    /// <remarks>
    /// Either a corpse or a chest. They differ in how they come to hold loot and in nothing after
    /// that, which is why everything downstream takes the holder as a <see cref="WorldObject"/>.
    /// </remarks>
    private (WorldObject Holder, Loot Loot)? FindOpenLoot(Player player)
    {
        if (player.LootTarget.IsEmpty)
        {
            return null;
        }

        Loot? loot = Find(player.LootTarget) switch
        {
            Creature creature => creature.Loot,
            GameObject chest => chest.Loot,
            _ => null,
        };

        if (loot is null || (!loot.Owner.IsEmpty && loot.Owner != player.Guid))
        {
            return null;
        }

        return (Find(player.LootTarget)!, loot);
    }

    /// <summary>Whether a player still needs an item for a quest, when quests are loaded at all.</summary>
    /// <remarks>
    /// With no quest store, quest drops are shown to everyone. That is the same behaviour as before
    /// this existed, and the safer failure: hiding every quest item because a store was not wired up
    /// would make those quests impossible rather than merely untidy.
    /// </remarks>
    private bool NeedsForQuest(Player viewer, uint itemId) =>
        Quests is null || viewer.Quests.NeedsItem(itemId, Quests);

    /// <summary>Stops a corpse or a chest sparkling once there is nothing left in it.</summary>
    private static void ClearIfEmpty(WorldObject holder, Loot loot)
    {
        if (!loot.IsEmpty)
        {
            return;
        }

        switch (holder)
        {
            case Creature creature:
                creature.Loot = null;
                creature.DynamicFlags &= ~UnitDynamicFlags.Lootable;
                break;

            case GameObject chest:
                // The loot stays on the object rather than being nulled: a chest that has been
                // emptied is still a chest, and upstream keeps the empty loot so a second player
                // opening it is told it is empty rather than being handed a fresh roll.
                chest.Loot = loot;
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The slots still worth drawing to this player, in their original numbering.
    /// </summary>
    /// <remarks>
    /// <b>Per player, not per corpse.</b> A quest drop is shown only to someone who has the quest
    /// and still needs one — everyone else does not see the slot at all. Building one list for the
    /// corpse means either everybody loots the quest item or nobody does.
    /// <para>
    /// The numbering is the corpse's, not the visible list's. The client sends the slot back, so a
    /// player who cannot see slot 2 must still see slot 3 as slot 3 — renumbering hands them
    /// somebody else's item.
    /// </para>
    /// </remarks>
    private List<LootSlot> VisibleSlots(Loot loot, Player viewer)
    {
        List<LootSlot> slots = [];

        foreach (LootItem item in loot.Items)
        {
            if (item.IsLooted)
            {
                continue;
            }

            if (item.NeedsQuest && !NeedsForQuest(viewer, item.ItemId))
            {
                continue;
            }

            slots.Add(new LootSlot(
                Slot: item.Index,
                ItemId: item.ItemId,
                Count: item.Count,
                DisplayId: item.DisplayId,
                SlotType: LootSlotType.AllowLoot));
        }

        return slots;
    }

    /// <summary>
    /// Credits a kill against everyone's quests.
    /// </summary>
    /// <remarks>
    /// Everyone on the threat list, not just the killer: upstream credits every group member in
    /// range, and the threat list is the closest thing to a group this server has. It is also why
    /// this runs before <c>Kill()</c>, which clears the list.
    /// <para>
    /// Every matching objective is credited, not the first — two quests can ask for the same
    /// creature, and crediting one of them is a bug a player notices and cannot explain.
    /// </para>
    /// </remarks>
    private void CreditQuestKills(Creature victim)
    {
        if (_quests is null)
        {
            return;
        }

        foreach (ThreatEntry entry in victim.Threat.Sorted)
        {
            if (entry.Target is not Player player || !player.IsAlive)
            {
                continue;
            }

            foreach (QuestKillCredit credit in player.Quests.CreditKill(victim.Entry, _quests))
            {
                player.Connection?.SendQuestKillCredit(
                    credit.Quest.Id, credit.WireEntry, credit.Current, credit.Required, victim.Guid);

                if (player.Quests.StatusOf(credit.Quest.Id) == QuestStatus.Complete)
                {
                    player.Connection?.SendQuestComplete(credit.Quest.Id);
                }
            }
        }
    }

    /// <summary>
    /// Decides what a corpse is holding, and who may open it.
    /// </summary>
    /// <remarks>
    /// Called from <see cref="Kill(Creature)"/>, before the threat list is cleared — the owner is
    /// whoever was at the top of it, and after <c>Kill()</c> there is nobody to ask.
    /// <para>
    /// <b>An empty pile is not marked lootable.</b> The dynamic flag is what makes the corpse
    /// sparkle, and a sparkling corpse that opens an empty window is worse than one that does not
    /// sparkle at all.
    /// </para>
    /// </remarks>
    private void RollLoot(Creature creature)
    {
        if (_creatureLoot is null || _lootReferences is null || _items is null)
        {
            return;
        }

        // Zero is not "the same as the entry": several entries share one list, and a template with
        // no loot id drops nothing at all.
        uint lootId = creature.LootId;

        Loot loot = new()
        {
            Owner = TopOfThreatList(creature),
            Gold = LootRoll.RollMoney(creature.MinGold, creature.MaxGold, GameRandom.Urand),
        };

        if (lootId != 0 && _creatureLoot.TryGet(lootId, out LootTemplate? template) && template is not null)
        {
            LootRoll.Fill(
                loot,
                template,
                _lootReferences,
                _items,
                () => GameRandom.Urand(0, 9999) / 100f,
                count => (int)GameRandom.Urand(0, (uint)count - 1),
                GameRandom.Urand);
        }

        if (loot.IsEmpty)
        {
            return;
        }

        creature.Loot = loot;
        creature.DynamicFlags |= UnitDynamicFlags.Lootable;
    }

    /// <summary>Whoever hated the creature most, which is who its corpse belongs to.</summary>
    private static ObjectGuid TopOfThreatList(Creature creature)
    {
        foreach (ThreatEntry entry in creature.Threat.Sorted)
        {
            if (entry.Target is Player player)
            {
                return player.Guid;
            }
        }

        return ObjectGuid.Empty;
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

            IReadOnlyList<LevelUp> levels = Experience.Give(killer, gain, _xpTable, _playerStats, Skills);

            killer.Connection?.SendExperienceGain(victim.Guid, gain, levels);
        }
    }

    /// <summary>
    /// Releases a dead player's spirit and sends it to a graveyard.
    /// </summary>
    /// <remarks>
    /// The corpse position is remembered before the move, because that is what a corpse run walks
    /// back to. Losing it is how a player ends up a permanent ghost with nowhere to resurrect.
    /// <para>
    /// A zone with no usable graveyard leaves the player where it fell — a ghost standing over its
    /// own corpse, which it can still reclaim. Teleporting to some other zone's graveyard would be
    /// worse than not moving.
    /// </para>
    /// </remarks>
    /// <returns>Whether there was a spirit to release.</returns>
    public bool ReleaseSpirit(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!PlayerDeath.Release(player))
        {
            return false;
        }

        // At release, not at death: until a player releases, the body standing there IS their own
        // character, still rendered dead. Creating it at death would put two of them in the world.
        SpawnCorpse(player);

        if (_graveyards is null || _worldSafeLocs is null)
        {
            return true;
        }

        if (PlayerDeath.ClosestGraveyard(player, _graveyards, _worldSafeLocs) is not { } graveyard)
        {
            return true;
        }

        Relocate(player, graveyard.Position);

        // The marker the client draws on the minimap, which is what a corpse run is navigated by.
        player.Connection?.SendSpiritHealerLocation(graveyard.MapId, graveyard.Position);

        return true;
    }

    /// <summary>
    /// Resurrects a ghost standing at its own corpse.
    /// </summary>
    /// <remarks>
    /// The range check is the whole mechanic: reclaiming from anywhere makes the corpse run
    /// optional, which is the cost of dying.
    /// </remarks>
    /// <returns>Whether the corpse was close enough to reclaim.</returns>
    public bool ReclaimCorpse(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!player.IsGhost || player.CorpseMapId is not { } corpseMap)
        {
            return false;
        }

        // Against this map's own id, not just the player's: a ghost that has been moved to another
        // map has a corpse this map cannot see, and reclaiming it here would resurrect someone into
        // a world their body is not in.
        if (corpseMap != MapId || player.MapId != MapId)
        {
            return false;
        }

        if (player.Position.GetExactDist2dSq(player.CorpsePosition) > CorpseReclaimRange * CorpseReclaimRange)
        {
            return false;
        }

        // The delay the client was told about at death. It counts it down itself and greys the
        // button, so enforcing it here changes nothing for an honest client — and everything for
        // one that simply sends the packet anyway.
        if (!CorpseReclaim.CanReclaim(player, GameTime.Now))
        {
            return false;
        }

        Resurrect(player);

        return true;
    }

    /// <summary>
    /// Puts a dead player back on their feet where they stand.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="ReclaimCorpse"/> rather than repeated, because the packet matters as
    /// much as the state: a player restored without <c>SendResurrected</c> is alive to the server
    /// and a ghost to their own client, which is not a state anything else knows how to leave.
    /// </remarks>
    public void Resurrect(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        PlayerDeath.Resurrect(player);

        // The body goes with them. A living player standing next to their own resurrectable corpse
        // is offered the dialog again, which resurrects them a second time from nothing.
        LayToRest(player);

        player.Connection?.SendResurrected();
    }

    /// <summary>
    /// The spirit healer's way back: instant, and it costs you.
    /// </summary>
    /// <remarks>
    /// Port of <c>WorldSession::SendSpiritResurrect</c>. This is the other half of the death loop
    /// and the only thing that makes the corpse run worth doing — walking back is free and slow,
    /// and this is immediate and charges for it twice over:
    /// <list type="bullet">
    /// <item>
    /// <b>A quarter of your durability, on everything you are carrying as well as wearing.</b>
    /// Dying itself takes a tenth off equipment only; this reaches the bags too, which is the
    /// difference between the two routes and easy to miss because both call the same method.
    /// </item>
    /// <item>
    /// <b>Resurrection sickness</b>, which halves your stats for up to ten minutes.
    /// </item>
    /// </list>
    /// <para>
    /// No corpse-range check, unlike <see cref="ReclaimCorpse"/> — the whole point is that it works
    /// wherever the ghost is standing.
    /// </para>
    /// </remarks>
    /// <returns>False when the player was not a ghost to begin with.</returns>
    public bool SpiritHealerResurrect(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!player.IsGhost)
        {
            return false;
        }

        Resurrect(player);

        // Before the sickness, because the sickness needs the player alive to take an aura — and
        // after the resurrect for the same reason.
        Durability.LoseAll(player, SpiritHealerDurabilityLoss, inventory: true);

        ApplyResurrectionSickness(player);

        return true;
    }

    /// <summary>
    /// A quarter, and it reaches the bags. <c>DurabilityLoss.OnSpiritResurrect</c>.
    /// </summary>
    /// <remarks>
    /// Two and a half times what dying costs, and over a wider set. Both numbers being a "durability
    /// loss" makes them look interchangeable; they are the price of the two different routes back.
    /// </remarks>
    public const double SpiritHealerDurabilityLoss = 0.25;

    /// <summary>Puts the sickness on a player who has just taken the quick way back.</summary>
    private void ApplyResurrectionSickness(Player player)
    {
        int duration = ResurrectionSickness.DurationMsFor(player.Level);

        if (duration <= 0 || _spells is null
            || !_spells.Spells.TryGet(ResurrectionSickness.SpellId, out SpellEntry? spell) || spell is null)
        {
            return;
        }

        // The duration is overridden rather than taken from the spell: the spell carries the full
        // ten minutes, and levels 11 to 19 serve less. Casting it and leaving the duration alone
        // gives a level-11 character the same sentence as a level-80 raider.
        Aura? aura = player.Auras.Apply(
            spell,
            player.Guid,
            player.Level,
            duration,
            effect => SpellEffects.CalculateValue(
                spell, effect, player.Level, (min, max) => (int)GameRandom.Urand((uint)min, (uint)max)));

        if (aura is not null)
        {
            BroadcastAuraApplied(player, aura);
            RefreshStats(player);
        }
    }

    /// <summary>Puts a player's body into the world where they died.</summary>
    private void SpawnCorpse(Player player)
    {
        // Any earlier body becomes bones first. Without this a player who dies, releases, and dies
        // again before reclaiming has two resurrectable corpses and can pick either.
        LayToRest(player);

        Corpse corpse = Corpse.Create(player, ++_nextCorpseGuid);

        _corpses[player.Guid] = corpse;

        Add(corpse);
    }

    /// <summary>
    /// Turns a player's body into bones and takes it out of the world.
    /// </summary>
    /// <remarks>
    /// <c>Player::SpawnCorpseBones</c>. Converted rather than simply removed, so that the object the
    /// clients already know about changes into something that cannot be resurrected at instead of
    /// vanishing — and then it is removed, because nothing here decays bones yet.
    /// </remarks>
    private void LayToRest(Player player)
    {
        if (!_corpses.Remove(player.Guid, out Corpse? corpse))
        {
            return;
        }

        corpse.ConvertToBones();

        Remove(corpse);
    }

    /// <summary>The body a player left behind, if it is still in the world.</summary>
    public Corpse? CorpseOf(ObjectGuid owner) =>
        _corpses.TryGetValue(owner, out Corpse? corpse) ? corpse : null;

    /// <summary>How close a ghost must be to its corpse to reclaim it.</summary>
    public const float CorpseReclaimRange = 39.0f;

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

        TeachCombatSkills(attacker, victim, damage.Outcome);

        // Death is noticed here, where health changed, rather than polled somewhere later. A hit
        // that takes the last point and a hit that takes ten times it are the same kill.
        NoticeDeath(victim);
    }

    /// <summary>
    /// Gives both sides of a melee exchange their chance at a skill-up.
    /// </summary>
    /// <remarks>
    /// Both sides from one call, because the exchange is one event: the attacker learns to swing and
    /// the victim learns to take it, and running them from separate places is how the two drift into
    /// disagreeing about what happened.
    /// <para>
    /// <b>Only against creatures.</b> Upstream skips this entirely when the other side is a player
    /// or a player's pet, which is what stops two characters standing in a city raising each other's
    /// weapon skill to the cap for free.
    /// </para>
    /// </remarks>
    private static void TeachCombatSkills(Unit attacker, Unit victim, MeleeHitOutcome outcome)
    {
        if (attacker is Player striker && victim is Creature
            && SkillGain.Teaches(outcome, defending: false))
        {
            SkillGain.RollCombat(striker, victim, defending: false, GameRandom.Urand);
        }

        if (victim is Player struck && attacker is Creature
            && SkillGain.Teaches(outcome, defending: true))
        {
            SkillGain.RollCombat(struck, attacker, defending: true, GameRandom.Urand);
        }
    }

    /// <summary>
    /// Turns a unit at zero health into a corpse.
    /// </summary>
    /// <remarks>
    /// One place for both kinds, so a swing and a spell cannot disagree about what a kill is. A
    /// player becomes a corpse that has to release; a creature becomes one that decays.
    /// </remarks>
    private void NoticeDeath(Unit victim)
    {
        if (victim.Health > 0 || !victim.IsAlive)
        {
            return;
        }

        // Death takes every aura with it. Leaving them on ticks a burn against a corpse, which is
        // harmless in itself but re-enters this method every tick with a victim that is already
        // dead — and on a player it leaves the buff bar populated through the release screen.
        ClearAuras(victim);

        switch (victim)
        {
            case Creature creature:
                Kill(creature);
                break;

            case Player player:
                KillPlayer(player);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Kills a player and stops everything that was fighting it.
    /// </summary>
    /// <remarks>
    /// The creatures are told to forget it explicitly. A creature left holding a dead player on its
    /// threat list stands over the corpse swinging at something the swing loop refuses to hit,
    /// forever — the player never releases, so nothing ever clears it.
    /// </remarks>
    public void KillPlayer(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!player.IsAlive)
        {
            return;
        }

        PlayerDeath.Kill(player);

        // The penalty window is pushed out first, then the delay is read from it — so this death's
        // wait already includes this death. Reading it first gives the previous death's figure.
        long now = GameTime.Now;

        CorpseReclaim.RecordDeath(player, now);

        player.GhostTime = now;
        player.ReclaimDelaySeconds = CorpseReclaim.DelayFor(player, now);

        // Ten percent off what is worn, and nothing off what is carried — upstream passes
        // inventory: false here. The spirit healer's own twenty-five percent is a separate charge
        // and hits the bags as well, which is the difference between the two ways back.
        Durability.LoseAll(player, Durability.DeathLoss, inventory: false);

        foreach (Creature creature in _creatures)
        {
            if (creature.Threat.Contains(player))
            {
                creature.Threat.Remove(player);
            }

            if (ReferenceEquals(creature.Victim, player))
            {
                CreatureAi.Evade(creature);
                SendCreatureMove(creature, creature.HomePosition);
            }
        }

        // The RECLAIM delay, not the release timer. They are different numbers for different things
        // — the release timer is the six minutes after which a corpse releases itself, and sending
        // it here made the client count down six minutes before it would let anyone take their body
        // back. The packet is SMSG_CORPSE_RECLAIM_DELAY and it means what it says.
        player.Connection?.SendPlayerDied(player.ReclaimDelaySeconds * 1000);
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

    /// <summary>
    /// Everyone close enough to hear a line, the speaker included.
    /// </summary>
    /// <remarks>
    /// By distance rather than by visibility, and that is the point: a yell carries three hundred
    /// yards, far past the radius anything is visible at. Walking the speaker's set of watchers is
    /// the obvious implementation and it makes a yell reach exactly as far as a say.
    /// <para>
    /// The speaker is included. Everyone sees their own line in their own chat window, and it comes
    /// back from the server rather than being echoed locally by the client.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Player> PlayersWithinEarshot(Position from, float range)
    {
        float rangeSquared = range * range;

        return [.. _players.Values.Where(player => from.GetExactDist2dSq(player.Position) <= rangeSquared)];
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

        // Auras are not in the create block — the fields that used to carry them are gone in
        // 3.3.5a — so anything already on the unit has to be sent separately or it is invisible to
        // whoever just arrived.
        if (target is Unit { Auras.Count: > 0 } afflicted)
        {
            viewer.Connection?.SendAllAuras(afflicted);
        }

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

