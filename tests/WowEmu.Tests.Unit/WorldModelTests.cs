using WowEmu.Data.Client;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The <c>.vmo</c> geometry files — the triangles that actually block a line of sight.
/// </summary>
/// <remarks>
/// The layer below the placement files: <c>.vmtile</c> says which model stands where,
/// <c>.vmo</c> holds what it is made of. Nothing can be occluded until these are read.
/// <para>
/// The format is tagged but not length-driven — upstream writes chunk sizes and then ignores them —
/// and a group with no vertices stops emitting chunks partway through its own record. So the parse
/// is content-driven, and a wrong turn is consumed as the next group's bounding box rather than
/// failing.
/// </para>
/// </remarks>
public sealed class WorldModelTests(ITestOutputHelper output)
{
    [RequiresVmapFact]
    public void ModelsParse()
    {
        int models = 0, groups = 0, empty = 0;
        long triangles = 0, vertices = 0;

        foreach (string path in Models(300))
        {
            WorldModel model = WorldModelFile.Read(File.ReadAllBytes(path));

            models++;
            groups += model.Groups.Length;

            foreach (WorldModelGroup group in model.Groups)
            {
                if (!group.HasGeometry)
                {
                    empty++;
                    continue;
                }

                triangles += group.Triangles.Length;
                vertices += group.VertexCount;
            }
        }

        Assert.True(models > 0);
        Assert.True(triangles > 0);

        output.WriteLine(
            $"{models} models, {groups} groups ({empty} with no geometry), " +
            $"{vertices:N0} vertices, {triangles:N0} triangles");
    }

    /// <summary>
    /// Every triangle indexes a vertex its own group has.
    /// </summary>
    /// <remarks>
    /// The check that matters most. A triangle is three <c>uint32</c> with nothing to distinguish
    /// them from any other three words, so if the vertex block's length were misread the triangles
    /// would be read out of vertex data — and vertex floats reinterpreted as indices are enormous,
    /// which this catches immediately.
    /// </remarks>
    [RequiresVmapFact]
    public void EveryTriangle_IndexesARealVertex()
    {
        long checkedTriangles = 0;

        foreach (string path in Models(300))
        {
            WorldModel model = WorldModelFile.Read(File.ReadAllBytes(path));

            foreach (WorldModelGroup group in model.Groups)
            {
                uint limit = (uint)group.VertexCount;

                foreach (MeshTriangle triangle in group.Triangles)
                {
                    Assert.True(triangle.Index0 < limit, $"{Path.GetFileName(path)}: index past {limit}");
                    Assert.True(triangle.Index1 < limit, $"{Path.GetFileName(path)}: index past {limit}");
                    Assert.True(triangle.Index2 < limit, $"{Path.GetFileName(path)}: index past {limit}");

                    checkedTriangles++;
                }
            }
        }

        Assert.True(checkedTriangles > 100_000, $"only {checkedTriangles} triangles checked");
        output.WriteLine($"{checkedTriangles:N0} triangles all index real vertices");
    }

    /// <summary>
    /// Every vertex sits inside its group's bounding box — for groups that have one.
    /// </summary>
    /// <remarks>
    /// Independent of the triangle check and catches a different fault: the bounds are the first
    /// 24 bytes of a group, so this fails if the previous group's record ended in the wrong place.
    /// <para>
    /// <b>Groups with an all-zero box are skipped, and there are real ones.</b> The extractor writes
    /// the bound it was given and some models arrive without one, so a degenerate box means "not
    /// recorded" rather than "empty". Anything that culls by group bounds has to treat it that way,
    /// or the geometry inside becomes invisible to collision — see TODO.md.
    /// </para>
    /// </remarks>
    [RequiresVmapFact]
    public void EveryVertex_LiesInsideItsGroupBounds()
    {
        const float Tolerance = 0.5f;
        long checkedVertices = 0;
        int degenerate = 0;

        foreach (string path in Models(200))
        {
            WorldModel model = WorldModelFile.Read(File.ReadAllBytes(path));

            foreach (WorldModelGroup group in model.Groups)
            {
                if (!group.HasBounds)
                {
                    degenerate++;
                    continue;
                }

                for (int i = 0; i < group.VertexCount; i++)
                {
                    float x = group.Vertices[(i * 3) + 0];
                    float y = group.Vertices[(i * 3) + 1];
                    float z = group.Vertices[(i * 3) + 2];

                    Assert.InRange(x, group.BoundsMinX - Tolerance, group.BoundsMaxX + Tolerance);
                    Assert.InRange(y, group.BoundsMinY - Tolerance, group.BoundsMaxY + Tolerance);
                    Assert.InRange(z, group.BoundsMinZ - Tolerance, group.BoundsMaxZ + Tolerance);

                    checkedVertices++;
                }
            }
        }

        Assert.True(checkedVertices > 50_000, $"only {checkedVertices} vertices checked");
        output.WriteLine(
            $"{checkedVertices:N0} vertices inside their group bounds; " +
            $"{degenerate} groups had no recorded box");
    }

