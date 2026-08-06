using System.Numerics;
using WowEmu.Data.Client;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Liquid that lives inside a model rather than in the terrain.
/// </summary>
/// <remarks>
/// The other half of the world's water. A terrain tile holds lakes, rivers and the ocean; every
/// fountain, canal, moat and flooded dungeon room is a grid inside a WMO, reached through the
/// <c>GetLocationInfo</c> chain instead. Until this existed the server believed all of it was dry
/// air — which is why a swim check could not be written.
/// </remarks>
public sealed class ModelLiquidTests
{
    // Stormwind's canal, and the slime channel in Undercity. Both are water the terrain does not
    // know about, and both are recognisable places rather than arbitrary coordinates — the point of
    // choosing them is that a wrong transform would put the answer somewhere that is not a canal.
    private const float CanalX = -8757.3f;
    private const float CanalY = 1106.6f;
    private const float CanalZ = 93.3f;

    private const float SlimeX = 1751.9f;
    private const float SlimeY = 238.9f;
    private const float SlimeZ = 51.0f;

    /// <summary>Stormwind's canal is water, and the terrain has never heard of it.</summary>
    /// <remarks>
    /// Both halves matter. The model finding water is the feature; the terrain <i>not</i> finding it
    /// is what makes the feature necessary, and asserting it here stops the test passing for the
    /// wrong reason if terrain liquid ever started covering the same point.
    /// </remarks>
    [RequiresMapsFact]
    public void StormwindsCanal_IsWaterTheTerrainDoesNotKnowAbout()
    {
        (TerrainMap terrain, StaticMapTree vmaps, DbcStore<LiquidTypeEntry> types) = World();

        Assert.Equal(LiquidStatus.NoWater, terrain.GetLiquidData(CanalX, CanalY, CanalZ, 2.0f).Status);

        LiquidData liquid = WorldLiquid.Get(terrain, vmaps, CanalX, CanalY, CanalZ, 2.0f, types);

        Assert.Equal(LiquidStatus.InWater, liquid.Status);
        Assert.True(liquid.IsSwimming);
        Assert.Equal(LiquidTypeMask.Water, liquid.Type);
        Assert.Equal(94.32f, liquid.Level, 0.1f);
    }

    /// <summary>
    /// Undercity's channels are slime, and saying so needs the DBC.
    /// </summary>
    /// <remarks>
    /// A WMO stores only a <c>LiquidType.dbc</c> row id, unlike a terrain tile whose type the
    /// extractor resolved. Entry 20 is "WMO Slime" and entry 13 is "WMO Water"; without the store
    /// both arrive as bare numbers and nothing can tell a swim from a bath in Undercity's slime.
    /// </remarks>
    [RequiresMapsFact]
    public void UndercitysChannels_AreSlime_OnceTheDbcIsLoaded()
    {
        (TerrainMap terrain, StaticMapTree vmaps, DbcStore<LiquidTypeEntry> types) = World();

        LiquidData typed = WorldLiquid.Get(terrain, vmaps, SlimeX, SlimeY, SlimeZ, 2.0f, types);

        Assert.Equal(LiquidStatus.InWater, typed.Status);
        Assert.Equal(LiquidTypeMask.Slime, typed.Type);
        Assert.Equal(20u, typed.Entry);

        // Without the store the water is still found — only its kind is unknown.
        LiquidData untyped = WorldLiquid.Get(terrain, vmaps, SlimeX, SlimeY, SlimeZ, 2.0f);

        Assert.Equal(LiquidStatus.InWater, untyped.Status);
        Assert.Equal(LiquidTypeMask.None, untyped.Type);
        Assert.Equal(20u, untyped.Entry);
    }

    /// <summary>A point in the open, inside no model at all, has no model liquid.</summary>
    /// <remarks>
    /// The negative case. A location chain that reported every point as inside something would make
    /// the two tests above pass and would drown the whole world.
    /// </remarks>
    [RequiresMapsTheory]
    [InlineData(-8949.95f, -132.493f, 83.5312f, "human start")]
    [InlineData(6361.905f, -1638.095f, -30f, "open ocean")]
    public void APointInTheOpen_HasNoModelLiquid(float x, float y, float z, string where)
    {
        (_, StaticMapTree vmaps, _) = World();

        Assert.True(vmaps.GetLiquid(x, y, z, 2.0f) is null, $"{where} reported liquid inside a model");
    }

