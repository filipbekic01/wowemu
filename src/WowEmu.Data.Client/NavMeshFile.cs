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

/// <summary>
/// One navigation polygon: up to six vertex indices, its neighbours, and what it is walkable as.
/// </summary>
/// <remarks>
/// <c>dtPoly</c>, 32 bytes. <c>areaAndtype</c> packs two things into one byte — the low six bits are
/// the terrain area (<c>NavTerrain</c>: ground, water, magma, slime), the top two are the polygon
/// type. A polygon read without unpacking them has an area of up to 255 and will not match any
/// filter.
/// </remarks>
public readonly record struct DetourPoly(
    uint FirstLink,
    ushort[] Verts,
    ushort[] Neighbours,
    ushort Flags,
    byte VertCount,
    byte AreaAndType)
{
    /// <summary>Vertex indices per polygon. <c>DT_VERTS_PER_POLYGON</c>.</summary>
    public const int MaxVerts = 6;

    /// <summary>The <c>NavTerrain</c> area: ground, water, magma or slime.</summary>
    public byte Area => (byte)(AreaAndType & 0x3F);

    /// <summary>0 for ground, 1 for an off-mesh connection.</summary>
    public byte Type => (byte)(AreaAndType >> 6);

    /// <summary>Whether this polygon is a jump or teleport link rather than walkable ground.</summary>
    public bool IsOffMeshConnection => Type == 1;
}

/// <summary>A polygon's slice of the detail mesh — the finer triangles that carry real heights.</summary>
/// <remarks>
/// <c>dtPolyDetail</c>, 12 bytes: two 4-byte bases, two 1-byte counts, and two bytes of padding the
/// compiler adds and the file therefore contains.
/// </remarks>
public readonly record struct DetourPolyDetail(
    uint VertBase,
    uint TriBase,
    byte VertCount,
    byte TriCount);

/// <summary>A node in the tile's bounding-volume tree, used to find polygons by position.</summary>
/// <remarks>
/// <c>dtBVNode</c>, 16 bytes. The bounds are quantised to the tile using the header's
/// <c>BvQuantFactor</c>, not world coordinates. A negative <c>Index</c> is an escape sequence — it
/// says how far to skip, not which polygon.
/// </remarks>
public readonly record struct DetourBvNode(
    ushort[] BoundsMin,
    ushort[] BoundsMax,
    int Index)
{
    /// <summary>Whether this node points at a polygon rather than telling the walk to skip ahead.</summary>
    public bool IsLeaf => Index >= 0;
}

/// <summary>A connection between two points that is not a walk — a jump, a ladder, a portal.</summary>
/// <remarks><c>dtOffMeshConnection</c>, 36 bytes.</remarks>
public readonly record struct DetourOffMeshConnection(
    float StartX,
    float StartY,
    float StartZ,
    float EndX,
    float EndY,
    float EndZ,
    float Radius,
    ushort Poly,
    byte Flags,
    byte Side,
    uint UserId);

