using System.Globalization;
using WowEmu.Data.Client;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>Marks a test that reads extracted VMAP files.</summary>
public sealed class RequiresVmapFactAttribute : FactAttribute
{
    public RequiresVmapFactAttribute()
    {
        if (!VmapData.Available)
        {
            Skip = $"no extracted vmaps at {VmapData.Directory}";
        }
    }
}

/// <summary>Where the extracted <c>.vmtree</c> and <c>.vmtile</c> files are, if anywhere.</summary>
internal static class VmapData
{
    static VmapData()
    {
        Directory = Path.Combine(ClientData.DataDirectory, "vmaps");

        Available = System.IO.Directory.Exists(Directory)
            && System.IO.Directory.EnumerateFiles(Directory, "*.vmtree").Any();
    }

    public static string Directory { get; }

    public static bool Available { get; }

    public static string Tree(string map) => Path.Combine(Directory, $"{map}.vmtree");

    public static IEnumerable<string> Trees(int limit) =>
        System.IO.Directory.EnumerateFiles(Directory, "*.vmtree").Order(StringComparer.Ordinal).Take(limit);

    public static IEnumerable<string> Tiles(string mapPrefix, int limit) =>
        System.IO.Directory
            .EnumerateFiles(Directory, $"{mapPrefix}_*.vmtile")
            .Order(StringComparer.Ordinal)
            .Take(limit);
}

/// <summary>
/// The VMAP placement files — the static collision geometry the client renders.
/// </summary>
/// <remarks>
/// PLAN.md §6 puts these next in Phase 8, and movement validation has been waiting on them since
/// Phase 7: without collision, a height check would reject every honest player standing on a bridge
/// or inside a building.
/// <para>
/// Neither file kind carries geometry. Both are placement layers naming <c>.vmo</c> files, and both
/// are packed with no lengths and no way to resynchronise — a model spawn is variable-length in two
/// independent ways, so one misread record corrupts every record after it.
/// </para>
/// </remarks>
public sealed class VmapTests(ITestOutputHelper output)
{
    [RequiresVmapFact]
    public void MapTrees_Parse()
    {
        int trees = 0, tiled = 0, globals = 0;
        long nodes = 0, primitives = 0;

        foreach (string path in VmapData.Trees(40))
        {
            VmapTree tree = VmapFile.ReadTree(File.ReadAllBytes(path));

            Assert.True(tree.Tree.Nodes.Length > 0, $"{Path.GetFileName(path)} has an empty tree");

            trees++;
            nodes += tree.Tree.Nodes.Length;
            primitives += tree.Tree.PrimitiveCount;

            if (tree.IsTiled)
            {
                tiled++;

                // A tiled map's models come from its tiles, never from the tree file.
                Assert.Null(tree.GlobalSpawn);
            }
            else if (tree.GlobalSpawn is not null)
            {
                globals++;
            }
        }

        Assert.True(trees > 0);
        output.WriteLine(
            $"{trees} trees: {tiled} tiled, {globals} with a global spawn, " +
            $"{nodes:N0} nodes indexing {primitives:N0} primitives");
    }

    /// <summary>
    /// The tree's bounding box is a real box, and every object index is inside the object array.
    /// </summary>
    /// <remarks>
    /// The first check that the BIH header was read at the right offset. The bounds are six floats
    /// straight after the <c>"NODE"</c> tag; if the tag were consumed wrongly these would be
    /// arbitrary and the min/max ordering would not hold.
    /// </remarks>
    [RequiresVmapFact]
    public void TreeBounds_AreOrderedAndFinite()
    {
        foreach (string path in VmapData.Trees(40))
        {
            BihTree tree = VmapFile.ReadTree(File.ReadAllBytes(path)).Tree;

            Assert.True(float.IsFinite(tree.BoundsMinX) && float.IsFinite(tree.BoundsMaxX));
            Assert.True(float.IsFinite(tree.BoundsMinY) && float.IsFinite(tree.BoundsMaxY));
            Assert.True(float.IsFinite(tree.BoundsMinZ) && float.IsFinite(tree.BoundsMaxZ));

            Assert.True(tree.BoundsMinX <= tree.BoundsMaxX, $"{Path.GetFileName(path)} x bounds inverted");
            Assert.True(tree.BoundsMinY <= tree.BoundsMaxY, $"{Path.GetFileName(path)} y bounds inverted");
            Assert.True(tree.BoundsMinZ <= tree.BoundsMaxZ, $"{Path.GetFileName(path)} z bounds inverted");
        }
    }

