using WowEmu.Core;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// Anything the client can be told about: it has a guid, update fields, and a kind.
/// </summary>
/// <remarks>
/// Port of <c>Object</c>. Deliberately thin — the hierarchy earns its layers as phases add
/// behaviour, and an <c>Item</c> is an <see cref="GameObjectBase"/> but never a
/// <see cref="WorldObject"/>, because it has no position.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Guid and Object are the client's own vocabulary for these; renaming them would obscure the port.")]
public abstract class GameObjectBase
{
    protected GameObjectBase(ObjectGuid guid, TypeId typeId, int fieldCount, uint typeMask)
    {
        Guid = guid;
        TypeId = typeId;
        Fields = new UpdateFieldStorage(fieldCount);

        Fields.SetGuid(UpdateFields.OBJECT_FIELD_GUID, guid);

        // The type mask is cumulative: a player sets the object, unit and player bits, and the
        // client uses it to decide which parts of the field block to interpret.
        Fields.SetUInt32(UpdateFields.OBJECT_FIELD_TYPE, typeMask);
        Fields.SetFloat(UpdateFields.OBJECT_FIELD_SCALE_X, 1.0f);
    }

    public ObjectGuid Guid { get; }

    public TypeId TypeId { get; }

    /// <summary>The client-visible state.</summary>
    public UpdateFieldStorage Fields { get; }
}

/// <summary>Type mask bits. An object sets one bit per level of the hierarchy it occupies.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "TYPEMASK_OBJECT is the client's name for the base type bit.")]
public static class TypeMask
{
    public const uint Object = 0x0001;
    public const uint Item = 0x0002;
    public const uint Container = 0x0004;
    public const uint Unit = 0x0008;
    public const uint Player = 0x0010;
    public const uint GameObject = 0x0020;
    public const uint DynamicObject = 0x0040;
    public const uint Corpse = 0x0080;

    /// <summary>What a player reports: object + unit + player.</summary>
    public const uint PlayerObject = Object | Unit | Player;

    /// <summary>What a creature reports: object + unit. There is no separate creature bit.</summary>
    public const uint CreatureObject = Object | Unit;
}

/// <summary>An object that exists somewhere on a map.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Matches the base class's vocabulary.")]
public abstract class WorldObject(ObjectGuid guid, TypeId typeId, int fieldCount, uint typeMask)
    : GameObjectBase(guid, typeId, fieldCount, typeMask)
{
    /// <summary>
    /// What the client and the logs call it.
    /// </summary>
    /// <remarks>
    /// On <c>WorldObject</c> rather than on each subclass because upstream puts it here too, and
    /// because everything that reports on an object — visibility logs, movement rejections — wants a
    /// name without caring what kind of object it has.
    /// </remarks>
    public string Name { get; protected set; } = string.Empty;

    /// <summary>Where it is, and which way it faces.</summary>
    public Position Position { get; set; }

    /// <summary>Which map it is on.</summary>
    public uint MapId { get; set; }

    /// <summary>Which zone, for the client's location display.</summary>
    public uint ZoneId { get; set; }

    /// <summary>Which cell the map currently has this object filed under.</summary>
    /// <remarks>
    /// Owned by the map, not by the object: it is the map's index into its own cell table, and an
    /// object that changes it without telling the map disappears from every range query.
    /// </remarks>
    public Maps.CellCoord Cell { get; set; }

    /// <summary>Movement state, sent in every create block.</summary>
    public MovementInfo Movement { get; } = new();

    /// <summary>The nine movement speeds.</summary>
    public MovementSpeeds Speeds { get; } = new();

    /// <summary>Copies the authoritative position into the movement block before serializing.</summary>
    public void SyncMovement()
    {
        Movement.Position = Position;
        Movement.Time = MsTime.Now;
    }
}
