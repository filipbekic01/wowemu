using System.Buffers.Binary;

namespace WowEmu.Data.Client;

/// <summary>One collision triangle, as three indices into its group's vertex array.</summary>
/// <remarks><c>MeshTriangle</c> — three <c>uint32</c>, 12 bytes.</remarks>
public readonly record struct MeshTriangle(uint Index0, uint Index1, uint Index2);

/// <summary>
/// A body of water inside a model: a height grid over a rectangle of tiles.
/// </summary>
/// <remarks>
/// <c>WmoLiquid</c>. The height grid is one larger than the tile grid in each axis — heights are at
/// the corners, flags are per tile — so a group with 4×3 tiles carries 20 heights and 12 flags.
/// <para>
/// A liquid with no tiles is still a liquid: it stores a single height and nothing else. That case
/// has its own branch in the file format, and reading the grid form for it consumes the bytes of
/// whatever follows.
/// </para>
/// </remarks>
public sealed record WmoLiquid(
    uint TilesX,
    uint TilesY,
    float CornerX,
    float CornerY,
    float CornerZ,
    uint Type,
    float[] Heights,
    byte[] Flags);

/// <summary>
/// One group of a model: a mesh, its spatial index, and any water in it.
/// </summary>
/// <remarks>
/// A WMO is built from groups — a building's rooms, floors and roof are separate groups — and each
/// carries its own triangles and its own BIH over them. A model's own BIH indexes the groups, so a
/// ray descends two levels of tree before it reaches a triangle.
/// </remarks>
public sealed record WorldModelGroup(
    float BoundsMinX,
    float BoundsMinY,
    float BoundsMinZ,
    float BoundsMaxX,
    float BoundsMaxY,
    float BoundsMaxZ,
    uint MogpFlags,
    uint GroupWmoId,
    float[] Vertices,
    MeshTriangle[] Triangles,
    BihTree? MeshTree,
    WmoLiquid? Liquid)
{
    /// <summary>How many vertices the group has. Three floats each.</summary>
    public int VertexCount => Vertices.Length / 3;

    /// <summary>
    /// Whether the group carries any collision geometry at all.
    /// </summary>
    /// <remarks>
    /// A group with no vertices is not malformed — upstream's own comment calls them "models without
    /// (collision) geometry" and is unsure whether they are useful. What matters is that such a
    /// group's record <b>ends early</b>: it has no triangle, BIH or liquid chunks.
    /// </remarks>
    public bool HasGeometry => Vertices.Length > 0;

    /// <summary>
    /// Whether the group's bounding box was recorded at all.
    /// </summary>
    /// <remarks>
    /// Some groups arrive with an all-zero box while carrying real geometry — the extractor writes
    /// whatever bound it was given, and for these it was given none. A zero box therefore means
    /// "not recorded", not "empty", and code that culls by it must not conclude the group is
    /// nowhere: doing so makes the triangles inside invisible to collision, silently.
    /// </remarks>
    public bool HasBounds =>
        BoundsMinX != BoundsMaxX || BoundsMinY != BoundsMaxY || BoundsMinZ != BoundsMaxZ;
}

/// <summary>A whole <c>.vmo</c>: the groups a model is made of, and the tree over them.</summary>
public sealed record WorldModel(uint RootWmoId, WorldModelGroup[] Groups, BihTree? GroupTree);

/// <summary>
/// Reads <c>.vmo</c> files — the collision geometry a <see cref="ModelSpawn"/> names.
/// </summary>
/// <remarks>
/// <c>WorldModel::readFile</c>. This is the layer below <see cref="VmapFile"/>: the placement files
/// say which models stand where, and these hold the triangles that actually block a line of sight.
/// <para>
/// The format is tagged chunks — <c>WMOD</c>, <c>GMOD</c>, then per group <c>VERT</c>, <c>TRIM</c>,
/// <c>MBIH</c>, <c>LIQU</c> — but the tags are the only structure. Chunk sizes are written and then
/// mostly ignored by upstream's own reader, and a group with no vertices simply stops emitting
/// chunks, so the parse is driven by content rather than by length.
/// </para>
/// </remarks>
public static class WorldModelFile
{
    /// <summary>Reads a whole model file.</summary>
    public static WorldModel Read(ReadOnlySpan<byte> data)
    {
        int offset = 0;

        ExpectTag(data, ref offset, VmapFile.Magic);
        ExpectTag(data, ref offset, "WMOD");

        // The chunk size is written but carries no information the parse uses — upstream reads and
        // discards it too.
        offset += 4;

        uint rootWmoId = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        // A model with no groups is legal and simply has no geometry.
        if (offset >= data.Length || !TagMatches(data, offset, "GMOD"))
        {
            return new WorldModel(rootWmoId, [], null);
        }

        offset += 4;

        uint groupCount = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        WorldModelGroup[] groups = new WorldModelGroup[groupCount];

        for (int i = 0; i < groupCount; i++)
        {
            groups[i] = ReadGroup(data, ref offset);
        }

        ExpectTag(data, ref offset, "GBIH");
        BihTree groupTree = ReadBih(data, ref offset);

        return new WorldModel(rootWmoId, groups, groupTree);
    }

