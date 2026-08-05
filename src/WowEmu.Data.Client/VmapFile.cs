using System.Buffers.Binary;
using System.Text;

namespace WowEmu.Data.Client;

/// <summary>Flags on a model spawn, from <c>ModelInstance.h</c>.</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "ModelFlags is upstream's name; keeping the suffix keeps the trail back to the C++.")]
public enum ModelSpawnFlags : uint
{
    None = 0,

    /// <summary>A doodad (tree, rock, lamppost) rather than a building.</summary>
    M2 = 1,

    /// <summary>The single model that is the whole map, on maps that are not tiled.</summary>
    WorldSpawn = 2,

    /// <summary>Carries a precomputed bounding box. Only WMOs do.</summary>
    HasBound = 4,
}

/// <summary>
/// One placed model: which file, where, how rotated, how big.
/// </summary>
/// <remarks>
/// <c>ModelSpawn</c>. The bounding box is present only when <see cref="ModelSpawnFlags.HasBound"/>
/// is set — the record is a different length depending on a flag, which is why it cannot be read as
/// a fixed-size struct.
/// </remarks>
public readonly record struct ModelSpawn(
    ModelSpawnFlags Flags,
    ushort AdtId,
    uint Id,
    float PositionX,
    float PositionY,
    float PositionZ,
    float RotationX,
    float RotationY,
    float RotationZ,
    float Scale,
    float BoundsMinX,
    float BoundsMinY,
    float BoundsMinZ,
    float BoundsMaxX,
    float BoundsMaxY,
    float BoundsMaxZ,
    string Name,
    bool NameHasTerminator)
{
    /// <summary>Whether the bounding box fields carry real values.</summary>
    public bool HasBound => Flags.HasFlag(ModelSpawnFlags.HasBound);

    /// <summary>Whether this is a doodad rather than a building.</summary>
    public bool IsM2 => Flags.HasFlag(ModelSpawnFlags.M2);

    /// <summary>
    /// The file this spawn's geometry lives in, relative to the vmap directory.
    /// </summary>
    /// <remarks>
    /// <b>The stored name's NUL terminator is significant, and this is the least obvious thing in
    /// the format.</b> Upstream builds the path as <c>name + ".vmo"</c> and hands it to
    /// <c>fopen</c> — a C API that stops at the first NUL. About half the stored names carry a
    /// terminator, so for those the <c>".vmo"</c> is never seen and the file actually opened is the
    /// bare name.
    /// <para>
    /// The extractor writes files to match, which is what makes it work rather than a latent bug:
    /// across map 0's tiles, every name without a terminator has a <c>.vmo</c> file, and 18,278
    /// spawns whose name carries one have <i>only</i> the bare file. Trimming the terminator and
    /// appending <c>.vmo</c> — the obvious reading — fails to find those.
    /// </para>
    /// </remarks>
    public string ModelFileName => NameHasTerminator ? Name : Name + ".vmo";
}

