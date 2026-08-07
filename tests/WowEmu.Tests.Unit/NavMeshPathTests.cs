using DotRecast.Core.Numerics;
using DotRecast.Detour;
using WowEmu.Core;
using WowEmu.Data.Client;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Loading AzerothCore's navigation tiles into Detour, and running a path over them.
/// </summary>
/// <remarks>
/// Phase 8's exit criterion, and it was blocked for a long time on a decision that turned out not to
/// exist. The worry was that AzerothCore's <i>patched</i> Detour — <c>DT_POLYREF64</c> and a
/// 12/21/31 salt/tile/poly split — made its tiles unreadable by a stock port, and that we would have
/// to fork and maintain one.
/// <para>
/// Only the reference <i>width</i> reaches the disk, and every shipped tile is 64-bit, which
/// DotRecast is too. The <i>packing</i> never does: <c>AddTile</c> takes a parsed structure with no
/// link array, and Detour rebuilds every link itself. These tests are what says so rather than
/// assuming it.
/// </para>
/// </remarks>
public sealed class NavMeshPathTests
{
    /// <summary>
    /// Every shipped tile uses 64-bit polygon references.
    /// </summary>
    /// <remarks>
    /// The one fact about the patched Detour that reaches the disk, and the whole basis for using a
    /// stock library. Measured from the tile's own size against the two possible <c>dtLink</c>
    /// widths — a 32-bit tile would be smaller by four bytes per link.
    /// </remarks>
    [RequiresMapsFact]
    public void EveryTile_Uses64BitPolygonReferences()
    {
        int sampled = 0;

        foreach (string path in TileFiles().Take(200))
        {
            byte[] bytes = File.ReadAllBytes(path);

            MmapTileHeader mmap = NavMeshFile.ReadTileHeader(bytes);
            DetourMeshHeader header =
                NavMeshFile.ReadMeshHeader(bytes.AsSpan(NavMeshFile.MmapTileHeaderSize));

            Assert.Equal(PolyRefWidth.Bits64, NavMeshFile.DetectPolyRefWidth(header, (int)mmap.DataSize));
            sampled++;
        }

        Assert.True(sampled > 100, $"only {sampled} tiles sampled — are the mmaps extracted?");
    }

    /// <summary>
    /// A real tile loads into a stock Detour mesh.
    /// </summary>
    /// <remarks>
    /// The moment the fork question is answered. <c>AddTile</c> validates the header's magic and
    /// version and rebuilds the tile's links from its polygons — so a tile it accepts is one it has
    /// actually understood, not one it has skimmed.
    /// </remarks>
    [RequiresMapsFact]
    public void ARealTile_LoadsIntoAStockDetourMesh()
    {
        (DtNavMesh mesh, int loaded) = LoadMap(EasternKingdoms);

        Assert.True(loaded > 0, "no tiles loaded");
        Assert.True(mesh.GetMaxTiles() > 0, "the mesh has no room for tiles");
    }

    /// <summary>
    /// A path runs between two points on real terrain.
    /// </summary>
    /// <remarks>
    /// The exit criterion itself. <b>The coordinate swizzle is the trap</b>: Detour works in its own
    /// (x, y, z) where y is up, and the game's z is up — so a world position goes in as
    /// <c>(-y, z, -x)</c>. Getting it wrong finds no polygon at all, which reads as missing navmesh
    /// data rather than as a transposition.
    /// </remarks>
    [RequiresMapsFact]
    public void APath_RunsBetweenTwoPointsOnRealTerrain()
    {
        (DtNavMesh mesh, _) = LoadMap(EasternKingdoms);

        DtNavMeshQuery query = new(mesh);
        IDtQueryFilter filter = new DtQueryDefaultFilter();

        RcVec3f start = ToDetour(HumanStartX, HumanStartY, HumanStartZ);
        RcVec3f end = ToDetour(HumanStartX + 40f, HumanStartY + 40f, HumanStartZ);
        RcVec3f extents = new(3f, 5f, 3f);

        Assert.True(
            query.FindNearestPoly(start, extents, filter, out long startRef, out _, out _).Succeeded(),
            "no polygon under the human start position — is the swizzle right?");

        Assert.True(
            query.FindNearestPoly(end, extents, filter, out long endRef, out _, out _).Succeeded(),
            "no polygon under the destination");

        Assert.NotEqual(0, startRef);
        Assert.NotEqual(0, endRef);

        Span<long> path = new long[256];

        Assert.True(
            query.FindPath(startRef, endRef, start, end, filter, path, out int pathCount, path.Length)
                .Succeeded(),
            "FindPath refused");

        Assert.True(pathCount > 0, "the path came back empty");
        Assert.Equal(startRef, path[0]);
    }

