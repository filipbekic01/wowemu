using System.Buffers;
using System.Buffers.Binary;
using System.Text;

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
