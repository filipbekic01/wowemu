using System.Globalization;
using WowEmu.Data.Client;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// What a map costs once every grid a player could reach has been loaded.
/// </summary>
/// <remarks>
/// Measurement rather than assertion, because the question it answers is a design one: TODO.md
/// carried "unload tiles when a grid empties" in two phases, on the reasoning that a grid once
/// loaded stays for the life of the process.
/// <para>
/// <b>That is also what AzerothCore does.</b> This version has no grid state machine at all — the
/// only caller of <c>MapGridManager::UnloadGrid</c> is <c>Map::UnloadAll</c>, which runs at
/// shutdown. Instances are unloaded whole when they empty; continents keep every grid they ever
/// touched. So the growth is not a leak but a bounded working set, and the number worth having is
/// the size of that bound.
/// </para>
/// </remarks>
public sealed class GridResidencyTests(ITestOutputHelper output)
{
    /// <summary>
    /// Loads every terrain tile of the eastern kingdoms and reports what they cost.
    /// </summary>
    /// <remarks>
    /// The ceiling, not a typical figure: a real server reaches it only if players between them
    /// visit every corner of the map. The assertion is deliberately loose — it is there to catch a
    /// change of an order of magnitude, which would mean the tile representation had grown a
    /// dimension, not to pin a number that varies with the runtime.
    /// </remarks>
    [RequiresMapsFact]
    public void EveryTerrainTileOfAContinent_Measured()
    {
        TerrainMap map = new(EasternKingdoms, Path.Combine(ClientData.DataDirectory, "maps"));

        long before = GC.GetTotalMemory(forceFullCollection: true);

        int loaded = 0;

        for (int gridX = 0; gridX < GridsPerSide; gridX++)
        {
            for (int gridY = 0; gridY < GridsPerSide; gridY++)
            {
                // The centre of the grid, which is enough to fault the whole tile in.
                (float x, float y) = CentreOf(gridX, gridY);

                if (map.HasTerrain(x, y))
                {
                    loaded++;
                }
            }
        }

        long after = GC.GetTotalMemory(forceFullCollection: true);
        double megabytes = (after - before) / (1024.0 * 1024.0);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"map {EasternKingdoms}: {loaded} tiles resident, {megabytes:F1} MB, {megabytes / loaded * 1024:F0} KB each"));

        Assert.True(loaded > 400, $"only {loaded} tiles found — is data/maps complete?");

        // Two gigabytes would mean something had gone badly wrong with the representation; the
        // measured figure is printed above and is what the board records.
        Assert.True(megabytes < 2048, $"{megabytes:F1} MB for one continent's terrain");
    }

    /// <summary>Eastern Kingdoms, which is the largest continent by tile count.</summary>
    private const uint EasternKingdoms = 0;

    /// <summary>A map is 64 × 64 grids. <c>MAX_NUMBER_OF_GRIDS</c>.</summary>
    private const int GridsPerSide = 64;

    /// <summary>
    /// The world coordinate at the middle of a grid.
    /// </summary>
    /// <remarks>
    /// The axes run backwards and the origin is the centre, which is why this is not
    /// <c>gridX * size</c>. Getting it wrong samples the wrong tile and quietly measures half the
    /// continent.
    /// </remarks>
    private static (float X, float Y) CentreOf(int gridX, int gridY)
    {
        const float gridSize = 533.33333f;
        const float centre = 32f;

        return (
            (centre - gridX - 0.5f) * gridSize,
            (centre - gridY - 0.5f) * gridSize);
    }
}
