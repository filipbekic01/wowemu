using System.Numerics;
using WowEmu.Data.Client;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Map-level collision: loading the right tile, and answering in world coordinates.
/// </summary>
/// <remarks>
/// The layer where two independent inversions meet — the tile file names put Y before X, and the
/// world-to-vmap conversion mirrors X and Y about the world's midpoint. Each is silent on its own
/// and getting both wrong cancels out over part of the map, so these check the mapping exactly
/// rather than by eye.
/// </remarks>
public sealed class StaticMapTreeTests(ITestOutputHelper output)
{
    /// <summary>Northshire Valley, where every human character starts.</summary>
    private const float StartX = -8949.95f;
    private const float StartY = -132.493f;
    private const float StartZ = 83.53f;

    /// <summary>
    /// A vmap tile names its grid the opposite way round from a terrain tile.
    /// </summary>
    /// <remarks>
    /// <c>.map</c> is <c>{map}{gridX}{gridY}</c>; <c>.vmtile</c> is <c>{map}_{gridY}_{gridX}</c>.
    /// Two extractors, two conventions, and nothing anywhere says so.
    /// </remarks>
    [RequiresVmapFact]
    public void AVmapTile_NamesItsGridTheOppositeWayRoundFromTerrain()
    {
        Assert.Equal("000_29_27.vmtile", StaticMapTree.TileFileName(0, gridX: 27, gridY: 29));
        Assert.Equal("001_01_00.vmtile", StaticMapTree.TileFileName(1, gridX: 0, gridY: 1));
        Assert.Equal("530.vmtree", StaticMapTree.TreeFileName(530));

        // The terrain tile for the same grid puts them the other way about.
        Assert.Equal("0002729.map", TerrainTile.FileName(0, gridX: 27, gridY: 29));
    }

    [RequiresVmapFact]
    public void AMapWithCollisionData_LoadsItsTree()
    {
        StaticMapTree tree = NewTree(0);

        Assert.True(tree.IsAvailable);
    }

    /// <summary>
    /// Every model a tile lists sits in that tile, or in one touching it.
    /// </summary>
    /// <remarks>
    /// <b>The test that catches a mirrored world.</b> A spawn's stored position is in vmap
    /// coordinates; converting it back through the same mirror gives world coordinates, and the grid
    /// those fall in is compared with the grid whose file we opened.
    /// <para>
    /// Adjacency rather than equality, because a model is listed in every tile its <i>bounds</i>
    /// overlap while its stored position is a single origin — a cathedral spanning four tiles is
    /// listed in all four and has its origin in one. So the honest property is that the origin is
    /// never more than one grid away, and the overwhelming majority are exact.
    /// </para>
    /// <para>
    /// A swapped file name fails this by tens of grids, not one; dropping the coordinate mirror
    /// fails it by half the map. Neither would be noticed anywhere else.
    /// </para>
    /// </remarks>
    [RequiresVmapFact]
    public void EveryModelInATile_SitsInThatTileOrOneTouchingIt()
    {
        int checkedTiles = 0, checkedModels = 0, exact = 0;

        foreach ((int gridX, int gridY) in TilesWithModels(12))
        {
            string path = Path.Combine(VmapData.Directory, StaticMapTree.TileFileName(0, gridX, gridY));

            Assert.True(File.Exists(path), $"expected {path} to exist");

            foreach (VmapTileSpawn placement in VmapFile.ReadTile(File.ReadAllBytes(path)))
            {
                ModelSpawn spawn = placement.Spawn;

                // ToInternal is its own inverse, so this converts vmap coordinates back to world.
                Vector3 world = Collision.ToInternal(spawn.PositionX, spawn.PositionY, spawn.PositionZ);
                (int modelGridX, int modelGridY) = MapGeometry.GridFor(world.X, world.Y);

                Assert.True(
                    Math.Abs(modelGridX - gridX) <= 1 && Math.Abs(modelGridY - gridY) <= 1,
                    $"a model in tile ({gridX}, {gridY}) has its origin in ({modelGridX}, {modelGridY})");

                if (modelGridX == gridX && modelGridY == gridY)
                {
                    exact++;
                }

                checkedModels++;
            }

            checkedTiles++;
        }

        Assert.True(checkedTiles > 5, $"only {checkedTiles} tiles checked");

        // Adjacency above is the real check — a swapped file name misses by tens of grids and
        // fails it outright. This is the weaker secondary: overlap should be the exception, and it
        // measures at about 88 % exact on map 0. A clear majority is the assertion; the exact
        // figure is data, not a contract.
        Assert.True(
            exact * 2 > checkedModels,
            $"only {exact} of {checkedModels} models had their origin in their own tile");

        output.WriteLine(
            $"{checkedModels} models across {checkedTiles} tiles: {exact} exactly in their tile, " +
            $"{checkedModels - exact} in a touching one");
    }