    private static WorldModelGroup ReadGroup(ReadOnlySpan<byte> data, ref int offset)
    {
        float minX = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
        float minY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 4)..]);
        float minZ = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 8)..]);
        float maxX = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 12)..]);
        float maxY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 16)..]);
        float maxZ = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 20)..]);
        offset += 24;

        uint mogpFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        uint groupWmoId = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
        offset += 8;

        ExpectTag(data, ref offset, "VERT");
        offset += 4;                                    // chunk size, unused

        uint vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        // The early exit that makes this format content-driven. A group with no vertices emits no
        // further chunks at all — reading TRIM next would consume the following group's bounds.
        if (vertexCount == 0)
        {
            return new WorldModelGroup(
                minX, minY, minZ, maxX, maxY, maxZ, mogpFlags, groupWmoId, [], [], null, null);
        }

        float[] vertices = ReadFloats(data, ref offset, (int)vertexCount * 3);

        ExpectTag(data, ref offset, "TRIM");
        offset += 4;                                    // chunk size, unused

        uint triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        MeshTriangle[] triangles = new MeshTriangle[triangleCount];

        for (int i = 0; i < triangleCount; i++)
        {
            int at = offset + (i * 12);

            triangles[i] = new MeshTriangle(
                BinaryPrimitives.ReadUInt32LittleEndian(data[at..]),
                BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 8)..]));
        }

        offset += (int)triangleCount * 12;

        ExpectTag(data, ref offset, "MBIH");
        BihTree meshTree = ReadBih(data, ref offset);

        ExpectTag(data, ref offset, "LIQU");

        uint liquidSize = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        WmoLiquid? liquid = liquidSize > 0 ? ReadLiquid(data, ref offset) : null;

        return new WorldModelGroup(
            minX, minY, minZ, maxX, maxY, maxZ,
            mogpFlags, groupWmoId,
            vertices, triangles, meshTree, liquid);
    }

    private static WmoLiquid ReadLiquid(ReadOnlySpan<byte> data, ref int offset)
    {
        uint tilesX = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        uint tilesY = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);

        float cornerX = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 8)..]);
        float cornerY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 12)..]);
        float cornerZ = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 16)..]);

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 20)..]);
        offset += 24;

        // No tiles means a single height and no flags — a different record shape, not an empty one.
        if (tilesX == 0 || tilesY == 0)
        {
            return new WmoLiquid(
                tilesX, tilesY, cornerX, cornerY, cornerZ, type,
                ReadFloats(data, ref offset, 1),
                []);
        }

        // Heights sit at tile corners, so the grid is one larger in each axis than the tile grid.
        float[] heights = ReadFloats(data, ref offset, (int)((tilesX + 1) * (tilesY + 1)));

        int flagCount = (int)(tilesX * tilesY);
        byte[] flags = data.Slice(offset, flagCount).ToArray();
        offset += flagCount;

        return new WmoLiquid(tilesX, tilesY, cornerX, cornerY, cornerZ, type, heights, flags);
    }

    private static BihTree ReadBih(ReadOnlySpan<byte> data, ref int offset)
    {
        float minX = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
        float minY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 4)..]);
        float minZ = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 8)..]);
        float maxX = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 12)..]);
        float maxY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 16)..]);
        float maxZ = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 20)..]);
        offset += 24;

        uint[] nodes = ReadUInt32Array(data, ref offset);
        uint[] objects = ReadUInt32Array(data, ref offset);

        return new BihTree(minX, minY, minZ, maxX, maxY, maxZ, nodes, objects);
    }

    private static uint[] ReadUInt32Array(ReadOnlySpan<byte> data, ref int offset)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        if (count > (uint)((data.Length - offset) / 4))
        {
            throw new InvalidDataException(
                $"Array claims {count} entries but only {(data.Length - offset) / 4} remain.");
        }

        uint[] values = new uint[count];

        for (int i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + (i * 4))..]);
        }

        offset += (int)count * 4;
        return values;
    }

    private static float[] ReadFloats(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (count > (data.Length - offset) / 4)
        {
            throw new InvalidDataException(
                $"Wanted {count} floats but only {(data.Length - offset) / 4} remain.");
        }

        float[] values = new float[count];

        for (int i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + (i * 4))..]);
        }

        offset += count * 4;
        return values;
    }

    private static bool TagMatches(ReadOnlySpan<byte> data, int offset, string tag)
    {
        if (offset + tag.Length > data.Length)
        {
            return false;
        }

        for (int i = 0; i < tag.Length; i++)
        {
            if (data[offset + i] != (byte)tag[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void ExpectTag(ReadOnlySpan<byte> data, ref int offset, string tag)
    {
        if (!TagMatches(data, offset, tag))
        {
            string found = offset < data.Length
                ? System.Text.Encoding.ASCII.GetString(
                    data.Slice(offset, Math.Min(tag.Length, data.Length - offset)))
                : "end of file";

            throw new InvalidDataException($"Expected '{tag}' at byte {offset}, found '{found}'.");
        }

        offset += tag.Length;
    }
}
