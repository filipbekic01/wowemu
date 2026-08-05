using System.Buffers.Binary;

namespace WowEmu.Data.Client;

/// <summary>The <c>dtNavMeshParams</c> at the head of a <c>.mmap</c> file.</summary>
/// <remarks>
/// Written raw by <c>mmaps_generator</c> — 28 bytes, no header of its own. It describes the grid the
/// tiles sit in, and every tile added to a mesh must agree with it.
/// </remarks>
public readonly record struct NavMeshParams(
    float OriginX,
    float OriginY,
    float OriginZ,
    float TileWidth,
    float TileHeight,
    int MaxTiles,
    int MaxPolys)
{
    /// <summary>
    /// How many bits of a polygon reference the generator gave to the polygon index.
    /// </summary>
    /// <remarks>
    /// <c>mmaps_generator</c> writes <c>maxPolys = 1 &lt;&lt; DT_POLY_BITS</c>, so this field is a
    /// direct fingerprint of the Detour the extractor was built against. It is read as an unsigned
    /// value because the patched constant is 31, and <c>1 &lt;&lt; 31</c> overflows the signed
    /// <c>int</c> Detour declares — the file legitimately contains a negative number.
    /// </remarks>
    public int PolyBits => System.Numerics.BitOperations.TrailingZeroCount((uint)MaxPolys);
}

/// <summary>
/// The 56-byte AzerothCore header on a <c>.mmtile</c>, before the Detour blob.
/// </summary>
/// <remarks>
/// <c>MmapTileHeader</c> from <c>MapDefines.h</c>. The <c>recastConfig</c> tail records what the
/// generator was configured with, so a tile built with different settings can be recognised rather
/// than silently mixed in.
/// </remarks>
public readonly record struct MmapTileHeader(
    uint MmapMagic,
    uint DetourVersion,
    uint MmapVersion,
    uint DataSize,
    bool UsesLiquids);

/// <summary>
/// The <c>dtMeshHeader</c> at the head of a Detour tile.
/// </summary>
/// <remarks>
/// 100 bytes. The counts are what make the rest of the tile parseable — and what make it possible to
/// tell, from the file alone, how wide a <c>dtPolyRef</c> is. See
/// <see cref="NavMeshFile.DetectPolyRefWidth"/>.
/// </remarks>
public readonly record struct DetourMeshHeader(
    int Magic,
    int Version,
    int X,
    int Y,
    int Layer,
    uint UserId,
    int PolyCount,
    int VertCount,
    int MaxLinkCount,
    int DetailMeshCount,
    int DetailVertCount,
    int DetailTriCount,
    int BvNodeCount,
    int OffMeshConCount,
    int OffMeshBase,
    float WalkableHeight,
    float WalkableRadius,
    float WalkableClimb,
    float BoundsMinX,
    float BoundsMinY,
    float BoundsMinZ,
    float BoundsMaxX,
    float BoundsMaxY,
    float BoundsMaxZ,
    float BvQuantFactor);

/// <summary>How wide a <c>dtPolyRef</c> is in a tile, which decides which Detour can read it.</summary>
public enum PolyRefWidth
{
    /// <summary>Neither layout accounts for the tile's size. Something else differs.</summary>
    Unknown,

    /// <summary>Stock Detour: 32-bit refs, <c>sizeof(dtLink) == 12</c>.</summary>
    Bits32,

    /// <summary><c>DT_POLYREF64</c>: 64-bit refs, <c>sizeof(dtLink) == 16</c>.</summary>
    Bits64,
}