    /// <summary>
    /// Each group's mesh BIH indexes its triangles, and the model's BIH indexes its groups.
    /// </summary>
    /// <remarks>
    /// Two levels of tree, and they index different things — a ray descends the group tree to find
    /// candidate groups, then each group's own tree to find candidate triangles. Confusing the two
    /// gives a tree whose indices happen to be in range and point at the wrong geometry.
    /// </remarks>
    [RequiresVmapFact]
    public void BothTreeLevels_IndexTheRightThings()
    {
        int meshTrees = 0, groupTrees = 0;

        foreach (string path in Models(200))
        {
            WorldModel model = WorldModelFile.Read(File.ReadAllBytes(path));

            if (model.GroupTree is { } groupTree)
            {
                foreach (uint index in groupTree.Objects)
                {
                    Assert.True(
                        index < (uint)model.Groups.Length,
                        $"{Path.GetFileName(path)}: group index {index} past {model.Groups.Length}");
                }

                groupTrees++;
            }

            foreach (WorldModelGroup group in model.Groups)
            {
                if (group.MeshTree is not { } meshTree)
                {
                    continue;
                }

                foreach (uint index in meshTree.Objects)
                {
                    Assert.True(
                        index < (uint)group.Triangles.Length,
                        $"{Path.GetFileName(path)}: triangle index {index} past {group.Triangles.Length}");
                }

                // The walk has to be structurally sound at this level too.
                foreach (BihNode node in meshTree.Walk())
                {
                    if (node.IsLeaf)
                    {
                        Assert.True(node.Offset + node.ObjectCount <= (uint)meshTree.Objects.Length);
                    }
                }

                meshTrees++;
            }
        }

        Assert.True(meshTrees > 0 && groupTrees > 0);
        output.WriteLine($"{groupTrees} group trees, {meshTrees} mesh trees, all in range");
    }

    /// <summary>
    /// Water inside models parses, in both of its two record shapes.
    /// </summary>
    /// <remarks>
    /// A liquid with tiles carries a corner-height grid one larger than the tile grid in each axis,
    /// plus a flag per tile. A liquid with no tiles carries a single height and nothing else —
    /// a different shape, not an empty one. Reading the grid form for the tileless case would
    /// consume whatever follows it.
    /// </remarks>
    [RequiresVmapFact]
    public void ModelLiquids_HaveConsistentGrids()
    {
        int withTiles = 0, tileless = 0;

        foreach (string path in Models(600))
        {
            WorldModel model = WorldModelFile.Read(File.ReadAllBytes(path));

            foreach (WorldModelGroup group in model.Groups)
            {
                if (group.Liquid is not { } liquid)
                {
                    continue;
                }

                if (liquid.TilesX > 0 && liquid.TilesY > 0)
                {
                    Assert.Equal((int)((liquid.TilesX + 1) * (liquid.TilesY + 1)), liquid.Heights.Length);
                    Assert.Equal((int)(liquid.TilesX * liquid.TilesY), liquid.Flags.Length);
                    withTiles++;
                }
                else
                {
                    Assert.Single(liquid.Heights);
                    Assert.Empty(liquid.Flags);
                    tileless++;
                }

                foreach (float height in liquid.Heights)
                {
                    Assert.True(float.IsFinite(height), "a liquid height was not a number");
                }
            }
        }

        output.WriteLine($"{withTiles} liquids with a tile grid, {tileless} without");
    }

