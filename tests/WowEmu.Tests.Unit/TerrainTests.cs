using WowEmu.Data.Client;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The terrain loader, against real extracted tiles.
/// </summary>
/// <remarks>
/// Terrain has an unusually good oracle: every race's starting position in <c>playercreateinfo</c>
/// is a point Blizzard placed standing on the ground. If the loader returns a height close to the
/// start position's Z, the tile lookup, the height decoding and the triangle interpolation are all
/// working together. If any of them is wrong — a swapped axis in particular — the query lands
/// somewhere else on the map and the answer is off by tens or hundreds of yards.
/// </remarks>
public sealed class TerrainTests
{
    // Race start positions, from playercreateinfo. All on map 0 or 1, all on solid ground.
    private const float HumanX = -8949.95f;
    private const float HumanY = -132.493f;
    private const float HumanZ = 83.5312f;

    private const float OrcX = -618.518f;
    private const float OrcY = -4251.67f;
    private const float OrcZ = 38.718f;

    private const float DwarfX = -6240.32f;
    private const float DwarfY = 331.033f;
    private const float DwarfZ = 382.758f;

    /// <summary>
    /// The load-bearing test. A start position is on the ground, so the terrain height there should
    /// match its Z within a yard or two.
    /// </summary>
    [RequiresMapsTheory]
    [InlineData(0u, HumanX, HumanY, HumanZ, "human")]
    [InlineData(1u, OrcX, OrcY, OrcZ, "orc")]
    [InlineData(0u, DwarfX, DwarfY, DwarfZ, "dwarf")]
    public void GroundHeight_MatchesTheStartPosition(uint mapId, float x, float y, float expectedZ, string race)
    {
        TerrainManager terrain = new(ClientData.DataDirectory);

        float height = terrain.GetMap(mapId).GetHeight(x, y);

        Assert.True(
            height > MapGeometry.InvalidHeight,
            $"{race} start position has no terrain under it — is the tile axis swapped?");

        Assert.True(
            Math.Abs(height - expectedZ) < 2.0f,
            $"{race} start: terrain says {height:F2}, the start position is at {expectedZ:F2}");
    }

    /// <summary>
    /// The axis check, stated directly. Reading the tile with X and Y swapped returns the height of
    /// a completely different place — which is the failure PLAN §5.1 warns about, because every
    /// tile loads fine and nothing errors.
    /// </summary>
    [RequiresMapsFact]
    public void SwappedAxis_WouldGiveADifferentAnswer()
    {
        TerrainManager terrain = new(ClientData.DataDirectory);

        TerrainMap map = terrain.GetMap(0);

        float correct = map.GetHeight(HumanX, HumanY);
        float swapped = map.GetHeight(HumanY, HumanX);

        Assert.True(
            Math.Abs(correct - swapped) > 1.0f,
            "swapping the axes gave the same height — the test cannot detect the bug it exists for");
    }

    [Fact]
    public void FileName_PutsTheRowFirst()
    {
        // mapId 0, grid (20, 35) -> "0002035.map". Y before X is the extractor's convention.
        Assert.Equal("0002035.map", TerrainTile.FileName(0, 20, 35));
        Assert.Equal("7242731.map", TerrainTile.FileName(724, 27, 31));
        Assert.Equal("5713048.map", TerrainTile.FileName(571, 30, 48));
    }

    /// <summary>The origin sits at the centre of the grid, which is what makes the axis inverted.</summary>
    [Fact]
    public void GridLookup_IsInverted()
    {
        Assert.Equal((32, 32), MapGeometry.GridFor(0f, 0f));

        // Positive world coordinates move towards lower grid indices.
        (int gridX, _) = MapGeometry.GridFor(MapGeometry.GridSize * 2, 0f);
        Assert.Equal(30, gridX);

        (int negativeX, _) = MapGeometry.GridFor(-MapGeometry.GridSize * 2, 0f);
        Assert.Equal(34, negativeX);
    }

    [RequiresMapsFact]
    public void AreaId_IsFoundUnderAStartPosition()
    {
        TerrainManager terrain = new(ClientData.DataDirectory);

        // Northshire Valley is area 9, inside Elwynn Forest.
        ushort area = terrain.GetMap(0).GetAreaId(HumanX, HumanY);

        Assert.NotEqual(0, area);
    }

    [RequiresMapsFact]
    public void MissingTile_ReportsNoTerrain()
    {
        TerrainManager terrain = new(ClientData.DataDirectory);

        // Far outside any real map, but still inside the 64×64 grid.
        Assert.False(terrain.GetMap(0).HasTerrain(30000f, 30000f));
        Assert.Equal(MapGeometry.InvalidHeight, terrain.GetMap(0).GetHeight(30000f, 30000f));
    }

    [RequiresMapsFact]
    public void CoordinatesOutsideTheGrid_AreRejected()
    {
        TerrainManager terrain = new(ClientData.DataDirectory);

        Assert.Equal(MapGeometry.InvalidHeight, terrain.GetMap(0).GetHeight(1_000_000f, 0f));
    }

    /// <summary>
    /// Heights are stored as float, uint16 or uint8 depending on the tile. Sampling a spread of
    /// tiles exercises all three, and a decoder that assumes floats fails loudly here.
    /// </summary>
    [RequiresMapsFact]
    public void ManyTiles_DecodeToPlausibleHeights()
    {
        TerrainManager terrain = new(ClientData.DataDirectory);

        TerrainMap map = terrain.GetMap(0);
        int sampled = 0;

        for (float x = -9000f; x < -7000f; x += 400f)
        {
            for (float y = -1000f; y < 1000f; y += 400f)
            {
                float height = map.GetHeight(x, y);

                if (height <= MapGeometry.InvalidHeight)
                {
                    continue;
                }

                sampled++;

                // Azeroth's terrain runs roughly -500 to +1500 yards of elevation.
                Assert.InRange(height, -500f, 2000f);
            }
        }

        Assert.True(sampled > 10, $"only {sampled} points had terrain — expected most of Elwynn");
    }

    [Fact]
    public void NonMapFile_IsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wowemu-not-a-map-{Environment.ProcessId}.map");
        File.WriteAllBytes(path, [.. new byte[64]]);

        try
        {
            Assert.Throws<InvalidDataException>(() => TerrainTile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_LoadsAsNull()
    {
        Assert.Null(TerrainTile.Load(Path.Combine(Path.GetTempPath(), "wowemu-no-such-tile.map")));
    }
}