/// <summary>
/// Reads the headers of extracted navmesh files.
/// </summary>
/// <remarks>
/// PLAN.md §3.4.1 makes this Phase 8's first task, ahead of any pathfinding: the vendored Detour is
/// patched (<c>DT_POLYREF64</c>, and a 12/21/31 salt/tile/poly split against stock's 16/28/20), and
/// upstream's own comment says tiles built with 32-bit refs are not compatible with 64-bit ones. If
/// the extracted tiles use the patched layout, a stock Detour port misreads every one of them —
/// and it misreads them into plausible-looking garbage rather than an error.
/// <para>
/// This reads headers only. Evaluating a path is the next step and needs a Detour implementation;
/// the point of stopping here is that <see cref="DetectPolyRefWidth"/> settles which one to get.
/// </para>
/// </remarks>
public static class NavMeshFile
{
    /// <summary><c>MMAP_MAGIC</c>, <c>'MMAP'</c>.</summary>
    public const uint MmapMagic = 0x4D4D4150;

    /// <summary><c>MMAP_VERSION</c>.</summary>
    public const uint MmapVersion = 20;

    /// <summary><c>DT_NAVMESH_MAGIC</c>, <c>'DNAV'</c>.</summary>
    public const int DetourMagic = ('D' << 24) | ('N' << 16) | ('A' << 8) | 'V';

    /// <summary><c>DT_NAVMESH_VERSION</c>.</summary>
    public const int DetourVersion = 7;

    /// <summary>Size of <c>MmapTileHeader</c>, asserted in the C++.</summary>
    public const int MmapTileHeaderSize = 56;

    /// <summary>Size of <c>dtMeshHeader</c>.</summary>
    public const int DetourMeshHeaderSize = 100;

    /// <summary>Size of <c>dtNavMeshParams</c>.</summary>
    public const int NavMeshParamsSize = 28;

