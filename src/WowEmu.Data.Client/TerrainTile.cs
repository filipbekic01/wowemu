using System.Buffers.Binary;

namespace WowEmu.Data.Client;

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

    // Hole lookup tables, verbatim from upstream.
    private static readonly ushort[] HoleColumnMask = [0x1111, 0x2222, 0x4444, 0x8888];
    private static readonly ushort[] HoleRowMask = [0x000F, 0x00F0, 0x0F00, 0xF000];

    private readonly ushort[]? _areaMap;
    private readonly ushort _gridArea;

    private readonly float[]? _v9;
    private readonly float[]? _v8;
    private readonly float _flatHeight;

    private readonly ushort[]? _holes;

    private TerrainTile(
        ushort gridArea,
        ushort[]? areaMap,
        float[]? v9,
        float[]? v8,
        float flatHeight,
        ushort[]? holes)
    {
        _gridArea = gridArea;
        _areaMap = areaMap;
        _v9 = v9;
        _v8 = v8;
        _flatHeight = flatHeight;
        _holes = holes;
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
        uint holesOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36));
        uint holesSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));

        (ushort gridArea, ushort[]? areaMap) = ReadArea(bytes, areaOffset, name);
        (float[]? v9, float[]? v8, float flatHeight) = ReadHeights(bytes, heightOffset, name);
        ushort[]? holes = ReadHoles(bytes, holesOffset, holesSize);

        return new TerrainTile(gridArea, areaMap, v9, v8, flatHeight, holes);
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