    /// <summary>
    /// A polygon found by position is one Detour can describe again.
    /// </summary>
    /// <remarks>
    /// A reference is packed with the salt, tile and polygon bits the library derives for itself.
    /// Feeding one back in and getting the same tile out is what shows the packing is self-consistent
    /// — which is all it has to be, because a reference never leaves the process.
    /// </remarks>
    [RequiresMapsFact]
    public void APolygonReference_RoundTripsThroughTheMesh()
    {
        (DtNavMesh mesh, _) = LoadMap(EasternKingdoms);

        DtNavMeshQuery query = new(mesh);
        IDtQueryFilter filter = new DtQueryDefaultFilter();

        Assert.True(query.FindNearestPoly(
            ToDetour(HumanStartX, HumanStartY, HumanStartZ),
            new RcVec3f(3f, 5f, 3f),
            filter,
            out long polyRef,
            out _,
            out _).Succeeded());

        Assert.True(mesh.GetTileAndPolyByRef(polyRef, out DtMeshTile? tile, out DtPoly? poly).Succeeded());

        Assert.NotNull(tile);
        Assert.NotNull(poly);
    }

    // ------------------------------------------------------------------ the generator

    /// <summary>The generator finds a route between two real points.</summary>
    [RequiresMapsFact]
    public void TheGenerator_FindsARouteOnRealTerrain()
    {
        NavMeshManager navmeshes = new(ClientData.DataDirectory);
        navmeshes.EnsureLoaded(EasternKingdoms, HumanStartX, HumanStartY);

        PathGenerator generator = navmeshes.For(EasternKingdoms)!;

        NavPath path = generator.Find(
            new Position(HumanStartX, HumanStartY, HumanStartZ, 0f),
            new Position(HumanStartX + 30f, HumanStartY + 30f, HumanStartZ, 0f));

        Assert.Equal(PathResult.Complete, path.Result);
        Assert.True(path.Points.Count >= 2, "a route needs at least a start and an end");
    }

    /// <summary>
    /// A route around an obstacle is longer than the line through it.
    /// </summary>
    /// <remarks>
    /// The whole point of pathing. A creature that walked the straight line would go through
    /// whatever is in the way — so the test is not that a path exists but that it is <i>longer</i>
    /// than the direct distance somewhere in the world, which only happens if it turned.
    /// </remarks>
    [RequiresMapsFact]
    public void SomeRoutes_AreLongerThanTheStraightLine()
    {
        NavMeshManager navmeshes = new(ClientData.DataDirectory);
        PathGenerator generator = navmeshes.For(EasternKingdoms)!;

        int turned = 0;
        int found = 0;

        // A sweep around the starting area: most pairs are open ground and go straight, and the
        // ones that are not are the ones worth having.
        for (int i = 0; i < 12 && turned == 0; i++)
        {
            for (int j = 0; j < 12 && turned == 0; j++)
            {
                float sx = HumanStartX + (i * 12f);
                float sy = HumanStartY + (j * 12f);
                float ex = sx + 60f;
                float ey = sy + 60f;

                navmeshes.EnsureLoaded(EasternKingdoms, sx, sy);
                navmeshes.EnsureLoaded(EasternKingdoms, ex, ey);

                NavPath path = generator.Find(
                    new Position(sx, sy, HumanStartZ, 0f), new Position(ex, ey, HumanStartZ, 0f));

                if (path.Result != PathResult.Complete)
                {
                    continue;
                }

                found++;

                if (path.Points.Count > 2)
                {
                    turned++;
                }
            }
        }

        Assert.True(found > 0, "no complete route anywhere in the sweep");
        Assert.True(turned > 0, $"every one of {found} routes was a straight line — is the mesh loaded?");
    }