    /// <summary>Reads the 28-byte parameter block that opens a <c>.mmap</c>.</summary>
    public static NavMeshParams ReadParams(ReadOnlySpan<byte> data)
    {
        if (data.Length < NavMeshParamsSize)
        {
            throw new InvalidDataException(
                $"A .mmap must be at least {NavMeshParamsSize} bytes, got {data.Length}.");
        }

        return new NavMeshParams(
            BinaryPrimitives.ReadSingleLittleEndian(data),
            BinaryPrimitives.ReadSingleLittleEndian(data[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[12..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[24..]));
    }

    /// <summary>Reads the AzerothCore header on a <c>.mmtile</c>.</summary>
    public static MmapTileHeader ReadTileHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < MmapTileHeaderSize)
        {
            throw new InvalidDataException(
                $"A .mmtile must be at least {MmapTileHeaderSize} bytes, got {data.Length}.");
        }

        return new MmapTileHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            data[16] != 0);
    }

    /// <summary>Reads the <c>dtMeshHeader</c> that opens the Detour blob.</summary>
    public static DetourMeshHeader ReadMeshHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < DetourMeshHeaderSize)
        {
            throw new InvalidDataException(
                $"A Detour tile must be at least {DetourMeshHeaderSize} bytes, got {data.Length}.");
        }

        // Written out rather than routed through a local helper: a span cannot be captured by a
        // lambda or local function, and the offsets are the specification anyway.
        return new DetourMeshHeader(
            Magic: BinaryPrimitives.ReadInt32LittleEndian(data),
            Version: BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
            X: BinaryPrimitives.ReadInt32LittleEndian(data[8..]),
            Y: BinaryPrimitives.ReadInt32LittleEndian(data[12..]),
            Layer: BinaryPrimitives.ReadInt32LittleEndian(data[16..]),
            UserId: BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            PolyCount: BinaryPrimitives.ReadInt32LittleEndian(data[24..]),
            VertCount: BinaryPrimitives.ReadInt32LittleEndian(data[28..]),
            MaxLinkCount: BinaryPrimitives.ReadInt32LittleEndian(data[32..]),
            DetailMeshCount: BinaryPrimitives.ReadInt32LittleEndian(data[36..]),
            DetailVertCount: BinaryPrimitives.ReadInt32LittleEndian(data[40..]),
            DetailTriCount: BinaryPrimitives.ReadInt32LittleEndian(data[44..]),
            BvNodeCount: BinaryPrimitives.ReadInt32LittleEndian(data[48..]),
            OffMeshConCount: BinaryPrimitives.ReadInt32LittleEndian(data[52..]),
            OffMeshBase: BinaryPrimitives.ReadInt32LittleEndian(data[56..]),
            WalkableHeight: BinaryPrimitives.ReadSingleLittleEndian(data[60..]),
            WalkableRadius: BinaryPrimitives.ReadSingleLittleEndian(data[64..]),
            WalkableClimb: BinaryPrimitives.ReadSingleLittleEndian(data[68..]),
            BoundsMinX: BinaryPrimitives.ReadSingleLittleEndian(data[72..]),
            BoundsMinY: BinaryPrimitives.ReadSingleLittleEndian(data[76..]),
            BoundsMinZ: BinaryPrimitives.ReadSingleLittleEndian(data[80..]),
            BoundsMaxX: BinaryPrimitives.ReadSingleLittleEndian(data[84..]),
            BoundsMaxY: BinaryPrimitives.ReadSingleLittleEndian(data[88..]),
            BoundsMaxZ: BinaryPrimitives.ReadSingleLittleEndian(data[92..]),
            BvQuantFactor: BinaryPrimitives.ReadSingleLittleEndian(data[96..]));
    }

    /// <summary>
    /// Works out how wide the tile's polygon references are, from the size of the tile itself.
    /// </summary>
    /// <remarks>
    /// The bit split is a compile-time constant and leaves no trace in the file. The <i>width</i>
    /// does: <c>DT_POLYREF64</c> widens <c>dtLink::ref</c> from 4 bytes to 8, and alignment takes
    /// <c>sizeof(dtLink)</c> from 12 to 16. Every tile carries <c>maxLinkCount</c> links, so the two
    /// layouts predict tile sizes that differ by <c>4 × maxLinkCount</c> bytes — thousands, on a real
    /// tile. Computing both and seeing which matches the recorded size answers the question from the
    /// data rather than from a header file that may not be the one the extractor was built from.
    /// <para>
    /// That distinction matters here: the two reference checkouts are already known to be at
    /// different points in AzerothCore's history, so "the C++ says <c>DT_POLYREF64</c>" and "the
    /// files we actually have use it" are different claims.
    /// </para>
    /// </remarks>
    public static PolyRefWidth DetectPolyRefWidth(DetourMeshHeader header, int detourDataSize)
    {
        if (ExpectedTileSize(header, linkSize: 16) == detourDataSize)
        {
            return PolyRefWidth.Bits64;
        }

        return ExpectedTileSize(header, linkSize: 12) == detourDataSize
            ? PolyRefWidth.Bits32
            : PolyRefWidth.Unknown;
    }

    /// <summary>
    /// How large a Detour tile with these counts should be, for a given link size.
    /// </summary>
    /// <remarks>
    /// Mirrors the layout <c>dtNavMesh::addTile</c> walks: every section is 4-byte aligned and laid
    /// out in this order, with no padding recorded anywhere. The struct sizes are fixed by the
    /// Detour headers — <c>dtPoly</c> 32, <c>dtPolyDetail</c> 12, <c>dtBVNode</c> 16,
    /// <c>dtOffMeshConnection</c> 36 — and only <c>dtLink</c> varies.
    /// </remarks>
    public static int ExpectedTileSize(DetourMeshHeader header, int linkSize) =>
        DetourMeshHeaderSize
        + Align4(12 * header.VertCount)              // float[3] per vertex
        + Align4(32 * header.PolyCount)              // dtPoly
        + Align4(linkSize * header.MaxLinkCount)     // dtLink
        + Align4(12 * header.DetailMeshCount)        // dtPolyDetail
        + Align4(12 * header.DetailVertCount)        // float[3] per detail vertex
        + Align4(4 * header.DetailTriCount)          // unsigned char[4] per detail triangle
        + Align4(16 * header.BvNodeCount)            // dtBVNode
        + Align4(36 * header.OffMeshConCount);       // dtOffMeshConnection

    private static int Align4(int value) => (value + 3) & ~3;
}
