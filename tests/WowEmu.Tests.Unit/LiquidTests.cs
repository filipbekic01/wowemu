using System.Buffers.Binary;
using WowEmu.Data.Client;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The liquid chunk of a terrain tile, and the swim/wade/stand answers built on it.
/// </summary>
/// <remarks>
/// Two halves, because neither alone is enough. The synthetic tiles cover the four shapes the chunk
/// can take — the flags are independent, so all four occur in the shipped data — and pin the
/// thresholds that separate swimming from wading from standing. The real-data tests then check that
/// the whole thing is pointed at the right place on the map, which no synthetic tile can tell you.
/// </remarks>
public sealed class LiquidTests
{
    // A point in open ocean west of Kalimdor: seabed far below, surface at sea level.
    private const float OceanX = 6361.905f;
    private const float OceanY = -1638.095f;

    /// <summary>Ocean is <c>LiquidType.dbc</c> row 2.</summary>
    private const uint OceanEntry = 2;

    /// <summary>
    /// Sea level is zero, and the query has to land on the right tile to say so.
    /// </summary>
    /// <remarks>
    /// The same oracle the height tests use, and for the same reason: an axis slip still loads a
    /// tile and still returns a number, so only a coordinate with a known answer catches it. Sea
    /// level being exactly 0.0 across the open ocean is what makes this one checkable at all.
    /// </remarks>
    [RequiresMapsFact]
    public void OpenOcean_HasItsSurfaceAtSeaLevel()
    {
        TerrainMap terrain = new TerrainManager(ClientData.DataDirectory).GetMap(0);

        Assert.Equal(0f, terrain.GetLiquidLevel(OceanX, OceanY), 0.001f);
    }

    /// <summary>
    /// Standing on the seabed hundreds of yards down is being under water, not near it.
    /// </summary>
    /// <remarks>
    /// Also pins the type: open ocean carries <see cref="LiquidTypeMask.DarkWater"/> alongside
    /// <see cref="LiquidTypeMask.Ocean"/>, which is the bit that makes deep water apply fatigue.
    /// Losing it would not break swimming, so nothing else here would notice.
    /// </remarks>
    [RequiresMapsFact]
    public void DeepOcean_IsUnderWater_AndCarriesDarkWater()
    {
        TerrainMap terrain = new TerrainManager(ClientData.DataDirectory).GetMap(0);

        LiquidData liquid = terrain.GetLiquidData(OceanX, OceanY, z: -30f, collisionHeight: 2.0f);

        Assert.Equal(LiquidStatus.UnderWater, liquid.Status);
        Assert.True(liquid.IsSwimming);
        Assert.Equal(OceanEntry, liquid.Entry);
        Assert.True(liquid.Type.HasFlag(LiquidTypeMask.Ocean), $"expected ocean, got {liquid.Type}");
        Assert.True(liquid.Type.HasFlag(LiquidTypeMask.DarkWater), $"expected dark water, got {liquid.Type}");
    }

    /// <summary>
    /// A boat's deck above the same water is above it, and still knows the water is there.
    /// </summary>
    /// <remarks>
    /// The distinction <see cref="LiquidStatus.NoWater"/> and <see cref="LiquidStatus.AboveWater"/>
    /// draw. Collapsing them would mean anything that has climbed out of the sea believes the sea
    /// has stopped existing, and there would be nothing to fall back into.
    /// </remarks>
    [RequiresMapsFact]
    public void AbovePointOnTheSameOcean_IsAboveWater_NotNoWater()
    {
        TerrainMap terrain = new TerrainManager(ClientData.DataDirectory).GetMap(0);

        LiquidData liquid = terrain.GetLiquidData(OceanX, OceanY, z: 12f, collisionHeight: 2.0f);

        Assert.Equal(LiquidStatus.AboveWater, liquid.Status);
        Assert.False(liquid.IsSwimming);
        Assert.Equal(0f, liquid.Level, 0.001f);
    }

