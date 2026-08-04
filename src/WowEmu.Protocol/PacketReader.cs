using System.Buffers.Binary;
using System.Text;

namespace WowEmu.Protocol;

/// <summary>
/// Little-endian packet reader over a span.
/// </summary>
/// <remarks>
/// Every read is a <c>TryRead</c> returning <see langword="false"/> on overrun rather than throwing.
/// AzerothCore's <c>ByteBuffer</c> throws <c>ByteBufferPositionException</c> and catches it per
/// packet, which is fine in C++ but would be a cheap denial-of-service vector in .NET: a hostile
/// client can send short packets as fast as the socket allows, and managed exceptions cost orders of
/// magnitude more than a bounds check.
/// </remarks>
public ref struct PacketReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;
    private int _position;

    /// <summary>Bytes not yet consumed.</summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>Bytes consumed so far.</summary>
    public readonly int Position => _position;

    /// <summary>True if every read so far succeeded.</summary>
    public bool Ok { get; private set; } = true;

    public bool TryReadUInt8(out byte value)
    {
        if (Remaining < 1)
        {
            value = 0;
            return Fail();
        }

        value = _buffer[_position];
        _position += 1;
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        if (Remaining < 2)
        {
            value = 0;
            return Fail();
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_position..]);
        _position += 2;
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return Fail();
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_position..]);
        _position += 4;
        return true;
    }

    public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
    {
        if (count < 0 || Remaining < count)
        {
            value = default;
            return Fail();
        }

        value = _buffer.Slice(_position, count);
        _position += count;
        return true;
    }

    /// <summary>Reads <paramref name="count"/> bytes and reverses them, for the byte-swapped
    /// fixed-width fields in the logon challenge (<c>os</c>, <c>country</c>).</summary>
    public bool TryReadReversedAscii(int count, out string value)
    {
        if (!TryReadBytes(count, out ReadOnlySpan<byte> bytes))
        {
            value = string.Empty;
            return false;
        }

        Span<byte> reversed = stackalloc byte[count];
        bytes.CopyTo(reversed);
        reversed.Reverse();

        int end = reversed.IndexOf((byte)0);
        value = Encoding.ASCII.GetString(end < 0 ? reversed : reversed[..end]);
        return true;
    }

    /// <summary>Reads a fixed-length, non-NUL-terminated UTF-8 string.</summary>
    public bool TryReadFixedString(int count, out string value)
    {
        if (!TryReadBytes(count, out ReadOnlySpan<byte> bytes))
        {
            value = string.Empty;
            return false;
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    public void Skip(int count)
    {
        if (count < 0 || Remaining < count)
        {
            Fail();
            return;
        }

        _position += count;
    }

    private bool Fail()
    {
        Ok = false;
        return false;
    }
}
