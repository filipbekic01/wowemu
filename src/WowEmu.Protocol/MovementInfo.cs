using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>
/// Movement state flags. Only the ones that change the wire layout are named so far.
/// </summary>
/// <remarks>
/// Several of these add fields to the movement block, so they are part of the packet's shape rather
/// than decoration — see <see cref="MovementInfo.WriteTo"/>.
/// </remarks>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "These are the client's own movement flags; the protocol calls them flags and so does every capture.")]
public enum MovementFlag : uint
{
    None = 0x00000000,
    Forward = 0x00000001,
    Backward = 0x00000002,
    StrafeLeft = 0x00000004,
    StrafeRight = 0x00000008,
    TurnLeft = 0x00000010,
    TurnRight = 0x00000020,

    /// <summary>Adds four jump fields to the movement block.</summary>
    Falling = 0x00001000,

    /// <summary>Adds the pitch field.</summary>
    Swimming = 0x00200000,

    /// <summary>Adds a transport block.</summary>
    OnTransport = 0x00000200,

    /// <summary>Also adds the pitch field.</summary>
    Flying = 0x02000000,

    /// <summary>Adds the spline elevation field.</summary>
    SplineElevation = 0x04000000,

    /// <summary>Adds a whole spline block.</summary>
    SplineEnabled = 0x08000000,
}

/// <summary>
/// Where an object is and how it is moving.
/// </summary>
/// <remarks>
/// Port of <c>MovementInfo</c> and <c>Unit::BuildMovementPacket</c>. This exact byte layout appears
/// in the create block and in all 27 client movement opcodes, so it is written once here.
/// <para>
/// The optional sections are keyed off <see cref="Flags"/>. A flag set without its payload — or a
/// payload written without its flag — shifts everything after it, and the client's reaction is a
/// disconnect rather than an error message.
/// </para>
/// </remarks>
public sealed class MovementInfo
{
    public MovementFlag Flags { get; set; }

    /// <summary>Secondary flags, 2.3.0 and later.</summary>
    public ushort ExtraFlags { get; set; }

    /// <summary>Client time in milliseconds. Echoed back rather than interpreted.</summary>
    public uint Time { get; set; }

    public Position Position { get; set; }

    /// <summary>Pitch, present only while swimming or flying.</summary>
    public float Pitch { get; set; }

    /// <summary>Milliseconds since the fall started. Always present.</summary>
    public uint FallTime { get; set; }

    public float JumpVerticalSpeed { get; set; }

    public float JumpSinAngle { get; set; }

    public float JumpCosAngle { get; set; }

    public float JumpHorizontalSpeed { get; set; }

    public float SplineElevation { get; set; }

    /// <summary>
    /// Reads a movement block sent by the client.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="WriteTo"/>, and it has to stay one: the client sends this same
    /// layout in all 27 movement opcodes. A field read in the wrong order does not fail — it
    /// silently reinterprets the rest of the packet, so a position ends up as a fall time.
    /// <para>
    /// Everything here is attacker-controlled. Nothing is trusted beyond being well-formed; the
    /// plausibility checks (speed, teleport distance) belong in the handler, not the parser.
    /// </para>
    /// </remarks>
    public bool TryReadFrom(ref PacketReader reader)
    {
        if (!reader.TryReadUInt32(out uint flags) ||
            !reader.TryReadUInt16(out ushort extraFlags) ||
            !reader.TryReadUInt32(out uint time) ||
            !reader.TryReadSingle(out float x) ||
            !reader.TryReadSingle(out float y) ||
            !reader.TryReadSingle(out float z) ||
            !reader.TryReadSingle(out float orientation))
        {
            return false;
        }

        Flags = (MovementFlag)flags;
        ExtraFlags = extraFlags;
        Time = time;
        Position = new Position(x, y, z, orientation);

        if (Flags.HasFlag(MovementFlag.OnTransport))
        {
            // Transports are not implemented, and guessing at the block's length would
            // desynchronise everything after it.
            return false;
        }

        if (Flags.HasFlag(MovementFlag.Swimming) || Flags.HasFlag(MovementFlag.Flying))
        {
            if (!reader.TryReadSingle(out float pitch))
            {
                return false;
            }

            Pitch = pitch;
        }

        if (!reader.TryReadUInt32(out uint fallTime))
        {
            return false;
        }

        FallTime = fallTime;

        if (Flags.HasFlag(MovementFlag.Falling))
        {
            if (!reader.TryReadSingle(out float zSpeed) ||
                !reader.TryReadSingle(out float sinAngle) ||
                !reader.TryReadSingle(out float cosAngle) ||
                !reader.TryReadSingle(out float xySpeed))
            {
                return false;
            }

            JumpVerticalSpeed = zSpeed;
            JumpSinAngle = sinAngle;
            JumpCosAngle = cosAngle;
            JumpHorizontalSpeed = xySpeed;
        }

        if (Flags.HasFlag(MovementFlag.SplineElevation))
        {
            if (!reader.TryReadSingle(out float elevation))
            {
                return false;
            }

            SplineElevation = elevation;
        }

        return reader.Ok;
    }

