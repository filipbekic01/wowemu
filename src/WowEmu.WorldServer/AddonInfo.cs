using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>One addon the client reported having enabled.</summary>
public readonly record struct ClientAddon(string Name, byte Enabled, uint Crc);

/// <summary>
/// Parses the compressed addon manifest that rides along on <c>CMSG_AUTH_SESSION</c>.
/// </summary>
/// <remarks>
/// The tail of the auth packet is a <c>uint32</c> uncompressed size followed by a zlib (RFC 1950)
/// stream of <c>count, then (CString name, uint8 enabled, uint32 crc, uint32 unknown)*</c>.
/// <para>
/// The size is attacker-controlled, so it is capped at upstream's <c>0xFFFFF</c> before any buffer
/// is allocated. Decompression bombs are the obvious hazard in a pre-authentication code path —
/// this runs before the digest is verified, because the manifest arrives in the same packet.
/// </para>
/// </remarks>
public static class AddonInfo
{
    /// <summary>Upstream's cap on the decompressed manifest.</summary>
    public const int MaxUncompressedSize = 0xFFFFF;

    /// <summary>
    /// The CRC every unmodified Blizzard addon reports. Anything else gets the public key.
    /// </summary>
    public const uint StandardCrc = 0x4C1C776D;

    /// <summary>
    /// Blizzard's 256-byte addon public key.
    /// </summary>
    /// <remarks>
    /// Opaque data. It is echoed back verbatim to clients whose addon CRC is non-standard; nothing
    /// here interprets it, and it must be copied byte for byte.
    /// </remarks>
    public static ReadOnlySpan<byte> PublicKey =>
    [
        0xC3, 0x5B, 0x50, 0x84, 0xB9, 0x3E, 0x32, 0x42, 0x8C, 0xD0, 0xC7, 0x48, 0xFA, 0x0E, 0x5D, 0x54,
        0x5A, 0xA3, 0x0E, 0x14, 0xBA, 0x9E, 0x0D, 0xB9, 0x5D, 0x8B, 0xEE, 0xB6, 0x84, 0x93, 0x45, 0x75,
        0xFF, 0x31, 0xFE, 0x2F, 0x64, 0x3F, 0x3D, 0x6D, 0x07, 0xD9, 0x44, 0x9B, 0x40, 0x85, 0x59, 0x34,
        0x4E, 0x10, 0xE1, 0xE7, 0x43, 0x69, 0xEF, 0x7C, 0x16, 0xFC, 0xB4, 0xED, 0x1B, 0x95, 0x28, 0xA8,
        0x23, 0x76, 0x51, 0x31, 0x57, 0x30, 0x2B, 0x79, 0x08, 0x50, 0x10, 0x1C, 0x4A, 0x1A, 0x2C, 0xC8,
        0x8B, 0x8F, 0x05, 0x2D, 0x22, 0x3D, 0xDB, 0x5A, 0x24, 0x7A, 0x0F, 0x13, 0x50, 0x37, 0x8F, 0x5A,
        0xCC, 0x9E, 0x04, 0x44, 0x0E, 0x87, 0x01, 0xD4, 0xA3, 0x15, 0x94, 0x16, 0x34, 0xC6, 0xC2, 0xC3,
        0xFB, 0x49, 0xFE, 0xE1, 0xF9, 0xDA, 0x8C, 0x50, 0x3C, 0xBE, 0x2C, 0xBB, 0x57, 0xED, 0x46, 0xB9,
        0xAD, 0x8B, 0xC6, 0xDF, 0x0E, 0xD6, 0x0F, 0xBE, 0x80, 0xB3, 0x8B, 0x1E, 0x77, 0xCF, 0xAD, 0x22,
        0xCF, 0xB7, 0x4B, 0xCF, 0xFB, 0xF0, 0x6B, 0x11, 0x45, 0x2D, 0x7A, 0x81, 0x18, 0xF2, 0x92, 0x7E,
        0x98, 0x56, 0x5D, 0x5E, 0x69, 0x72, 0x0A, 0x0D, 0x03, 0x0A, 0x85, 0xA2, 0x85, 0x9C, 0xCB, 0xFB,
        0x56, 0x6E, 0x8F, 0x44, 0xBB, 0x8F, 0x02, 0x22, 0x68, 0x63, 0x97, 0xBC, 0x85, 0xBA, 0xA8, 0xF7,
        0xB5, 0x40, 0x68, 0x3C, 0x77, 0x86, 0x6F, 0x4B, 0xD7, 0x88, 0xCA, 0x8A, 0xD7, 0xCE, 0x36, 0xF0,
        0x45, 0x6E, 0xD5, 0x64, 0x79, 0x0F, 0x17, 0xFC, 0x64, 0xDD, 0x10, 0x6F, 0xF3, 0xF5, 0xE0, 0xA6,
        0xC3, 0xFB, 0x1B, 0x8C, 0x29, 0xEF, 0x8E, 0xE5, 0x34, 0xCB, 0xD1, 0x2A, 0xCE, 0x79, 0xC3, 0x9A,
        0x0D, 0x36, 0xEA, 0x01, 0xE0, 0xAA, 0x91, 0x20, 0x54, 0xF0, 0x72, 0xD8, 0x1E, 0xC7, 0x89, 0xD2
    ];

    /// <summary>
    /// Parses the manifest. Returns an empty list for anything malformed — a client with a broken
    /// addon list still deserves to reach the character screen.
    /// </summary>
    public static IReadOnlyList<ClientAddon> Parse(ReadOnlySpan<byte> compressed, ILogger logger)
    {
        if (compressed.Length < 4)
        {
            return [];
        }

        uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(compressed);

        if (uncompressedSize == 0)
        {
            return [];
        }

        if (uncompressedSize > MaxUncompressedSize)
        {
            Log.AddonInfoTooLarge(logger, uncompressedSize);
            return [];
        }

        byte[] plain = new byte[uncompressedSize];

        try
        {
            using MemoryStream source = new(compressed[4..].ToArray());
            using ZLibStream inflate = new(source, CompressionMode.Decompress);

            inflate.ReadExactly(plain);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            Log.AddonInfoUnreadable(logger);
            return [];
        }

        return ReadEntries(plain, logger);
    }

    private static List<ClientAddon> ReadEntries(ReadOnlySpan<byte> plain, ILogger logger)
    {
        List<ClientAddon> addons = [];

        PacketReader reader = new(plain);

        if (!reader.TryReadUInt32(out uint count))
        {
            return addons;
        }

        for (uint i = 0; i < count; i++)
        {
            if (!reader.TryReadCString(out string name) ||
                !reader.TryReadUInt8(out byte enabled) ||
                !reader.TryReadUInt32(out uint crc) ||
                !reader.TryReadUInt32(out _))
            {
                Log.AddonInfoTruncated(logger, i, count);
                break;
            }

            addons.Add(new ClientAddon(name, enabled, crc));
        }

        return addons;
    }
}