    /// <summary>
    /// The starting positions Blizzard chose are on dry land, and none of them is underwater.
    /// </summary>
    /// <remarks>
    /// The negative half of the oracle. A parser that returned liquid everywhere — a stride wrong,
    /// or the crop box ignored — would pass every test above and drown every new character.
    /// </remarks>
    [RequiresMapsTheory]
    [InlineData(0u, -8949.95f, -132.493f, 83.5312f, "human")]
    [InlineData(1u, -618.518f, -4251.67f, 38.718f, "orc")]
    [InlineData(0u, -6240.32f, 331.033f, 382.758f, "dwarf")]
    public void RaceStartPositions_AreNotUnderWater(uint mapId, float x, float y, float z, string race)
    {
        TerrainMap terrain = new TerrainManager(ClientData.DataDirectory).GetMap(mapId);

        LiquidData liquid = terrain.GetLiquidData(x, y, z, collisionHeight: 2.0f);

        Assert.False(liquid.IsSwimming, $"{race} start position reports swimming: {liquid}");
    }

    /// <summary>
    /// Wherever a tile reports liquid, its surface is at or above its floor.
    /// </summary>
    /// <remarks>
    /// Swept over real tiles rather than asserted at one point, because the failure this catches is
    /// an indexing slip that reads the height map at the wrong offset — which produces a plausible
    /// number almost everywhere and an impossible one somewhere. Upstream refuses the same case, so
    /// a surface below its own seabed should never reach a caller at all.
    /// </remarks>
    [RequiresMapsFact]
    public void WhereverThereIsLiquid_TheSurfaceIsAboveTheFloor()
    {
        string directory = Path.Combine(ClientData.DataDirectory, "maps");
        int sampled = 0;

        foreach (string path in Directory.EnumerateFiles(directory, "000*.map").OrderBy(p => p).Take(60))
        {
            if (TerrainTile.Load(path) is not { } tile)
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(path);
            int gridX = int.Parse(stem.AsSpan(3, 2));
            int gridY = int.Parse(stem.AsSpan(5, 2));

            for (int i = 0; i < 9; i++)
            {
                for (int j = 0; j < 9; j++)
                {
                    float x = (32 - gridX - ((i + 0.5f) / 9f)) * MapGeometry.GridSize;
                    float y = (32 - gridY - ((j + 0.5f) / 9f)) * MapGeometry.GridSize;

                    LiquidData liquid = tile.GetLiquidData(x, y, z: 0f, collisionHeight: 2.0f);

                    if (liquid.Status == LiquidStatus.NoWater)
                    {
                        continue;
                    }

                    sampled++;

                    Assert.True(
                        liquid.Level >= liquid.FloorLevel,
                        $"{stem} at ({x:F1}, {y:F1}): surface {liquid.Level:F2} is below floor {liquid.FloorLevel:F2}");
                }
            }
        }

        Assert.True(sampled > 100, $"only {sampled} liquid samples found — is the chunk being read at all?");
    }

    // ------------------------------------------------------------------ synthetic tiles

    /// <summary>A uniform pool covering the tile, surface at 10, floor at 0.</summary>
    [Fact]
    public void AFlatPool_ReportsItsLevelEverywhere()
    {
        TerrainTile tile = Build(new LiquidSpec { Level = 10f });

        Assert.True(tile.HasLiquid);
        Assert.Equal(10f, tile.GetLiquidLevel(0f, 0f), 0.001f);
    }

    /// <summary>The thresholds that separate swimming from wading from standing on top.</summary>
    /// <remarks>
    /// The boundaries are upstream's and none of the three comparisons is symmetric with the others:
    /// deeper than the unit's own height is submerged, any depth at all is in the water, and a tenth
    /// of a yard either side of the surface counts as walking on it. Note where the last one lands —
    /// a unit floating exactly at the surface has depth zero, which fails the strict <c>&gt; 0</c>
    /// and falls through to water-walking rather than to being in the water.
    /// </remarks>
    [Theory]
    [InlineData(0f, LiquidStatus.UnderWater)]    // 10 deep, far more than the 2.0 collision height
    [InlineData(7.9f, LiquidStatus.UnderWater)]  // 2.1 deep, just past the collision height
    [InlineData(8f, LiquidStatus.InWater)]       // 2 deep, exactly the collision height, so not under
    [InlineData(9.5f, LiquidStatus.InWater)]     // half a yard deep
    [InlineData(10f, LiquidStatus.WaterWalk)]    // exactly at the surface — depth 0 is not > 0
    [InlineData(10.05f, LiquidStatus.WaterWalk)] // just above, within the tenth-yard band
    [InlineData(11f, LiquidStatus.AboveWater)]   // clear of it
    public void Depth_DecidesTheStatus(float z, LiquidStatus expected)
    {
        TerrainTile tile = Build(new LiquidSpec { Level = 10f });

        Assert.Equal(expected, tile.GetLiquidData(0f, 0f, z, collisionHeight: 2.0f).Status);
    }

