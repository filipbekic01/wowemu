using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game.Combat;
using WowEmu.Game.Movement;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// A creature standing in the world: one <c>creature</c> row, built out through its template.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Creature</c> that Phase 6 needs — <c>InitEntry</c>, <c>UpdateEntry</c>,
/// <c>SelectLevel</c> and the health handling at the end of <c>LoadFromDB</c>. Everything past that
/// is behaviour: AI, threat, respawn, loot, movement generators. None of it exists yet, and a
/// creature without it stands still and can be looked at, which is exactly what M4 asks for.
/// </remarks>
public sealed class Creature : Unit
{
    private Creature(ObjectGuid guid)
        : base(guid, TypeId.Unit, UpdateFields.UNIT_END, TypeMask.CreatureObject)
    {
    }

    /// <summary>
    /// The surface under a point, supplied by the map this creature is standing on.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a map reference, so <see cref="Creature"/> keeps knowing nothing about
    /// maps, grids or terrain files — the same arrangement that lets one be built and tested with no
    /// world behind it. The map hands it over when it files the creature.
    /// <para>
    /// Null means nobody has said, and a generator that needs a height must then leave the creature
    /// where it is rather than assume one.
    /// </para>
    /// </remarks>
    public Func<float, float, float, float?>? FloorAt { get; set; }

    /// <summary>The <c>creature</c> row this came from — upstream's <c>m_spawnId</c>.</summary>
    /// <remarks>
    /// Kept separate from <see cref="GameObjectBase.Guid"/> because they are different numbers with
    /// different lifetimes: the spawn id is stable in the database, the guid is what the client is
    /// told and carries the entry in its middle bits.
    /// </remarks>
    public uint SpawnId { get; private init; }

    /// <summary>
    /// Row in <c>creature_loot_template</c>. <b>Zero means it drops nothing.</b>
    /// </summary>
    /// <remarks>
    /// Usually the same as <see cref="Entry"/>, and often not — several entries share one list, so
    /// assuming the entry is the loot id gives a large part of the game the wrong drops.
    /// </remarks>
    public uint LootId { get; private init; }

    /// <summary>Which gossip menu right-clicking opens. Zero means it has none of its own.</summary>
    public uint GossipMenuId { get; private init; }

    /// <summary>The copper range the corpse carries, before it is rolled.</summary>
    public uint MinGold { get; private init; }

    /// <inheritdoc cref="MinGold"/>
    public uint MaxGold { get; private init; }

    /// <summary>
    /// What this corpse is holding, or null if it was never worth looting.
    /// </summary>
    /// <remarks>
    /// Rolled once, when the creature dies, rather than at spawn: rolling at spawn would decide
    /// 145,946 piles the moment a continent loads, and most of them would never be seen.
    /// </remarks>
    public Loot? Loot { get; set; }

    /// <summary>The <c>creature_template</c> entry.</summary>
    public uint Entry { get; private init; }

    /// <summary>Which phases can see it. Everything is phase 1 until phasing exists.</summary>
    public uint PhaseMask { get; private init; }

    /// <summary>
    /// The entry's exceptions to the general rules — <c>creature_template.flags_extra</c>.
    /// </summary>
    /// <remarks>
    /// Server-side only; the client is never told. Combat consults it for the five bits named in
    /// <see cref="CreatureFlagsExtra"/>, which is how a target dummy is made unable to dodge without
    /// the attack table growing a special case for it.
    /// </remarks>
    public CreatureFlagsExtra FlagsExtra { get; private init; }

    /// <summary>
    /// Normal, elite, rare elite, world boss or rare — <c>creature_template.rank</c>.
    /// </summary>
    /// <remarks>
    /// Server-side; the client infers the silver or gold portrait border from other data. Combat
    /// reads it because a world boss dodges and parries far more than anything else.
    /// </remarks>
    public byte Rank { get; private init; }

    /// <summary>Beast, humanoid, undead and so on — <c>creature_template.type</c>.</summary>
    /// <remarks>Parry turns on this: only a humanoid has anything to parry with.</remarks>
    public byte CreatureType { get; private init; }

    /// <summary>
    /// Which expansion's content this belongs to — <c>creature_template.exp</c>.
    /// </summary>
    /// <remarks>
    /// Already used to pick a health and damage slot; experience reads it too, because the base
    /// figure a kill pays is five times higher in Outland than in Azeroth.
    /// </remarks>
    public byte Expansion { get; private init; }

