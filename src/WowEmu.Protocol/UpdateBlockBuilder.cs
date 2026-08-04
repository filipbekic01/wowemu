using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>
/// Builds the individual blocks that make up <c>SMSG_UPDATE_OBJECT</c>.
/// </summary>
/// <remarks>
/// Port of <c>Object::BuildCreateUpdateBlockForPlayer</c>, <c>BuildValuesUpdate</c> and
/// <c>BuildMovementUpdate</c>.
/// <para>
/// A create block is: update type, packed guid, type id, movement data, then the field mask and the
/// values it selects. Nothing carries a length, so every section has to be exactly the right size —
/// the client reads positionally from start to end.
/// </para>
/// </remarks>
public static class UpdateBlockBuilder
{
    /// <summary>
    /// Builds a create block for a living object — anything with a movement block.
    /// </summary>
    /// <param name="objectGuid">The object's guid.</param>
    /// <param name="typeId">What kind of object this is.</param>
    /// <param name="fields">The object's update fields.</param>
    /// <param name="movement">Where it is and how it is moving.</param>
    /// <param name="speeds">Its nine movement speeds.</param>
    /// <param name="isSelf">True when building the observer's own character.</param>
    /// <remarks>
    /// Players use <see cref="UpdateType.CreateObject2"/> rather than
    /// <see cref="UpdateType.CreateObject"/> — upstream picks it for anything with a stationary
    /// position that is a player, corpse, dynamic object or pet. The client tracks the two
    /// differently and a player sent as plain <c>CreateObject</c> does not appear.
    /// </remarks>
    public static byte[] BuildCreateBlock(
        ObjectGuid objectGuid,
        TypeId typeId,
        UpdateFieldStorage fields,
        MovementInfo movement,
        MovementSpeeds speeds,
        bool isSelf)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(speeds);

        UpdateFlag flags = UpdateFlag.Living | UpdateFlag.StationaryPosition;

        if (isSelf)
        {
            flags |= UpdateFlag.Self;
        }

        UpdateType updateType = typeId is TypeId.Player or TypeId.Corpse or TypeId.DynamicObject
            ? UpdateType.CreateObject2
            : UpdateType.CreateObject;

        PacketWriter writer = new(512);

        writer.WriteUInt8((byte)updateType);
        writer.WritePackedGuid(objectGuid);
        writer.WriteUInt8((byte)typeId);

        WriteMovement(writer, flags, movement, speeds, typeId, isSelf);

        // A create block sends every non-zero field, not just the changed ones: the observer has no
        // previous copy to diff against.
        WriteValues(writer, fields, fields.BuildCreateMask());

        return writer.ToArray();
    }

    /// <summary>Builds a values block for an object the observer already has.</summary>
    public static byte[] BuildValuesBlock(ObjectGuid objectGuid, UpdateFieldStorage fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        PacketWriter writer = new(256);

        writer.WriteUInt8((byte)UpdateType.Values);
        writer.WritePackedGuid(objectGuid);

        WriteValues(writer, fields, fields.BuildDirtyMask());

        return writer.ToArray();
    }

    private static void WriteValues(PacketWriter writer, UpdateFieldStorage fields, UpdateMask mask)
    {
        mask.WriteTo(writer);
        fields.WriteSelected(writer, mask);
    }

    private static void WriteMovement(
        PacketWriter writer,
        UpdateFlag flags,
        MovementInfo movement,
        MovementSpeeds speeds,
        TypeId typeId,
        bool isSelf)
    {
        writer.WriteUInt16((ushort)flags);

        if (flags.HasFlag(UpdateFlag.Living))
        {
            movement.WriteTo(writer);
            speeds.WriteTo(writer);
        }
        else if (flags.HasFlag(UpdateFlag.StationaryPosition))
        {
            // Only reached for objects that cannot move; a living object's position already went
            // out inside the movement block.
            writer.WriteSingle(movement.Position.X);
            writer.WriteSingle(movement.Position.Y);
            writer.WriteSingle(movement.Position.Z);
            writer.WriteSingle(movement.Position.Orientation);
        }

        if (flags.HasFlag(UpdateFlag.LowGuid))
        {
            // Upstream's own comment admits the values here are wrong for units and players, but
            // the client accepts them, so they are reproduced rather than "fixed".
            writer.WriteUInt32(typeId switch
            {
                TypeId.Unit => 0x0000000B,
                TypeId.Player => isSelf ? 0x0000002Fu : 0x00000008u,
                _ => 0x00000000,
            });
        }
    }
}