    /// <summary>
    /// A taller unit stays wading where a shorter one is submerged.
    /// </summary>
    /// <remarks>
    /// The reason the collision height is a parameter rather than a constant. Hard-coding it would
    /// have a tauren and a gnome go under at the same depth.
    /// </remarks>
    [Fact]
    public void CollisionHeight_DecidesWhoIsSubmerged()
    {
        TerrainTile tile = Build(new LiquidSpec { Level = 10f });

        Assert.Equal(LiquidStatus.UnderWater, tile.GetLiquidData(0f, 0f, 7f, collisionHeight: 2.0f).Status);
        Assert.Equal(LiquidStatus.InWater, tile.GetLiquidData(0f, 0f, 7f, collisionHeight: 4.0f).Status);
    }

    /// <summary>A tile whose chunks carry no liquid type reports no water, whatever its level says.</summary>
    [Fact]
    public void AChunkWithNoType_ReportsNoWater()
    {
        TerrainTile tile = Build(new LiquidSpec { Level = 10f, GlobalType = LiquidTypeMask.None });

        Assert.Equal(LiquidStatus.NoWater, tile.GetLiquidData(0f, 0f, 0f, collisionHeight: 2.0f).Status);
    }

    /// <summary>
    /// Liquid whose surface is below the ground is not liquid you are in.
    /// </summary>
    /// <remarks>
    /// This is the cave-under-a-lake case upstream's <c>liquid_level >= ground_level</c> guard
    /// exists for. Without it, walking through a tunnel beneath a lake reports the lake overhead as
    /// water you are swimming in.
    /// </remarks>
    [Fact]
    public void LiquidBelowTheGround_IsNotReported()
    {
        TerrainTile tile = Build(new LiquidSpec { Level = -50f, GroundHeight = 0f });

        Assert.Equal(LiquidStatus.NoWater, tile.GetLiquidData(0f, 0f, -60f, collisionHeight: 2.0f).Status);
    }

    /// <summary>
    /// A point below the floor is under the world, not under the water.
    /// </summary>
    /// <remarks>
    /// The second half of the same guard — <c>z >= ground - 0.2</c>. The 0.2 slack is upstream's and
    /// is there because the floor and the liquid come from different grids and disagree slightly.
    /// </remarks>
    [Fact]
    public void APointBelowTheFloor_IsNotInTheLiquid()
    {
        TerrainTile tile = Build(new LiquidSpec { Level = 10f, GroundHeight = 0f });

        Assert.Equal(LiquidStatus.InWater, tile.GetLiquidData(0f, 0f, -0.1f, collisionHeight: 20f).Status);
        Assert.Equal(LiquidStatus.NoWater, tile.GetLiquidData(0f, 0f, -5f, collisionHeight: 20f).Status);
    }

    /// <summary>
    /// The height map is cropped to where the liquid is, and outside that box there is none.
    /// </summary>
    /// <remarks>
    /// The single most error-prone part of the format. The extractor stores only the bounding box of
    /// the cells that hold liquid and records where it starts; a reader that treats the array as a
    /// full 128×128 grid finds a surface everywhere and puts every lake in the wrong place.
    /// </remarks>
    [Fact]
    public void OutsideTheCroppedBox_ThereIsNoLiquid()
    {
        // A pond occupying samples [64, 80) on both axes, and nothing elsewhere.
        TerrainTile tile = Build(new LiquidSpec
        {
            OffsetX = 64,
            OffsetY = 64,
            Width = 16,
            Height = 16,
            Surface = 10f,
        });

        (float insideX, float insideY) = WorldAt(sampleRow: 70, sampleColumn: 70);
        (float outsideX, float outsideY) = WorldAt(sampleRow: 10, sampleColumn: 10);

        Assert.Equal(10f, tile.GetLiquidLevel(insideX, insideY), 0.001f);
        Assert.Equal(MapGeometry.InvalidHeight, tile.GetLiquidLevel(outsideX, outsideY), 0.001f);

        Assert.Equal(
            LiquidStatus.InWater,
            tile.GetLiquidData(insideX, insideY, 5f, collisionHeight: 20f).Status);

        Assert.Equal(
            LiquidStatus.NoWater,
            tile.GetLiquidData(outsideX, outsideY, 5f, collisionHeight: 20f).Status);
    }