    /// <summary>Whether this is a world boss, which has its own dodge and parry values.</summary>
    public bool IsWorldBoss => Rank == MeleeChances.WorldBossRank;

    /// <summary>
    /// Whether it starts fights, only finishes them, or neither.
    /// </summary>
    /// <remarks>
    /// Aggressive by default, which is what most of the world is. Upstream reads a per-spawn
    /// override from <c>creature_addon</c>; that table is not vendored yet, so a passive quest prop
    /// currently behaves like anything else — visible as a critter that fights back.
    /// </remarks>
    public ReactState React { get; set; } = ReactState.Aggressive;

    /// <inheritdoc/>
    public override float DodgeChance => MeleeChances.CreatureDodge(IsWorldBoss);

    /// <inheritdoc/>
    public override float ParryChance => MeleeChances.CreatureParry(IsWorldBoss, CreatureType);

    /// <inheritdoc/>
    public override bool CanDodge => !FlagsExtra.HasFlag(CreatureFlagsExtra.NoDodge);

    /// <inheritdoc/>
    public override bool CanParry => !FlagsExtra.HasFlag(CreatureFlagsExtra.NoParry);

    /// <inheritdoc/>
    public override bool CanBlock => !FlagsExtra.HasFlag(CreatureFlagsExtra.NoBlock);

    /// <inheritdoc/>
    public override bool CanCrush => !FlagsExtra.HasFlag(CreatureFlagsExtra.NoCrushingBlows);

    /// <inheritdoc/>
    public override bool CanCrit => !FlagsExtra.HasFlag(CreatureFlagsExtra.NoCrit);

    /// <summary>
    /// Where the spawn row put it, which is what it wanders around and returns to.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="WorldObject.Position"/>, which is where it is now. Conflating the
    /// two makes a wandering creature drift: each new destination is drawn around wherever it
    /// happened to stop, so over an hour it walks away and never comes back.
    /// </remarks>
    public Position HomePosition { get; private init; }

    /// <summary>How far from home it may stray. Zero for a creature that stands still.</summary>
    public float WanderDistance { get; private init; }

    /// <summary>Decides where it is trying to go.</summary>
    public MotionMaster Motion { get; private init; } = null!;

    /// <summary>The move in progress, if any.</summary>
    public CreatureMove CurrentMove { get; private set; }

    /// <summary>How far through <see cref="CurrentMove"/> it is.</summary>
    public uint MoveElapsedMs { get; private set; }

    /// <summary>Whether it is part-way through a move.</summary>
    public bool IsMoving => CurrentMove.IsMoving && MoveElapsedMs < CurrentMove.DurationMs;

    /// <summary>
    /// Identifies the current move to the client, so a new one supersedes the old.
    /// </summary>
    /// <remarks>
    /// Upstream draws these from one process-wide counter. Per-creature is enough — the client only
    /// compares a spline id against the last one it saw for that object — and it keeps the number
    /// small enough to read in a packet log.
    /// </remarks>
    public uint SplineId { get; private set; }

    /// <summary>
    /// What is left of the current move, from where the creature is now.
    /// </summary>
    /// <remarks>
    /// For a player who arrives part-way through. Sending the original move would make the client
    /// interpolate from where the creature started, snapping it backwards before it walks on.
    /// </remarks>
    public CreatureMove? RemainingMove => IsMoving
        ? new CreatureMove(Position, CurrentMove.Destination, CurrentMove.DurationMs - MoveElapsedMs)
        : null;

