using System.Buffers.Binary;

namespace WowEmu.Data.Client;

/// <summary>
/// What kind of liquid a point is in. <c>MAP_LIQUID_TYPE_*</c>.
/// </summary>
/// <remarks>
/// These bits are resolved by the <i>extractor</i>, not at runtime: it reads each chunk's liquid
/// entry out of <c>LiquidType.dbc</c> and writes the resulting sound-bank bit into the tile. So the
/// stored flags already say water, ocean, magma or slime, and no DBC is needed to read them back.
/// <para>
/// <see cref="DarkWater"/> is not a kind of liquid but a property of one — it is what makes deep
/// ocean apply fatigue — so it appears alongside one of the other four rather than instead of it.
/// </para>
/// </remarks>
[Flags]
public enum LiquidTypeMask : byte
{
    None = 0x00,
    Water = 0x01,
    Ocean = 0x02,
    Magma = 0x04,
    Slime = 0x08,
    DarkWater = 0x10,

    /// <summary>Everything that is a liquid, as opposed to a property of one.</summary>
    AllLiquids = Water | Ocean | Magma | Slime,
}

/// <summary>
/// Where a point sits relative to the liquid covering it. <c>LiquidStatus</c>.
/// </summary>
/// <remarks>
/// Ordered by depth, and the thresholds are what separate them: deeper than the unit's collision
/// height is <see cref="UnderWater"/>, any depth at all is <see cref="InWater"/>, within 0.1 yards
/// above the surface is <see cref="WaterWalk"/>, and anything higher is <see cref="AboveWater"/>.
/// <para>
/// <see cref="NoWater"/> and <see cref="AboveWater"/> are different answers. The first means there
/// is no liquid here at all; the second means there is, and the point is above it — which is what a
/// unit standing on a jetty is doing, and it still has a surface underneath it to fall into.
/// </para>
/// </remarks>
[Flags]
public enum LiquidStatus
{
    NoWater = 0x00,
    AboveWater = 0x01,
    WaterWalk = 0x02,
    InWater = 0x04,
    UnderWater = 0x08,

    /// <summary>Deep enough to be swimming. <c>MAP_LIQUID_STATUS_SWIMMING</c>.</summary>
    Swimming = InWater | UnderWater,

    /// <summary>Touching the liquid at all, including walking on its surface.</summary>
    InContact = Swimming | WaterWalk,
}

/// <summary>
/// The liquid at a point: what it is, where its surface is, and how deep the asker is in it.
/// </summary>
/// <param name="Entry">The <c>LiquidType.dbc</c> row, for spells and sounds that care which pool.</param>
/// <param name="Type">Water, ocean, magma or slime, plus dark water.</param>
/// <param name="Level">The height of the liquid's surface.</param>
/// <param name="FloorLevel">The ground under the liquid — the bottom, not the surface.</param>
/// <param name="Status">Where the queried point sits relative to the surface.</param>
public readonly record struct LiquidData(
    uint Entry,
    LiquidTypeMask Type,
    float Level,
    float FloorLevel,
    LiquidStatus Status)
{
    /// <summary>No liquid here.</summary>
    public static LiquidData None => new(
        0, LiquidTypeMask.None, MapGeometry.InvalidHeight, MapGeometry.InvalidHeight, LiquidStatus.NoWater);

    /// <summary>Whether the queried point is deep enough to be swimming.</summary>
    public bool IsSwimming => (Status & LiquidStatus.Swimming) != 0;

    /// <summary>Whether the queried point touches the liquid at all.</summary>
    public bool IsInContact => (Status & LiquidStatus.InContact) != 0;
}

/// <summary>Map geometry constants. These are the client's, not ours.</summary>
public static class MapGeometry
{
    /// <summary>One grid tile, in yards. A map is 64×64 of these.</summary>
    public const float GridSize = 533.3333f;

    /// <summary>Grids per axis.</summary>
    public const int GridsPerAxis = 64;

