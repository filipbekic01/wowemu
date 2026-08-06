using System.Collections.Concurrent;
using System.Numerics;

namespace WowEmu.Data.Client;

/// <summary>
/// One map's static collision, answering questions in world coordinates.
/// </summary>
/// <remarks>
/// Port of <c>StaticMapTree</c>. Below it sit the file readers and the intersection maths; this is
/// what joins them to the world — it owns the map's BIH, loads a tile's models when something asks
/// about that part of the map, and converts coordinates at the boundary.
/// <para>
/// <b>Tiles are loaded on demand and never unloaded.</b> A map's worth of models is a few hundred
/// megabytes at worst and a query touches one tile, so the cost is bounded by where players
/// actually go. Unloading needs to know nobody is near, which is the same problem grid unloading
/// has — see TODO.md.
/// </para>
/// </remarks>
public sealed class StaticMapTree(uint mapId, string vmapsDirectory)
{
    private readonly ConcurrentDictionary<(int GridX, int GridY), bool> _loadedTiles = new();
    private readonly ConcurrentDictionary<uint, ModelInstance> _instances = new();
    private readonly Lock _loadLock = new();

    private VmapTree? _tree;
    private bool _treeLoadAttempted;

    public uint MapId { get; } = mapId;

    /// <summary>How many tiles have been asked for and loaded.</summary>
    public int LoadedTileCount => _loadedTiles.Count;

    /// <summary>How many model instances are placed and ready to be tested against.</summary>
    public int InstanceCount => _instances.Count;

    /// <summary>Whether this map has a collision tree at all.</summary>
    public bool IsAvailable
    {
        get
        {
            EnsureTreeLoaded();
            return _tree is not null;
        }
    }

    /// <summary>
    /// Builds a vmap tile's file name.
    /// </summary>
    /// <remarks>
    /// <b>A vmap tile names its grid the opposite way round from a terrain tile.</b> A
    /// <c>.map</c> file is <c>{map}{gridX}{gridY}</c>, but a <c>.vmtile</c> is
    /// <c>{map}_{gridY}_{gridX}</c> — upstream writes <c>tileY</c> before <c>tileX</c>, with the
    /// natural ordering sitting commented out on the line above.
    /// <para>
    /// Measured, not assumed: converting each model's stored position back to world coordinates and
    /// asking which grid it falls in agrees with this ordering for 139 of 144 sampled tiles on
    /// map 0, and the five exceptions are diagonal tiles where both orderings are identical.
    /// Use the terrain convention here and every query outside the diagonal loads a tile mirrored
    /// across the map — which finds real geometry in the wrong place and reports it confidently.
    /// </para>
    /// </remarks>
    public static string TileFileName(uint mapId, int gridX, int gridY) =>
        $"{mapId:D3}_{gridY:D2}_{gridX:D2}.vmtile";

    /// <summary>Builds a map's tree file name.</summary>
    public static string TreeFileName(uint mapId) => $"{mapId:D3}.vmtree";

    /// <summary>
    /// Whether one world position can see another.
    /// </summary>
    /// <remarks>
    /// Returns <b>true when the view is clear</b>. A map with no collision data answers true rather
    /// than false: a missing file must not make the whole world opaque.
    /// </remarks>
    public bool IsInLineOfSight(
        float x1, float y1, float z1,
        float x2, float y2, float z2)
    {
        Vector3 from = Collision.ToInternal(x1, y1, z1);
        Vector3 to = Collision.ToInternal(x2, y2, z2);

        Vector3 delta = to - from;
        float maxDistance = delta.Length();

        if (!float.IsFinite(maxDistance))
        {
            // A position this broken is not a line-of-sight question. Upstream refuses it too.
            return false;
        }

        // Two points at the same place always see each other, and normalising a zero vector would
        // put NaN into the traversal — which upstream notes can spin the BIH walk forever.
        if (maxDistance < 1e-10f)
        {
            return true;
        }

        EnsureTilesLoadedFor(x1, y1);
        EnsureTilesLoadedFor(x2, y2);

        Ray ray = new(from, delta / maxDistance);
        return !Intersects(ray, ref maxDistance, stopAtFirstHit: true);
    }