/// <summary>
/// A bounding-interval-hierarchy tree: the spatial index over a map's or a model's primitives.
/// </summary>
/// <remarks>
/// <c>BIH</c>.
/// <para>
/// <b><see cref="Nodes"/> is not an array of nodes.</b> It is a flat word array in which a node
/// occupies <i>three</i> consecutive words — one packed descriptor followed by two split planes
/// stored as floats. Walking the array and decoding every word as a node therefore decodes those
/// floats as descriptors, and a float's bit pattern makes a plausible-looking node with an offset
/// of a hundred million. The only way to enumerate nodes is to traverse from the root; see
/// <see cref="Walk"/>.
/// </para>
/// </remarks>
public sealed record BihTree(
    float BoundsMinX,
    float BoundsMinY,
    float BoundsMinZ,
    float BoundsMaxX,
    float BoundsMaxY,
    float BoundsMaxZ,
    uint[] Nodes,
    uint[] Objects)
{
    /// <summary>How many primitives the tree indexes.</summary>
    public int PrimitiveCount => Objects.Length;

    /// <summary>
    /// The split axis of a node: 0, 1 or 2 for x, y or z, and 3 for a leaf.
    /// </summary>
    /// <remarks>
    /// Bits 31-30. Bit 29 is a separate "BVH2" flag for a node with one child, and the remaining 29
    /// bits are an offset — so the offset mask is <c>~(7 &lt;&lt; 29)</c>, not <c>~(3 &lt;&lt; 30)</c>.
    /// Masking off only the axis leaves the BVH2 bit in the offset and sends traversal to a node
    /// 536,870,912 words away.
    /// </remarks>
    public static uint NodeAxis(uint node) => (node & (3u << 30)) >> 30;

    /// <summary>Whether a node has a single child rather than two.</summary>
    public static bool NodeIsBvh2(uint node) => (node & (1u << 29)) != 0;

    /// <summary>The node's payload: a child offset for an interior node, an object index for a leaf.</summary>
    public static uint NodeOffset(uint node) => node & ~(7u << 29);

    /// <summary>Whether a node holds primitives rather than splitting space.</summary>
    public static bool NodeIsLeaf(uint node) => NodeAxis(node) == 3;

    /// <summary>Words per node: the descriptor plus two split planes.</summary>
    public const int WordsPerNode = 3;

    /// <summary>
    /// Visits every node reachable from the root, depth first.
    /// </summary>
    /// <remarks>
    /// The children of an interior node sit at <c>offset</c> and <c>offset + 3</c> — the two are the
    /// near and far side of the split, and which is which depends on the ray's direction, so a
    /// structural walk simply takes both. A single-child node has just the one at <c>offset</c>.
    /// <para>
    /// Guarded against cycles by a visited set. A malformed tree that pointed a child back at an
    /// ancestor would otherwise walk forever, and a file on disk is not a thing to trust with that.
    /// </para>
    /// </remarks>
    public IEnumerable<BihNode> Walk()
    {
        if (Nodes.Length == 0)
        {
            yield break;
        }

        HashSet<int> seen = [];
        Stack<int> pending = new();
        pending.Push(0);

        while (pending.Count > 0)
        {
            int index = pending.Pop();

            if (index < 0 || index + WordsPerNode > Nodes.Length || !seen.Add(index))
            {
                continue;
            }

            uint word = Nodes[index];
            uint axis = NodeAxis(word);
            uint offset = NodeOffset(word);

            if (NodeIsLeaf(word))
            {
                // A leaf names a run of the object array: start at offset, length in the next word.
                yield return new BihNode(index, axis, offset, Nodes[index + 1], IsLeaf: true, IsBvh2: false);
                continue;
            }

            bool bvh2 = NodeIsBvh2(word);
            yield return new BihNode(index, axis, offset, 0, IsLeaf: false, IsBvh2: bvh2);

            pending.Push((int)offset);

            if (!bvh2)
            {
                pending.Push((int)offset + WordsPerNode);
            }
        }
    }
}

/// <summary>One decoded node of a <see cref="BihTree"/>.</summary>
/// <param name="Index">Where it starts in the word array.</param>
/// <param name="Axis">Split axis, or 3 for a leaf.</param>
/// <param name="Offset">Child word index, or the first object index for a leaf.</param>
/// <param name="ObjectCount">How many objects a leaf holds. Zero for an interior node.</param>
/// <param name="IsLeaf">Whether it holds primitives.</param>
/// <param name="IsBvh2">Whether an interior node has one child rather than two.</param>
public readonly record struct BihNode(
    int Index,
    uint Axis,
    uint Offset,
    uint ObjectCount,
    bool IsLeaf,
    bool IsBvh2);

/// <summary>A map's static collision tree, from a <c>.vmtree</c>.</summary>
public sealed record VmapTree(bool IsTiled, BihTree Tree, ModelSpawn? GlobalSpawn);

/// <summary>One model placed by a tile, and which slot of the map tree it fills.</summary>
public readonly record struct VmapTileSpawn(ModelSpawn Spawn, uint TreeIndex);

/// <summary>
/// Reads extracted VMAP files — the static collision geometry the client renders and the server
/// must agree with.
/// </summary>
/// <remarks>
/// Two file kinds. A <c>.vmtree</c> holds one map's spatial index and, for a map that is not tiled,
/// the single model that is the whole map. A <c>.vmtile</c> holds the models a 533-yard tile places,
/// each naming the tree slot it belongs in.
/// <para>
/// Neither carries geometry. Both name <c>.vmo</c> files, which hold the actual triangles — this
/// reader stops at the placement layer, which is what a map needs to know what to load.
/// </para>
/// </remarks>
public static class VmapFile
{
    /// <summary><c>VMAP_MAGIC</c>. Eight bytes, and <b>not</b> NUL-terminated.</summary>
    public const string Magic = "VMAP_4.8";

    /// <summary>Length of the magic, in bytes.</summary>
    public const int MagicLength = 8;

    /// <summary>Reads a map's <c>.vmtree</c>.</summary>
    /// <remarks>
    /// The layout is a sequence of tagged chunks with no lengths: magic, a tiled flag,
    /// <c>"NODE"</c>, the tree, <c>"GOBJ"</c>, and then — only when the map is not tiled — one
    /// global model spawn. Reading the global spawn on a tiled map consumes whatever follows.
    /// </remarks>
    public static VmapTree ReadTree(ReadOnlySpan<byte> data)
    {
        int offset = 0;

        ExpectTag(data, ref offset, Magic);

        bool tiled = data[offset] != 0;
        offset++;

        ExpectTag(data, ref offset, "NODE");
        BihTree tree = ReadBih(data, ref offset);
        ExpectTag(data, ref offset, "GOBJ");

        // Only untiled maps carry one, and they carry exactly one.
        ModelSpawn? global = null;

        if (!tiled && offset < data.Length)
        {
            global = ReadModelSpawn(data, ref offset);
        }

        return new VmapTree(tiled, tree, global);
    }

