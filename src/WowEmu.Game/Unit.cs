using WowEmu.Core;
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
}
