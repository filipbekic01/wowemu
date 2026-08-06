using System.Collections.Concurrent;

namespace WowEmu.Data.Client;

/// <summary>
/// The terrain of one map, loaded tile by tile as it is asked for.
/// </summary>
/// <remarks>
/// A map is 64×64 tiles, but almost all of them are empty — Azeroth ships around 1,100 of a
/// possible 4,096. Loading on demand keeps startup fast and memory proportional to where players
/// actually are; Phase 6 will unload tiles again when a grid empties.
/// <para>
/// Tiles are immutable once loaded, so reads need no lock. The dictionary is concurrent because two
/// sessions can ask for the same tile at once — the worst case is loading it twice and keeping one.
/// </para>
/// </remarks>
public sealed class TerrainMap(uint mapId, string mapsDirectory)
{
    private readonly ConcurrentDictionary<(int GridX, int GridY), TerrainTile?> _tiles = new();

    public uint MapId { get; } = mapId;

    /// <summary>How many tiles have been loaded, including ones found to be absent.</summary>
    public int LoadedTileCount => _tiles.Count;

    /// <summary>
    /// The ground height at a world coordinate, or <see cref="MapGeometry.InvalidHeight"/> where
    /// there is no terrain.
    /// </summary>
    public float GetHeight(float x, float y)
    {
        TerrainTile? tile = GetTile(x, y);

        return tile?.GetHeight(x, y) ?? MapGeometry.InvalidHeight;
    }

    /// <summary>The area id under a world coordinate, or 0 where there is no terrain.</summary>
    public ushort GetAreaId(float x, float y)
    {
        TerrainTile? tile = GetTile(x, y);

        return tile?.GetArea(x, y) ?? (ushort)0;
    }

    /// <summary>Whether a coordinate has terrain under it at all.</summary>
    public bool HasTerrain(float x, float y) => GetTile(x, y) is not null;

    /// <summary>
    /// The height of the liquid surface at a world coordinate.
    /// </summary>
    /// <remarks>
    /// <see cref="MapGeometry.InvalidHeight"/> where there is no liquid. Callers must treat that as
    /// "no answer" rather than as a surface far below — a comparison against it succeeds for every
    /// real z, so a missing check reads as "standing above the water" everywhere on the map.
    /// </remarks>
    public float GetLiquidLevel(float x, float y)
    {
        TerrainTile? tile = GetTile(x, y);

        return tile?.GetLiquidLevel(x, y) ?? MapGeometry.InvalidHeight;
    }

    /// <summary>The liquid at a point, and where that point sits in it.</summary>
    /// <param name="collisionHeight">
    /// How tall the unit asking is — the threshold between wading and being submerged.
    /// </param>
    public LiquidData GetLiquidData(float x, float y, float z, float collisionHeight)
    {
        TerrainTile? tile = GetTile(x, y);

        return tile?.GetLiquidData(x, y, z, collisionHeight) ?? LiquidData.None;
    }

    private TerrainTile? GetTile(float x, float y)
    {
        (int gridX, int gridY) = MapGeometry.GridFor(x, y);

        if ((uint)gridX >= MapGeometry.GridsPerAxis || (uint)gridY >= MapGeometry.GridsPerAxis)
        {
            return null;
        }

        // A null value is cached deliberately: "this tile does not exist" is worth remembering, or
        // every query over open ocean would hit the filesystem again.
        return _tiles.GetOrAdd((gridX, gridY), key => TerrainTile.Load(
            Path.Combine(mapsDirectory, TerrainTile.FileName(MapId, key.GridX, key.GridY))));
    }
}

/// <summary>Terrain for every map, created on first use.</summary>
public sealed class TerrainManager(string dataDirectory)
{
    private readonly ConcurrentDictionary<uint, TerrainMap> _maps = new();
    private readonly string _mapsDirectory = Path.Combine(dataDirectory, "maps");

    /// <summary>Whether the maps directory exists at all.</summary>
    public bool IsAvailable => Directory.Exists(_mapsDirectory);

    public string MapsDirectory => _mapsDirectory;

    public TerrainMap GetMap(uint mapId) =>
        _maps.GetOrAdd(mapId, id => new TerrainMap(id, _mapsDirectory));

    /// <summary>How many <c>.map</c> files are present, for the startup log.</summary>
    public int CountTileFiles() =>
        IsAvailable ? Directory.EnumerateFiles(_mapsDirectory, "*.map").Count() : 0;
}
