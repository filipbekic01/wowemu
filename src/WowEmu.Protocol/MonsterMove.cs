using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>How a moving object should face when it arrives.</summary>
/// <remarks>From <c>MovementPacketBuilder.cpp</c>. Only <see cref="Normal"/> and <see cref="Stop"/>
/// are produced yet — the three facing forms need a target or an angle to face, which nothing
/// computes.</remarks>
public enum MonsterMoveType : byte
{
    Normal = 0,
    Stop = 1,
    FacingSpot = 2,
    FacingTarget = 3,
    FacingAngle = 4,
}

/// <summary>Spline flags, from <c>MoveSplineFlag.h</c>. Only the ones this phase can produce.</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "MoveSplineFlag is upstream's name for this enum; renaming it would break the trail back to the C++.")]
public enum MoveSplineFlag : uint
{
    None = 0x00000000,
    Done = 0x00000100,
    Falling = 0x00000200,
    NoSpline = 0x00000400,
    Parabolic = 0x00000800,
    CanSwim = 0x00001000,

    /// <summary>Smooth Catmull-Rom interpolation and a flying animation.</summary>
    Flying = 0x00002000,

    OrientationFixed = 0x00004000,
    FinalPoint = 0x00008000,
    FinalTarget = 0x00010000,
    FinalAngle = 0x00020000,
    Catmullrom = 0x00040000,
    Cyclic = 0x00080000,
    EnterCycle = 0x00100000,
    Animation = 0x00200000,

    /// <summary>Never arrives.</summary>
    Frozen = 0x00400000,

    /// <summary>Facing flags, animation ids and <see cref="Done"/> are stripped before sending.</summary>
    MaskNoMonsterMove = FinalPoint | FinalTarget | FinalAngle | 0xFF | Done,
}

/// <summary>
/// Writes <c>SMSG_MONSTER_MOVE</c>.
/// </summary>
/// <remarks>
/// Port of <c>PacketBuilder::WriteMonsterMove</c> and <c>WriteCommonMonsterMovePart</c>, for the one
/// shape this phase produces: a straight line from where the creature is to where it is going.
/// <para>
/// <b>What is not here.</b> Upstream's spline system carries Catmull-Rom paths, cyclic routes,
/// parabolic arcs, facing targets and animation ids. None of it is needed to make a creature wander,
/// and all of it changes the encoding — see <see cref="WriteLinearPath"/> for the one place where
/// the difference is not obvious.
/// </para>
/// </remarks>
public static class MonsterMove
{
    /// <summary>
    /// Writes a straight-line move.
    /// </summary>
    /// <param name="writer">Where to write.</param>
    /// <param name="mover">Whose movement this is.</param>
    /// <param name="start">Where the move begins. Must be where the client thinks the object is.</param>
    /// <param name="destination">Where it ends.</param>
    /// <param name="splineId">A number that increases per move, so the client can tell them apart.</param>
    /// <param name="durationMs">How long the whole move takes. The client interpolates against it.</param>
    /// <param name="flags">Spline flags. <see cref="MoveSplineFlag.None"/> for a walk on the ground.</param>
    public static void Write(
        PacketWriter writer,
        ObjectGuid mover,
        Position start,
        Position destination,
        uint splineId,
        uint durationMs,
        MoveSplineFlag flags = MoveSplineFlag.None)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePackedGuid(mover);

        WriteCommonPart(writer, start, splineId, durationMs, flags);
        WriteLinearPath(writer, destination);
    }

    /// <summary>
    /// Writes a move that stops the object where it stands.
    /// </summary>
    /// <remarks>
    /// Port of <c>WriteStopMovement</c>. Shorter than a move: it ends at the type byte, with no
    /// flags, duration or path. A client told to stop with a full move body reads the flags as a
    /// path and puts the creature somewhere impossible.
    /// </remarks>
    public static void WriteStop(PacketWriter writer, ObjectGuid mover, Position at, uint splineId)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePackedGuid(mover);

        writer.WriteUInt8(0);
        writer.WriteSingle(at.X);
        writer.WriteSingle(at.Y);
        writer.WriteSingle(at.Z);
        writer.WriteUInt32(splineId);
        writer.WriteUInt8((byte)MonsterMoveType.Stop);
    }

    private static void WriteCommonPart(
        PacketWriter writer,
        Position start,
        uint splineId,
        uint durationMs,
        MoveSplineFlag flags)
    {
        // Upstream's comment calls this "sets/unsets MOVEMENTFLAG2_UNK7"; it is always zero.
        writer.WriteUInt8(0);

        // The point the client moves *from*. Upstream takes it from the spline's first index, which
        // MoveSplineInit::Launch has already overwritten with the unit's real current position —
        // sending anything else makes the creature snap before it walks.
        writer.WriteSingle(start.X);
        writer.WriteSingle(start.Y);
        writer.WriteSingle(start.Z);

        writer.WriteUInt32(splineId);

        writer.WriteUInt8((byte)MonsterMoveType.Normal);

        // Facing flags, animation ids and the done bit never go on the wire.
        writer.WriteUInt32((uint)flags & ~(uint)MoveSplineFlag.MaskNoMonsterMove);

        writer.WriteUInt32(durationMs);
    }

    /// <summary>
    /// Writes the path of a straight-line move: a count of one, then the destination.
    /// </summary>
    /// <remarks>
    /// The count is not the number of points. Upstream computes it as
    /// <c>spline.getPointCount() - 3</c>, and the spline for a two-point move holds four points —
    /// because <b>linear mode is initialised with the Catmull-Rom initialiser</b>, which pads a
    /// virtual point at each end. So a move from A to B writes 1, not 2.
    /// <para>
    /// Getting that number from the obvious place — the number of points the caller supplied — gives
    /// 2, and the client then reads the next twelve bytes of the packet as a second point.
    /// </para>
    /// </remarks>
    private static void WriteLinearPath(PacketWriter writer, Position destination)
    {
        writer.WriteUInt32(1);

        writer.WriteSingle(destination.X);
        writer.WriteSingle(destination.Y);
        writer.WriteSingle(destination.Z);
    }
}