/// <summary>
/// A whole Detour tile, parsed out of AzerothCore's raw layout.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> DotRecast reads recast4j's own serialisation format, not the C++ struct
/// blob <c>mmaps_generator</c> writes — so a reader for this layout is needed whichever Detour we
/// end up using. See PLAN.md §3.4.1.1.
/// <para>
/// Links are deliberately absent. The file reserves <c>MaxLinkCount</c> links' worth of space, but
/// it is <i>space</i>: Detour builds the links when the tile is added to a mesh, so what is on disk
/// carries no information. Reading it would be reading uninitialised bytes.
/// </para>
/// </remarks>
public sealed record DetourTile(
    DetourMeshHeader Header,
    float[] Vertices,
    DetourPoly[] Polys,
    DetourPolyDetail[] DetailMeshes,
    float[] DetailVertices,
    byte[] DetailTriangles,
    DetourBvNode[] BvTree,
    DetourOffMeshConnection[] OffMeshConnections);

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
    /// Reads a whole <c>.mmtile</c>: the AzerothCore header, then the Detour tile behind it.
    /// </summary>
    /// <remarks>
    /// Sections appear in the order <c>dtNavMesh::addTile</c> walks them — vertices, polygons,
    /// links, detail meshes, detail vertices, detail triangles, BV tree, off-mesh connections —
    /// each 4-byte aligned, with no offsets recorded anywhere. Every section's position is implied
    /// by the sizes of the ones before it, so a single wrong struct size does not fail: it shifts
    /// everything after it and yields geometry that looks plausible and is wrong.
    /// <para>
    /// That is why this verifies the total against <see cref="ExpectedTileSize"/> before returning.
    /// </para>
    /// </remarks>
    public static DetourTile ReadTile(ReadOnlySpan<byte> file)
    {
        MmapTileHeader tile = ReadTileHeader(file);

        if (tile.MmapMagic != MmapMagic)
        {
            throw new InvalidDataException(
                $"Not a .mmtile: magic was 0x{tile.MmapMagic:X8}, expected 0x{MmapMagic:X8}.");
        }

        if (tile.MmapVersion != MmapVersion)
        {
            throw new InvalidDataException(
                $"This .mmtile is version {tile.MmapVersion}; this reader handles {MmapVersion}. " +
                "Re-run mmaps_generator rather than reading it anyway.");
        }

        ReadOnlySpan<byte> body = file[MmapTileHeaderSize..];
        DetourMeshHeader header = ReadMeshHeader(body);

        if (header.Magic != DetourMagic || header.Version != DetourVersion)
        {
            throw new InvalidDataException(
                $"Detour tile magic/version was 0x{header.Magic:X8}/{header.Version}, " +
                $"expected 0x{DetourMagic:X8}/{DetourVersion}.");
        }

        // The tiles we have use 64-bit polygon references — established from the data itself, see
        // PLAN.md §3.4.1.1. Anything else means a tile built against a different Detour, and the
        // section offsets below would all be wrong.
        int expected = ExpectedTileSize(header, PolyRefLinkSize);

        if (expected != (int)tile.DataSize)
        {
            throw new InvalidDataException(
                $"Tile is {tile.DataSize} bytes but its counts predict {expected} for the 64-bit " +
                "layout. It was built against a different Detour and its sections would not line up.");
        }

        int offset = DetourMeshHeaderSize;

        float[] vertices = ReadFloats(body, ref offset, 3 * header.VertCount);
        DetourPoly[] polys = ReadPolys(body, ref offset, header.PolyCount);

        // Skipped, not read: the file reserves this space but Detour fills it when the tile is
        // added to a mesh. What is on disk here is uninitialised.
        offset += Align4(PolyRefLinkSize * header.MaxLinkCount);

        DetourPolyDetail[] detailMeshes = ReadDetailMeshes(body, ref offset, header.DetailMeshCount);
        float[] detailVertices = ReadFloats(body, ref offset, 3 * header.DetailVertCount);

        byte[] detailTriangles = body.Slice(offset, 4 * header.DetailTriCount).ToArray();
        offset += Align4(4 * header.DetailTriCount);

        DetourBvNode[] bvTree = ReadBvNodes(body, ref offset, header.BvNodeCount);
        DetourOffMeshConnection[] offMesh = ReadOffMeshConnections(body, ref offset, header.OffMeshConCount);

        if (offset != expected)
        {
            throw new InvalidDataException(
                $"Tile parse consumed {offset} bytes but the layout predicts {expected}.");
        }

        return new DetourTile(
            header, vertices, polys, detailMeshes, detailVertices, detailTriangles, bvTree, offMesh);
    }

    /// <summary><c>sizeof(dtLink)</c> with 64-bit references, which ours use.</summary>
    public const int PolyRefLinkSize = 16;

    private static float[] ReadFloats(ReadOnlySpan<byte> body, ref int offset, int count)
    {
        float[] values = new float[count];

        for (int i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(body[(offset + (i * 4))..]);
        }

        offset += Align4(4 * count);
        return values;
    }

    private static DetourPoly[] ReadPolys(ReadOnlySpan<byte> body, ref int offset, int count)
    {
        DetourPoly[] polys = new DetourPoly[count];

        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * 32);

            ushort[] verts = new ushort[DetourPoly.MaxVerts];
            ushort[] neighbours = new ushort[DetourPoly.MaxVerts];

            for (int v = 0; v < DetourPoly.MaxVerts; v++)
            {
                verts[v] = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 4 + (v * 2))..]);
                neighbours[v] = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 16 + (v * 2))..]);
            }

            polys[i] = new DetourPoly(
                FirstLink: BinaryPrimitives.ReadUInt32LittleEndian(body[at..]),
                Verts: verts,
                Neighbours: neighbours,
                Flags: BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 28)..]),
                VertCount: body[at + 30],
                AreaAndType: body[at + 31]);
        }

        offset += Align4(32 * count);
        return polys;
    }

    private static DetourPolyDetail[] ReadDetailMeshes(ReadOnlySpan<byte> body, ref int offset, int count)
    {
        DetourPolyDetail[] meshes = new DetourPolyDetail[count];

        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * 12);

            meshes[i] = new DetourPolyDetail(
                VertBase: BinaryPrimitives.ReadUInt32LittleEndian(body[at..]),
                TriBase: BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 4)..]),
                VertCount: body[at + 8],
                TriCount: body[at + 9]);
        }

        offset += Align4(12 * count);
        return meshes;
    }

    private static DetourBvNode[] ReadBvNodes(ReadOnlySpan<byte> body, ref int offset, int count)
    {
        DetourBvNode[] nodes = new DetourBvNode[count];

        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * 16);

            ushort[] min = new ushort[3];
            ushort[] max = new ushort[3];

            for (int axis = 0; axis < 3; axis++)
            {
                min[axis] = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + (axis * 2))..]);
                max[axis] = BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 6 + (axis * 2))..]);
            }

            nodes[i] = new DetourBvNode(min, max, BinaryPrimitives.ReadInt32LittleEndian(body[(at + 12)..]));
        }

        offset += Align4(16 * count);
        return nodes;
    }

    private static DetourOffMeshConnection[] ReadOffMeshConnections(
        ReadOnlySpan<byte> body,
        ref int offset,
        int count)
    {
        DetourOffMeshConnection[] connections = new DetourOffMeshConnection[count];

        for (int i = 0; i < count; i++)
        {
            int at = offset + (i * 36);

            connections[i] = new DetourOffMeshConnection(
                StartX: BinaryPrimitives.ReadSingleLittleEndian(body[at..]),
                StartY: BinaryPrimitives.ReadSingleLittleEndian(body[(at + 4)..]),
                StartZ: BinaryPrimitives.ReadSingleLittleEndian(body[(at + 8)..]),
                EndX: BinaryPrimitives.ReadSingleLittleEndian(body[(at + 12)..]),
                EndY: BinaryPrimitives.ReadSingleLittleEndian(body[(at + 16)..]),
                EndZ: BinaryPrimitives.ReadSingleLittleEndian(body[(at + 20)..]),
                Radius: BinaryPrimitives.ReadSingleLittleEndian(body[(at + 24)..]),
                Poly: BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 28)..]),
                Flags: body[at + 30],
                Side: body[at + 31],
                UserId: BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 32)..]));
        }

        offset += Align4(36 * count);
        return connections;
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