    /// <summary>
    /// The height of the highest surface below a point, or null when there is none.
    /// </summary>
    /// <remarks>
    /// A downward ray. This is what terrain alone cannot answer: a bridge, a floor inside a
    /// building, a platform. Terrain height and this one are both real answers, and the caller wants
    /// whichever is higher — which is why this returns null rather than falling back, so that
    /// "no model here" is distinguishable from "a model at ground level".
    /// </remarks>
    public float? GetHeight(float x, float y, float z, float searchDistance = 50f)
    {
        EnsureTilesLoadedFor(x, y);

        Vector3 from = Collision.ToInternal(x, y, z);
        Ray ray = new(from, new Vector3(0f, 0f, -1f));

        float distance = searchDistance;

        return Intersects(ray, ref distance, stopAtFirstHit: false)
            ? z - distance
            : null;
    }

    /// <summary>
    /// The liquid inside a model at a world point, if the point is inside one that has any.
    /// </summary>
    /// <remarks>
    /// Port of <c>StaticVMapCollisionData::GetAreaAndLiquidData</c>, less the area half. This is the
    /// other kind of water: terrain liquid covers lakes, rivers and the ocean, and everything under
    /// a roof — a fountain, a canal, a flooded dungeon room — is a grid inside a WMO instead. A
    /// server that only reads terrain liquid believes an indoor pool is dry air.
    /// <para>
    /// Returns null when the point is not inside any model, or is inside one with no liquid in that
    /// group. Both are ordinary and mean "ask the terrain instead".
    /// </para>
    /// <para>
    /// The <see cref="LiquidData.Entry"/> a model stores is a <c>LiquidType.dbc</c> row, <b>not</b>
    /// the sound-bank bitmask a terrain tile carries — the extractor resolves that for terrain and
    /// leaves a WMO's to the reader. So <paramref name="liquidTypes"/> is what turns entry 20 into
    /// "slime" rather than an opaque number; without it <see cref="LiquidData.Type"/> is left empty
    /// rather than guessed at, and a caller cannot tell Undercity's slime from Stormwind's canal.
    /// </para>
    /// </remarks>
    public ModelLiquid? GetLiquid(
        float x,
        float y,
        float z,
        float collisionHeight,
        DbcStore<LiquidTypeEntry>? liquidTypes = null)
    {
        EnsureTreeLoaded();
        EnsureTilesLoadedFor(x, y);

        if (_tree is null)
        {
            return null;
        }

        Vector3 point = Collision.ToInternal(x, y, z);

        List<uint> candidates = [];
        Collision.CollectCandidatesAtPoint(_tree.Tree, point, candidates);

        // The highest floor wins, the same way upstream keeps the greatest ground_Z across
        // instances: two models can overlap, and the one you are standing in is the upper one.
        ModelLocation? best = null;
        ModelSpawn bestSpawn = default;

        foreach (uint candidate in candidates)
        {
            if (!_instances.TryGetValue(candidate, out ModelInstance? instance))
            {
                continue;
            }

            if (ModelLocator.TryLocate(instance.Spawn, instance.Model, point, out ModelLocation found)
                && (best is null || found.GroundZ > best.Value.GroundZ))
            {
                best = found;
                bestSpawn = instance.Spawn;
            }
        }

        if (best is not { } location || location.Group.Liquid is not { } liquid)
        {
            return null;
        }

        if (!ModelLocator.TryGetLiquidLevel(bestSpawn, location.Group, point, out float level))
        {
            return null;
        }

        float depth = level - z;

        LiquidStatus status = depth > collisionHeight ? LiquidStatus.UnderWater
            : depth > 0.0f ? LiquidStatus.InWater
            : depth > -0.1f ? LiquidStatus.WaterWalk
            : LiquidStatus.AboveWater;

        LiquidTypeMask type = liquidTypes is not null && liquidTypes.TryGet(liquid.Type, out LiquidTypeEntry? entry)
            ? entry!.Type
            : LiquidTypeMask.None;

        return new ModelLiquid(
            new LiquidData(liquid.Type, type, level, location.GroundZ, status),
            IsInterior: (location.Group.MogpFlags & WmoInteriorFlag) != 0);
    }

    /// <summary>
    /// The <c>MOGP</c> bit that marks a group as an enclosed interior.
    /// </summary>
    /// <remarks>
    /// It decides which liquid wins when a point has both kinds. Indoors the model's own water is
    /// the only water there is, and letting the terrain override it would put a lake through the
    /// floor of every building standing in one.
    /// </remarks>
    public const uint WmoInteriorFlag = 0x2000;

