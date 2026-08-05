using System.Globalization;
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
}
