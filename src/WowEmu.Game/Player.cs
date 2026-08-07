using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game.Combat;
using WowEmu.Data.Db;
using WowEmu.Game.Maps;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// A player character in the world.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Player</c> that M3 needs: enough update fields for the client to render
/// a character standing in the world with the right model, name, level and stats.
/// <para>
/// Everything the client sees lives in <see cref="GameObjectBase.Fields"/>. The properties here are
/// conveniences over those indices, not a second copy — writing <see cref="Level"/> writes the
/// field, because a value that lives anywhere else would not reach the client.
/// </para>
/// </remarks>
public sealed class Player : Unit
{
    private Player(ObjectGuid guid)
        : base(guid, TypeId.Player, UpdateFields.PLAYER_END, TypeMask.PlayerObject)
    {
        Inventory = new Inventory(this);
        Quests = new QuestLog(this);
        Spells = new SpellBook(this);
    }

    /// <summary>How to reach this player's client. Null for a player with no session.</summary>
    public IPlayerConnection? Connection { get; set; }

    /// <summary>Everything this character is carrying and wearing.</summary>
    public Inventory Inventory { get; }

    /// <summary>Every quest this character has taken, and what became of it.</summary>
    public QuestLog Quests { get; }

    /// <summary>Every spell this character knows.</summary>
    public SpellBook Spells { get; }

    /// <summary>What is on this character's action bars.</summary>
    public ActionBar Actions { get; } = new();

    /// <summary>What this character has sold and can still buy back.</summary>
    public Buyback Buyback => _buyback ??= new Buyback(this);

    private Buyback? _buyback;

    /// <summary>What this character is trained in.</summary>
    public PlayerSkills Skills => _skills ??= new PlayerSkills(this);

    private PlayerSkills? _skills;

    /// <summary>Drowning, fatigue and standing in lava.</summary>
    public PlayerEnvironment Environment { get; } = new();

    /// <summary>
    /// The height this character was last standing at before it started falling.
    /// </summary>
    /// <remarks>
    /// The fall is measured from here, not from where the client says the fall began: the client
    /// reports its own <c>FallTime</c> and a fall-start position, and both are exactly what a client
    /// wanting to avoid fall damage would understate. Upstream's <c>m_lastFallZ</c>.
    /// </remarks>
    public float LastFallZ { get; set; }

    /// <summary>
    /// The corpse whose loot window is open, or empty.
    /// </summary>
    /// <remarks>
    /// <c>PLAYER_LOOT_TARGET_GUID</c> upstream, and a plain field here — the client never reads it,
    /// and it exists so the server can answer "take slot 3" without the client naming what it is
    /// taking from. The client does not send that, which is why it has to be remembered.
    /// </remarks>
    public ObjectGuid LootTarget { get; set; }

    /// <summary>
    /// How much copper the character is carrying.
    /// </summary>
    /// <remarks>
    /// One field, in copper — silver and gold are only how the client draws it. The client's own
    /// ceiling is 214,748 gold, and upstream refuses anything that would exceed it rather than
    /// wrapping; nothing here can generate that much yet.
    /// </remarks>
    public uint Money
    {
        get => Fields.GetUInt32(UpdateFields.PLAYER_FIELD_COINAGE);
        set => Fields.SetUInt32(UpdateFields.PLAYER_FIELD_COINAGE, value);
    }

