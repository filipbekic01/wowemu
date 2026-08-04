using WowEmu.Data.Client;

namespace WowEmu.Game.Maps;

/// <summary>A grid tile's position on a map. 64×64 of them cover a map.</summary>
public readonly record struct GridCoord(int X, int Y);

/// <summary>
/// A cell's position on a map, at 8×8 cells per grid — so 512×512 across the whole map.
/// </summary>
/// <remarks>
/// Cells are the unit visibility works in: a range query visits the cells a circle touches rather
/// than every object on the map. At 66.7 yards a cell and 100 yards of visibility, that is a 5×5
/// block of cells instead of potentially thousands of objects.
/// </remarks>
public readonly record struct CellCoord(int X, int Y);

/// <summary>
/// Converts world coordinates into grid and cell indices.
/// </summary>
/// <remarks>
/// <b>The axis is inverted and the origin is in the middle.</b> World X grows in the opposite
/// direction to grid indices, and (0, 0) sits at grid (32, 32) — so the conversion subtracts rather
/// than dividing straight. Every one of these formulas is upstream's; an inverted sign produces
/// coordinates that are valid, in range, and on the wrong side of the world.
/// </remarks>
public static class MapCoordinates
{
    /// <summary>Cells per grid, per axis.</summary>
    public const int CellsPerGrid = 8;

    /// <summary>Cells across the whole map, per axis.</summary>
    public const int CellsPerMap = MapGeometry.GridsPerAxis * CellsPerGrid;

    /// <summary>One cell, in yards.</summary>
    public const float CellSize = MapGeometry.GridSize / CellsPerGrid;

    /// <summary>The cell index the world origin falls in.</summary>
    public const int CenterCell = CellsPerMap / 2;

    /// <summary>How far a player can see on a continent, in yards.</summary>
    public const float DefaultVisibilityDistance = 100.0f;

    /// <summary>Upstream's hard ceiling on visibility.</summary>
    public const float MaxVisibilityDistance = 250.0f;

    /// <summary>The grid containing a world coordinate.</summary>
    public static GridCoord GridFor(float x, float y)
    {
        (int gridX, int gridY) = MapGeometry.GridFor(x, y);
        return new GridCoord(Clamp(gridX, MapGeometry.GridsPerAxis), Clamp(gridY, MapGeometry.GridsPerAxis));
    }

    /// <summary>The cell containing a world coordinate.</summary>
    public static CellCoord CellFor(float x, float y) => new(
        Clamp((int)(CenterCell - (x / CellSize)), CellsPerMap),
        Clamp((int)(CenterCell - (y / CellSize)), CellsPerMap));

    /// <summary>
    /// Every cell a circle of <paramref name="radius"/> around a point touches.
    /// </summary>
    /// <remarks>
    /// Returns the bounding square of cells, not the exact circle — a few extra cells cost one
    /// distance check each, while missing one makes an object invisible from a direction.
    /// </remarks>
    public static IEnumerable<CellCoord> CellsInRange(float x, float y, float radius)
    {
        CellCoord low = CellFor(x + radius, y + radius);
        CellCoord high = CellFor(x - radius, y - radius);

        for (int cellX = low.X; cellX <= high.X; cellX++)
        {
            for (int cellY = low.Y; cellY <= high.Y; cellY++)
            {
                yield return new CellCoord(cellX, cellY);
            }
        }
    }

    /// <summary>The grid a cell belongs to.</summary>
    public static GridCoord GridOf(CellCoord cell) => new(cell.X / CellsPerGrid, cell.Y / CellsPerGrid);

    private static int Clamp(int value, int limit) => value < 0 ? 0 : value >= limit ? limit - 1 : value;
}
