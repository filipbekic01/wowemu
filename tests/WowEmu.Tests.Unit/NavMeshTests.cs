using System.Globalization;
using System.IO;
using WowEmu.Data.Client;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>Marks a test that reads extracted navmesh tiles.</summary>
public sealed class RequiresNavMeshFactAttribute : FactAttribute
{
    public RequiresNavMeshFactAttribute()
    {
        if (!NavMeshData.Available)
        {
            Skip = $"no extracted navmesh at {NavMeshData.Directory}";
        }
    }
}

/// <summary>Where the extracted <c>.mmap</c> and <c>.mmtile</c> files are, if anywhere.</summary>
internal static class NavMeshData
{
    static NavMeshData()
    {
        Directory = Path.Combine(ClientData.DataDirectory, "mmaps");

        Available = System.IO.Directory.Exists(Directory)
            && System.IO.Directory.EnumerateFiles(Directory, "*.mmtile").Any();
    }

    public static string Directory { get; }

    public static bool Available { get; }

    /// <summary>Eastern Kingdoms, whose tiles every human character walks on.</summary>
    public static string EasternKingdomsParams => Path.Combine(Directory, "000.mmap");

    /// <summary>A specific tile by file name, or null when it is not extracted.</summary>
    public static string? Tile(string fileName)
    {
        string path = Path.Combine(Directory, fileName);
        return File.Exists(path) ? path : null;
    }

    public static IEnumerable<string> Tiles(string mapPrefix, int limit) =>
        System.IO.Directory
            .EnumerateFiles(Directory, $"{mapPrefix}*.mmtile")
            .Order(StringComparer.Ordinal)
            .Take(limit);
}

