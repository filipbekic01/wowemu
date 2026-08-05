using WowEmu.Core;
using WowEmu.Game.Combat;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// Anything alive: it has a level, health, a faction and a model.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Unit</c> that Phase 6 needs. The layer exists because a player and a
/// creature are the same thing to the client — both occupy update-field indices 6 through 147, both
/// carry a movement block, and both are drawn from the same fields. Everything below
/// <c>UNIT_END</c> belongs here; anything a player alone has belongs in <see cref="Player"/>.
/// <para>
/// The properties are windows onto <see cref="GameObjectBase.Fields"/>, never a second copy. A value
/// stored anywhere else would not reach the client, and the two would drift the first time one was
/// written without the other.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Matches the base class's vocabulary.")]
public abstract class Unit(ObjectGuid guid, TypeId typeId, int fieldCount, uint typeMask)
    : WorldObject(guid, typeId, fieldCount, typeMask)
{
    /// <summary>Power types, from <c>ChrClasses.dbc</c> and <c>SharedDefines.h</c>.</summary>
    public const byte PowerMana = 0;
    public const byte PowerRage = 1;
    public const byte PowerFocus = 2;
    public const byte PowerEnergy = 3;
    public const byte PowerHappiness = 4;
    public const byte PowerRunicPower = 6;

    /// <summary>Zero for a creature, which has a class but no race.</summary>
    public byte Race => Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_0, 0);

    public byte Class => Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_0, 1);

    public byte Gender => Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_0, 2);

    /// <summary>Which of the power fields the client should read.</summary>
    public byte PowerType => Fields.GetByte(UpdateFields.UNIT_FIELD_BYTES_0, 3);

    public byte Level
    {
        get => (byte)Fields.GetUInt32(UpdateFields.UNIT_FIELD_LEVEL);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_LEVEL, value);
    }

    public uint Health
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_HEALTH);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_HEALTH, value);
    }

    public uint MaxHealth
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_MAXHEALTH);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_MAXHEALTH, value);
    }

    /// <summary>
    /// The first power slot.
    /// </summary>
    /// <remarks>
    /// Always slot 0 whatever the unit's actual resource is: the client reads the slot named by
    /// <see cref="PowerType"/>, and the seven slots exist so that a unit can hold several at once.
    /// </remarks>
    public uint Power
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_POWER1);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_POWER1, value);
    }

    public uint MaxPower
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_MAXPOWER1);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_MAXPOWER1, value);
    }

    /// <summary>Decides who this unit is hostile to, and what colour its nameplate is.</summary>
    public uint FactionTemplate
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_FACTIONTEMPLATE);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_FACTIONTEMPLATE, value);
    }

    /// <summary>
    /// The model the client draws. Zero means an invisible unit and no error anywhere.
    /// </summary>
    public uint DisplayId
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_DISPLAYID);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_DISPLAYID, value);
    }

    /// <summary>The model underneath any shapeshift or transform.</summary>
    public uint NativeDisplayId
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_NATIVEDISPLAYID);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_NATIVEDISPLAYID, value);
    }

    public uint UnitFlags
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_FLAGS);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_FLAGS, value);
    }

    public uint UnitFlags2
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_FLAGS_2);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_FLAGS_2, value);
    }

    /// <summary>What the unit offers: vendor, quest giver, flight master, and so on.</summary>
    public uint NpcFlags
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_NPC_FLAGS);
        set => Fields.SetUInt32(UpdateFields.UNIT_NPC_FLAGS, value);
    }

    public uint DynamicFlags
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_DYNAMIC_FLAGS);
        set => Fields.SetUInt32(UpdateFields.UNIT_DYNAMIC_FLAGS, value);
    }

    /// <summary>
    /// How wide the unit is, and how far it can reach.
    /// </summary>
    /// <remarks>
    /// Not cosmetic. These two are how the client decides where a unit physically is: a unit left at
    /// zero cannot be clicked, and melee cannot connect with it.
    /// </remarks>
    public float BoundingRadius
    {
        get => Fields.GetFloat(UpdateFields.UNIT_FIELD_BOUNDINGRADIUS);
        set => Fields.SetFloat(UpdateFields.UNIT_FIELD_BOUNDINGRADIUS, value);
    }

    /// <inheritdoc cref="BoundingRadius"/>
    public float CombatReach
    {
        get => Fields.GetFloat(UpdateFields.UNIT_FIELD_COMBATREACH);
        set => Fields.SetFloat(UpdateFields.UNIT_FIELD_COMBATREACH, value);
    }

    /// <summary>How large the model is drawn. 1.0 is the model's own size.</summary>
    public float ObjectScale
    {
        get => Fields.GetFloat(UpdateFields.OBJECT_FIELD_SCALE_X);
        set => Fields.SetFloat(UpdateFields.OBJECT_FIELD_SCALE_X, value);
    }

    // ------------------------------------------------------------------ combat

    /// <summary>Physical resistance. Slot 0 of the seven; the other six are magic schools.</summary>
    public uint Armor
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_RESISTANCES);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_RESISTANCES, value);
    }

    public uint AttackPower
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_ATTACK_POWER);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_ATTACK_POWER, value);
    }

    public uint RangedAttackPower
    {
        get => Fields.GetUInt32(UpdateFields.UNIT_FIELD_RANGED_ATTACK_POWER);
        set => Fields.SetUInt32(UpdateFields.UNIT_FIELD_RANGED_ATTACK_POWER, value);
    }

    /// <summary>The low end of a main-hand swing, after attack power.</summary>
    public float MinDamage
    {
        get => Fields.GetFloat(UpdateFields.UNIT_FIELD_MINDAMAGE);
        set => Fields.SetFloat(UpdateFields.UNIT_FIELD_MINDAMAGE, value);
    }

    /// <inheritdoc cref="MinDamage"/>
    public float MaxDamage
    {
        get => Fields.GetFloat(UpdateFields.UNIT_FIELD_MAXDAMAGE);
        set => Fields.SetFloat(UpdateFields.UNIT_FIELD_MAXDAMAGE, value);
    }

    /// <summary>
    /// Milliseconds between main-hand swings.
    /// </summary>
    /// <remarks>
    /// Two consecutive field slots hold the main hand and the off hand, which is why this is indexed
    /// rather than named per weapon.
    /// </remarks>
    public uint GetAttackTime(WeaponAttackType attackType) => attackType switch
    {
        WeaponAttackType.RangedAttack => Fields.GetUInt32(UpdateFields.UNIT_FIELD_RANGEDATTACKTIME),
        _ => Fields.GetUInt32(UpdateFields.UNIT_FIELD_BASEATTACKTIME + (int)attackType),
    };

    /// <inheritdoc cref="GetAttackTime"/>
    public void SetAttackTime(WeaponAttackType attackType, uint milliseconds)
    {
        if (attackType == WeaponAttackType.RangedAttack)
        {
            Fields.SetUInt32(UpdateFields.UNIT_FIELD_RANGEDATTACKTIME, milliseconds);
            return;
        }

        Fields.SetUInt32(UpdateFields.UNIT_FIELD_BASEATTACKTIME + (int)attackType, milliseconds);
    }

    /// <summary>What this unit is currently attacking or has selected. Empty for nothing.</summary>
    public ObjectGuid Target
    {
        get => Fields.GetGuid(UpdateFields.UNIT_FIELD_TARGET);
        set => Fields.SetGuid(UpdateFields.UNIT_FIELD_TARGET, value);
    }

    /// <summary>
    /// How far through dying this unit is.
    /// </summary>
    /// <remarks>
    /// Server-side. The client learns about death from health reaching zero and from the unit flags,
    /// not from this — it is the server's own state machine.
    /// </remarks>
    public DeathState DeathState { get; set; } = DeathState.Alive;

    /// <summary>Whether the unit can act and be attacked.</summary>
    public bool IsAlive => DeathState == DeathState.Alive;

    /// <summary>
    /// Whether the unit is in combat, which the client draws on the nameplate.
    /// </summary>
    /// <remarks>
    /// Held in <c>UNIT_FIELD_FLAGS</c> rather than beside it, because the client reads the flag and
    /// a separate server-side bool would drift from what the player sees.
    /// </remarks>
    public bool IsInCombat
    {
        get => (UnitFlags & (uint)Game.UnitFlags.InCombat) != 0;
        set => UnitFlags = value
            ? UnitFlags | (uint)Game.UnitFlags.InCombat
            : UnitFlags & ~(uint)Game.UnitFlags.InCombat;
    }

    // ------------------------------------------------------------------ combat inputs
    //
    // The attack table asks for these, and a player and a creature answer them differently: a
    // creature's are constants from its type and rank, a player's come from the character sheet. The
    // defaults here are the creature answers, because Unit is what a creature is.

    /// <summary>
    /// Whether a player is driving this unit, directly or through a pet.
    /// </summary>
    /// <remarks>
    /// Not the same as "is a player". Glancing blows and crushing blows both turn on this rather than
    /// on the type, so that a hunter's pet fights by the player's rules and a mind-controlled player
    /// fights by the creature's.
    /// </remarks>
    public virtual bool IsPlayerControlled => false;

    /// <summary>The skill the unit swings with. A creature's is always its level cap.</summary>
    public virtual int WeaponSkillValue => Level * 5;

    /// <summary>The skill the unit defends with. A creature's is always its level cap.</summary>
    public virtual int DefenseSkillValue => Level * 5;

    /// <summary>The unit's chance to dodge, as a percentage.</summary>
    public virtual float DodgeChance => MeleeChances.CreatureDodgeChance;

    /// <summary>The unit's chance to parry, as a percentage.</summary>
    public virtual float ParryChance => 0f;

    /// <summary>The unit's chance to block, as a percentage.</summary>
    public virtual float BlockChance => MeleeChances.CreatureBlockChance;

    /// <summary>How much a blocked hit is reduced by. Zero without a shield.</summary>
    public virtual uint ShieldBlockValue => 0;

    /// <summary>The unit's base crit for a weapon, before the skill difference is applied.</summary>
    public virtual float CritChanceFor(WeaponAttackType attackType) => MeleeChances.CreatureCritChance;

    /// <summary>Whether the general dodge rule applies to this unit. <c>flags_extra</c> can revoke it.</summary>
    public virtual bool CanDodge => true;

    /// <inheritdoc cref="CanDodge"/>
    public virtual bool CanParry => true;

    /// <inheritdoc cref="CanDodge"/>
    public virtual bool CanBlock => true;

    /// <inheritdoc cref="CanDodge"/>
    public virtual bool CanCrush => true;

    /// <inheritdoc cref="CanDodge"/>
    public virtual bool CanCrit => true;

    // ------------------------------------------------------------------ auto-attack

    /// <summary>Milliseconds until each weapon may swing again, indexed by <see cref="WeaponAttackType"/>.</summary>
    /// <remarks>
    /// Signed, and allowed to be negative between the tick that brings it below zero and the swing
    /// that resets it. Clamping to zero instead loses the overshoot, and a slow weapon then drifts
    /// later by up to a tick every swing.
    /// </remarks>
    private readonly int[] _attackTimers = new int[3];

    /// <summary>Who this unit is attacking, if anyone.</summary>
    /// <remarks>
    /// Distinct from <see cref="Target"/>, which is what the client draws as selected. A player can
    /// have something targeted without attacking it, and upstream keeps the two apart for exactly
    /// that reason.
    /// </remarks>
    public Unit? Victim { get; private set; }

    /// <summary>Whether the unit is swinging at <see cref="Victim"/>, as opposed to merely engaged.</summary>
    /// <remarks><c>UNIT_STATE_MELEE_ATTACKING</c>.</remarks>
    public bool IsMeleeAttacking { get; private set; }

    /// <summary>Milliseconds until <paramref name="attackType"/> may swing.</summary>
    public int GetAttackTimer(WeaponAttackType attackType) => _attackTimers[(int)attackType];

    /// <summary>Sets the milliseconds until <paramref name="attackType"/> may swing.</summary>
    public void SetAttackTimer(WeaponAttackType attackType, int milliseconds) =>
        _attackTimers[(int)attackType] = milliseconds;

    /// <summary>Whether a weapon is off cooldown.</summary>
    public bool IsAttackReady(WeaponAttackType attackType = WeaponAttackType.BaseAttack) =>
        _attackTimers[(int)attackType] <= 0;

    /// <summary>
    /// Starts the cooldown after a swing.
    /// </summary>
    /// <remarks>
    /// <c>min(timer + speed, speed)</c>, not simply <c>speed</c>. The two differ only when the timer
    /// is already negative — which is the normal case, because a tick overshoots — and taking the
    /// minimum is what carries that overshoot into the next swing instead of discarding it. Assigning
    /// the speed outright makes every weapon slower than its tooltip by half a tick on average.
    /// </remarks>
    public void ResetAttackTimer(WeaponAttackType attackType = WeaponAttackType.BaseAttack)
    {
        int speed = (int)GetAttackTime(attackType);

        _attackTimers[(int)attackType] = Math.Min(_attackTimers[(int)attackType] + speed, speed);
    }

    /// <summary>Counts every weapon's cooldown down by one tick.</summary>
    public void UpdateAttackTimers(uint diff)
    {
        for (int i = 0; i < _attackTimers.Length; i++)
        {
            if (_attackTimers[i] != 0)
            {
                _attackTimers[i] = _attackTimers[i] > 0 ? _attackTimers[i] - (int)diff : 0;
            }
        }
    }

    /// <summary>
    /// How close this unit has to be to swing at <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Both combat reaches plus 4/3 of a yard, floored at five. The floor is what lets two small
    /// creatures fight each other at all — their reaches alone add up to well under it.
    /// </remarks>
    public float MeleeRangeTo(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return MathF.Max(CombatReach + target.CombatReach + (4f / 3f), UnitDefaults.NominalMeleeRange);
    }

    /// <summary>Whether <paramref name="target"/> is close enough to swing at.</summary>
    /// <remarks>
    /// Measured in three dimensions and compared squared, so no square root is taken — this runs per
    /// weapon per attacking unit per tick.
    /// </remarks>
    public bool IsWithinMeleeRange(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (MapId != target.MapId)
        {
            return false;
        }

        float dx = Position.X - target.Position.X;
        float dy = Position.Y - target.Position.Y;
        float dz = Position.Z - target.Position.Z;

        float range = MeleeRangeTo(target);

        return (dx * dx) + (dy * dy) + (dz * dz) < range * range;
    }

    /// <summary>
    /// Starts attacking a victim.
    /// </summary>
    /// <remarks>
    /// Port of the parts of <c>Unit::Attack</c> that apply without auras, vehicles or pets. Returns
    /// false when nothing changed, which the caller uses to decide whether to tell the client —
    /// re-sending an attack start for a fight already in progress restarts the animation.
    /// </remarks>
    /// <returns>Whether the attack state changed.</returns>
    public bool Attack(Unit victim, bool meleeAttack = true)
    {
        ArgumentNullException.ThrowIfNull(victim);

        // Neither the dead nor the self-attacking.
        if (ReferenceEquals(victim, this) || !IsAlive() || !victim.IsAlive || MapId != victim.MapId)
        {
            return false;
        }

        if (Victim is not null)
        {
            if (ReferenceEquals(Victim, victim))
            {
                // Already fighting this one. The only thing that can change is whether the swing is
                // melee, which is how switching between a bow and a sword is expressed.
                if (meleeAttack == IsMeleeAttacking)
                {
                    return false;
                }

                IsMeleeAttacking = meleeAttack;
                return true;
            }

            // Switching victims mid-fight keeps the swing timers running, deliberately: swapping
            // targets would otherwise reset the cooldown and be a free way to cancel a slow swing.
            if (!meleeAttack)
            {
                IsMeleeAttacking = false;
            }
        }

        Victim = victim;
        Target = victim.Guid;

        if (meleeAttack)
        {
            IsMeleeAttacking = true;
        }

        return true;

        bool IsAlive() => DeathState == DeathState.Alive;
    }

    /// <summary>Stops attacking, if attacking anything.</summary>
    /// <returns>Whether there was an attack to stop.</returns>
    public bool AttackStop()
    {
        if (Victim is null)
        {
            return false;
        }

        Victim = null;
        IsMeleeAttacking = false;
        Target = ObjectGuid.Empty;

        return true;
    }

    /// <summary>Rolls a swing's damage between the unit's two bounds.</summary>
    /// <remarks>
    /// Inclusive of both ends, like upstream's <c>urand</c> — the attack table depends on that
    /// convention elsewhere, and mixing the two within one system is how off-by-one damage creeps in.
    /// </remarks>
    public uint RollSwingDamage(Func<uint, uint, uint> pick)
    {
        ArgumentNullException.ThrowIfNull(pick);

        uint low = (uint)MathF.Max(0f, MathF.Floor(MinDamage));
        uint high = (uint)MathF.Max(low, MathF.Floor(MaxDamage));

        return low == high ? low : pick(low, high);
    }

    /// <summary>
    /// Swings at a victim: rolls the damage, mitigates it, rolls the table, applies the outcome.
    /// </summary>
    /// <remarks>
    /// Port of <c>Unit::CalculateMeleeDamage</c>'s shape. The order is the part that matters and it
    /// is not the intuitive one — the raw hit is mitigated by armour <i>before</i> the attack table
    /// is rolled, so a crit doubles a post-armour number. Rolling the outcome first and mitigating
    /// the result reads more naturally and gives different numbers.
    /// </remarks>
    /// <param name="victim">Who is being hit.</param>
    /// <param name="attackType">Which weapon.</param>
    /// <param name="roll">The random source, for both the damage roll and the table roll.</param>
    /// <param name="attackerIsBehindVictim">Whether the swing comes from behind.</param>
    public MeleeDamageInfo CalculateMeleeDamage(
        Unit victim,
        WeaponAttackType attackType,
        Func<uint, uint, uint> roll,
        bool attackerIsBehindVictim = false)
    {
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(roll);

        uint raw = RollSwingDamage(roll);
        uint mitigated = ArmorMitigation.Reduce(raw, victim.Armor, Level);

        MeleeAttack attack = MeleeChances.For(this, victim, attackType, attackerIsBehindVictim);
        MeleeHitOutcome outcome = MeleeAttackTable.Roll(attack, roll);

        return MeleeDamage.Apply(
            outcome,
            mitigated,
            Level,
            victim.Level,
            victim.ShieldBlockValue,
            attackType == WeaponAttackType.OffAttack);
    }
}