    /// <summary>Loads the models a tile places, if it has not been loaded already.</summary>
    public void LoadTile(int gridX, int gridY)
    {
        EnsureTreeLoaded();

        if (_tree is null || !_loadedTiles.TryAdd((gridX, gridY), true))
        {
            return;
        }

        string path = Path.Combine(vmapsDirectory, TileFileName(MapId, gridX, gridY));

        if (!File.Exists(path))
        {
            // Most of a map's 4096 tiles have no models at all.
            return;
        }

        IReadOnlyList<VmapTileSpawn> placements;

        try
        {
            placements = VmapFile.ReadTile(File.ReadAllBytes(path));
        }
        catch (InvalidDataException)
        {
            // A corrupt tile costs its own collision, not the map's.
            return;
        }

        foreach (VmapTileSpawn placement in placements)
        {
            // The tile names the slot in the map tree this model fills. A slot past the end means
            // the tile and the tree disagree, which upstream logs and skips.
            if (placement.TreeIndex >= (uint)_tree.Tree.PrimitiveCount)
            {
                continue;
            }

            if (_instances.ContainsKey(placement.TreeIndex))
            {
                continue;
            }

            WorldModel? model = LoadModel(placement.Spawn.ModelFileName);

            if (model is not null)
            {
                _instances.TryAdd(placement.TreeIndex, new ModelInstance(placement.Spawn, model));
            }
        }
    }

    private bool Intersects(Ray ray, ref float maxDistance, bool stopAtFirstHit)
    {
        if (_tree is null)
        {
            return false;
        }

        bool hit = false;

        // The map tree indexes every slot a tile could fill, but only loaded tiles have instances —
        // so a candidate with no instance is a model in a tile nobody has asked about.
        List<uint> candidates = [];
        Collision.CollectCandidates(_tree.Tree, ray, maxDistance, candidates);

        foreach (uint candidate in candidates)
        {
            if (!_instances.TryGetValue(candidate, out ModelInstance? instance))
            {
                continue;
            }

            if (Collision.IntersectInstance(instance.Spawn, instance.Model, ray, ref maxDistance, stopAtFirstHit))
            {
                hit = true;

                if (stopAtFirstHit)
                {
                    return true;
                }
            }
        }

        return hit;
    }

    private void EnsureTilesLoadedFor(float x, float y)
    {
        (int gridX, int gridY) = MapGeometry.GridFor(x, y);

        if ((uint)gridX < MapGeometry.GridsPerAxis && (uint)gridY < MapGeometry.GridsPerAxis)
        {
            LoadTile(gridX, gridY);
        }
    }

    private void EnsureTreeLoaded()
    {
        if (_treeLoadAttempted)
        {
            return;
        }

        lock (_loadLock)
        {
            if (_treeLoadAttempted)
            {
                return;
            }

            _treeLoadAttempted = true;

            string path = Path.Combine(vmapsDirectory, TreeFileName(MapId));

            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                _tree = VmapFile.ReadTree(File.ReadAllBytes(path));
            }
            catch (InvalidDataException)
            {
                _tree = null;
            }
        }
    }

    private WorldModel? LoadModel(string fileName)
    {
        // Shared across maps: the same tree model stands on several continents, and a WMO is
        // expensive enough that reading it once matters.
        return ModelCache.GetOrAdd(
            Path.Combine(vmapsDirectory, fileName),
            static path =>
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    return WorldModelFile.Read(File.ReadAllBytes(path));
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            });
    }

    private static readonly ConcurrentDictionary<string, WorldModel?> ModelCache = new();

    /// <summary>A model and where it stands. Upstream's <c>ModelInstance</c>.</summary>
    private sealed record ModelInstance(ModelSpawn Spawn, WorldModel Model);
}

/// <summary>Static collision for every map, created on first use.</summary>
public sealed class VmapManager(string dataDirectory)
{
    private readonly ConcurrentDictionary<uint, StaticMapTree> _maps = new();
    private readonly string _vmapsDirectory = Path.Combine(dataDirectory, "vmaps");

    /// <summary>Whether the vmaps directory exists at all.</summary>
    public bool IsAvailable => Directory.Exists(_vmapsDirectory);

    public string VmapsDirectory => _vmapsDirectory;

    public StaticMapTree GetMap(uint mapId) =>
        _maps.GetOrAdd(mapId, id => new StaticMapTree(id, _vmapsDirectory));

    /// <summary>How many <c>.vmtree</c> files are present, for the startup log.</summary>
    public int CountTreeFiles() =>
        IsAvailable ? Directory.EnumerateFiles(_vmapsDirectory, "*.vmtree").Count() : 0;
}