/// <summary>
/// PLAN.md §3.4.1's Detour compatibility spike, and Phase 8's first task.
/// </summary>
/// <remarks>
/// The vendored Detour is patched: <c>DT_POLYREF64</c> is on, and the salt/tile/poly split is
/// 12/21/31 against stock's 16/28/20. Upstream's own comment says tiles built with 32-bit refs are
/// not compatible with 64-bit ones. Risk #2 in PLAN.md's register is that a stock Detour port reads
/// every tile wrongly — and it would read them into plausible-looking garbage, not an error.
/// <para>
/// These settle it from the files themselves rather than from the C++ headers, which matters
/// because the two reference checkouts are already known to be at different points in AzerothCore's
/// history. "The C++ defines <c>DT_POLYREF64</c>" and "the tiles we have were built with it" are
/// different claims, and only the second one decides what we need.
/// </para>
/// </remarks>
public sealed class NavMeshSpikeTests(ITestOutputHelper output)
{
    [RequiresNavMeshFact]
    public void MmapParams_AreReadable()
    {
        byte[] data = File.ReadAllBytes(NavMeshData.EasternKingdomsParams);

        Assert.Equal(NavMeshFile.NavMeshParamsSize, data.Length);

        NavMeshParams parameters = NavMeshFile.ReadParams(data);

        // The tile grid is the same 533.33-yard grid the terrain uses, and every tile added to the
        // mesh must agree with these or Detour rejects it.
        Assert.Equal(533.3333f, parameters.TileWidth, 0.01f);
        Assert.Equal(533.3333f, parameters.TileHeight, 0.01f);
        Assert.True(parameters.MaxTiles > 0);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"origin ({parameters.OriginX:F1}, {parameters.OriginY:F1}, {parameters.OriginZ:F1}), " +
            $"tile {parameters.TileWidth:F2} x {parameters.TileHeight:F2}, " +
            $"maxTiles {parameters.MaxTiles}, maxPolys 0x{(uint)parameters.MaxPolys:X8} " +
            $"({parameters.PolyBits} poly bits)"));
    }

    /// <summary>
    /// The clinching evidence: the file records the patched <c>DT_POLY_BITS</c> directly.
    /// </summary>
    /// <remarks>
    /// <c>mmaps_generator</c> writes <c>maxPolys = 1 &lt;&lt; DT_POLY_BITS</c>. Stock Detour's
    /// constant is 20, which would put 1,048,576 in this field. Ours holds <c>0x80000000</c> —
    /// <c>1 &lt;&lt; 31</c>, overflowing the signed <c>int</c> Detour declares it as, which is why
    /// the field reads negative.
    /// <para>
    /// This is better evidence than the tile-size test, because it pins the <i>bit split</i> and not
    /// merely the reference width. A Detour with 64-bit refs but stock's 16/28/20 split would pass
    /// the size test and still decode every reference to the wrong polygon.
    /// </para>
    /// </remarks>
    [RequiresNavMeshFact]
    public void TheMmapParams_RecordThePatchedPolyBits()
    {
        NavMeshParams parameters = NavMeshFile.ReadParams(
            File.ReadAllBytes(NavMeshData.EasternKingdomsParams));

        Assert.Equal(31, parameters.PolyBits);
        Assert.Equal(0x80000000u, (uint)parameters.MaxPolys);

        // Stock Detour would have written this instead.
        Assert.NotEqual(1 << 20, parameters.MaxPolys);
    }

    [RequiresNavMeshFact]
    public void TileHeaders_CarryTheExpectedMagicAndVersion()
    {
        foreach (string path in NavMeshData.Tiles("000", 20))
        {
            byte[] data = File.ReadAllBytes(path);
            MmapTileHeader header = NavMeshFile.ReadTileHeader(data);

            Assert.Equal(NavMeshFile.MmapMagic, header.MmapMagic);
            Assert.Equal(NavMeshFile.MmapVersion, header.MmapVersion);
            Assert.Equal((uint)NavMeshFile.DetourVersion, header.DetourVersion);

            // The recorded size must account for everything after the 56-byte header.
            Assert.Equal(data.Length - NavMeshFile.MmapTileHeaderSize, (int)header.DataSize);
        }
    }

    [RequiresNavMeshFact]
    public void DetourHeaders_AreWellFormed()
    {
        foreach (string path in NavMeshData.Tiles("000", 20))
        {
            byte[] data = File.ReadAllBytes(path);
            DetourMeshHeader mesh = NavMeshFile.ReadMeshHeader(data.AsSpan(NavMeshFile.MmapTileHeaderSize));

            Assert.Equal(NavMeshFile.DetourMagic, mesh.Magic);
            Assert.Equal(NavMeshFile.DetourVersion, mesh.Version);

            Assert.True(mesh.PolyCount > 0, $"{Path.GetFileName(path)} has no polygons");
            Assert.True(mesh.VertCount > 0);
            Assert.True(mesh.BoundsMaxX >= mesh.BoundsMinX);
            Assert.True(mesh.BoundsMaxZ >= mesh.BoundsMinZ);
        }
    }

    /// <summary>
    /// <b>The spike.</b> Whether the extracted tiles use 64-bit polygon references.
    /// </summary>
    /// <remarks>
    /// Decided by size. <c>DT_POLYREF64</c> widens <c>dtLink::ref</c> from 4 bytes to 8 and, with
    /// alignment, <c>sizeof(dtLink)</c> from 12 to 16. A tile stores <c>maxLinkCount</c> of them, so
    /// the two layouts predict sizes differing by thousands of bytes on a real tile. Exactly one of
    /// them can match.
    /// </remarks>
    [RequiresNavMeshFact]
    public void ExtractedTiles_Use64BitPolyRefs()
    {
        int checkedTiles = 0;

        foreach (string path in NavMeshData.Tiles("000", 40))
        {
            byte[] data = File.ReadAllBytes(path);

            MmapTileHeader tile = NavMeshFile.ReadTileHeader(data);
            DetourMeshHeader mesh = NavMeshFile.ReadMeshHeader(data.AsSpan(NavMeshFile.MmapTileHeaderSize));

            PolyRefWidth width = NavMeshFile.DetectPolyRefWidth(mesh, (int)tile.DataSize);

            if (checkedTiles == 0)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(path)}: {mesh.PolyCount} polys, {mesh.MaxLinkCount} links, " +
                    $"actual {tile.DataSize} bytes; " +
                    $"32-bit layout predicts {NavMeshFile.ExpectedTileSize(mesh, 12)}, " +
                    $"64-bit predicts {NavMeshFile.ExpectedTileSize(mesh, 16)}"));
            }

            Assert.Equal(PolyRefWidth.Bits64, width);
            checkedTiles++;
        }

        Assert.True(checkedTiles > 0, "no tiles were checked");
        output.WriteLine($"{checkedTiles} tiles all use 64-bit polyrefs");
    }

    /// <summary>
    /// The 32-bit layout is not merely unused — it is wrong by a wide margin.
    /// </summary>
    /// <remarks>
    /// Worth asserting separately. If the two predictions ever came out close, the size test above
    /// would be distinguishing between them on a handful of bytes, and a padding change could flip
    /// it silently.
    /// </remarks>
    [RequiresNavMeshFact]
    public void The32BitLayout_IsWrongByThousandsOfBytes()
    {
        string path = NavMeshData.Tiles("000", 1).Single();
        byte[] data = File.ReadAllBytes(path);

        MmapTileHeader tile = NavMeshFile.ReadTileHeader(data);
        DetourMeshHeader mesh = NavMeshFile.ReadMeshHeader(data.AsSpan(NavMeshFile.MmapTileHeaderSize));

        int shortfall = (int)tile.DataSize - NavMeshFile.ExpectedTileSize(mesh, 12);

        Assert.Equal(4 * mesh.MaxLinkCount, shortfall);
        Assert.True(shortfall > 1000, $"only {shortfall} bytes apart — too close to distinguish safely");
    }

    /// <summary>
    /// The whole tile parses, and every section consumes exactly what the layout predicts.
    /// </summary>
    /// <remarks>
    /// <see cref="NavMeshFile.ReadTile"/> throws if the running offset does not land on the
    /// predicted total, so reaching the end at all is the first real check on the struct sizes.
    /// </remarks>
    [RequiresNavMeshFact]
    public void WholeTiles_Parse()
    {
        int tiles = 0, polys = 0, verts = 0, offMesh = 0;

        foreach (string path in NavMeshData.Tiles("000", 60))
        {
            DetourTile tile = NavMeshFile.ReadTile(File.ReadAllBytes(path));

            Assert.Equal(3 * tile.Header.VertCount, tile.Vertices.Length);
            Assert.Equal(tile.Header.PolyCount, tile.Polys.Length);
            Assert.Equal(tile.Header.DetailMeshCount, tile.DetailMeshes.Length);
            Assert.Equal(3 * tile.Header.DetailVertCount, tile.DetailVertices.Length);
            Assert.Equal(4 * tile.Header.DetailTriCount, tile.DetailTriangles.Length);
            Assert.Equal(tile.Header.BvNodeCount, tile.BvTree.Length);
            Assert.Equal(tile.Header.OffMeshConCount, tile.OffMeshConnections.Length);

            tiles++;
            polys += tile.Polys.Length;
            verts += tile.Header.VertCount;
            offMesh += tile.OffMeshConnections.Length;
        }

        Assert.True(tiles > 0);
        output.WriteLine($"{tiles} tiles: {polys} polygons, {verts} vertices, {offMesh} off-mesh connections");
    }

    /// <summary>
    /// Every vertex lies inside the bounding box its own header declares.
    /// </summary>
    /// <remarks>
    /// This is the check that catches a layout that is subtly wrong. A misplaced section still
    /// parses — it reads neighbouring bytes as floats — but those floats are arbitrary, and
    /// arbitrary floats do not land inside a 533-yard box by accident. A small tolerance is allowed
    /// because Detour's own bounds come from the quantised build, not from the vertices.
    /// </remarks>
    [RequiresNavMeshFact]
    public void EveryVertex_LiesInsideItsTileBounds()
    {
        const float Tolerance = 1.0f;
        int checkedVerts = 0;

        foreach (string path in NavMeshData.Tiles("000", 40))
        {
            DetourTile tile = NavMeshFile.ReadTile(File.ReadAllBytes(path));
            DetourMeshHeader h = tile.Header;

            for (int i = 0; i < h.VertCount; i++)
            {
                float x = tile.Vertices[(i * 3) + 0];
                float y = tile.Vertices[(i * 3) + 1];
                float z = tile.Vertices[(i * 3) + 2];

                Assert.InRange(x, h.BoundsMinX - Tolerance, h.BoundsMaxX + Tolerance);
                Assert.InRange(y, h.BoundsMinY - Tolerance, h.BoundsMaxY + Tolerance);
                Assert.InRange(z, h.BoundsMinZ - Tolerance, h.BoundsMaxZ + Tolerance);

                checkedVerts++;
            }
        }

        Assert.True(checkedVerts > 10_000, $"only {checkedVerts} vertices checked");
        output.WriteLine($"{checkedVerts} vertices all inside their tile bounds");
    }

    /// <summary>
    /// Polygons index vertices that exist, and declare a sane vertex count.
    /// </summary>
    /// <remarks>
    /// The second independent check on the layout: <c>dtPoly</c> is 32 bytes with the counts at the
    /// very end, so reading it one byte off puts a vertex index byte into <c>vertCount</c> and the
    /// numbers stop making sense immediately.
    /// </remarks>
    [RequiresNavMeshFact]
    public void EveryPolygon_IndexesRealVertices()
    {
        int ground = 0, offMeshPolys = 0;

        foreach (string path in NavMeshData.Tiles("000", 40))
        {
            DetourTile tile = NavMeshFile.ReadTile(File.ReadAllBytes(path));

            foreach (DetourPoly poly in tile.Polys)
            {
                Assert.InRange(poly.VertCount, (byte)1, (byte)DetourPoly.MaxVerts);

                for (int v = 0; v < poly.VertCount; v++)
                {
                    Assert.InRange(poly.Verts[v], 0, tile.Header.VertCount - 1);
                }

                // NavTerrain is a 6-bit field; the type is the top two bits and is 0 or 1.
                Assert.InRange(poly.Type, (byte)0, (byte)1);

                if (poly.IsOffMeshConnection)
                {
                    offMeshPolys++;
                }
                else
                {
                    ground++;
                }
            }
        }

        Assert.True(ground > 0);
        output.WriteLine($"{ground} ground polygons, {offMeshPolys} off-mesh connection polygons");
    }

    /// <summary>
    /// The detail mesh's bases and counts stay inside the arrays they index.
    /// </summary>
    /// <remarks>
    /// The detail mesh is where real heights live — the polygon layer is flat. If these bases were
    /// misread, height queries would sample the wrong triangles and be wrong by metres without
    /// anything failing.
    /// </remarks>
    [RequiresNavMeshFact]
    public void TheDetailMesh_StaysInsideItsArrays()
    {
        foreach (string path in NavMeshData.Tiles("000", 40))
        {
            DetourTile tile = NavMeshFile.ReadTile(File.ReadAllBytes(path));

            // One detail sub-mesh per polygon.
            Assert.Equal(tile.Header.PolyCount, tile.DetailMeshes.Length);

            foreach (DetourPolyDetail detail in tile.DetailMeshes)
            {
                Assert.True(
                    detail.VertBase + detail.VertCount <= (uint)tile.Header.DetailVertCount,
                    $"detail vertices {detail.VertBase}+{detail.VertCount} exceed {tile.Header.DetailVertCount}");

                Assert.True(
                    detail.TriBase + detail.TriCount <= (uint)tile.Header.DetailTriCount,
                    $"detail triangles {detail.TriBase}+{detail.TriCount} exceed {tile.Header.DetailTriCount}");
            }
        }
    }

    /// <summary>
    /// The BV tree's escape offsets stay inside the tree, and its leaves name real polygons.
    /// </summary>
    /// <remarks>
    /// A negative index is an escape sequence saying how far to skip, not a polygon. Treating one as
    /// an index is how a position lookup returns a polygon from somewhere else in the tile.
    /// </remarks>
    [RequiresNavMeshFact]
    public void TheBvTree_IsInternallyConsistent()
    {
        int leaves = 0, escapes = 0;

        foreach (string path in NavMeshData.Tiles("000", 40))
        {
            DetourTile tile = NavMeshFile.ReadTile(File.ReadAllBytes(path));

            foreach (DetourBvNode node in tile.BvTree)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    Assert.True(
                        node.BoundsMin[axis] <= node.BoundsMax[axis],
                        "a BV node's minimum exceeded its maximum");
                }

                if (node.IsLeaf)
                {
                    Assert.InRange(node.Index, 0, tile.Header.PolyCount - 1);
                    leaves++;
                }
                else
                {
                    Assert.InRange(-node.Index, 1, tile.BvTree.Length);
                    escapes++;
                }
            }
        }

        Assert.True(leaves > 0);
        output.WriteLine($"{leaves} BV leaves, {escapes} escape nodes");
    }

    /// <summary>The parse holds on other maps too, not just Eastern Kingdoms.</summary>
    [RequiresNavMeshFact]
    public void TilesOnOtherMaps_ParseToo()
    {
        int parsed = 0;

        // Kalimdor, Outland, Northrend.
        foreach (string prefix in (string[])["001", "530", "571"])
        {
            foreach (string path in NavMeshData.Tiles(prefix, 12))
            {
                DetourTile tile = NavMeshFile.ReadTile(File.ReadAllBytes(path));
                Assert.True(tile.Polys.Length > 0);
                parsed++;
            }
        }

        Assert.True(parsed > 0, "no tiles found on the other maps");
        output.WriteLine($"{parsed} tiles parsed across Kalimdor, Outland and Northrend");
    }

    /// <summary>
    /// The one tile in the whole extraction that has off-mesh connections.
    /// </summary>
    /// <remarks>
    /// A scan of all 3,682 extracted tiles — 11,073,201 polygons — found exactly <b>two</b> off-mesh
    /// connections, both in <c>5622031.mmtile</c> (Blade's Edge Arena). Every other tile has none,
    /// so without this test that branch of the reader is never executed by anything and its struct
    /// layout is asserted only by the size arithmetic.
    /// <para>
    /// If this ever starts failing after a re-extraction, suspect the extractor's configuration
    /// before suspecting the reader — off-mesh connections are generated, not authored.
    /// </para>
    /// </remarks>
    [RequiresNavMeshFact]
    public void TheOneTileWithOffMeshConnections_ParsesThem()
    {
        string? path = NavMeshData.Tile("5622031.mmtile");

        // Asserted rather than skipped: if this file is missing from an extraction that has 3,682
        // others, that is a fact worth surfacing, not one to pass over quietly.
        Assert.True(path is not null, "5622031.mmtile is not in the extraction");

        DetourTile tile = NavMeshFile.ReadTile(File.ReadAllBytes(path!));

        Assert.Equal(2, tile.OffMeshConnections.Length);

        foreach (DetourOffMeshConnection connection in tile.OffMeshConnections)
        {
            // A connection with no radius could never be entered.
            Assert.True(connection.Radius > 0f, "off-mesh connection had no radius");

            // Its polygon must exist, and must be flagged as a connection rather than ground.
            Assert.InRange(connection.Poly, 0, tile.Header.PolyCount - 1);
            Assert.True(
                tile.Polys[connection.Poly].IsOffMeshConnection,
                "the polygon an off-mesh connection names was not typed as one");

            // Endpoints inside the tile, generously — they are allowed to reach its edge.
            Assert.InRange(connection.StartX, tile.Header.BoundsMinX - 5f, tile.Header.BoundsMaxX + 5f);
            Assert.InRange(connection.EndX, tile.Header.BoundsMinX - 5f, tile.Header.BoundsMaxX + 5f);

            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"({connection.StartX:F1}, {connection.StartY:F1}, {connection.StartZ:F1}) -> " +
                $"({connection.EndX:F1}, {connection.EndY:F1}, {connection.EndZ:F1}) " +
                $"r={connection.Radius:F2} poly={connection.Poly}"));
        }
    }

    /// <summary>
    /// The polygon count across every extracted tile, as a scale reference.
    /// </summary>
    /// <remarks>
    /// Reads only headers, so it stays fast over 3,682 files. It is here to make the size of what
    /// pathfinding will work over concrete, and to catch a re-extraction that silently produced far
    /// less than before.
    /// </remarks>
    [RequiresNavMeshFact]
    public void TheWholeExtraction_HasTheExpectedScale()
    {
        long polygons = 0;
        int tiles = 0;

        foreach (string path in Directory.EnumerateFiles(NavMeshData.Directory, "*.mmtile"))
        {
            byte[] head = ReadPrefix(path, NavMeshFile.MmapTileHeaderSize + NavMeshFile.DetourMeshHeaderSize);

            if (head.Length < NavMeshFile.MmapTileHeaderSize + NavMeshFile.DetourMeshHeaderSize)
            {
                continue;
            }

            DetourMeshHeader header = NavMeshFile.ReadMeshHeader(head.AsSpan(NavMeshFile.MmapTileHeaderSize));
            polygons += header.PolyCount;
            tiles++;
        }

        Assert.True(tiles > 3_000, $"only {tiles} tiles found");
        Assert.True(polygons > 10_000_000, $"only {polygons} polygons found");

        output.WriteLine($"{tiles} tiles, {polygons:N0} polygons across every map");
    }

    private static byte[] ReadPrefix(string path, int count)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[count];
        int read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return read == count ? buffer : buffer[..read];
    }
}