    /// <summary>
    /// Advances the creature by one tick, returning a move to broadcast if a new one started.
    /// </summary>
    /// <param name="diffMs">
    /// Gameplay milliseconds. Zero when the map is out of phase, which is three ticks in four — so
    /// this must do nothing at all when it is zero, rather than treating it as "a very short tick".
    /// </param>
    /// <returns>
    /// The move that just began, or null. Returning it rather than sending it keeps the creature
    /// free of any knowledge of packets or of who can see it; the map owns both.
    /// </returns>
    public CreatureMove? Update(uint diffMs)
    {
        if (diffMs == 0 || !IsAlive)
        {
            return null;
        }

        if (IsMoving)
        {
            MoveElapsedMs += diffMs;
            Position = CurrentMove.At(MoveElapsedMs);

            // Still going: the client is interpolating the same move and needs nothing further.
            if (IsMoving)
            {
                return null;
            }

            // It has arrived. A generator pushed for one journey — going home — takes this as its
            // cue to stand down, revealing whatever it interrupted.
            Motion.NotifyArrived(this);
        }

        // In a fight the AI decides where to go, so the wander generator is not consulted. Letting
        // it run anyway would have a creature amble off mid-chase towards a random point near its
        // spawn — the move it is on is still interpolated above, only the *next* one is withheld.
        if (Victim is not null)
        {
            return null;
        }

        if (!Motion.TryGetDestination(this, diffMs, out MovementDecision decision))
        {
            return null;
        }

        // The move starts from where the creature actually is, not from where the generator thinks
        // it should be. Upstream overwrites its path's first point with the unit's real position for
        // this reason — anything else makes the creature snap before it walks.
        CreatureMove? move = CreatureMove.Create(
            Position, decision.Destination, decision.Run ? Speeds.Run : Speeds.Walk);

        if (move is null)
        {
            // Already close enough that the walk is not worth a packet — but the journey is over
            // all the same, and the generator has to be told. A creature that evades while standing
            // on its own spawn point would otherwise wait forever for an arrival that no move can
            // produce, and never wander or patrol again.
            Motion.NotifyArrived(this);
            return null;
        }

        CurrentMove = move.Value;
        MoveElapsedMs = 0;
        SplineId++;

        return CurrentMove;
    }

    /// <summary>
    /// Sends the creature walking to a point, whatever its movement generator wanted.
    /// </summary>
    /// <remarks>
    /// How chasing and returning home are expressed. Distinct from <see cref="Update"/>, which asks
    /// the generator where to go — combat overrides that rather than negotiating with it.
    /// <para>
    /// The creature runs rather than walks. A wandering creature ambles; one chasing you does not,
    /// and using the walk speed here makes a fight impossible to lose by running away.
    /// </para>
    /// </remarks>
    /// <returns>The move that started, or null when it is already close enough to be pointless.</returns>
    public CreatureMove? MoveTo(Position destination)
    {
        CreatureMove? move = CreatureMove.Create(Position, destination, Speeds.Run);

        if (move is null)
        {
            return null;
        }

        CurrentMove = move.Value;
        MoveElapsedMs = 0;
        SplineId++;

        return CurrentMove;
    }

    // ------------------------------------------------------------------ dying and coming back

    /// <summary>How long this creature's corpse lies there before it disappears.</summary>
    /// <remarks>
    /// From the rank. A rare or elite corpse lasts five times as long as a common one, which is what
    /// gives a group time to loot something they fought hard for.
    /// </remarks>
    public uint CorpseDelayMs { get; private init; }

    /// <summary>How long after the corpse goes before the creature is back.</summary>
    /// <remarks><c>creature.spawntimesecs</c> — per spawn, not per template.</remarks>
    public uint RespawnDelayMs { get; private init; }

    /// <summary>Milliseconds until the corpse disappears. Zero unless there is a corpse.</summary>
    public uint CorpseRemainingMs { get; private set; }

    /// <summary>Milliseconds until the creature comes back. Zero unless it is waiting to.</summary>
    public uint RespawnRemainingMs { get; private set; }

    /// <summary>Whether the creature is off the map, waiting out its respawn.</summary>
    public bool IsDespawned => DeathState == DeathState.Dead;

    /// <summary>What one tick did to a dead creature.</summary>
    public enum DeathTransition : byte
    {
        /// <summary>Nothing yet.</summary>
        None = 0,

        /// <summary>The corpse just disappeared; everyone watching has to be told.</summary>
        CorpseRemoved = 1,

        /// <summary>The creature is back; everyone in range has to be told.</summary>
        Respawned = 2,
    }