    /// <summary>
    /// Writes the movement block.
    /// </summary>
    /// <remarks>
    /// The transport branch is not implemented: nothing rides a boat until Phase 6 at the earliest,
    /// and writing a half-correct transport block is worse than refusing to write one.
    /// </remarks>
    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (Flags.HasFlag(MovementFlag.OnTransport))
        {
            throw new NotSupportedException("Transport movement is not implemented yet (Phase 6).");
        }

        writer.WriteUInt32((uint)Flags);
        writer.WriteUInt16(ExtraFlags);
        writer.WriteUInt32(Time);

        writer.WriteSingle(Position.X);
        writer.WriteSingle(Position.Y);
        writer.WriteSingle(Position.Z);
        writer.WriteSingle(Position.Orientation);

        if (Flags.HasFlag(MovementFlag.Swimming) || Flags.HasFlag(MovementFlag.Flying))
        {
            writer.WriteSingle(Pitch);
        }

        writer.WriteUInt32(FallTime);

        if (Flags.HasFlag(MovementFlag.Falling))
        {
            writer.WriteSingle(JumpVerticalSpeed);
            writer.WriteSingle(JumpSinAngle);
            writer.WriteSingle(JumpCosAngle);
            writer.WriteSingle(JumpHorizontalSpeed);
        }

        if (Flags.HasFlag(MovementFlag.SplineElevation))
        {
            writer.WriteSingle(SplineElevation);
        }
    }
}

/// <summary>
/// The nine movement speeds, in the order the create block expects them.
/// </summary>
/// <remarks>
/// These are absolute values, not multipliers — the client uses them directly, so the defaults are
/// retail's base rates. Send zero for run speed and the character cannot move.
/// </remarks>
public sealed class MovementSpeeds
{
    public float Walk { get; set; } = 2.5f;

    public float Run { get; set; } = 7.0f;

    public float RunBack { get; set; } = 4.5f;

    public float Swim { get; set; } = 4.722222f;

    public float SwimBack { get; set; } = 2.5f;

    public float Flight { get; set; } = 7.0f;

    public float FlightBack { get; set; } = 4.5f;

    /// <summary>Radians per second. Not a speed like the others.</summary>
    public float TurnRate { get; set; } = 3.141594f;

    public float PitchRate { get; set; } = 3.141594f;

    public void WriteTo(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteSingle(Walk);
        writer.WriteSingle(Run);
        writer.WriteSingle(RunBack);
        writer.WriteSingle(Swim);
        writer.WriteSingle(SwimBack);
        writer.WriteSingle(Flight);
        writer.WriteSingle(FlightBack);
        writer.WriteSingle(TurnRate);
        writer.WriteSingle(PitchRate);
    }
}
