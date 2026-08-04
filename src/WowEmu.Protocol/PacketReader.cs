using System.Buffers.Binary;
using System.Text;
using WowEmu.Core;

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

    public bool TryReadUInt64(out ulong value)
    {
        if (Remaining < sizeof(ulong))
        {
            value = 0;
            return Fail();
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer[_position..]);
        _position += sizeof(ulong);
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

    /// <summary>
    /// Reads a NUL-terminated UTF-8 string, consuming the terminator.
    /// </summary>
    /// <remarks>
    /// A string with no terminator in the remaining bytes is a truncated packet, not a string that
    /// runs to the end — upstream's <c>ByteBuffer</c> would throw here, so this refuses too rather
    /// than inventing a value.
    /// </remarks>
    public bool TryReadCString(out string value)
    {
        value = string.Empty;

        ReadOnlySpan<byte> remaining = _buffer[_position..];
        int terminator = remaining.IndexOf((byte)0);

        if (terminator < 0)
        {
            return Fail();
        }

        value = Encoding.UTF8.GetString(remaining[..terminator]);
        _position += terminator + 1;
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

    /// <summary>
    /// Reads a packed GUID: a mask byte, then one byte per set bit.
    /// </summary>
    /// <remarks>
    /// Port of <c>ByteBuffer::readPackGUID</c>. A mask of zero is a valid encoding of the empty
    /// guid and consumes exactly one byte.
    /// </remarks>
    public bool TryReadPackedGuid(out ulong value)
    {
        value = 0;

        if (!TryReadUInt8(out byte mask))
        {
            return false;
        }

        for (int i = 0; i < 8; i++)
        {
            if ((mask & (1 << i)) == 0)
            {
                continue;
            }

            if (!TryReadUInt8(out byte part))
            {
                value = 0;
                return false;
            }

            value |= (ulong)part << (i * 8);
        }

        return true;
    }

    /// <inheritdoc cref="TryReadPackedGuid(out ulong)"/>
    public bool TryReadPackedGuid(out ObjectGuid value)
    {
        bool ok = TryReadPackedGuid(out ulong raw);
        value = new ObjectGuid(raw);
        return ok;
    }

    /// <summary>
    /// Reads three coordinates packed into one <see cref="uint"/>.
    /// </summary>
    /// <remarks>
    /// The inverse of <c>appendPackXYZ</c>. The fields are <b>signed</b> — 11 bits for X and Y, 10
    /// for Z — so each is sign-extended before scaling back up by 0.25. Reading them as unsigned
    /// turns every negative offset into a large positive one, which shows up as objects flung
    /// across the map.
    /// </remarks>
    public bool TryReadPackedXYZ(out float x, out float y, out float z)
    {
        x = y = z = 0f;

        if (!TryReadUInt32(out uint packed))
        {
            return false;
        }

        x = SignExtend((int)(packed & 0x7FF), 11) * 0.25f;
        y = SignExtend((int)((packed >> 11) & 0x7FF), 11) * 0.25f;
        z = SignExtend((int)((packed >> 22) & 0x3FF), 10) * 0.25f;
        return true;
    }

    /// <summary>
    /// Reads a packed calendar timestamp. The result has no time zone attached; the client means
    /// local time.
    /// </summary>
    public bool TryReadPackedTime(out DateTime value)
    {
        value = default;

        if (!TryReadUInt32(out uint packed))
        {
            return false;
        }

        int minute = (int)(packed & 0x3F);
        int hour = (int)((packed >> 6) & 0x1F);
        int day = (int)((packed >> 14) & 0x3F) + 1;
        int month = (int)((packed >> 20) & 0xF) + 1;
        int year = (int)((packed >> 24) & 0x1F) + 2000;

        // The weekday field at bits 11-13 is redundant with the date and upstream ignores it too.
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59)
        {
            return Fail();
        }

        value = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return true;
    }

    private static int SignExtend(int value, int bits)
    {
        int signBit = 1 << (bits - 1);
        return (value ^ signBit) - signBit;
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