    /// <summary>Asking about a position loads the tile that position sits in.</summary>
    [RequiresVmapFact]
    public void AskingAboutAPosition_LoadsItsTile()
    {
        StaticMapTree tree = NewTree(0);

        Assert.Equal(0, tree.LoadedTileCount);

        // Any query reaching the tree loads what it needs.
        tree.IsInLineOfSight(StartX, StartY, StartZ + 5f, StartX + 10f, StartY, StartZ + 5f);

        Assert.True(tree.LoadedTileCount > 0);
        Assert.True(tree.InstanceCount > 0, "the tile around the human start placed no models");

        output.WriteLine($"{tree.LoadedTileCount} tiles loaded, {tree.InstanceCount} model instances");
    }

    /// <summary>A tile is loaded once however many times it is asked about.</summary>
    [RequiresVmapFact]
    public void RepeatedQueries_DoNotReloadTheTile()
    {
        StaticMapTree tree = NewTree(0);

        tree.IsInLineOfSight(StartX, StartY, StartZ + 5f, StartX + 5f, StartY, StartZ + 5f);

        int tiles = tree.LoadedTileCount;
        int instances = tree.InstanceCount;

        for (int i = 0; i < 20; i++)
        {
            tree.IsInLineOfSight(StartX, StartY, StartZ + 5f, StartX + 5f, StartY, StartZ + 5f);
        }

        Assert.Equal(tiles, tree.LoadedTileCount);
        Assert.Equal(instances, tree.InstanceCount);
    }

    /// <summary>Two points in the same place always see each other, without dividing by zero.</summary>
    /// <remarks>
    /// Normalising a zero-length direction produces NaN, which upstream warns can send the BIH walk
    /// into an infinite loop. The guard is upstream's, and so is the threshold.
    /// </remarks>
    [RequiresVmapFact]
    public void APointSeesItself()
    {
        StaticMapTree tree = NewTree(0);

        Assert.True(tree.IsInLineOfSight(StartX, StartY, StartZ, StartX, StartY, StartZ));
    }

    /// <summary>A position that is not a number is refused rather than propagated.</summary>
    [RequiresVmapFact]
    public void ANonFinitePosition_IsRefused()
    {
        StaticMapTree tree = NewTree(0);

        Assert.False(tree.IsInLineOfSight(StartX, StartY, StartZ, float.NaN, StartY, StartZ));
        Assert.False(tree.IsInLineOfSight(StartX, StartY, StartZ, float.PositiveInfinity, StartY, StartZ));
    }

    /// <summary>
    /// A map with no collision data reports a clear view rather than a blocked one.
    /// </summary>
    /// <remarks>
    /// The failure direction matters. If a missing file made everything opaque, a map whose vmaps
    /// were not extracted would silently stop every spell and every attack.
    /// </remarks>
    [RequiresVmapFact]
    public void AMapWithNoCollisionData_SeesEverything()
    {
        StaticMapTree tree = NewTree(9999);

        Assert.False(tree.IsAvailable);
        Assert.True(tree.IsInLineOfSight(0f, 0f, 0f, 1000f, 1000f, 100f));
        Assert.Null(tree.GetHeight(0f, 0f, 100f));
    }