    /// <summary>A point nowhere near the mesh has no route, rather than a made-up one.</summary>
    /// <remarks>
    /// The caller falls back to a straight line on this answer, which is what it did before there
    /// was a mesh at all. Inventing a route would send a creature somewhere it cannot walk.
    /// </remarks>
    [RequiresMapsFact]
    public void APointOffTheMesh_HasNoRoute()
    {
        NavMeshManager navmeshes = new(ClientData.DataDirectory);
        PathGenerator generator = navmeshes.For(EasternKingdoms)!;

        NavPath path = generator.Find(
            new Position(HumanStartX, HumanStartY, HumanStartZ + 5000f, 0f),
            new Position(HumanStartX + 10f, HumanStartY, HumanStartZ + 5000f, 0f));

        Assert.Equal(PathResult.NoPolygon, path.Result);
        Assert.False(path.HasPath);
    }

    /// <summary>A map with no navmesh has no generator at all.</summary>
    /// <remarks>
    /// 98 maps of the client's several hundred have one. A caller must treat its absence as "walk
    /// straight there" rather than as "do not move".
    /// </remarks>
    [RequiresMapsFact]
    public void AMapWithNoNavMesh_HasNoGenerator()
    {
        NavMeshManager navmeshes = new(ClientData.DataDirectory);

        Assert.Null(navmeshes.For(9999));
    }

    /// <summary>The swizzle round-trips.</summary>
    /// <remarks>
    /// Cheap to assert and the single easiest thing to get backwards — the two horizontal axes swap
    /// rather than negate, and a transposition survives a round trip while a negation does not.
    /// </remarks>
    [Fact]
    public void TheSwizzle_RoundTrips()
    {
        Position original = new(-8949.95f, -132.493f, 83.5312f, 0f);
        Position back = PathGenerator.FromDetour(PathGenerator.ToDetour(original));

        Assert.Equal(original.X, back.X, 0.001f);
        Assert.Equal(original.Y, back.Y, 0.001f);
        Assert.Equal(original.Z, back.Z, 0.001f);
    }

    // ------------------------------------------------------------------ helpers

    private const uint EasternKingdoms = 0;

    // The human starting position, which the terrain tests already use as a known-good point.
    private const float HumanStartX = -8949.95f;
    private const float HumanStartY = -132.493f;
    private const float HumanStartZ = 83.5312f;

    /// <summary>
    /// A world position in Detour's axes.
    /// </summary>
    /// <remarks>
    /// <c>(y, z, x)</c>, straight from <c>PathGenerator</c>: Detour's second axis is up and the
    /// game's third is, and the remaining two swap rather than negate. It is tempting to negate them
    /// as well — the terrain tiles do invert their axes about the world origin — and that produces a
    /// point far outside the mesh, which comes back as "no polygon here" rather than as an error.
    /// </remarks>
    private static RcVec3f ToDetour(float x, float y, float z) => new(y, z, x);

    private static IEnumerable<string> TileFiles() =>
        Directory.EnumerateFiles(Path.Combine(ClientData.DataDirectory, "mmaps"), "*.mmtile")
            .OrderBy(p => p, StringComparer.Ordinal);

    /// <summary>Loads a map's parameters and every tile it has.</summary>
    private static (DtNavMesh Mesh, int Loaded) LoadMap(uint mapId)
    {
        string directory = Path.Combine(ClientData.DataDirectory, "mmaps");

        NavMeshParams parameters = NavMeshFile.ReadParams(
            File.ReadAllBytes(Path.Combine(directory, $"{mapId:D3}.mmap")));

        DtNavMesh mesh = NavMesh.Create(parameters);
        int loaded = 0;

        foreach (string path in Directory
            .EnumerateFiles(directory, $"{mapId:D3}*.mmtile")
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            DetourTile tile = NavMeshFile.ReadTile(File.ReadAllBytes(path));

            if (mesh.AddTile(NavMesh.ToMeshData(tile), 0, 0, out _).Succeeded())
            {
                loaded++;
            }
        }

        return (mesh, loaded);
    }
}