    /// <summary>Reads a tile's <c>.vmtile</c>: the models it places and where they go in the tree.</summary>
    public static IReadOnlyList<VmapTileSpawn> ReadTile(ReadOnlySpan<byte> data)
    {
        int offset = 0;

        ExpectTag(data, ref offset, Magic);

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        VmapTileSpawn[] spawns = new VmapTileSpawn[count];

        for (int i = 0; i < count; i++)
        {
            ModelSpawn spawn = ReadModelSpawn(data, ref offset);

            uint treeIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            offset += 4;

            spawns[i] = new VmapTileSpawn(spawn, treeIndex);
        }

        if (offset != data.Length)
        {
            throw new InvalidDataException(
                $"Tile parse consumed {offset} of {data.Length} bytes; the spawn records did not add up.");
        }

        return spawns;
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

    /// <summary>Reads a count-prefixed <c>uint32</c> array.</summary>
    private static uint[] ReadUInt32Array(ReadOnlySpan<byte> data, ref int offset)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        if (count > (uint)((data.Length - offset) / 4))
        {
            throw new InvalidDataException(
                $"Array claims {count} entries but only {(data.Length - offset) / 4} remain in the file.");
        }

        uint[] values = new uint[count];

        for (int i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + (i * 4))..]);
        }

        offset += (int)count * 4;
        return values;
    }

    /// <summary>
    /// Reads one model spawn.
    /// </summary>
    /// <remarks>
    /// Variable length in two ways: the bounding box is present only for models whose flags say so,
    /// and the name is a length-prefixed run of bytes with no terminator. Both have to be honoured
    /// exactly, because spawns are packed back to back with nothing to resynchronise on.
    /// </remarks>
    private static ModelSpawn ReadModelSpawn(ReadOnlySpan<byte> data, ref int offset)
    {
        ModelSpawnFlags flags = (ModelSpawnFlags)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        ushort adtId = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 6)..]);

        float posX = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 10)..]);
        float posY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 14)..]);
        float posZ = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 18)..]);

        float rotX = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 22)..]);
        float rotY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 26)..]);
        float rotZ = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 30)..]);

        float scale = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 34)..]);
        offset += 38;

        float minX = 0f, minY = 0f, minZ = 0f, maxX = 0f, maxY = 0f, maxZ = 0f;

        if (flags.HasFlag(ModelSpawnFlags.HasBound))
        {
            minX = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
            minY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 4)..]);
            minZ = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 8)..]);
            maxX = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 12)..]);
            maxY = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 16)..]);
            maxZ = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 20)..]);
            offset += 24;
        }

        uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;

        // Upstream's own sanity check: a model file name is never this long, so anything past it
        // means the read has already gone wrong and the length is not a length.
        if (nameLength > MaxNameLength)
        {
            throw new InvalidDataException(
                $"Model name claims {nameLength} bytes; the read has desynchronised.");
        }

        // The stored length counts a terminator about half the time — 54.5 % with, 45.5 % without,
        // mixed inside a single file. Whether it is there decides which file on disk holds the
        // geometry, so it is recorded rather than discarded. See ModelSpawn.ModelFileName.
        ReadOnlySpan<byte> nameBytes = data.Slice(offset, (int)nameLength);
        bool terminated = nameBytes.Length > 0 && nameBytes[^1] == 0;

        if (terminated)
        {
            nameBytes = nameBytes[..^1];
        }

        string name = Encoding.UTF8.GetString(nameBytes);
        offset += (int)nameLength;

        return new ModelSpawn(
            flags, adtId, id,
            posX, posY, posZ,
            rotX, rotY, rotZ,
            scale,
            minX, minY, minZ, maxX, maxY, maxZ,
            name,
            terminated);
    }

    /// <summary>Upstream's cap on a model file name, from <c>ModelSpawn::readFromFile</c>.</summary>
    public const int MaxNameLength = 500;

    private static void ExpectTag(ReadOnlySpan<byte> data, ref int offset, string tag)
    {
        if (offset + tag.Length > data.Length)
        {
            throw new InvalidDataException($"File ended before its '{tag}' tag.");
        }

        for (int i = 0; i < tag.Length; i++)
        {
            if (data[offset + i] != (byte)tag[i])
            {
                string found = Encoding.ASCII.GetString(
                    data.Slice(offset, Math.Min(tag.Length, data.Length - offset)));

                throw new InvalidDataException($"Expected '{tag}' at byte {offset}, found '{found}'.");
            }
        }

        offset += tag.Length;
    }
}