    /// <summary>
    /// Puts back the health, powers and death state a character logged out with.
    /// </summary>
    /// <remarks>
    /// Everything above this fills the fields from the base tables, which is right for a character
    /// being created and wrong for one being loaded — those are derivations, and these are state.
    /// <para>
    /// <b>The death state matters most.</b> Without it a player who logs out dead comes back alive,
    /// which undoes the corpse, the reclaim penalty and the durability charge in one loading screen.
    /// The ghost bit rides in <c>PLAYER_FLAGS</c>, so restoring the flags restores the state.
    /// </para>
    /// <para>
    /// A character that has never been saved has a health of zero, which is indistinguishable from
    /// one that logged out dead — so zero is read as "never saved" and the freshly computed values
    /// stand. It costs a corpse its state exactly once, on the first login after this landed.
    /// </para>
    /// </remarks>
    private static void RestoreSavedState(Player player, CharacterSummary character)
    {
        // Outside the health guard: the penalty window is a window in absolute time and decays on
        // its own, so it is meaningful even for a character with nothing else saved. Carrying it is
        // what stops chain-dying being free for anyone willing to sit through a loading screen.
        player.DeathExpireTime = character.DeathExpireTime;

        if (character.Health == 0)
        {
            return;
        }

        player.PlayerFlags = character.PlayerFlags;

        // Derived from the ghost flag, exactly as upstream does it — a character with the flag comes
        // back Dead, and everything else comes back alive. Restoring the flags alone would leave a
        // ghost that IsGhost says is a ghost and IsAlive says is alive, which is a state nothing
        // else in the codebase knows how to be in.
        if (player.IsGhost)
        {
            player.DeathState = DeathState.Dead;
        }

        // Clamped, because the maximum is recomputed from the base tables above and a character
        // whose gear or level changed could otherwise come back with more health than it can hold.
        player.Health = Math.Min(character.Health, player.MaxHealth);

        if (character.Powers is not { } powers)
        {
            return;
        }

        for (byte power = 0; power < powers.Length; power++)
        {
            player.SetPower(power, Math.Min(powers[power], player.GetMaxPower(power)));
        }
    }

    /// <summary>
    /// The five attributes before anything is worn.
    /// </summary>
    /// <remarks>
    /// Kept separately because <c>UNIT_FIELD_STAT0</c> holds the <i>total</i>, and equipment is
    /// added to it. Without a base to rebuild from, taking off a +3 strength belt would have to
    /// subtract 3 — and any drift, from a level-up or a reload, would compound silently.
    /// </remarks>
    public PlayerBaseStats BaseStats { get; set; }

    /// <summary>
    /// Guids this player's client has been told about and has not been told to forget.
    /// </summary>
    /// <remarks>
    /// The server has to track this because the client cannot be asked. Sending a create for
    /// something already visible makes it flicker; forgetting to send a destroy leaves a ghost
    /// standing where a player used to be.
    /// </remarks>
    public HashSet<ObjectGuid> VisibleObjects { get; } = [];

    // ------------------------------------------------------------------ combat inputs
    //
    // A player's defences are on the character sheet, so they are read back out of the update fields
    // the client is already shown rather than recomputed. Whatever the tooltip says is what the
    // attack table rolls against, by construction — the two cannot disagree.

    /// <inheritdoc/>
    public override bool IsPlayerControlled => true;

    /// <summary>
    /// Base mana, from the character sheet rather than from the current maximum.
    /// </summary>
    /// <remarks>
    /// <c>UNIT_FIELD_BASE_MANA</c> is what a percentage-priced spell is a percentage of — the
    /// class's mana before any gear. Reading the current maximum instead makes those spells get more
    /// expensive as a character gears up, which is backwards.
    /// <para>
    /// Only mana has a base field; every other resource has a flat cap that gear does not move, so
    /// the maximum is the right answer for those.
    /// </para>
    /// </remarks>
    public override uint BasePowerFor(byte powerType) => powerType == PowerMana
        ? BaseMana
        : GetMaxPower(powerType);

    /// <summary>Experience accumulated towards the next level.</summary>
    /// <remarks>
    /// Reset to the <i>remainder</i> on levelling, not to zero — overshooting a level carries the
    /// surplus forward rather than throwing it away.
    /// </remarks>
    public uint Xp
    {
        get => Fields.GetUInt32(UpdateFields.PLAYER_XP);
        set => Fields.SetUInt32(UpdateFields.PLAYER_XP, value);
    }