/// <summary>How far through dying a unit is. <c>DeathState</c> in <c>Unit.h</c>.</summary>
/// <remarks>
/// Five states, not two. The gap between <see cref="JustDied"/> and <see cref="Corpse"/> is where
/// loot is assigned and experience awarded, and it exists precisely so those happen once.
/// </remarks>
public enum DeathState : byte
{
    Alive = 0,
    JustDied = 1,
    Corpse = 2,
    Dead = 3,
    JustRespawned = 4,
}

/// <summary>Unit flags, from <c>UnitDefines.h</c>. Only the ones this phase sets or reads.</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "UnitFlags is upstream's name for this enum.")]
public enum UnitFlags : uint
{
    None = 0x00000000,
    ServerControlled = 0x00000001,
    NonAttackable = 0x00000002,
    DisableMove = 0x00000004,
    PlayerControlled = 0x00000008,
    Rename = 0x00000010,
    NotAttackable1 = 0x00000080,
    ImmuneToPc = 0x00000100,
    ImmuneToNpc = 0x00000200,
    Looting = 0x00000400,
    PetInCombat = 0x00000800,
    Pvp = 0x00001000,
    Silenced = 0x00002000,
    Pacified = 0x00020000,
    Stunned = 0x00040000,
    InCombat = 0x00080000,
    Disarmed = 0x00200000,
    Confused = 0x00400000,
    Fleeing = 0x00800000,
    NotSelectable = 0x02000000,
    Skinnable = 0x04000000,
    Mount = 0x08000000,
}