    /// <summary>
    /// Every node reachable from the root decodes to a usable axis and an in-range offset.
    /// </summary>
    /// <remarks>
    /// <b>Traversed, not scanned</b> — and the difference is the whole point. A BIH node occupies
    /// three words: a packed descriptor and two split planes stored as floats. Scanning the array
    /// linearly decodes those floats as descriptors, and a float's bit pattern makes a perfectly
    /// plausible node whose offset points a hundred million words away. An earlier version of this
    /// test did exactly that and failed on the first map, which is how the structure came to light.
    /// <para>
    /// Reaching every node by walking also proves something scanning could not: the tree is
    /// connected and its offsets lead somewhere real.
    /// </para>
    /// </remarks>
    [RequiresVmapFact]
    public void EveryReachableNode_DecodesToSomethingUsable()
    {
        long leaves = 0, interior = 0, bvh2 = 0, objectsNamed = 0;

        foreach (string path in VmapData.Trees(20))
        {
            BihTree tree = VmapFile.ReadTree(File.ReadAllBytes(path)).Tree;
            string name = Path.GetFileName(path);

            foreach (BihNode node in tree.Walk())
            {
                Assert.InRange(node.Axis, 0u, 3u);

                if (node.IsLeaf)
                {
                    Assert.True(
                        node.Offset + node.ObjectCount <= (uint)tree.Objects.Length,
                        $"leaf {node.Offset}+{node.ObjectCount} past {tree.Objects.Length} objects in {name}");

                    leaves++;
                    objectsNamed += node.ObjectCount;
                }
                else
                {
                    // Both children have to fit, so the far one bounds the check.
                    uint far = node.IsBvh2 ? node.Offset : node.Offset + BihTree.WordsPerNode;

                    Assert.True(
                        far + BihTree.WordsPerNode <= (uint)tree.Nodes.Length,
                        $"child at {far} past {tree.Nodes.Length} words in {name}");

                    interior++;

                    if (node.IsBvh2)
                    {
                        bvh2++;
                    }
                }
            }
        }

        Assert.True(leaves > 0 && interior > 0);
        output.WriteLine(
            $"{leaves:N0} leaves naming {objectsNamed:N0} objects, " +
            $"{interior:N0} interior nodes ({bvh2:N0} single-child)");
    }

    /// <summary>
    /// The walk reaches every primitive the tree claims to index, exactly once.
    /// </summary>
    /// <remarks>
    /// The check that the traversal is complete rather than merely safe. If the child offsets were
    /// misread, the walk would terminate early and quietly cover a fraction of the map — which as a
    /// collision index means buildings that are simply not there.
    /// </remarks>
    [RequiresVmapFact]
    public void TheWalk_ReachesEveryPrimitive()
    {
        foreach (string path in VmapData.Trees(12))
        {
            BihTree tree = VmapFile.ReadTree(File.ReadAllBytes(path)).Tree;

            if (tree.PrimitiveCount == 0)
            {
                continue;
            }

            HashSet<uint> reached = [];

            foreach (BihNode node in tree.Walk().Where(n => n.IsLeaf))
            {
                for (uint i = 0; i < node.ObjectCount; i++)
                {
                    reached.Add(tree.Objects[node.Offset + i]);
                }
            }

            Assert.Equal(tree.PrimitiveCount, reached.Count);
        }
    }

    [RequiresVmapFact]
    public void EveryObjectIndex_IsInsideTheTree()
    {
        foreach (string path in VmapData.Trees(20))
        {
            BihTree tree = VmapFile.ReadTree(File.ReadAllBytes(path)).Tree;

            foreach (uint index in tree.Objects)
            {
                Assert.True(
                    index < (uint)tree.PrimitiveCount,
                    $"object index {index} past {tree.PrimitiveCount} primitives");
            }
        }
    }

    /// <summary>
    /// Tiles parse, and every spawn they place points at a slot the map tree actually has.
    /// </summary>
    /// <remarks>
    /// This is the cross-file check: a tile names indices into its map's tree, and the two files are
    /// written by separate passes of the extractor. An index past the end would mean the tile and
    /// the tree disagree — upstream logs and skips that case, which is a good sign it happens.
    /// </remarks>
    [RequiresVmapFact]
    public void TileSpawns_PointAtRealTreeSlots()
    {
        VmapTree tree = VmapFile.ReadTree(File.ReadAllBytes(VmapData.Tree("000")));

        int tiles = 0, spawns = 0, wmos = 0, doodads = 0, outOfRange = 0;

        foreach (string path in VmapData.Tiles("000", 60))
        {
            IReadOnlyList<VmapTileSpawn> placed = VmapFile.ReadTile(File.ReadAllBytes(path));
            tiles++;

            foreach (VmapTileSpawn placement in placed)
            {
                spawns++;

                if (placement.TreeIndex >= (uint)tree.Tree.PrimitiveCount)
                {
                    outOfRange++;
                }

                Assert.NotEmpty(placement.Spawn.Name);
                Assert.True(placement.Spawn.Scale > 0f, "a spawn had no scale");

                if (placement.Spawn.IsM2)
                {
                    doodads++;
                }
                else
                {
                    wmos++;
                }
            }
        }

        Assert.True(tiles > 0 && spawns > 0);
        Assert.Equal(0, outOfRange);

        output.WriteLine(
            $"{tiles} tiles placing {spawns} models ({wmos} buildings, {doodads} doodads) " +
            $"into a tree of {tree.Tree.PrimitiveCount:N0} slots");
    }