    /// <summary>Experience needed to leave the current level, which the client draws the bar from.</summary>
    public uint NextLevelXp
    {
        get => Fields.GetUInt32(UpdateFields.PLAYER_NEXT_LEVEL_XP);
        set => Fields.SetUInt32(UpdateFields.PLAYER_NEXT_LEVEL_XP, value);
    }

    /// <summary>One of the five attributes: strength, agility, stamina, intellect, spirit.</summary>
    public uint GetStat(int index) => Fields.GetUInt32(UpdateFields.UNIT_FIELD_STAT0 + index);

    /// <inheritdoc cref="GetStat"/>
    public void SetStat(int index, uint value) => Fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT0 + index, value);

    // ------------------------------------------------------------------ death

    /// <summary>
    /// Whether the player is walking around as a spirit.
    /// </summary>
    /// <remarks>
    /// A client-visible flag, so the wisp the client draws and the server's idea of the state cannot
    /// drift. Distinct from being dead: a corpse has not released yet and cannot move.
    /// </remarks>
    public bool IsGhost
    {
        get => (PlayerFlags & PlayerDeath.PlayerFlagGhost) != 0;
        set => PlayerFlags = value
            ? PlayerFlags | PlayerDeath.PlayerFlagGhost
            : PlayerFlags & ~PlayerDeath.PlayerFlagGhost;
    }

    /// <summary><c>PLAYER_FLAGS</c> — ghost, resting, AFK and the rest.</summary>
    public uint PlayerFlags
    {
        get => Fields.GetUInt32(UpdateFields.PLAYER_FLAGS);
        set => Fields.SetUInt32(UpdateFields.PLAYER_FLAGS, value);
    }

    /// <summary>Milliseconds left before the client offers to release for you. Zero when alive.</summary>
    public int ReleaseTimerMs { get; set; }

    /// <summary>Where the body was left, for the corpse run. Null when there is no corpse.</summary>
    public uint? CorpseMapId { get; set; }

    /// <inheritdoc cref="CorpseMapId"/>
    public Position CorpsePosition { get; set; }

    /// <summary>
    /// When this character became a ghost, in unix seconds. The reclaim wait counts from here.
    /// </summary>
    /// <remarks>
    /// From the death, not from the release — a player who sits on the release screen for a minute
    /// has already served the wait, and restarting it there would punish reading the dialog.
    /// </remarks>
    public long GhostTime { get; set; }

    /// <summary>How long this particular death's reclaim wait is, in seconds.</summary>
    /// <remarks>
    /// Fixed at the moment of death rather than recomputed on reclaim. The penalty window is
    /// decaying the whole time, so recomputing would shrink a wait the player is halfway through.
    /// </remarks>
    public int ReclaimDelaySeconds { get; set; }

    /// <summary>
    /// When the escalating death penalty runs out, in unix seconds.
    /// </summary>
    /// <remarks>
    /// A window rather than a counter, which is what lets the penalty fade without anything having
    /// to remember to reset it.
    /// </remarks>
    public long DeathExpireTime { get; set; }

    /// <summary>
    /// Which side the character is on, from its race.
    /// </summary>
    /// <remarks>
    /// Stored rather than looked up, because a graveyard query happens at the moment of death and
    /// should not need the DBC stores threaded into it.
    /// </remarks>
    public bool IsAlliance { get; private init; }

    /// <summary>The class's mana before any gear. <c>UNIT_FIELD_BASE_MANA</c>.</summary>
    public uint BaseMana
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_BASE_MANA);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_BASE_MANA, value);
    }

    /// <summary>The cap for a resource that is not mana.</summary>
    /// <remarks>
    /// Rage and runic power are stored ten times what the client displays, so a full bar is 1000.
    /// Storing 100 gives a bar that fills after ten points.
    /// </remarks>
    private static uint MaxPowerFor(byte powerType) => powerType switch
    {
        PowerRage => 1000,
        PowerRunicPower => 1000,
        PowerEnergy => 100,
        PowerFocus => 100,
        _ => 0,
    };

    /// <inheritdoc/>
    /// <remarks>
    /// The skill for whatever is in the main hand, or Unarmed with nothing there.
    /// <para>
    /// <b>Falls back to the level cap when the skill is unknown</b>, rather than to zero. A player
    /// who has never been granted the skill is a gap in our data, not a character who cannot hold a
    /// sword — and zero here means missing every swing, which is a far worse failure than being
    /// slightly too good at it. The fallback stops mattering once every character is granted their
    /// starting skills; until then it keeps combat honest.
    /// </para>
    /// </remarks>
    public override int WeaponSkillValue
    {
        get
        {
            Item? weapon = Inventory.Equipped(InventorySlots.MainHand);

            uint skillId = weapon is null
                ? SkillType.Unarmed
                : SkillType.ForItem(weapon.Template.Class, weapon.Template.SubClass);

            return SkillOr(skillId, base.WeaponSkillValue);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Defence is one skill for everyone, unlike the weapon skills.</remarks>
    public override int DefenseSkillValue => SkillOr(SkillType.Defense, base.DefenseSkillValue);

    /// <summary>A skill's value, or a fallback when the character has not been granted it.</summary>
    private int SkillOr(uint skillId, int fallback) =>
        skillId != 0 && Skills.Has(skillId) ? Skills.Value(skillId) : fallback;

    /// <inheritdoc/>
    public override float DodgeChance => Fields.GetFloat(UpdateFields.PLAYER_DODGE_PERCENTAGE);

    /// <inheritdoc/>
    /// <remarks>
    /// Parry needs a weapon and a class that can do it; without equipment the sheet reads zero, which
    /// is the right answer rather than a missing one.
    /// </remarks>
    public override float ParryChance => Fields.GetFloat(UpdateFields.PLAYER_PARRY_PERCENTAGE);

    /// <inheritdoc/>
    /// <remarks>Zero without a shield, which is every player until equipment exists.</remarks>
    public override float BlockChance => Fields.GetFloat(UpdateFields.PLAYER_BLOCK_PERCENTAGE);

    /// <inheritdoc/>
    public override float CritChanceFor(WeaponAttackType attackType) => attackType switch
    {
        WeaponAttackType.OffAttack => Fields.GetFloat(UpdateFields.PLAYER_OFFHAND_CRIT_PERCENTAGE),
        WeaponAttackType.RangedAttack => Fields.GetFloat(UpdateFields.PLAYER_RANGED_CRIT_PERCENTAGE),
        _ => Fields.GetFloat(UpdateFields.PLAYER_CRIT_PERCENTAGE),
    };

    /// <summary>
    /// Builds a player from everything that describes it: the saved row, the race's client data,
    /// and the level's base stats.
    /// </summary>
    /// <remarks>
    /// The display id comes from <paramref name="race"/> and is the single field that decides
    /// whether the character renders at all — a zero there produces an invisible character with no
    /// error anywhere.
    /// </remarks>
    public static Player Create(
        CharacterSummary character,
        ChrRacesEntry race,
        ChrClassesEntry characterClass,
        PlayerBaseStats stats)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(race);
        ArgumentNullException.ThrowIfNull(characterClass);

        Player player = new(ObjectGuid.Create(HighGuid.Player, character.Id))
        {
            Name = character.Name,
            MapId = character.Map,
            ZoneId = character.Zone,
            Position = new Position(character.PositionX, character.PositionY, character.PositionZ, 0f),
            IsAlliance = race.IsAlliance,
            BaseStats = stats,
        };

        UpdateFieldStorage fields = player.Fields;

        // UNIT_FIELD_BYTES_0 packs four values into one slot: race, class, gender, power type.
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 0, character.Race);
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 1, character.Class);
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 2, character.Gender);
        fields.SetByte(UpdateFields.UNIT_FIELD_BYTES_0, 3, (byte)characterClass.PowerType);

        // Appearance, echoed straight back to the client that chose it.
        fields.SetByte(UpdateFields.PLAYER_BYTES, 0, character.Skin);
        fields.SetByte(UpdateFields.PLAYER_BYTES, 1, character.Face);
        fields.SetByte(UpdateFields.PLAYER_BYTES, 2, character.HairStyle);
        fields.SetByte(UpdateFields.PLAYER_BYTES, 3, character.HairColor);
        fields.SetByte(UpdateFields.PLAYER_BYTES_2, 0, character.FacialStyle);

        // Rest state 0x01 in the top byte — "normal", not resting.
        fields.SetByte(UpdateFields.PLAYER_BYTES_2, 3, 0x01);

        fields.SetUInt32(UpdateFields.UNIT_FIELD_LEVEL, character.Level);
        fields.SetUInt32(UpdateFields.PLAYER_XP, character.Experience);
        fields.SetUInt32(UpdateFields.PLAYER_FIELD_COINAGE, character.Money);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_FACTIONTEMPLATE, race.FactionId);

        uint displayId = character.Gender == 0 ? race.MaleDisplayId : race.FemaleDisplayId;
        fields.SetUInt32(UpdateFields.UNIT_FIELD_DISPLAYID, displayId);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_NATIVEDISPLAYID, displayId);

        fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, stats.MaxHealth);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_MAXHEALTH, stats.MaxHealth);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_BASE_HEALTH, stats.MaxHealth);

        // Power slot 0 is mana whatever the class's actual resource is; the client reads the one
        // named by the power type in UNIT_FIELD_BYTES_0.
        fields.SetUInt32(UpdateFields.UNIT_FIELD_POWER1, stats.MaxMana);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_MAXPOWER1, stats.MaxMana);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_BASE_MANA, stats.MaxMana);

        // A class whose resource is not mana needs its own slot filled too, or the client draws a
        // bar reading 0 / 0. Rage and runic power are stored ten times their displayed value, which
        // is why a full rage bar is 1000 rather than 100.
        byte powerType = (byte)characterClass.PowerType;

        if (powerType != PowerMana)
        {
            fields.SetUInt32(UpdateFields.UNIT_FIELD_MAXPOWER1 + powerType, MaxPowerFor(powerType));

            // Rage and runic power start empty and are earned; energy starts full.
            fields.SetUInt32(
                UpdateFields.UNIT_FIELD_POWER1 + powerType,
                powerType == PowerEnergy ? MaxPowerFor(powerType) : 0);
        }

        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT0, stats.Strength);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT1, stats.Agility);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT2, stats.Stamina);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT3, stats.Intellect);
        fields.SetUInt32(UpdateFields.UNIT_FIELD_STAT4, stats.Spirit);

        // Without a bounding radius and combat reach the client cannot work out where the character
        // physically is, and collision and targeting misbehave.
        fields.SetFloat(UpdateFields.UNIT_FIELD_BOUNDINGRADIUS, 0.389f);
        fields.SetFloat(UpdateFields.UNIT_FIELD_COMBATREACH, 1.5f);

        fields.SetFloat(UpdateFields.OBJECT_FIELD_SCALE_X, 1.0f);

        // Watched faction -1 means "none"; the client renders a reputation bar without it.
        fields.SetInt32(UpdateFields.PLAYER_FIELD_WATCHED_FACTION_INDEX, -1);

        // Last, because it reads the stats and level set above. Without it the character has no
        // weapon damage and no attack time — a swing every tick, for nothing, with no error.
        PlayerCombatStats.Apply(player);

        RestoreSavedState(player, character);

        player.SyncMovement();
        return player;
    }
}

/// <summary>
/// A character's base stats at its level, assembled from the two world tables that hold them.
/// </summary>
/// <remarks>
/// <c>player_levelstats</c> is per race, class and level; <c>player_classlevelstats</c> is per class
/// and level. Neither alone is enough, which is why they are combined before they reach the player.
/// </remarks>
public readonly record struct PlayerBaseStats(
    uint MaxHealth,
    uint MaxMana,
    uint Strength,
    uint Agility,
    uint Stamina,
    uint Intellect,
    uint Spirit);