    /// <summary>
    /// Kills the creature.
    /// </summary>
    /// <remarks>
    /// Port of <c>Creature::setDeathState(JUST_DIED)</c>, which ends by promoting straight to
    /// <see cref="DeathState.Corpse"/> — <c>JustDied</c> never survives the call. That is why
    /// upstream logs an error if it ever sees a creature <i>updating</i> in the just-died state.
    /// The moment exists so that everything which happens exactly once at death — loot assignment,
    /// experience, the kill log — happens between the two.
    /// </remarks>
    public void Kill()
    {
        if (!IsAlive)
        {
            return;
        }

        DeathState = DeathState.JustDied;

        // Both timers start now. Respawn is measured from death and *includes* the corpse delay, so
        // a creature is not back the instant its corpse fades.
        CorpseRemainingMs = CorpseDelayMs;
        RespawnRemainingMs = RespawnDelayMs + CorpseDelayMs;

        Health = 0;
        Power = 0;
        Target = ObjectGuid.Empty;
        AttackStop();
        IsInCombat = false;

        // Everything it hated is forgotten. Carrying the list through a respawn would have a
        // creature come back already angry at whoever killed it, wherever they had got to.
        Threat.Clear();

        // A corpse offers no services. Leaving the flags up gives a dead innkeeper a usable gossip
        // icon, which the client will happily draw.
        NpcFlags = 0;

        // Stop where it fell. Without this the client keeps interpolating the spline it was last
        // given and the corpse slides away from where it died.
        CancelMove();

        // The client draws the loot sparkle from this. There is no loot yet, so it is not set — the
        // flag would promise a lootable corpse that opens an empty window.
        DeathState = DeathState.Corpse;
    }

    /// <summary>
    /// Advances a dead creature's corpse and respawn timers.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="Update"/> rather than folded into it: a living creature walks and a dead
    /// one decays, and they share nothing. The transition is returned rather than acted on, for the
    /// same reason a move is — the creature knows nothing about who can see it.
    /// </remarks>
    public DeathTransition UpdateDeath(uint diffMs)
    {
        if (diffMs == 0 || IsAlive)
        {
            return DeathTransition.None;
        }

        RespawnRemainingMs = RespawnRemainingMs > diffMs ? RespawnRemainingMs - diffMs : 0;

        if (DeathState == DeathState.Corpse)
        {
            CorpseRemainingMs = CorpseRemainingMs > diffMs ? CorpseRemainingMs - diffMs : 0;

            if (CorpseRemainingMs == 0)
            {
                DeathState = DeathState.Dead;

                // Back to the spawn point the moment the corpse goes, not when it respawns. A
                // creature dragged across the zone would otherwise reappear where it was killed.
                Position = HomePosition;

                return DeathTransition.CorpseRemoved;
            }

            return DeathTransition.None;
        }

        if (RespawnRemainingMs == 0)
        {
            Respawn();

            return DeathTransition.Respawned;
        }

        return DeathTransition.None;
    }

    /// <summary>Brings the creature back at full health, where it spawned.</summary>
    public void Respawn()
    {
        DeathState = DeathState.Alive;

        Health = MaxHealth;
        Power = MaxPower;
        Position = HomePosition;

        NpcFlags = _spawnNpcFlags;
        UnitFlags = _spawnUnitFlags;
        DynamicFlags = _spawnDynamicFlags;

        CorpseRemainingMs = 0;
        RespawnRemainingMs = 0;

        CancelMove();
        SyncMovement();
    }

    /// <summary>
    /// How long a corpse of a given rank lies there. <c>Corpse.Decay.*</c>.
    /// </summary>
    /// <remarks>
    /// A minute for anything ordinary, five for rare and elite. A world boss outside an instance
    /// gets ten rather than the hour it would get inside one — upstream shortens it deliberately, so
    /// an open-world boss corpse does not block its own spawn point for an hour.
    /// </remarks>
    public static uint CorpseDelayMsFor(byte rank) => rank switch
    {
        RankRare => 300_000,
        RankElite => 300_000,
        RankRareElite => 300_000,
        RankWorldBoss => 600_000,
        _ => 60_000,
    };

    /// <summary>Ranks, from <c>CreatureEliteType</c>.</summary>
    public const byte RankNormal = 0;
    public const byte RankElite = 1;
    public const byte RankRareElite = 2;
    public const byte RankWorldBoss = 3;
    public const byte RankRare = 4;

    /// <summary>Ends any move in progress, leaving the creature where it stands.</summary>
    private void CancelMove()
    {
        CurrentMove = default;
        MoveElapsedMs = 0;
    }