    /// <summary>
    /// A group with no vertices ends its record early, and the group after it still reads.
    /// </summary>
    /// <remarks>
    /// The format's one content-driven branch. Reading a <c>TRIM</c> chunk that is not there
    /// consumes the next group's bounding box, and every group after that is garbage — so a model
    /// containing both kinds of group is the case worth naming.
    /// </remarks>
    [RequiresVmapFact]
    public void ModelsMixingEmptyAndSolidGroups_StillParse()
    {
        int mixed = 0;

        foreach (string path in Models(600))
        {
            WorldModel model = WorldModelFile.Read(File.ReadAllBytes(path));

            bool hasEmpty = model.Groups.Any(g => !g.HasGeometry);
            bool hasSolid = model.Groups.Any(g => g.HasGeometry);

            if (!hasEmpty || !hasSolid)
            {
                continue;
            }

            mixed++;

            // The solid groups after an empty one must still be coherent.
            foreach (WorldModelGroup group in model.Groups.Where(g => g.HasGeometry))
            {
                Assert.NotEmpty(group.Triangles);

                foreach (MeshTriangle triangle in group.Triangles)
                {
                    Assert.True(triangle.Index0 < (uint)group.VertexCount);
                }
            }
        }

        Assert.True(mixed > 0, "no model mixed empty and solid groups — the early exit is untested");
        output.WriteLine($"{mixed} models mix empty and solid groups and parse correctly");
    }

    /// <summary>
    /// Every model any tile on map 0 places can actually be opened and read.
    /// </summary>
    /// <remarks>
    /// The end-to-end check across both layers: resolve a spawn's model file by the terminator rule,
    /// then parse it. A failure here is either the placement reader picking the wrong file or the
    /// geometry reader mis-parsing it, and both matter.
    /// </remarks>
    [RequiresVmapFact]
    public void EveryModelPlacedOnMapZero_Reads()
    {
        HashSet<string> seen = [];
        List<string> failures = [];
        long triangles = 0;

        foreach (string tilePath in VmapData.Tiles("000", 25))
        {
            foreach (VmapTileSpawn placement in VmapFile.ReadTile(File.ReadAllBytes(tilePath)))
            {
                if (!seen.Add(placement.Spawn.ModelFileName))
                {
                    continue;
                }

                string modelPath = Path.Combine(VmapData.Directory, placement.Spawn.ModelFileName);

                try
                {
                    WorldModel model = WorldModelFile.Read(File.ReadAllBytes(modelPath));
                    triangles += model.Groups.Sum(g => (long)g.Triangles.Length);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException)
                {
                    if (failures.Count < 5)
                    {
                        failures.Add($"{placement.Spawn.ModelFileName}: {exception.Message}");
                    }
                }
            }
        }

        Assert.Empty(failures);
        Assert.True(seen.Count > 20, $"only {seen.Count} distinct models seen");

        output.WriteLine($"{seen.Count} distinct models placed on map 0, {triangles:N0} triangles, all read");
    }

    /// <summary>
    /// Every model file in the extraction parses.
    /// </summary>
    /// <remarks>
    /// The whole set, not a sample. A content-driven format fails on the shapes a sample misses —
    /// the group with no vertices, the liquid with no tiles, the model with no groups at all — and
    /// there is no reason to guess which files carry them when reading all of them takes a second.
    /// </remarks>
    [RequiresVmapFact]
    public void EveryModelInTheExtraction_Parses()
    {
        int models = 0, groups = 0;
        long triangles = 0;
        List<string> failures = [];

        foreach (string path in Directory.EnumerateFiles(VmapData.Directory, "*.vmo"))
        {
            try
            {
                WorldModel model = WorldModelFile.Read(File.ReadAllBytes(path));

                models++;
                groups += model.Groups.Length;
                triangles += model.Groups.Sum(g => (long)g.Triangles.Length);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException)
            {
                if (failures.Count < 8)
                {
                    failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }

        Assert.Empty(failures);
        Assert.True(models > 7_000, $"only {models} models parsed");

        output.WriteLine($"all {models:N0} models parse: {groups:N0} groups, {triangles:N0} triangles");
    }

    private static IEnumerable<string> Models(int limit) =>
        Directory.EnumerateFiles(VmapData.Directory, "*.vmo")
            .Order(StringComparer.Ordinal)
            .Take(limit);
}