    /// <summary>
    /// A varying surface is read at the right cell, not just the right tile.
    /// </summary>
    /// <remarks>
    /// The row/column and offset-swap trap: the indices are crossed relative to their names, so a
    /// tidied-up version returns a neighbouring cell's height. A gradient makes that visible —
    /// against a flat surface every wrong index gives the right answer.
    /// </remarks>
    [Fact]
    public void AVaryingSurface_IsSampledPerCell()
    {
        TerrainTile tile = Build(new LiquidSpec
        {
            Width = MapGeometry.Resolution,
            Height = MapGeometry.Resolution,
            SurfaceAt = (row, column) => (row * 1000f) + column,
        });

        foreach ((int row, int column) in new[] { (0, 0), (5, 9), (100, 3), (127, 127) })
        {
            (float x, float y) = WorldAt(row, column);

            Assert.Equal((row * 1000f) + column, tile.GetLiquidLevel(x, y), 0.001f);
        }
    }

    /// <summary>Per-chunk types beat the global one, and the 16×16 grid is indexed independently.</summary>
    [Fact]
    public void PerChunkTypes_OverrideTheGlobalOne()
    {
        TerrainTile tile = Build(new LiquidSpec
        {
            Level = 10f,

            // Chunk (0, 0) is lava; everything else is water.
            TypeAt = chunk => chunk == 0 ? LiquidTypeMask.Magma : LiquidTypeMask.Water,
            EntryAt = chunk => chunk == 0 ? 3u : 1u,
        });

        (float lavaX, float lavaY) = WorldAt(sampleRow: 2, sampleColumn: 2);
        (float waterX, float waterY) = WorldAt(sampleRow: 100, sampleColumn: 100);

        LiquidData lava = tile.GetLiquidData(lavaX, lavaY, 5f, collisionHeight: 20f);
        LiquidData water = tile.GetLiquidData(waterX, waterY, 5f, collisionHeight: 20f);

        Assert.Equal(LiquidTypeMask.Magma, lava.Type);
        Assert.Equal(3u, lava.Entry);

        Assert.Equal(LiquidTypeMask.Water, water.Type);
        Assert.Equal(1u, water.Entry);
    }

    /// <summary>A tile with no liquid chunk answers "no water" rather than throwing.</summary>
    [Fact]
    public void ATileWithNoLiquidChunk_ReportsNone()
    {
        TerrainTile tile = Build(liquid: null);

        Assert.False(tile.HasLiquid);
        Assert.Equal(MapGeometry.InvalidHeight, tile.GetLiquidLevel(0f, 0f), 0.001f);
        Assert.Equal(LiquidStatus.NoWater, tile.GetLiquidData(0f, 0f, 0f, collisionHeight: 2.0f).Status);
    }

    // ------------------------------------------------------------------ tile building

    /// <summary>What the synthetic tile's liquid chunk should contain.</summary>
    private sealed class LiquidSpec
    {
        /// <summary>The uniform surface height, used when <see cref="SurfaceAt"/> is null.</summary>
        public float? Level { get; init; }

        /// <summary>A uniform surface written as a real height map rather than a flat level.</summary>
        public float? Surface { get; init; }

        /// <summary>A per-cell surface, indexed by cropped-box row and column.</summary>
        public Func<int, int, float>? SurfaceAt { get; init; }

        public LiquidTypeMask GlobalType { get; init; } = LiquidTypeMask.Water;

        public uint GlobalEntry { get; init; } = 1;

        public Func<int, LiquidTypeMask>? TypeAt { get; init; }

        public Func<int, uint>? EntryAt { get; init; }

        public byte OffsetX { get; init; }

        public byte OffsetY { get; init; }

        public byte Width { get; init; } = MapGeometry.Resolution;

        public byte Height { get; init; } = MapGeometry.Resolution;

