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

        writer.WriteUInt16((ushort)flags);

        movement.WriteTo(writer);
        speeds.WriteTo(writer);

        WriteLowGuid(writer, flags, objectGuid, typeId, isSelf);

        // A create block sends every non-zero field, not just the changed ones: the observer has no
        // previous copy to diff against.
        WriteValues(writer, fields, fields.BuildCreateMask());

        return writer.ToArray();
    }

    /// <summary>
    /// Builds a create block for an item or a bag.
    /// </summary>
    /// <remarks>
    /// The simplest block in the protocol: <c>LOWGUID</c> and nothing else. An item has no position
    /// and no movement, so none of the sections a creature's block carries appear — writing a
    /// movement block here shifts the field mask by 60-odd bytes and the client reads the whole
    /// item as noise.
    /// <para>
    /// <c>CreateObject</c>, not <c>CreateObject2</c>. Upstream promotes only players, corpses and
    /// dynamic objects.
    /// </para>
    /// </remarks>
    public static byte[] BuildItemCreateBlock(ObjectGuid objectGuid, TypeId typeId, UpdateFieldStorage fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        const UpdateFlag flags = UpdateFlag.LowGuid;

        PacketWriter writer = new(256);

        writer.WriteUInt8((byte)UpdateType.CreateObject);
        writer.WritePackedGuid(objectGuid);
        writer.WriteUInt8((byte)typeId);
        writer.WriteUInt16((ushort)flags);

        WriteLowGuid(writer, flags, objectGuid, typeId, isSelf: false);
        WriteValues(writer, fields, fields.BuildCreateMask());

        return writer.ToArray();
    }

    /// <summary>
    /// Builds a create block for a gameobject — something that stands still and has an orientation
    /// in three dimensions.
    /// </summary>
    /// <param name="objectGuid">The object's guid.</param>
    /// <param name="fields">The object's update fields.</param>
    /// <param name="position">Where it stands.</param>
    /// <param name="packedRotation">Its world rotation, packed. See <see cref="PackRotation"/>.</param>
    /// <remarks>
    /// Upstream gives a gameobject
    /// <c>LOWGUID | STATIONARY_POSITION | POSITION | ROTATION</c> in the <c>GameObject</c>
    /// constructor. Both position flags are set, and <b>only the <c>POSITION</c> branch runs</b> —
    /// <c>BuildMovementUpdate</c> tests them as if/else, so the stationary four floats are never
    /// written for a gameobject. Writing both is the obvious reading of the flags and produces a
    /// block sixteen bytes too long, which shifts the field mask and everything after it.
    /// </remarks>
    public static byte[] BuildGameObjectCreateBlock(
        ObjectGuid objectGuid,
        UpdateFieldStorage fields,
        Position position,
        ulong packedRotation)
    {
        ArgumentNullException.ThrowIfNull(fields);

        const UpdateFlag flags =
            UpdateFlag.LowGuid | UpdateFlag.StationaryPosition | UpdateFlag.Position | UpdateFlag.Rotation;

        PacketWriter writer = new(256);

        // CreateObject, not CreateObject2. Upstream promotes traps, duel arbiters and the two flag
        // types, and anything with an owner — none of which a plain world spawn is.
        writer.WriteUInt8((byte)UpdateType.CreateObject);
        writer.WritePackedGuid(objectGuid);
        writer.WriteUInt8((byte)TypeId.GameObject);

        writer.WriteUInt16((ushort)flags);

        WritePosition(writer, position);
        WriteLowGuid(writer, flags, objectGuid, TypeId.GameObject, isSelf: false);

        // Rotation comes last of all the optional sections, after transport and vehicle.
        writer.WriteUInt64(packedRotation);

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

    /// <summary>
    /// Packs a world rotation into the 64 bits the client expects.
    /// </summary>
    /// <remarks>
    /// Port of <c>GameObject::UpdatePackedRotation</c>: x, y and z are scaled, signed by w, masked,
    /// and packed 21 bits each with x in the top. The <paramref name="orientation"/> fallback is not
    /// a nicety — 15,478 of the 85,552 spawns carry an all-zero quaternion, and normalising that
    /// divides by zero and fills the field with NaN. This fork guards it explicitly, and so does
    /// this.
    /// </remarks>
    public static ulong PackRotation(float x, float y, float z, float w, float orientation)
    {
        const int PackYz = 1 << 20;
        const int PackX = PackYz << 1;
        const long PackYzMask = ((long)PackYz << 1) - 1;
        const long PackXMask = ((long)PackX << 1) - 1;

        double magnitude = Math.Sqrt((x * (double)x) + (y * (double)y) + (z * (double)z) + (w * (double)w));

        if (magnitude < 1e-6)
        {
            // A rotation about the vertical axis by the object's own facing, which is what a spawn
            // with no quaternion means.
            x = 0f;
            y = 0f;
            z = MathF.Sin(orientation / 2f);
            w = MathF.Cos(orientation / 2f);
            magnitude = 1.0;
        }

        double unitX = x / magnitude;
        double unitY = y / magnitude;
        double unitZ = z / magnitude;
        double unitW = w / magnitude;

        int sign = unitW >= 0.0 ? 1 : -1;

        long packedX = (int)(unitX * PackX) * sign & PackXMask;
        long packedY = (int)(unitY * PackYz) * sign & PackYzMask;
        long packedZ = (int)(unitZ * PackYz) * sign & PackYzMask;

        return (ulong)(packedZ | (packedY << 21) | (packedX << 42));
    }

    private static void WriteValues(PacketWriter writer, UpdateFieldStorage fields, UpdateMask mask)
    {
        mask.WriteTo(writer);
        fields.WriteSelected(writer, mask);
    }

    /// <summary>
    /// The <c>UPDATEFLAG_POSITION</c> branch: the position twice, then orientation, then a zero.
    /// </summary>
    /// <remarks>
    /// The repetition is not a mistake. The second triple is the transport-relative offset, and with
    /// no transport upstream writes the world position again rather than zeroes. The trailing float
    /// is the corpse orientation, and is zero for everything that is not a corpse.
    /// </remarks>
    private static void WritePosition(PacketWriter writer, Position position)
    {
        writer.WriteUInt8(0);            // no transport, so an empty packed guid

        writer.WriteSingle(position.X);
        writer.WriteSingle(position.Y);
        writer.WriteSingle(position.Z);

        writer.WriteSingle(position.X);
        writer.WriteSingle(position.Y);
        writer.WriteSingle(position.Z);

        writer.WriteSingle(position.Orientation);
        writer.WriteSingle(0f);
    }

    private static void WriteLowGuid(
        PacketWriter writer,
        UpdateFlag flags,
        ObjectGuid objectGuid,
        TypeId typeId,
        bool isSelf)
    {
        if (!flags.HasFlag(UpdateFlag.LowGuid))
        {
            return;
        }

        // Upstream's own comment admits the unit and player values are wrong, but the client accepts
        // them, so they are reproduced rather than "fixed". The object types send a real counter.
        writer.WriteUInt32(typeId switch
        {
            TypeId.Object or TypeId.Item or TypeId.Container
                or TypeId.GameObject or TypeId.DynamicObject or TypeId.Corpse => objectGuid.Counter,
            TypeId.Unit => 0x0000000B,
            TypeId.Player => isSelf ? 0x0000002Fu : 0x00000008u,
            _ => 0x00000000,
        });
    }
}
