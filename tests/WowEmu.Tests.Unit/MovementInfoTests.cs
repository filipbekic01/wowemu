using WowEmu.Core;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The movement block, in both directions.
/// </summary>
/// <remarks>
/// The reader and writer must stay exact mirrors: the same layout appears in the create block the
/// server sends and in all 27 movement opcodes the client sends. A field read in the wrong order
/// does not fail — it reinterprets everything after it, so a position silently becomes a fall time.
/// </remarks>
public sealed class MovementInfoTests
{
    [Fact]
    public void PlainMovement_RoundTrips()
    {
        MovementInfo original = new()
        {
            Flags = MovementFlag.Forward,
            ExtraFlags = 0,
            Time = 123456,
            Position = new Position(-8949.95f, -132.493f, 83.5312f, 1.25f),
            FallTime = 42,
        };

        MovementInfo parsed = RoundTrip(original);

        Assert.Equal(MovementFlag.Forward, parsed.Flags);
        Assert.Equal(123456u, parsed.Time);
        Assert.Equal(-8949.95f, parsed.Position.X, 0.001f);
        Assert.Equal(-132.493f, parsed.Position.Y, 0.001f);
        Assert.Equal(83.5312f, parsed.Position.Z, 0.001f);
        Assert.Equal(1.25f, parsed.Position.Orientation, 0.001f);
        Assert.Equal(42u, parsed.FallTime);
    }

    /// <summary>Falling adds four fields. Reading them when they are absent eats the next packet.</summary>
    [Fact]
    public void Falling_RoundTripsItsFourExtraFields()
    {
        MovementInfo original = new()
        {
            Flags = MovementFlag.Falling,
            Position = new Position(1, 2, 3, 0),
            FallTime = 900,
            JumpVerticalSpeed = -7.5f,
            JumpSinAngle = 0.5f,
            JumpCosAngle = 0.86f,
            JumpHorizontalSpeed = 4.2f,
        };

        MovementInfo parsed = RoundTrip(original);

        Assert.Equal(-7.5f, parsed.JumpVerticalSpeed, 0.001f);
        Assert.Equal(0.5f, parsed.JumpSinAngle, 0.001f);
        Assert.Equal(0.86f, parsed.JumpCosAngle, 0.001f);
        Assert.Equal(4.2f, parsed.JumpHorizontalSpeed, 0.001f);
    }

    /// <summary>Swimming and flying each add a pitch field — the same one, not two.</summary>
    [Theory]
    [InlineData(MovementFlag.Swimming)]
    [InlineData(MovementFlag.Flying)]
    public void PitchIsPresent_WhileSwimmingOrFlying(MovementFlag flag)
    {
        MovementInfo original = new()
        {
            Flags = flag,
            Position = new Position(1, 2, 3, 0),
            Pitch = -0.75f,
        };

        Assert.Equal(-0.75f, RoundTrip(original).Pitch, 0.001f);
    }

    [Fact]
    public void SplineElevation_RoundTrips()
    {
        MovementInfo original = new()
        {
            Flags = MovementFlag.SplineElevation,
            Position = new Position(1, 2, 3, 0),
            SplineElevation = 12.5f,
        };

        Assert.Equal(12.5f, RoundTrip(original).SplineElevation, 0.001f);
    }

    [Fact]
    public void EveryOptionalSection_ChangesTheBlockLength()
    {
        int plain = Write(new MovementInfo { Position = new Position(1, 2, 3, 0) }).Length;
        int falling = Write(new MovementInfo { Flags = MovementFlag.Falling }).Length;
        int swimming = Write(new MovementInfo { Flags = MovementFlag.Swimming }).Length;

        Assert.Equal(plain + 16, falling);    // four floats
        Assert.Equal(plain + 4, swimming);    // one float
    }

    /// <summary>
    /// Transports are not implemented, and guessing at the block's length would desynchronise
    /// everything after it — so both directions refuse rather than improvise.
    /// </summary>
    [Fact]
    public void Transport_IsRefusedRatherThanGuessed()
    {
        MovementInfo onTransport = new() { Flags = MovementFlag.OnTransport };

        Assert.Throws<NotSupportedException>(() => Write(onTransport));

        // A client claiming to be on a transport is rejected, not half-parsed.
        PacketWriter writer = new();
        writer.WriteUInt32((uint)MovementFlag.OnTransport);
        writer.WriteUInt16(0);
        writer.WriteUInt32(0);
        writer.WriteSingle(1);
        writer.WriteSingle(2);
        writer.WriteSingle(3);
        writer.WriteSingle(0);

        PacketReader reader = new(writer.WrittenSpan);
        MovementInfo parsed = new();

        Assert.False(parsed.TryReadFrom(ref reader));
    }

    [Fact]
    public void TruncatedBlock_IsRejected()
    {
        PacketReader reader = new([0x01, 0x02, 0x03]);
        MovementInfo parsed = new();

        Assert.False(parsed.TryReadFrom(ref reader));
    }

    [Fact]
    public void Speeds_AreWrittenInTheClientsOrder()
    {
        MovementSpeeds speeds = new();
        PacketWriter writer = new();
        speeds.WriteTo(writer);

        PacketReader reader = new(writer.WrittenSpan);

        // Walk then run: the second value is the one that decides whether the character can move.
        Assert.True(reader.TryReadSingle(out float walk));
        Assert.Equal(2.5f, walk, 0.001f);

        Assert.True(reader.TryReadSingle(out float run));
        Assert.Equal(7.0f, run, 0.001f);

        // Nine in total.
        for (int i = 0; i < 7; i++)
        {
            Assert.True(reader.TryReadSingle(out _));
        }

        Assert.Equal(0, reader.Remaining);
    }

    private static byte[] Write(MovementInfo movement)
    {
        PacketWriter writer = new();
        movement.WriteTo(writer);
        return writer.ToArray();
    }

    private static MovementInfo RoundTrip(MovementInfo original)
    {
        byte[] bytes = Write(original);

        PacketReader reader = new(bytes);
        MovementInfo parsed = new();

        Assert.True(parsed.TryReadFrom(ref reader));
        Assert.Equal(0, reader.Remaining);

        return parsed;
    }
}