    /// <summary>
    /// Terrain liquid still works where no model claims the point.
    /// </summary>
    /// <remarks>
    /// The combination must not have cost anything. Open ocean has no model anywhere near it, and
    /// the answer has to be exactly what the terrain alone would have said.
    /// </remarks>
    [RequiresMapsFact]
    public void OpenOcean_IsUnchangedByTheModelPath()
    {
        (TerrainMap terrain, StaticMapTree vmaps, DbcStore<LiquidTypeEntry> types) = World();

        LiquidData alone = terrain.GetLiquidData(6361.905f, -1638.095f, -30f, 2.0f);
        LiquidData combined = WorldLiquid.Get(terrain, vmaps, 6361.905f, -1638.095f, -30f, 2.0f, types);

        Assert.Equal(alone, combined);
        Assert.Equal(LiquidStatus.UnderWater, combined.Status);
    }

    // ------------------------------------------------------------------ transforms

    /// <summary>
    /// Moving a point into a model's space and back returns it unchanged.
    /// </summary>
    /// <remarks>
    /// The whole chain rests on this pair being exact inverses. They are not written symmetrically —
    /// one transforms by the inverse rotation and the other by its transpose — so a slip would look
    /// right and put every indoor lake a few yards sideways. The rotation and scale here are
    /// deliberately awkward rather than round.
    /// </remarks>
    [Fact]
    public void ModelAndWorldTransforms_AreInverses()
    {
        ModelSpawn spawn = Spawn(rotationX: 37f, rotationY: 114f, rotationZ: -63f, scale: 2.75f);

        foreach (Vector3 point in new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(17.5f, -3.25f, 88f),
            new Vector3(-400f, 250f, -12.5f),
        })
        {
            Vector3 roundTripped = ModelLocator.ToWorld(spawn, ModelLocator.ToModel(spawn, point));

            Assert.Equal(point.X, roundTripped.X, 0.001f);
            Assert.Equal(point.Y, roundTripped.Y, 0.001f);
            Assert.Equal(point.Z, roundTripped.Z, 0.001f);
        }
    }

    // ------------------------------------------------------------------ the liquid grid

    /// <summary>A liquid with no per-tile flags is one height covering everything.</summary>
    /// <remarks>
    /// Its own branch in the file format, and it has to be taken before any tile arithmetic — there
    /// are no tiles to index, and the grid form would read past the single stored height.
    /// </remarks>
    [Fact]
    public void ALiquidWithNoTiles_IsASingleHeightEverywhere()
    {
        WmoLiquid liquid = new(0, 0, 0f, 0f, 0f, Type: 1, Heights: [42f], Flags: []);

        Assert.True(ModelLocator.TryGetLiquidHeight(liquid, new Vector3(999f, -999f, 0f), out float height));
        Assert.Equal(42f, height, 0.001f);
    }

    /// <summary>
    /// A flat grid reads its own height at every tile.
    /// </summary>
    /// <remarks>
    /// The corner offsets are non-zero on purpose: the grid is positioned in model space, and
    /// forgetting to subtract the corner reads the wrong tile everywhere but the origin.
    /// </remarks>
    [Fact]
    public void AFlatGrid_ReadsItsHeightAtEveryTile()
    {
        WmoLiquid liquid = Grid(tilesX: 4, tilesY: 3, cornerX: 10f, cornerY: -20f, (cx, cy) => 7f);

        for (int tx = 0; tx < 4; tx++)
        {
            for (int ty = 0; ty < 3; ty++)
            {
                Vector3 at = new(
                    10f + ((tx + 0.5f) * ModelLocator.LiquidTileSize),
                    -20f + ((ty + 0.5f) * ModelLocator.LiquidTileSize),
                    0f);

                Assert.True(ModelLocator.TryGetLiquidHeight(liquid, at, out float height));
                Assert.Equal(7f, height, 0.001f);
            }
        }
    }

    /// <summary>
    /// A sloped surface is interpolated across each tile's two triangles.
    /// </summary>
    /// <remarks>
    /// Heights sit at tile <i>corners</i>, so the grid is one larger than the tile count in each
    /// axis — a 4×3 tile grid carries 20 heights. A plane is the check that catches both the stride
    /// and the triangle split at once: any misreading of either bends it.
    /// </remarks>
    [Fact]
    public void ASlopedSurface_IsInterpolatedAcrossTheTile()
    {
        // A plane: height rises by 1 per corner in x and by 10 per corner in y.
        WmoLiquid liquid = Grid(tilesX: 4, tilesY: 3, cornerX: 0f, cornerY: 0f, (cx, cy) => cx + (10f * cy));

        foreach ((float fx, float fy) in new[] { (0.5f, 0.5f), (0.25f, 0.75f), (0.9f, 0.1f) })
        {
            Vector3 at = new(
                (1 + fx) * ModelLocator.LiquidTileSize,
                (1 + fy) * ModelLocator.LiquidTileSize,
                0f);

            Assert.True(ModelLocator.TryGetLiquidHeight(liquid, at, out float height));

            // The plane's value at that point, in corner units.
            Assert.Equal((1 + fx) + (10f * (1 + fy)), height, 0.001f);
        }
    }

    /// <summary>A tile flagged as carrying no liquid has none, even mid-grid.</summary>
    /// <remarks>
    /// Disabled tiles are how a WMO cuts a hole in its own water — a walkway across a moat. The test
    /// is on the low nibble being <c>0x0F</c>, not on a single bit.
    /// </remarks>
    [Fact]
    public void ADisabledTile_HasNoLiquid()
    {
        WmoLiquid liquid = Grid(tilesX: 4, tilesY: 3, cornerX: 0f, cornerY: 0f, (cx, cy) => 5f);

        // Disable tile (1, 1).
        liquid.Flags[1 + (1 * 4)] = 0x0F;

        Vector3 inside = new(1.5f * ModelLocator.LiquidTileSize, 1.5f * ModelLocator.LiquidTileSize, 0f);
        Vector3 neighbour = new(2.5f * ModelLocator.LiquidTileSize, 1.5f * ModelLocator.LiquidTileSize, 0f);

        Assert.False(ModelLocator.TryGetLiquidHeight(liquid, inside, out _));
        Assert.True(ModelLocator.TryGetLiquidHeight(liquid, neighbour, out _));
    }

    /// <summary>Outside the grid there is no liquid, on either axis and in either direction.</summary>
    [Fact]
    public void OutsideTheGrid_ThereIsNoLiquid()
    {
        WmoLiquid liquid = Grid(tilesX: 4, tilesY: 3, cornerX: 0f, cornerY: 0f, (cx, cy) => 5f);

        float size = ModelLocator.LiquidTileSize;

        Assert.False(ModelLocator.TryGetLiquidHeight(liquid, new Vector3(-0.5f * size, size, 0f), out _));
        Assert.False(ModelLocator.TryGetLiquidHeight(liquid, new Vector3(size, -0.5f * size, 0f), out _));
        Assert.False(ModelLocator.TryGetLiquidHeight(liquid, new Vector3(4.5f * size, size, 0f), out _));
        Assert.False(ModelLocator.TryGetLiquidHeight(liquid, new Vector3(size, 3.5f * size, 0f), out _));
    }

    // ------------------------------------------------------------------ helpers

    private static (TerrainMap Terrain, StaticMapTree Vmaps, DbcStore<LiquidTypeEntry> Types) World()
    {
        TerrainMap terrain = new TerrainManager(ClientData.DataDirectory).GetMap(0);
        StaticMapTree vmaps = new VmapManager(ClientData.DataDirectory).GetMap(0);
        DbcStore<LiquidTypeEntry> types = DbcStores.Load(ClientData.DbcDirectory).LiquidTypes;

        return (terrain, vmaps, types);
    }

    /// <summary>A liquid grid whose corner heights come from a function of corner indices.</summary>
    private static WmoLiquid Grid(
        uint tilesX,
        uint tilesY,
        float cornerX,
        float cornerY,
        Func<int, int, float> heightAt)
    {
        float[] heights = new float[(tilesX + 1) * (tilesY + 1)];

        for (int cy = 0; cy <= tilesY; cy++)
        {
            for (int cx = 0; cx <= tilesX; cx++)
            {
                heights[cx + (cy * (int)(tilesX + 1))] = heightAt(cx, cy);
            }
        }

        return new WmoLiquid(
            tilesX, tilesY, cornerX, cornerY, 0f, Type: 1, heights, new byte[tilesX * tilesY]);
    }

    private static ModelSpawn Spawn(float rotationX, float rotationY, float rotationZ, float scale) =>
        new(
            Flags: ModelSpawnFlags.HasBound,
            AdtId: 0,
            Id: 1,
            PositionX: 100f,
            PositionY: -250f,
            PositionZ: 30f,
            RotationX: rotationX,
            RotationY: rotationY,
            RotationZ: rotationZ,
            Scale: scale,
            BoundsMinX: -1000f,
            BoundsMinY: -1000f,
            BoundsMinZ: -1000f,
            BoundsMaxX: 1000f,
            BoundsMaxY: 1000f,
            BoundsMaxZ: 1000f,
            Name: "test",
            NameHasTerminator: false);
}
