using WowEmu.Core;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The three packed wire encodings: GUID, XYZ and calendar time. Packed-GUID round-tripping is a
/// Phase 0 exit criterion in PLAN.md §6.
/// </summary>
public sealed class PackedEncodingTests
{
    /// <summary>
    /// The mask byte's bit <c>i</c> says "byte <c>i</c> of the guid is present". Zero bytes are
    /// dropped, which is where the saving comes from.
    /// </summary>
    [Fact]
    public void PackedGuid_WritesMaskThenNonZeroBytesOnly()
    {
        PacketWriter writer = new();
        writer.WritePackedGuid(0x0000_0000_0000_00F1ul);

        Assert.Equal([0b0000_0001, 0xF1], writer.ToArray());
    }

    [Fact]
    public void PackedGuid_SkipsInteriorZeroBytes()
    {
        PacketWriter writer = new();
        writer.WritePackedGuid(0xF130_0000_0000_0001ul);

        // Bytes 0 (0x01), 6 (0x30) and 7 (0xF1) are non-zero; the four in between are dropped.
        Assert.Equal([0b1100_0001, 0x01, 0x30, 0xF1], writer.ToArray());
    }

    [Fact]
    public void PackedGuid_Empty_IsASingleZeroByte()
    {
        PacketWriter writer = new();
        writer.WritePackedGuid(ObjectGuid.Empty);

        Assert.Equal([0x00], writer.ToArray());
    }

    [Fact]
    public void PackedGuid_AllBytesSet_IsMaskPlusEight()
    {
        PacketWriter writer = new();
        writer.WritePackedGuid(0xFFFF_FFFF_FFFF_FFFFul);

        Assert.Equal(9, writer.Length);
        Assert.Equal(0xFF, writer.ToArray()[0]);
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(0x00FFul)]
    [InlineData(0xFF00ul)]
    [InlineData(0xF130_0000_012B_0303ul)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFul)]
    [InlineData(0x0102_0304_0506_0708ul)]
    public void PackedGuid_RoundTrips(ulong value)
    {
        PacketWriter writer = new();
        writer.WritePackedGuid(value);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadPackedGuid(out ulong read));
        Assert.Equal(value, read);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void PackedGuid_RoundTripsAsObjectGuid()
    {
        ObjectGuid guid = ObjectGuid.Create(HighGuid.Unit, entry: 299, counter: 12345);

        PacketWriter writer = new();
        writer.WritePackedGuid(guid);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadPackedGuid(out ObjectGuid read));
        Assert.Equal(guid, read);
    }

    [Fact]
    public void PackedGuid_TruncatedPayload_FailsWithoutThrowing()
    {
        // Mask claims two bytes follow; only one does.
        PacketReader reader = new([0b0000_0011, 0x01]);

        Assert.False(reader.TryReadPackedGuid(out ulong value));
        Assert.Equal(0ul, value);
    }

    /// <summary>
    /// X and Y get 11 bits, Z gets 10 — the asymmetry is easy to miss and shifts everything if
    /// mirrored wrongly.
    /// </summary>
    [Fact]
    public void PackedXYZ_PacksElevenElevenAndTenBits()
    {
        PacketWriter writer = new();
        writer.WritePackedXYZ(1f, 2f, 3f);

        PacketReader reader = new(writer.WrittenSpan);
        Assert.True(reader.TryReadUInt32(out uint packed));

        Assert.Equal(4u, packed & 0x7FF);               // 1 / 0.25
        Assert.Equal(8u, (packed >> 11) & 0x7FF);       // 2 / 0.25
        Assert.Equal(12u, (packed >> 22) & 0x3FF);      // 3 / 0.25
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(1f, 2f, 3f)]
    [InlineData(-1f, -2f, -3f)]
    [InlineData(255.75f, -255.75f, 127.75f)]
    [InlineData(0.25f, -0.25f, 0.5f)]
    public void PackedXYZ_RoundTripsWithinItsQuantum(float x, float y, float z)
    {
        PacketWriter writer = new();
        writer.WritePackedXYZ(x, y, z);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadPackedXYZ(out float readX, out float readY, out float readZ));
        Assert.Equal(x, readX, 0.25f);
        Assert.Equal(y, readY, 0.25f);
        Assert.Equal(z, readZ, 0.25f);
    }

    /// <summary>
    /// The fields are signed. Reading them as unsigned turns every negative offset into a large
    /// positive one — objects appear flung across the map.
    /// </summary>
    [Fact]
    public void PackedXYZ_PreservesNegativeOffsets()
    {
        PacketWriter writer = new();
        writer.WritePackedXYZ(-10f, -20f, -30f);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadPackedXYZ(out float x, out float y, out float z));
        Assert.True(x < 0, $"X came back as {x}");
        Assert.True(y < 0, $"Y came back as {y}");
        Assert.True(z < 0, $"Z came back as {z}");
        Assert.Equal(-10f, x, 0.25f);
        Assert.Equal(-20f, y, 0.25f);
        Assert.Equal(-30f, z, 0.25f);
    }

    [Fact]
    public void PackedTime_RoundTripsToTheMinute()
    {
        DateTime time = new(2026, 8, 4, 20, 37, 0, DateTimeKind.Unspecified);

        PacketWriter writer = new();
        writer.WritePackedTime(time);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.True(reader.TryReadPackedTime(out DateTime read));
        Assert.Equal(time, read);
    }

    [Fact]
    public void PackedTime_LaysOutFieldsAsTheClientExpects()
    {
        DateTime time = new(2026, 8, 4, 20, 37, 0, DateTimeKind.Unspecified);

        PacketWriter writer = new();
        writer.WritePackedTime(time);

        PacketReader reader = new(writer.WrittenSpan);
        Assert.True(reader.TryReadUInt32(out uint packed));

        Assert.Equal(37u, packed & 0x3F);                    // minute
        Assert.Equal(20u, (packed >> 6) & 0x1F);             // hour
        Assert.Equal((uint)(int)time.DayOfWeek, (packed >> 11) & 0x7);
        Assert.Equal(3u, (packed >> 14) & 0x3F);             // day of month, zero-based
        Assert.Equal(7u, (packed >> 20) & 0xF);              // month, zero-based
        Assert.Equal(26u, (packed >> 24) & 0x1F);            // years since 2000
    }

    [Fact]
    public void PackedTime_RejectsAnImpossibleDate()
    {
        // Month field 12 means "month 13", which no calendar has.
        uint packed = 12u << 20;

        PacketWriter writer = new();
        writer.WriteUInt32(packed);

        PacketReader reader = new(writer.WrittenSpan);

        Assert.False(reader.TryReadPackedTime(out _));
        Assert.False(reader.Ok);
    }
}