    /// <summary>
    /// Every named model file exists on disk.
    /// </summary>
    /// <remarks>
    /// The strongest evidence the variable-length record was read correctly. A name is a
    /// length-prefixed byte run packed immediately against the next record — if the length or the
    /// preceding bounding box were misread, the name would be a slice of the wrong bytes, and a
    /// slice of the wrong bytes does not match a file on disk.
    /// <para>
    /// It also pins the terminator rule, which is the subtlest thing in the format: a NUL in the
    /// stored name means the geometry is in the bare file, not in <c>&lt;name&gt;.vmo</c>. Both
    /// forms exist on disk and 18,278 spawns resolve only under the right rule, so every name
    /// resolving is what proves it.
    /// </para>
    /// </remarks>
    [RequiresVmapFact]
    public void EverySpawn_NamesAModelThatExists()
    {
        HashSet<string> missing = [];
        int checkedSpawns = 0;

        foreach (string path in VmapData.Tiles("000", 40))
        {
            foreach (VmapTileSpawn placement in VmapFile.ReadTile(File.ReadAllBytes(path)))
            {
                checkedSpawns++;

                if (!File.Exists(Path.Combine(VmapData.Directory, placement.Spawn.ModelFileName)))
                {
                    missing.Add(placement.Spawn.Name);
                }
            }
        }

        Assert.True(checkedSpawns > 100, $"only {checkedSpawns} spawns checked");
        Assert.Empty(missing.Take(5));

        output.WriteLine($"{checkedSpawns} spawns, every named .vmo present on disk");
    }

    /// <summary>
    /// A building's precomputed bounds are a real box around where it stands.
    /// </summary>
    /// <remarks>
    /// Only models flagged <c>HasBound</c> carry one, and that flag is what decides whether the
    /// record is 24 bytes longer. If the flag were misread the bounds would be read out of the name,
    /// or the name out of the bounds — either way the numbers stop being coordinates.
    /// </remarks>
    [RequiresVmapFact]
    public void BoundedSpawns_HaveABoxAroundTheirPosition()
    {
        int bounded = 0;

        foreach (string path in VmapData.Tiles("000", 40))
        {
            foreach (VmapTileSpawn placement in VmapFile.ReadTile(File.ReadAllBytes(path)))
            {
                ModelSpawn spawn = placement.Spawn;

                if (!spawn.HasBound)
                {
                    continue;
                }

                Assert.True(spawn.BoundsMinX <= spawn.BoundsMaxX, $"{spawn.Name} x bounds inverted");
                Assert.True(spawn.BoundsMinY <= spawn.BoundsMaxY, $"{spawn.Name} y bounds inverted");
                Assert.True(spawn.BoundsMinZ <= spawn.BoundsMaxZ, $"{spawn.Name} z bounds inverted");

                // The model stands inside its own bounds, give or take — the box is axis-aligned in
                // world space and the position is the model's origin, which need not be centred.
                const float Slack = 600f;
                Assert.InRange(spawn.PositionX, spawn.BoundsMinX - Slack, spawn.BoundsMaxX + Slack);
                Assert.InRange(spawn.PositionZ, spawn.BoundsMinZ - Slack, spawn.BoundsMaxZ + Slack);

                bounded++;
            }
        }

        Assert.True(bounded > 0, "no bounded spawns found — the HasBound flag may not be read");
        output.WriteLine($"{bounded} bounded spawns, all with a sane box");
    }

    /// <summary>The readers hold across every map, not just Eastern Kingdoms.</summary>
    [RequiresVmapFact]
    public void EveryMapTree_Parses()
    {
        int trees = 0, failures = 0;
        List<string> broken = [];

        foreach (string path in Directory.EnumerateFiles(VmapData.Directory, "*.vmtree"))
        {
            try
            {
                VmapFile.ReadTree(File.ReadAllBytes(path));
                trees++;
            }
            catch (InvalidDataException exception)
            {
                failures++;

                if (broken.Count < 5)
                {
                    broken.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }

        Assert.Empty(broken);
        Assert.Equal(0, failures);
        Assert.True(trees > 90, $"only {trees} trees parsed");

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"all {trees} map trees parse"));
    }
}