    // The flags the spawn row asked for, kept so a respawn restores them rather than whatever the
    // creature was carrying when it died.
    private uint _spawnNpcFlags;
    private uint _spawnUnitFlags;
    private uint _spawnDynamicFlags;

    /// <summary>
    /// Builds a creature from its spawn row, its template, its model and the base stats for the
    /// level that gets rolled.
    /// </summary>
    /// <param name="spawn">The <c>creature</c> row.</param>
    /// <param name="template">The <c>creature_template</c> row for <c>spawn.Entry</c>.</param>
    /// <param name="models">Resolves a display id to its size and opposite-gender twin.</param>
    /// <param name="stats">Base stats per level and unit class.</param>
    /// <param name="level">
    /// The level to build at. Callers pass a roll between the template's minimum and maximum;
    /// leaving it to the caller keeps the random draw out of a method that is otherwise a pure
    /// function of its inputs, which is what makes it testable.
    /// </param>
    /// <param name="useOppositeGenderModel">
    /// Whether to swap to <c>DisplayID_Other_Gender</c>. Upstream rolls this at 50 % inside
    /// <c>GetCreatureModelRandomGender</c>; hoisting it out has the same reason as
    /// <paramref name="level"/>.
    /// </param>
    /// <returns>The creature, or null when the display id has no model info — the one failure that
    /// is worth refusing rather than papering over, since it produces something unclickable.</returns>
    public static Creature? Create(
        CreatureSpawn spawn,
        CreatureTemplate template,
        ICreatureModelSource models,
        CreatureBaseStats stats,
        byte level,
        bool useOppositeGenderModel,
        uint displayId,
        IReadOnlyList<Waypoint>? path = null,
        CreatureEquipment? equipment = null,
        CreatureAddon? addon = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(models);

        if (!models.TryGetModel(displayId, out CreatureModelInfo model))
        {
            return null;
        }

        // The opposite-gender twin has its own model info: swapping the display id without swapping
        // the size leaves a male model wearing a female hitbox.
        if (useOppositeGenderModel
            && model.DisplayIdOtherGender != 0
            && models.TryGetModel(model.DisplayIdOtherGender, out CreatureModelInfo otherGender))
        {
            displayId = model.DisplayIdOtherGender;
            model = otherGender;
        }

        Creature creature = new(ObjectGuid.Create(HighGuid.Unit, spawn.Entry, spawn.SpawnId))
        {
            SpawnId = spawn.SpawnId,
            Entry = spawn.Entry,
            PhaseMask = spawn.PhaseMask,
            FlagsExtra = (CreatureFlagsExtra)template.FlagsExtra,
            Rank = template.Rank,
            CreatureType = template.CreatureType,
            Expansion = template.Expansion,
            LootId = template.LootId,
            GossipMenuId = template.GossipMenuId,
            MinGold = template.MinGold,
            MaxGold = template.MaxGold,
            CorpseDelayMs = CorpseDelayMsFor(template.Rank),
            RespawnDelayMs = spawn.RespawnDelaySeconds * 1000,
            MapId = spawn.MapId,
            Position = spawn.Position,
            HomePosition = spawn.Position,
            WanderDistance = spawn.WanderDistance,
            Motion = BuildMotionMaster(spawn, path),
        };

        creature.Name = template.Name;

        // Weapons the client draws but nothing carries. Three item template ids, and writing them
        // is the whole of it — a creature's sword is a model, not an item, and never enters any
        // inventory.
        if (equipment is { } outfit)
        {
            for (int slot = 0; slot < CreatureEquipment.SlotCount; slot++)
            {
                creature.Fields.SetUInt32(UpdateFields.UNIT_VIRTUAL_ITEM_SLOT_ID + slot, outfit[slot]);
            }
        }


        UpdateFieldStorage fields = creature.Fields;

        // Creatures carry their entry in OBJECT_FIELD_ENTRY; players do not. Without it the client
        // has no template to look the creature up in and draws nothing.
        fields.SetUInt32(UpdateFields.OBJECT_FIELD_ENTRY, spawn.Entry);

        // Race stays 0 — a creature has a class but no race. Only 1, 2, 4 and 8 appear as unit
        // classes, matching warrior, paladin, rogue and mage.
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 0, 0);
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 1, template.UnitClass);
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 2, model.Gender);
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 3, PowerTypeFor(template.UnitClass));

        // Weapons drawn. Upstream does this for every creature without an addon row.
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_2, 0, SheathStateMelee);

        creature.DisplayId = displayId;
        creature.NativeDisplayId = displayId;
        creature.FactionTemplate = template.Faction;
        creature.Level = level;

        // Scale first: bounding radius and combat reach are both multiplied by it, so setting them
        // before the scale is known bakes in the wrong size.
        creature.ObjectScale = template.Scale;
        creature.BoundingRadius = model.BoundingRadius * template.Scale;
        creature.CombatReach =
            (model.CombatReach > 0 ? model.CombatReach : UnitDefaults.CombatReach) * template.Scale;

        // The spawn row *replaces* these rather than adding to them. `if (data->npcflag) npcflag =
        // data->npcflag` in ChooseCreatureFlags — a spawn that sets one flag drops every template
        // flag with it, and OR-ing them instead silently gives creatures abilities they should not
        // have.
        creature.NpcFlags = spawn.NpcFlags != 0 ? spawn.NpcFlags : template.NpcFlags;
        creature.UnitFlags = spawn.UnitFlags != 0 ? spawn.UnitFlags : template.UnitFlags;
        creature.DynamicFlags = spawn.DynamicFlags != 0 ? spawn.DynamicFlags : template.DynamicFlags;
        creature.UnitFlags2 = template.UnitFlags2;

        // Snapshotted so a respawn restores what the spawn row asked for, rather than whatever the
        // creature was carrying when it died — death clears the npc flags, and without this a
        // respawned innkeeper would come back with nothing to offer.
        creature._spawnNpcFlags = creature.NpcFlags;
        creature._spawnUnitFlags = creature.UnitFlags;
        creature._spawnDynamicFlags = creature.DynamicFlags;

        ApplyLevelStats(creature, template, stats, spawn);

        creature.Speeds.Walk = UnitDefaults.BaseWalkSpeed * template.SpeedWalk;
        creature.Speeds.Run = UnitDefaults.BaseRunSpeed * template.SpeedRun;

        // The unmodified pair, so a slow that wears off restores this creature's own speed rather
        // than the global one. Set from the same expressions, not copied afterwards, so the two
        // cannot drift if either line changes.
        creature.BaseSpeeds.Walk = UnitDefaults.BaseWalkSpeed * template.SpeedWalk;
        creature.BaseSpeeds.Run = UnitDefaults.BaseRunSpeed * template.SpeedRun;

        // Last of all, and that is the point: the addon overrides defaults set above it — the
        // weapons-drawn sheath state in particular. Applied any earlier and the defaults win, which
        // looks like the addon table being ignored rather than like an ordering mistake.
        ApplyAddon(creature, addon);

        creature.SyncMovement();
        return creature;
    }

    /// <summary>Picks the level upstream would roll for a template.</summary>
    /// <remarks>
    /// The minimum and maximum are ordered before the roll rather than trusted: a handful of
    /// templates carry <c>maxlevel</c> below <c>minlevel</c>, and an unordered <c>urand</c> would
    /// draw from an empty range.
    /// </remarks>
    public static byte RollLevel(CreatureTemplate template, Func<uint, uint, uint> pick)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(pick);

        byte low = Math.Min(template.MinLevel, template.MaxLevel);
        byte high = Math.Max(template.MinLevel, template.MaxLevel);

        return low == high ? low : (byte)pick(low, high);
    }

    /// <summary>
    /// Applies a spawn's addon row: how it stands, what it rides, what it is doing.
    /// </summary>
    /// <remarks>
    /// Port of <c>Creature::LoadCreaturesAddon</c>, less the auras. Each packed column is applied
    /// <b>only when it is non-zero</b>, which is upstream's own guard and not an optimisation: the
    /// sheath state is already set to weapons-drawn above, and a blanket write of a zero column would
    /// put every creature in the game back to weapons-sheathed.
    /// <para>
    /// Two bytes inside those columns are deliberately dropped rather than written through. The pet
    /// talent byte of <c>bytes1</c> and the rename and shapeshift bytes of <c>bytes2</c> only mean
    /// anything on a pet or under a shapeshift spell, and the columns carry leftovers for everything
    /// else — upstream zeroes them explicitly, with the original lines commented out beside.
    /// </para>
    /// </remarks>
    private static void ApplyAddon(Creature creature, CreatureAddon? addon)
    {
        if (addon is not { } info)
        {
            return;
        }

        UpdateFieldStorage fields = creature.Fields;

        if (info.Mount != 0)
        {
            fields.SetUInt32(UpdateFields.UNIT_FIELD_MOUNTDISPLAYID, info.Mount);
        }

        if (info.Bytes1 != 0)
        {
            fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_1, 0, info.StandState);
            fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_1, 1, 0);
            fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_1, 2, info.VisibilityFlags);
            fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_1, 3, info.AnimationTier);
        }

        if (info.Bytes2 != 0)
        {
            fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_2, 0, info.SheathState);
            fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_2, 2, 0);
            fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_2, 3, 0);
        }

        // Written unconditionally, unlike the rest: upstream sets it outside every guard, so an
        // addon row with no emote actively clears one rather than leaving whatever was there.
        fields.SetUInt32(UpdateFields.UNIT_NPC_EMOTESTATE, info.Emote);
    }

    /// <summary>
    /// Builds the motion master for a spawn's movement type.
    /// </summary>
    /// <remarks>
    /// A random-movement row with no wander distance would walk on the spot forever, so it falls
    /// back to idle — upstream does the same in <c>InitEntry</c>.
    /// <para>
    /// So does a waypoint row with no route. 35 of the 5,290 patrolling spawns name a path that is
    /// not in <c>waypoint_data</c>, and a waypoint generator over an empty list would be asked for a
    /// destination on every tick forever. Standing still is what upstream does with them too.
    /// </para>
    /// <para>
    /// The remaining types — flight, and the two fleeing rows — are idle, which leaves those three
    /// creatures standing rather than moving wrongly.
    /// </para>
    /// </remarks>
    private static MotionMaster BuildMotionMaster(CreatureSpawn spawn, IReadOnlyList<Waypoint>? path)
    {
        MovementGeneratorType type = (MovementGeneratorType)spawn.MovementType;

        if (type == MovementGeneratorType.Random && spawn.WanderDistance <= 0f)
        {
            type = MovementGeneratorType.Idle;
        }

        if (type == MovementGeneratorType.Waypoint && (path is null || path.Count == 0))
        {
            type = MovementGeneratorType.Idle;
        }

        MotionMaster motion = new(type);

        motion.Initialize(type switch
        {
            MovementGeneratorType.Random => new RandomMovementGenerator(spawn.WanderDistance),
            MovementGeneratorType.Waypoint => new WaypointMovementGenerator(path!),
            _ => IdleMovementGenerator.Instance,
        });

        return motion;
    }

    /// <summary>Weapons-drawn sheath state, <c>SHEATH_STATE_MELEE</c>.</summary>
    private const byte SheathStateMelee = 1;

    /// <summary>Unit classes, from <c>SharedDefines.h</c>. Only these four appear on creatures.</summary>
    private const byte UnitClassWarrior = 1;
    private const byte UnitClassPaladin = 2;
    private const byte UnitClassRogue = 4;
    private const byte UnitClassMage = 8;

    private static byte PowerTypeFor(byte unitClass) => unitClass switch
    {
        UnitClassWarrior => PowerRage,
        UnitClassRogue => PowerEnergy,
        UnitClassPaladin or UnitClassMage => PowerMana,
        _ => PowerMana,
    };

    /// <summary>
    /// Sizes health, mana and armour for the rolled level.
    /// </summary>
    /// <remarks>
    /// Port of <c>SelectLevel</c> plus the health block at the end of <c>LoadFromDB</c>. The health
    /// is rounded <b>up</b> — <c>ceil</c>, not a cast — because a <c>Health_mod</c> below 1 on a
    /// low-level creature otherwise truncates to zero, and a creature with no health is drawn dead.
    /// <para>
    /// Upstream also multiplies by a per-rank rate from the config (<c>Rate.Creature.*.HP</c>). Every
    /// one of those defaults to 1.0 and there is no config system for them yet, so they are omitted
    /// rather than hard-coded to a number that would look deliberate.
    /// </para>
    /// </remarks>
    private static void ApplyLevelStats(
        Creature creature,
        CreatureTemplate template,
        CreatureBaseStats stats,
        CreatureSpawn spawn)
    {
        uint maxHealth = Math.Max(
            1u,
            (uint)Math.Ceiling(stats.BaseHealthFor(template.Expansion) * (double)template.HealthModifier));

        // Mana of zero is meaningful — a warrior creature has none — so it is not floored at 1.
        uint maxMana = stats.BaseMana == 0
            ? 0
            : (uint)Math.Ceiling(stats.BaseMana * (double)template.ManaModifier);

        creature.MaxHealth = maxHealth;
        creature.Fields.SetUInt32(UpdateFields.UNIT_FIELD_BASE_HEALTH, maxHealth);

        creature.MaxPower = maxMana;
        creature.Fields.SetUInt32(UpdateFields.UNIT_FIELD_BASE_MANA, maxMana);

        // A creature that regenerates spawns full; one that does not keeps whatever the row saved,
        // which is how scripted encounters spawn something already wounded.
        if (template.RegeneratesHealth)
        {
            creature.Health = maxHealth;
            creature.Power = maxMana;
        }
        else
        {
            creature.Health = Math.Min(spawn.CurrentHealth, maxHealth);
            creature.Power = Math.Min(spawn.CurrentMana, maxMana);
        }

        creature.Armor = (uint)Math.Ceiling(stats.BaseArmor * (double)template.ArmorModifier);

        ApplyCombatStats(creature, template, stats);
    }

    /// <summary>
    /// Sets what a swing is worth: attack power, swing speed, and the damage range.
    /// </summary>
    /// <remarks>
    /// <b>The formula follows our data, not the C++ checkout.</b> The current tree computes weapon
    /// damage from <c>creature_classlevelstats.damage_base</c> scaled by a <c>BaseVariance</c> column
    /// on the template — but our vendored dump predates that column and carries <c>mindmg</c>,
    /// <c>maxdmg</c> and <c>dmg_multiplier</c> instead, all populated (only 911 of 29,928 templates
    /// have no damage). This is the same divergence as <c>creature_template_model</c>: read the C++
    /// for behaviour, but check the dump before trusting a column name.
    /// <para>
    /// So: <c>damage = template range × multiplier + attackPower / 14 × swing seconds</c>, which is
    /// the older form the schema supports. A level-2 wolf comes out around 5 per swing and a level-60
    /// vendor around 90–110, both of which are right.
    /// </para>
    /// <para>
    /// Auras and items contribute nothing here because neither exists. Upstream's full calculation
    /// runs the result through four modifier layers, all of which are identity without them.
    /// </para>
    /// </remarks>
    private static void ApplyCombatStats(Creature creature, CreatureTemplate template, CreatureBaseStats stats)
    {
        creature.AttackPower = template.AttackPower != 0 ? template.AttackPower : stats.AttackPower;
        creature.RangedAttackPower =
            template.RangedAttackPower != 0 ? template.RangedAttackPower : stats.RangedAttackPower;

        // A template with no swing time would otherwise attack infinitely fast.
        uint swing = template.BaseAttackTime != 0 ? template.BaseAttackTime : DefaultAttackTimeMs;

        creature.SetAttackTime(WeaponAttackType.BaseAttack, swing);
        creature.SetAttackTime(WeaponAttackType.OffAttack, swing);
        creature.SetAttackTime(
            WeaponAttackType.RangedAttack,
            template.RangeAttackTime != 0 ? template.RangeAttackTime : DefaultAttackTimeMs);

        float multiplier = template.DamageModifier > 0f ? template.DamageModifier : 1f;
        float fromAttackPower = creature.AttackPower / 14f * (swing / 1000f);

        float min = (template.MinDamage * multiplier) + fromAttackPower;
        float max = (template.MaxDamage * multiplier) + fromAttackPower;

        // 911 templates carry no damage range at all; the class/level table is the fallback so they
        // do not stand there hitting for nothing.
        if (template.MinDamage <= 0f && template.MaxDamage <= 0f)
        {
            float baseDamage = stats.BaseDamageFor(template.Expansion);
            min = baseDamage + fromAttackPower;
            max = (baseDamage * 1.5f) + fromAttackPower;
        }

        creature.MinDamage = MathF.Max(0f, min);
        creature.MaxDamage = MathF.Max(creature.MinDamage, max);
    }

    /// <summary>Two seconds, the commonest swing in the table and upstream's fallback.</summary>
    private const uint DefaultAttackTimeMs = 2000;
}