    /// <summary>The grid index the world's origin falls in.</summary>
    public const int CenterGrid = GridsPerAxis / 2;

    /// <summary>Height samples per axis within a tile.</summary>
    public const int Resolution = 128;

    /// <summary>The outer height grid: one more sample per axis than <see cref="Resolution"/>.</summary>
    public const int V9Size = Resolution + 1;

    /// <summary>The inner height grid, offset half a cell from V9.</summary>
    public const int V8Size = Resolution;

    /// <summary>Returned when there is no terrain — a hole, or an unloaded tile.</summary>
    public const float InvalidHeight = -100000.0f;

    /// <summary>
    /// Converts a world coordinate to the grid that contains it.
    /// </summary>
    /// <remarks>
    /// <b>The axis is inverted.</b> World coordinates grow in the opposite direction to grid
    /// indices, so this subtracts rather than divides straight. Getting the sign wrong loads a tile
    /// from the far side of the map — with no error, because that tile exists too.
    /// </remarks>
    public static (int GridX, int GridY) GridFor(float x, float y) =>
        ((int)(CenterGrid - (x / GridSize)), (int)(CenterGrid - (y / GridSize)));
}

/// <summary>
/// One <c>.map</c> tile: the terrain of a 533-yard square.
/// </summary>
/// <remarks>
/// Port of <c>GridTerrainData</c>. The file is a small header of offsets followed by three or four
/// independent chunks — area ids, heights, liquid, holes — each of which may be absent or stored in
/// a reduced form when the terrain is flat enough to allow it.
/// <para>
/// <b>Heights come in three widths.</b> The extractor stores them as <c>float</c>, <c>uint16</c> or
/// <c>uint8</c> depending on how much the tile varies, with the narrow forms scaled between a
/// minimum and maximum carried in the header. A reader that assumes floats produces plausible
/// nonsense for most of the world, since the majority of tiles are packed.
/// </para>
/// <para>
/// <b>One deliberate deviation in the liquid queries.</b> Upstream re-derives a liquid's type from
/// <c>LiquidType.dbc</c> at query time so that it can apply an <i>area override</i>: for entries
/// below 21, a zone may substitute its own liquid, which is how Naxxramas gets slime where the
/// geometry says water. We use the type the extractor already resolved into the tile, which is
/// correct everywhere no zone overrides it. Wiring the override needs both <c>LiquidType.dbc</c> and
/// <c>AreaTable.dbc</c>, and AreaTable is a separate open item.
/// </para>
/// </remarks>
public sealed class TerrainTile
{
    private const uint MapMagic = 0x5350414D;      // 'MAPS'
    private const uint AreaMagic = 0x41455241;     // 'AREA'
    private const uint HeightMagic = 0x5447484D;   // 'MHGT'
    private const uint LiquidMagic = 0x51494C4D;   // 'MLIQ'
    private const uint SupportedVersion = 9;

    private const ushort AreaNoArea = 0x0001;

    private const uint HeightNoHeight = 0x0001;
    private const uint HeightAsInt16 = 0x0002;
    private const uint HeightAsInt8 = 0x0004;

    private const byte LiquidNoType = 0x0001;
    private const byte LiquidNoHeight = 0x0002;

    /// <summary>Liquid is described per 16×16 chunk, the same grid the area map uses.</summary>
    private const int LiquidChunksPerAxis = 16;

    // Hole lookup tables, verbatim from upstream.
    private static readonly ushort[] HoleColumnMask = [0x1111, 0x2222, 0x4444, 0x8888];
    private static readonly ushort[] HoleRowMask = [0x000F, 0x00F0, 0x0F00, 0xF000];

    private readonly ushort[]? _areaMap;
    private readonly ushort _gridArea;

    private readonly float[]? _v9;
    private readonly float[]? _v8;
    private readonly float _flatHeight;

    private readonly ushort[]? _holes;

    private readonly LiquidChunk? _liquid;

