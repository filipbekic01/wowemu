using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>
/// Little-endian packet builder.
/// </summary>
/// <remarks>
/// Equivalent to AzerothCore's <c>ByteBuffer</c> write side. Everything the 3.3.5a protocol writes is
/// fixed-width little-endian; strings are raw bytes followed by a single NUL, and an empty string is
/// therefore one zero byte.
/// <para>
/// Deliberately <i>not</i> a copy of <c>ByteBuffer::append</c>'s growth heuristic, which reserves
/// 400 KB for anything over 6 KB.
/// </para>
/// </remarks>
public sealed class PacketWriter
{
    private byte[] _buffer;
    private int _position;

    /// <summary>Creates a writer with an initial capacity.</summary>
    public PacketWriter(int capacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _buffer = capacity == 0 ? [] : ArrayPool<byte>.Shared.Rent(capacity);
    }

    /// <summary>Number of bytes written so far.</summary>
    public int Length => _position;

    /// <summary>The bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

    /// <summary>Returns the written bytes as a new array.</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();

    public void WriteUInt8(byte value) => GetSpan(1)[0] = value;

    public void WriteInt8(sbyte value) => WriteUInt8((byte)value);

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(GetSpan(2), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(GetSpan(4), value);

    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(GetSpan(8), value);

    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(GetSpan(4), value);

    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(GetSpan(value.Length));

    /// <summary>
    /// Writes a NUL-terminated string. An empty or null string writes a single zero byte, matching
    /// <c>ByteBuffer::operator&lt;&lt;(std::string)</c>.
    /// </summary>
    public void WriteCString(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            Encoding.UTF8.GetBytes(value, GetSpan(byteCount));
        }

        WriteUInt8(0);
    }

    /// <summary>
    /// Writes a GUID in the client's packed form: a mask byte whose bits say which of the eight
    /// bytes are non-zero, followed by only those bytes, little-endian.
    /// </summary>
    /// <remarks>
    /// Port of <c>ByteBuffer::appendPackGUID</c>. Most guids have a lot of zero bytes, so this
    /// typically costs 3-5 bytes instead of 8 — worth it because guids appear in nearly every
    /// movement and update packet. An empty guid is a single zero byte.
    /// </remarks>
    public void WritePackedGuid(ulong value)
    {
        Span<byte> packed = stackalloc byte[9];
        int size = 1;

        for (int i = 0; value != 0; i++)
        {
            if ((value & 0xFF) != 0)
            {
                packed[0] |= (byte)(1 << i);
                packed[size++] = (byte)(value & 0xFF);
            }

            value >>= 8;
        }

        WriteBytes(packed[..size]);
    }

    /// <inheritdoc cref="WritePackedGuid(ulong)"/>
    public void WritePackedGuid(ObjectGuid value) => WritePackedGuid(value.Value);

    /// <summary>
    /// Writes a position as one <see cref="uint"/> of three packed offsets.
    /// </summary>
    /// <remarks>
    /// Port of <c>ByteBuffer::appendPackXYZ</c>. Each coordinate is divided by 0.25 and truncated —
    /// <b>X and Y keep 11 bits, Z keeps 10</b>, which is asymmetric and easy to get wrong.
    /// <para>
    /// This is lossy and wraps silently: the encodable range is about ±256 yards for X and Y and
    /// ±128 for Z, so it is only ever used for offsets relative to something else (spline points,
    /// transport offsets), never for absolute world coordinates.
    /// </para>
    /// </remarks>
    public void WritePackedXYZ(float x, float y, float z)
    {
        uint packed = 0;
        packed |= (uint)((int)(x / 0.25f) & 0x7FF);
        packed |= (uint)((int)(y / 0.25f) & 0x7FF) << 11;
        packed |= (uint)((int)(z / 0.25f) & 0x3FF) << 22;

        WriteUInt32(packed);
    }

    /// <summary>
    /// Writes a timestamp in the client's packed calendar format.
    /// </summary>
    /// <remarks>
    /// Port of <c>ByteBuffer::AppendPackedTime</c>: minute, hour, weekday, day-of-month, month and
    /// a year offset from 2000, bit-packed into a <see cref="uint"/>. Only five bits go to the
    /// year, so it runs out in 2032 — that is the client's format, not a choice we get to make.
    /// <para>
    /// The client interprets this as <i>local</i> time. Upstream builds it with <c>localtime</c>,
    /// so the value passed here should already be in whatever zone the server presents.
    /// </para>
    /// </remarks>
    public void WritePackedTime(DateTime time)
    {
        uint packed =
            ((uint)(time.Year - 2000) << 24) |
            ((uint)(time.Month - 1) << 20) |
            ((uint)(time.Day - 1) << 14) |
            ((uint)time.DayOfWeek << 11) |
            ((uint)time.Hour << 6) |
            (uint)time.Minute;

        WriteUInt32(packed);
    }

    /// <summary>
    /// Overwrites <paramref name="length"/> bytes at <paramref name="offset"/>. Used to backfill a
    /// size field once the payload length is known.
    /// </summary>
    public Span<byte> GetSpanAt(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, _position);
        return _buffer.AsSpan(offset, length);
    }

    private Span<byte> GetSpan(int count)
    {
        EnsureCapacity(count);
        Span<byte> span = _buffer.AsSpan(_position, count);
        _position += count;
        return span;
    }

    private void EnsureCapacity(int count)
    {
        if (_position + count <= _buffer.Length)
        {
            return;
        }

        int required = Math.Max(_position + count, Math.Max(_buffer.Length * 2, 128));
        byte[] grown = ArrayPool<byte>.Shared.Rent(required);
        _buffer.AsSpan(0, _position).CopyTo(grown);

        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _buffer = grown;
    }
}