        public float GroundHeight { get; init; }
    }

    /// <summary>
    /// The world coordinate that lands on a given tile-local sample, in grid (32, 32).
    /// </summary>
    /// <remarks>
    /// The inverse of the tile's own indexing, aimed at the middle of the sample so that float
    /// rounding at a cell boundary cannot tip the answer into the neighbour.
    /// </remarks>
    private static (float X, float Y) WorldAt(int sampleRow, int sampleColumn) =>
        ((32 - 32 - ((sampleRow + 0.5f) / MapGeometry.Resolution)) * MapGeometry.GridSize,
         (32 - 32 - ((sampleColumn + 0.5f) / MapGeometry.Resolution)) * MapGeometry.GridSize);

    /// <summary>
    /// Writes a real <c>.map</c> file and loads it back.
    /// </summary>
    /// <remarks>
    /// Through the file rather than through a constructor on purpose: the header offsets and the
    /// optional-chunk flags are the part most likely to be read wrong, and a test that bypassed them
    /// would exercise the arithmetic while skipping the parsing.
    /// </remarks>
    private static TerrainTile Build(LiquidSpec? liquid)
    {
        const int Cells = 16 * 16;

        List<byte> body = [];

        // Height chunk: a flat tile, so the floor is one value everywhere.
        int heightOffset = 44;
        float ground = liquid?.GroundHeight ?? 0f;

        body.AddRange(Bytes(0x5447484Du));   // 'MHGT'
        body.AddRange(Bytes(0x0001u));       // MAP_HEIGHT_NO_HEIGHT
        body.AddRange(Bytes(ground));
        body.AddRange(Bytes(ground));

        int heightSize = body.Count;
        int liquidOffset = 0;

        if (liquid is not null)
        {
            liquidOffset = heightOffset + heightSize;

            bool hasTypes = liquid.TypeAt is not null || liquid.EntryAt is not null;
            bool hasMap = liquid.SurfaceAt is not null || liquid.Surface is not null;

            byte flags = 0;
            if (!hasTypes) { flags |= 0x01; }   // MAP_LIQUID_NO_TYPE
            if (!hasMap) { flags |= 0x02; }     // MAP_LIQUID_NO_HEIGHT

            body.AddRange(Bytes(0x51494C4Du));  // 'MLIQ'
            body.Add(flags);
            body.Add((byte)liquid.GlobalType);
            body.AddRange(Bytes((ushort)liquid.GlobalEntry));
            body.Add(liquid.OffsetX);
            body.Add(liquid.OffsetY);
            body.Add(liquid.Width);
            body.Add(liquid.Height);
            body.AddRange(Bytes(liquid.Level ?? 0f));

            if (hasTypes)
            {
                for (int i = 0; i < Cells; i++)
                {
                    body.AddRange(Bytes((ushort)(liquid.EntryAt?.Invoke(i) ?? liquid.GlobalEntry)));
                }

                for (int i = 0; i < Cells; i++)
                {
                    body.Add((byte)(liquid.TypeAt?.Invoke(i) ?? liquid.GlobalType));
                }
            }

            if (hasMap)
            {
                for (int row = 0; row < liquid.Height; row++)
                {
                    for (int column = 0; column < liquid.Width; column++)
                    {
                        body.AddRange(Bytes(
                            liquid.SurfaceAt?.Invoke(row, column) ?? liquid.Surface ?? 0f));
                    }
                }
            }
        }

        List<byte> file =
        [
            .. Bytes(0x5350414Du),                  // 'MAPS'
            .. Bytes(9u),                           // version
            .. Bytes(0u),                           // build
            .. Bytes(0u), .. Bytes(0u),             // no area chunk
            .. Bytes((uint)heightOffset), .. Bytes((uint)heightSize),
            .. Bytes((uint)liquidOffset), .. Bytes(0u),
            .. Bytes(0u), .. Bytes(0u),             // no holes
            .. body,
        ];

        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.map");

        try
        {
            File.WriteAllBytes(path, [.. file]);

            return TerrainTile.Load(path)
                ?? throw new InvalidOperationException("the synthetic tile did not load");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Bytes(uint value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] Bytes(ushort value)
    {
        byte[] buffer = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] Bytes(float value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        return buffer;
    }
}