    private TerrainTile(
        ushort gridArea,
        ushort[]? areaMap,
        float[]? v9,
        float[]? v8,
        float flatHeight,
        ushort[]? holes,
        LiquidChunk? liquid)
    {
        _gridArea = gridArea;
        _areaMap = areaMap;
        _v9 = v9;
        _v8 = v8;
        _flatHeight = flatHeight;
        _holes = holes;
        _liquid = liquid;
    }

    /// <summary>
    /// The liquid chunk of a tile, in the shape the file stores it.
    /// </summary>
    /// <remarks>
    /// Both the per-chunk arrays and the height map are optional and independently so — a tile can
    /// have one uniform liquid at varying heights, four different liquids all at one height, or any
    /// combination. Every one of the four occurs in the shipped data, so none of them is a case that
    /// can be left unhandled.
    /// <para>
    /// <b>The height map is not the full tile.</b> The extractor crops it to the bounding box of the
    /// cells that actually hold liquid, and <see cref="OffsetX"/>/<see cref="OffsetY"/> say where
    /// that box starts. Reading it as a 128×128 grid puts a lake in the wrong place.
    /// </para>
    /// </remarks>
    private sealed class LiquidChunk
    {
        public required ushort GlobalEntry { get; init; }

        public required LiquidTypeMask GlobalType { get; init; }

        public required byte OffsetX { get; init; }

        public required byte OffsetY { get; init; }

        public required byte Width { get; init; }

        public required byte Height { get; init; }

        /// <summary>The surface height when <see cref="Map"/> is absent, and its floor when not.</summary>
        public required float Level { get; init; }

        /// <summary>Per-chunk liquid entries, or null when the whole tile is one liquid.</summary>
        public ushort[]? Entries { get; init; }

        /// <summary>Per-chunk type bits, or null when the whole tile is one liquid.</summary>
        public byte[]? Types { get; init; }

        /// <summary>The cropped surface height map, or null when the surface is flat.</summary>
        public float[]? Map { get; init; }
    }

    /// <summary>Whether this tile carries a height grid, as opposed to a single flat height.</summary>
    public bool HasHeightData => _v9 is not null && _v8 is not null;

    /// <summary>Whether this tile carries per-cell area ids rather than one for the whole tile.</summary>
    public bool HasAreaMap => _areaMap is not null;

    /// <summary>
    /// Builds the file name for a tile.
    /// </summary>
    /// <remarks>
    /// <b>Y comes before X.</b> The extractor writes <c>(mapId, adtY, adtX)</c> while the server
    /// thinks in <c>(gridX, gridY)</c>, so the first number in the name is the ADT row. Swap them
    /// and every tile still loads — just the wrong one — and the world is mirrored across its
    /// diagonal with no error anywhere. PLAN.md §5.1.
    /// </remarks>
    public static string FileName(uint mapId, int gridX, int gridY) =>
        $"{mapId:D3}{gridX:D2}{gridY:D2}.map";