/// <summary>Which weapon a swing comes from. <c>WeaponAttackType</c>.</summary>
public enum WeaponAttackType : byte
{
    BaseAttack = 0,
    OffAttack = 1,
    RangedAttack = 2,
}

/// <summary>Sizes every unit falls back to, from <c>ObjectDefines.h</c>.</summary>
public static class UnitDefaults
{
    /// <summary><c>DEFAULT_WORLD_OBJECT_SIZE</c> — a human's bounding radius.</summary>
    public const float WorldObjectSize = 0.388999998569489f;

    /// <summary><c>DEFAULT_COMBAT_REACH</c>.</summary>
    public const float CombatReach = 1.5f;

    /// <summary>Base walk speed, in yards per second. <c>creature_template.speed_walk</c> scales it.</summary>
    public const float BaseWalkSpeed = 2.5f;

    /// <summary>Base run speed, in yards per second. <c>creature_template.speed_run</c> scales it.</summary>
    public const float BaseRunSpeed = 7.0f;

    /// <summary>
    /// The shortest melee range any pair of units can have. <c>NOMINAL_MELEE_RANGE</c>.
    /// </summary>
    /// <remarks>
    /// A floor rather than a constant: two rabbits have combat reaches adding to well under five
    /// yards, and without the floor they could not reach each other at all.
    /// </remarks>
    public const float NominalMeleeRange = 5.0f;

    /// <summary>
    /// How long a swing waits so the client can draw it. <c>ATTACK_DISPLAY_DELAY</c>.
    /// </summary>
    /// <remarks>
    /// Stops a main-hand and an off-hand swing landing on the same tick, where the client would draw
    /// one animation and one number for two hits.
    /// </remarks>
    public const int AttackDisplayDelayMs = 200;

    /// <summary>How long a swing is deferred when the target is out of range or behind the attacker.</summary>
    /// <remarks>
    /// Short on purpose: it is a retry interval, not a cooldown. The swing has not happened, so the
    /// weapon's own timer must not be spent — the attacker just checks again shortly.
    /// </remarks>
    public const int SwingRetryDelayMs = 100;
}