    /// <summary>
    /// A downward ray finds a surface where models stand, and finds nothing in open sky.
    /// </summary>
    /// <remarks>
    /// Height from vmaps is what terrain alone cannot give: the floor of a building, the deck of a
    /// bridge. It returns null rather than a fallback so the caller can tell "no model here" from
    /// "a model at ground level" and take whichever of terrain and model height is higher.
    /// </remarks>
    [RequiresVmapFact]
    public void ADownwardRay_FindsModelSurfacesAndNothingInOpenSky()
    {
        StaticMapTree tree = NewTree(0);

        int found = 0;
        (float X, float Y, float Z)? example = null;

        // Sample a grid across Stormwind, which is dense with buildings.
        for (float x = -8900f; x <= -8400f; x += 25f)
        {
            for (float y = 400f; y <= 900f; y += 25f)
            {
                float? height = tree.GetHeight(x, y, 200f, searchDistance: 300f);

                if (height is not null)
                {
                    found++;
                    example ??= (x, y, height.Value);
                }
            }
        }

        Assert.True(found > 0, "no model surface found anywhere over Stormwind");

        if (example is { } sample)
        {
            output.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{found} sample points hit a model; e.g. ({sample.X:F0}, {sample.Y:F0}) at z {sample.Z:F1}"));
        }

        // Far above the world, pointing down a short way: nothing should be in reach.
        Assert.Null(tree.GetHeight(StartX, StartY, 5000f, searchDistance: 50f));
    }

    /// <summary>
    /// Line of sight through a building is blocked, and across open air is not.
    /// </summary>
    /// <remarks>
    /// Uses a real surface found by a downward ray, so it does not depend on knowing where any
    /// particular wall is: a point just above a model surface cannot see a point just below it.
    /// </remarks>
    [RequiresVmapFact]
    public void AModelSurface_BlocksTheViewThroughIt()
    {
        StaticMapTree tree = NewTree(0);

        int blocked = 0, tested = 0;

        for (float x = -8900f; x <= -8500f && tested < 25; x += 20f)
        {
            for (float y = 400f; y <= 800f && tested < 25; y += 20f)
            {
                if (tree.GetHeight(x, y, 200f, searchDistance: 300f) is not { } surface)
                {
                    continue;
                }

                tested++;

                // Straight through the surface: two metres above to two metres below.
                if (!tree.IsInLineOfSight(x, y, surface + 2f, x, y, surface - 2f))
                {
                    blocked++;
                }
            }
        }

        Assert.True(tested > 5, $"only {tested} surfaces found to test");

        // Most should block. A few surfaces are one-sided or thin enough that a 4-yard segment
        // clears them, so this is a proportion rather than an absolute.
        Assert.True(blocked * 2 > tested, $"only {blocked} of {tested} surfaces blocked the view through them");

        output.WriteLine($"{blocked} of {tested} model surfaces blocked a ray passing through them");
    }

    private static StaticMapTree NewTree(uint mapId) => new(mapId, VmapData.Directory);

    /// <summary>Grid coordinates of map 0 tiles that actually place models.</summary>
    private static IEnumerable<(int GridX, int GridY)> TilesWithModels(int limit)
    {
        int found = 0;

        foreach (string path in Directory
            .EnumerateFiles(VmapData.Directory, "000_*.vmtile")
            .Order(StringComparer.Ordinal))
        {
            if (found >= limit)
            {
                yield break;
            }

            // The file is {map}_{gridY}_{gridX}, so the second number is the grid X.
            string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');

            if (parts.Length != 3
                || !int.TryParse(parts[1], out int gridY)
                || !int.TryParse(parts[2], out int gridX))
            {
                continue;
            }

            found++;
            yield return (gridX, gridY);
        }
    }
}