    /// <summary>Loads a tile, or returns null if the file does not exist.</summary>
    /// <exception cref="InvalidDataException">The file exists but is not a version 9 map tile.</exception>
    public static TerrainTile? Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            // Perfectly normal: most of a map's 4096 tiles are empty ocean or void.
            return null;
        }

        byte[] bytes = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);

        if (bytes.Length < 44)
        {
            throw new InvalidDataException($"{name}: too short to be a map tile.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != MapMagic)
        {
            throw new InvalidDataException($"{name}: not a map tile.");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        if (version != SupportedVersion)
        {
            throw new InvalidDataException(
                $"{name}: map version {version}, expected {SupportedVersion}. Re-run the extractor.");
        }

        uint areaOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        uint heightOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
        uint liquidOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28));
        uint holesOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36));
        uint holesSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));

        (ushort gridArea, ushort[]? areaMap) = ReadArea(bytes, areaOffset, name);
        (float[]? v9, float[]? v8, float flatHeight) = ReadHeights(bytes, heightOffset, name);
        LiquidChunk? liquid = ReadLiquid(bytes, liquidOffset, name);
        ushort[]? holes = ReadHoles(bytes, holesOffset, holesSize);

        return new TerrainTile(gridArea, areaMap, v9, v8, flatHeight, holes, liquid);
    }

    /// <summary>
    /// Reads the liquid chunk: a header, then optionally per-chunk types and a surface height map.
    /// </summary>
    /// <remarks>
    /// The header is 16 bytes and naturally packed — <c>uint32</c>, two <c>uint8</c>, <c>uint16</c>,
    /// four <c>uint8</c>, <c>float</c> — so it has no padding on any platform the extractor runs on.
    /// That is worth stating because the two <c>uint8</c> flag fields sit where a reader skimming the
    /// struct would expect a single <c>uint16</c>, and swapping them silently turns "no height map"
    /// into "ocean".
    /// </remarks>
    private static LiquidChunk? ReadLiquid(byte[] bytes, uint offset, string name)
    {
        if (offset == 0)
        {
            // Normal: 2,548 of the 5,744 shipped tiles have no liquid anywhere on them.
            return null;
        }

        int at = (int)offset;

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) != LiquidMagic)
        {
            throw new InvalidDataException($"{name}: liquid chunk is missing its marker.");
        }

        byte flags = bytes[at + 4];
        byte globalType = bytes[at + 5];
        ushort globalEntry = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 6));
        byte offsetX = bytes[at + 8];
        byte offsetY = bytes[at + 9];
        byte width = bytes[at + 10];
        byte height = bytes[at + 11];
        float level = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 12));

        int data = at + 16;

        ushort[]? entries = null;
        byte[]? types = null;

        if ((flags & LiquidNoType) == 0)
        {
            const int Cells = LiquidChunksPerAxis * LiquidChunksPerAxis;

            entries = new ushort[Cells];

            for (int i = 0; i < Cells; i++)
            {
                entries[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(data + (i * 2)));
            }

            data += Cells * 2;

            types = bytes.AsSpan(data, Cells).ToArray();
            data += Cells;
        }

        float[]? map = null;

        if ((flags & LiquidNoHeight) == 0)
        {
            map = new float[width * height];

            for (int i = 0; i < map.Length; i++)
            {
                map[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(data + (i * 4)));
            }
        }

        return new LiquidChunk
        {
            GlobalEntry = globalEntry,
            GlobalType = (LiquidTypeMask)globalType,
            OffsetX = offsetX,
            OffsetY = offsetY,
            Width = width,
            Height = height,
            Level = level,
            Entries = entries,
            Types = types,
            Map = map,
        };
    }

    private static (ushort GridArea, ushort[]? AreaMap) ReadArea(byte[] bytes, uint offset, string name)
    {
        if (offset == 0)
        {
            return (0, null);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset)) != AreaMagic)
        {
            throw new InvalidDataException($"{name}: area chunk is missing its marker.");
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset + 4));
        ushort gridArea = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)offset + 6));

        if ((flags & AreaNoArea) != 0)
        {
            // The whole tile is one area, so there is no 16×16 map to read.
            return (gridArea, null);
        }

        ushort[] areaMap = new ushort[16 * 16];
        ReadOnlySpan<byte> source = bytes.AsSpan((int)offset + 8, areaMap.Length * 2);

        for (int i = 0; i < areaMap.Length; i++)
        {
            areaMap[i] = BinaryPrimitives.ReadUInt16LittleEndian(source[(i * 2)..]);
        }

        return (gridArea, areaMap);
    }

    /// <summary>
    /// Reads the height grids, widening the packed forms back to floats.
    /// </summary>
    /// <remarks>
    /// Widening on load costs memory — a packed tile is 4× smaller on disk — but it removes the
    /// three-way branch from every single height query, and queries vastly outnumber loads.
    /// </remarks>
    private static (float[]? V9, float[]? V8, float FlatHeight) ReadHeights(byte[] bytes, uint offset, string name)
    {
        if (offset == 0)
        {
            return (null, null, MapGeometry.InvalidHeight);
        }

        int at = (int)offset;

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) != HeightMagic)
        {
            throw new InvalidDataException($"{name}: height chunk is missing its marker.");
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
        float gridHeight = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 8));
        float gridMaxHeight = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 12));

        if ((flags & HeightNoHeight) != 0)
        {
            // Perfectly flat tile — open ocean, mostly.
            return (null, null, gridHeight);
        }

        int data = at + 16;
        float[] v9 = new float[MapGeometry.V9Size * MapGeometry.V9Size];
        float[] v8 = new float[MapGeometry.V8Size * MapGeometry.V8Size];

        if ((flags & HeightAsInt16) != 0)
        {
            float scale = (gridMaxHeight - gridHeight) / 65535f;

            for (int i = 0; i < v9.Length; i++)
            {
                v9[i] = gridHeight + (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(data + (i * 2))) * scale);
            }

            data += v9.Length * 2;

            for (int i = 0; i < v8.Length; i++)
            {
                v8[i] = gridHeight + (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(data + (i * 2))) * scale);
            }
        }
        else if ((flags & HeightAsInt8) != 0)
        {
            float scale = (gridMaxHeight - gridHeight) / 255f;

            for (int i = 0; i < v9.Length; i++)
            {
                v9[i] = gridHeight + (bytes[data + i] * scale);
            }

            data += v9.Length;

            for (int i = 0; i < v8.Length; i++)
            {
                v8[i] = gridHeight + (bytes[data + i] * scale);
            }
        }
        else
        {
            for (int i = 0; i < v9.Length; i++)
            {
                v9[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(data + (i * 4)));
            }

            data += v9.Length * 4;

            for (int i = 0; i < v8.Length; i++)
            {
                v8[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(data + (i * 4)));
            }
        }

        return (v9, v8, gridHeight);
    }

    private static ushort[]? ReadHoles(byte[] bytes, uint offset, uint size)
    {
        if (offset == 0 || size == 0)
        {
            return null;
        }

        ushort[] holes = new ushort[16 * 16];
        ReadOnlySpan<byte> source = bytes.AsSpan((int)offset, Math.Min((int)size, holes.Length * 2));

        for (int i = 0; i < holes.Length && (i * 2) + 1 < source.Length; i++)
        {
            holes[i] = BinaryPrimitives.ReadUInt16LittleEndian(source[(i * 2)..]);
        }

        return holes;
    }

    /// <summary>The area id under a world coordinate, or 0 if this tile has none.</summary>
    public ushort GetArea(float x, float y)
    {
        if (_areaMap is null)
        {
            return _gridArea;
        }

        float cellX = 16 * (MapGeometry.CenterGrid - (x / MapGeometry.GridSize));
        float cellY = 16 * (MapGeometry.CenterGrid - (y / MapGeometry.GridSize));

        int lx = (int)cellX & 15;
        int ly = (int)cellY & 15;

        return _areaMap[(lx * 16) + ly];
    }

    /// <summary>
    /// The ground height at a world coordinate.
    /// </summary>
    /// <remarks>
    /// Each cell of the grid is four triangles meeting at its centre — the V8 sample — so the
    /// lookup picks a triangle from the fractional position and solves its plane equation. The
    /// centre value is doubled because V8 stores half-heights.
    /// <para>
    /// Returns <see cref="MapGeometry.InvalidHeight"/> over a hole. Holes are real: they are how
    /// the terrain gets openings for caves and building interiors.
    /// </para>
    /// </remarks>
    public float GetHeight(float x, float y)
    {
        if (_v9 is null || _v8 is null)
        {
            return _flatHeight;
        }

        float sampleX = MapGeometry.Resolution * (MapGeometry.CenterGrid - (x / MapGeometry.GridSize));
        float sampleY = MapGeometry.Resolution * (MapGeometry.CenterGrid - (y / MapGeometry.GridSize));

        int intX = (int)sampleX;
        int intY = (int)sampleY;

        sampleX -= intX;
        sampleY -= intY;

        intX &= MapGeometry.Resolution - 1;
        intY &= MapGeometry.Resolution - 1;

        if (IsHole(intX, intY))
        {
            return MapGeometry.InvalidHeight;
        }

        // h1---h2   The cell's four corners come from V9; h5 is its centre, from V8.
        // | \1/ |
        // |2 h5 3|
        // | /4\ |
        // h3---h4
        const int Stride = MapGeometry.V9Size;

        float a, b, c;
        float centre = 2 * _v8[(intX * MapGeometry.V8Size) + intY];

        if (sampleX + sampleY < 1)
        {
            if (sampleX > sampleY)
            {
                float h1 = _v9[(intX * Stride) + intY];
                float h2 = _v9[((intX + 1) * Stride) + intY];
                a = h2 - h1;
                b = centre - h1 - h2;
                c = h1;
            }
            else
            {
                float h1 = _v9[(intX * Stride) + intY];
                float h3 = _v9[(intX * Stride) + intY + 1];
                a = centre - h1 - h3;
                b = h3 - h1;
                c = h1;
            }
        }
        else
        {
            if (sampleX > sampleY)
            {
                float h2 = _v9[((intX + 1) * Stride) + intY];
                float h4 = _v9[((intX + 1) * Stride) + intY + 1];
                a = h2 + h4 - centre;
                b = h4 - h2;
                c = centre - h4;
            }
            else
            {
                float h3 = _v9[(intX * Stride) + intY + 1];
                float h4 = _v9[((intX + 1) * Stride) + intY + 1];
                a = h4 - h3;
                b = h3 + h4 - centre;
                c = centre - h4;
            }
        }

        return (a * sampleX) + (b * sampleY) + c;
    }

    /// <summary>Whether this tile carries any liquid at all.</summary>
    public bool HasLiquid => _liquid is not null;

    /// <summary>
    /// The height of the liquid surface at a world coordinate.
    /// </summary>
    /// <remarks>
    /// <see cref="MapGeometry.InvalidHeight"/> where this tile has no liquid, or where the point
    /// falls outside the cropped box the height map covers. Note that being outside the box is not
    /// the same as being outside the liquid — a tile with a uniform surface has no map at all and
    /// answers everywhere.
    /// </remarks>
    public float GetLiquidLevel(float x, float y)
    {
        if (_liquid is not { } liquid)
        {
            return MapGeometry.InvalidHeight;
        }

        if (liquid.Map is null)
        {
            return liquid.Level;
        }

        (int row, int column) = LiquidCell(x, y);

        int mapRow = row - liquid.OffsetY;
        int mapColumn = column - liquid.OffsetX;

        return InsideMap(liquid, mapRow, mapColumn)
            ? liquid.Map[(mapRow * liquid.Width) + mapColumn]
            : MapGeometry.InvalidHeight;
    }

    /// <summary>
    /// The liquid at a point, and where that point sits in it.
    /// </summary>
    /// <param name="x">World x.</param>
    /// <param name="y">World y.</param>
    /// <param name="z">The height being asked about — usually a unit's feet.</param>
    /// <param name="collisionHeight">
    /// How tall the unit is. It is the threshold between wading and being submerged, so passing a
    /// creature's height here rather than a constant is what stops a tauren drowning in a puddle.
    /// </param>
    /// <remarks>
    /// Port of <c>GridTerrainData::GetLiquidData</c>, less the zone override — see the class remarks.
    /// <para>
    /// The <c>z >= ground - 0.2</c> guard is upstream's and is load-bearing: without it, standing in
    /// a cave <i>underneath</i> a lake reports you as swimming in the lake overhead.
    /// </para>
    /// </remarks>
    public LiquidData GetLiquidData(float x, float y, float z, float collisionHeight)
    {
        if (_liquid is not { } liquid)
        {
            return LiquidData.None;
        }

        (int row, int column) = LiquidCell(x, y);

        // The type grid is 16×16 over the whole tile and is indexed independently of the height
        // map's cropped box — hence the shift by three rather than a subtraction of the offsets.
        int chunk = ((row >> 3) * LiquidChunksPerAxis) + (column >> 3);

        LiquidTypeMask type = liquid.Types is null
            ? liquid.GlobalType
            : (LiquidTypeMask)liquid.Types[chunk];

        uint entry = liquid.Entries is null ? liquid.GlobalEntry : liquid.Entries[chunk];

        if (type == LiquidTypeMask.None)
        {
            return LiquidData.None;
        }

        // The height map is cropped; the type grid is not. A point can legitimately have a liquid
        // type and fall outside the box, and that means no liquid rather than a level of zero.
        int mapRow = row - liquid.OffsetY;
        int mapColumn = column - liquid.OffsetX;

        if (!InsideMap(liquid, mapRow, mapColumn))
        {
            return LiquidData.None;
        }

        float surface = liquid.Map is null
            ? liquid.Level
            : liquid.Map[(mapRow * liquid.Width) + mapColumn];

        float floor = GetHeight(x, y);

        if (surface < floor || z < floor - 0.2f)
        {
            return LiquidData.None;
        }

        float depth = surface - z;

        LiquidStatus status = depth > collisionHeight ? LiquidStatus.UnderWater
            : depth > 0.0f ? LiquidStatus.InWater
            : depth > -0.1f ? LiquidStatus.WaterWalk
            : LiquidStatus.AboveWater;

        return new LiquidData(entry, type, surface, floor, status);
    }

    /// <summary>
    /// The tile-local sample a world coordinate falls on, as (row, column).
    /// </summary>
    /// <remarks>
    /// <b>Row comes from x and column from y</b>, and the liquid offsets are applied crossed —
    /// upstream subtracts <c>liquidOffY</c> from the x index and <c>liquidOffX</c> from the y index.
    /// That looks like a bug and is not one: the extractor's <c>minX</c>/<c>minY</c> are measured
    /// over its own <c>[y][x]</c> arrays, so they arrive already transposed relative to the names
    /// they carry. Straightening it out here would offset every cropped lake by its own bounding box.
    /// </remarks>
    private static (int Row, int Column) LiquidCell(float x, float y)
    {
        float sampleX = MapGeometry.Resolution * (MapGeometry.CenterGrid - (x / MapGeometry.GridSize));
        float sampleY = MapGeometry.Resolution * (MapGeometry.CenterGrid - (y / MapGeometry.GridSize));

        return ((int)sampleX & (MapGeometry.Resolution - 1), (int)sampleY & (MapGeometry.Resolution - 1));
    }

    /// <summary>
    /// Whether a cropped-box coordinate is inside the height map.
    /// </summary>
    /// <remarks>
    /// The row is bounded by the height and the column by the width, while the stride is the width.
    /// Upstream's own naming makes this read backwards; the bounds are reproduced as it has them
    /// because the data was written to match.
    /// </remarks>
    private static bool InsideMap(LiquidChunk liquid, int row, int column) =>
        row >= 0 && row < liquid.Height && column >= 0 && column < liquid.Width;

    /// <summary>Whether a sample falls in a hole in the terrain.</summary>
    private bool IsHole(int row, int column)
    {
        if (_holes is null)
        {
            return false;
        }

        int cellRow = row / 8;
        int cellColumn = column / 8;
        int holeRow = row % 8 / 2;
        int holeColumn = (column - (cellColumn * 8)) / 2;

        ushort hole = _holes[(cellRow * 16) + cellColumn];

        return (hole & HoleColumnMask[holeColumn] & HoleRowMask[holeRow]) != 0;
    }
}
