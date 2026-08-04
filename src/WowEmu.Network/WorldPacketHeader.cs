using System.Buffers.Binary;
using WowEmu.Protocol;

namespace WowEmu.Network;

/// <summary>
/// The world protocol's packet headers, which are not the same shape in each direction.
/// </summary>
/// <remarks>
/// Port of <c>ClientPktHeader</c> (WorldSocket.h) and <c>ServerPktHeader</c>
/// (Protocol/ServerPktHeader.h).
/// <para>
/// <b>The size field is big-endian and the opcode is little-endian, in the same header.</b> That
/// mixed endianness is the single most common way to get this layer wrong, and it fails in a way
/// that looks like a corrupt stream rather than a byte-order bug.
/// </para>
/// </remarks>
public static class WorldPacketHeader
{
    /// <summary>Client header: <c>uint16</c> size (big-endian) then <c>uint32</c> opcode.</summary>
    public const int ClientSize = 6;

    /// <summary>A server header is 4 bytes, or 5 when the payload needs a third size byte.</summary>
    public const int ServerSizeSmall = 4;

    /// <summary>Largest possible server header.</summary>
    public const int ServerSizeLarge = 5;

    /// <summary>
    /// Payloads at or above this need the three-byte size form, flagged by the top bit of the
    /// first byte.
    /// </summary>
    public const int LargePacketThreshold = 0x7FFF;

    /// <summary>Smallest legal value of the client's size field: it counts the 4-byte opcode.</summary>
    public const int MinClientSizeField = 4;

    /// <summary>Upstream's cap on a single client packet.</summary>
    public const int MaxClientSizeField = 10240;

    /// <summary>
    /// Decodes a client header. Returns false if the size or opcode is out of range, which upstream
    /// treats as a protocol violation and closes the connection over.
    /// </summary>
    public static bool TryReadClient(ReadOnlySpan<byte> header, out Opcode opcode, out int payloadLength)
    {
        opcode = default;
        payloadLength = 0;

        if (header.Length < ClientSize)
        {
            return false;
        }

        // Big-endian size, little-endian opcode. Yes, really.
        ushort size = BinaryPrimitives.ReadUInt16BigEndian(header);
        uint command = BinaryPrimitives.ReadUInt32LittleEndian(header[2..]);

        if (size < MinClientSizeField || size >= MaxClientSizeField || command > ushort.MaxValue)
        {
            return false;
        }

        opcode = (Opcode)command;

        // The size field counts the opcode; the payload is what is left.
        payloadLength = size - MinClientSizeField;
        return true;
    }

    /// <summary>
    /// Writes a server header for a payload of <paramref name="payloadLength"/> bytes and returns
    /// how many bytes it used.
    /// </summary>
    /// <remarks>
    /// The encoded size counts the two opcode bytes as well as the payload — off-by-two here shifts
    /// every subsequent packet in the stream.
    /// </remarks>
    public static int WriteServer(Span<byte> destination, Opcode opcode, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);

        int size = payloadLength + 2;
        int index = 0;

        if (size > LargePacketThreshold)
        {
            destination[index++] = (byte)(0x80 | ((size >> 16) & 0xFF));
        }

        destination[index++] = (byte)((size >> 8) & 0xFF);
        destination[index++] = (byte)(size & 0xFF);

        destination[index++] = (byte)((ushort)opcode & 0xFF);
        destination[index++] = (byte)(((ushort)opcode >> 8) & 0xFF);

        return index;
    }

    /// <summary>How many bytes <see cref="WriteServer"/> will produce for this payload.</summary>
    public static int ServerHeaderLength(int payloadLength) =>
        payloadLength + 2 > LargePacketThreshold ? ServerSizeLarge : ServerSizeSmall;
}
