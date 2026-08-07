using System.Collections.Concurrent;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using WowEmu.Core;

namespace WowEmu.Data.Client;

/// <summary>
/// Which polygons a path may cross. <c>NavTerrainFlag</c>.
/// </summary>
/// <remarks>
/// The same four the generator bakes into each polygon's area. Ground alone is what a walking
/// creature is allowed; water is added for anything that swims, and lava and slime are things to be
/// routed around rather than through.
/// </remarks>
[Flags]
public enum NavArea : ushort
{
    Empty = 0x00,
    Ground = 0x01,
    Magma = 0x02,
    Slime = 0x04,
    Water = 0x08,

    /// <summary>Everything a creature that both walks and swims may cross.</summary>
    GroundAndWater = Ground | Water,
}

/// <summary>How a path attempt turned out.</summary>
public enum PathResult
{
    /// <summary>No navmesh for this map, or none loaded where the points are.</summary>
    NoNavMesh,

    /// <summary>Neither end sits on a polygon — off the mesh, or inside geometry.</summary>
    NoPolygon,

    /// <summary>A complete route from start to end.</summary>
    Complete,

    /// <summary>
    /// A route that stops short of the destination.
    /// </summary>
    /// <remarks>
    /// Detour returns one whenever the destination is unreachable — across a chasm, behind a closed
    /// door — and it is a useful answer rather than a failure: a creature should walk as far as it
    /// can and then give up, which is what chasing something onto a rooftop looks like.
    /// </remarks>
    Partial,
}

/// <summary>The corners of a route through the world, in world coordinates.</summary>
/// <param name="Result">Whether it reached the destination.</param>
/// <param name="Points">
/// The corners to walk, starting at the path's own start. Empty unless <see cref="Result"/> is
/// <see cref="PathResult.Complete"/> or <see cref="PathResult.Partial"/>.
/// </param>
public readonly record struct NavPath(PathResult Result, IReadOnlyList<Position> Points)
{
    /// <summary>Whether there is anything to walk.</summary>
    public bool HasPath => Points.Count > 0;

    /// <summary>Where the route ends, which is not the destination for a partial path.</summary>
    public Position Destination => Points.Count > 0 ? Points[^1] : default;

    /// <summary>A path that found nothing, and why.</summary>
    public static NavPath None(PathResult result) => new(result, []);
}

/// <summary>
/// Finds routes across a map's navigation mesh.
/// </summary>
/// <remarks>
/// Port of the reachable part of <c>PathGenerator</c>.
/// <para>
/// <b>Detour's axes are not the game's.</b> A world position goes in as <c>(y, z, x)</c> — Detour's
/// second axis is up where the game's third is, and the other two swap. They are <i>not</i> negated,
/// which the terrain tiles' origin-centred inversion tempts you into; a negated point lands outside
/// the mesh and comes back as "no polygon here" rather than as an error.
/// </para>
/// <para>
/// <b>Corners, not a resampled line.</b> Upstream walks the corridor in four-yard steps
/// (<c>findSmoothPath</c>); this asks Detour for the corridor's corners instead
/// (<c>findStraightPath</c>, the funnel algorithm). The route is the same one — the corners are
/// where it actually turns — and a creature that walks corner to corner covers it exactly. The
/// resampling matters for following terrain height along a leg, which is a separate problem and is
/// noted as a gap rather than approximated here.
/// </para>
/// </remarks>
public sealed class PathGenerator
{
    /// <summary>Most polygons a route may cross. <c>MAX_PATH_LENGTH</c>.</summary>
    public const int MaxPathPolygons = 74;

    /// <summary>Most corners a route may have. <c>MAX_POINT_PATH_LENGTH</c>.</summary>
    public const int MaxPathPoints = 74;

    /// <summary>How far apart the points of a resampled leg are. <c>SMOOTH_PATH_STEP_SIZE</c>.</summary>
    public const float StepSize = 4.0f;

    /// <summary>
    /// How far above the polygon a resampled point sits.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>result[1] += 0.5f</c>. The mesh is built a little below where a unit's feet
    /// actually rest, and without the lift every sampled point is fractionally underground — which
    /// the client draws as a creature wading through the floor.
    /// </remarks>
    public const float HeightLift = 0.5f;

    /// <summary>Polygons one surface step may cross. <c>MAX_VISIT_POLY</c>.</summary>
    private const int MaxVisitedPerStep = 16;

    /// <summary>
    /// How far from a point to look for a polygon, in Detour's axes.
    /// </summary>
    /// <remarks>
    /// Upstream's search box. Generous vertically because a unit's reported position sits at its
    /// feet and the mesh is built to its centre, and tight horizontally so a point inside a wall
    /// does not snap to the corridor on the other side of it.
    /// </remarks>
    public static readonly RcVec3f SearchExtents = new(3.0f, 5.0f, 3.0f);

    private readonly DtNavMesh _mesh;
    private readonly DtNavMeshQuery _query;
    private readonly IDtQueryFilter _filter;

    public PathGenerator(DtNavMesh mesh, NavArea allowed = NavArea.Ground)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        _mesh = mesh;
        _query = new DtNavMeshQuery(mesh);
        _filter = new SlopeAwareFilter(allowed);
    }

    /// <summary>The mesh this generator paths over.</summary>
    public DtNavMesh Mesh => _mesh;

    /// <summary>
    /// Finds a route between two world positions.
    /// </summary>
    /// <remarks>
    /// Both ends are snapped to the nearest polygon first. A point off the mesh — in the air, inside
    /// a rock, on a map with no navmesh — has no polygon, and that is reported rather than guessed
    /// around: the caller then walks a straight line, which is what it did before there was a mesh
    /// at all.
    /// </remarks>
    public NavPath Find(Position start, Position end)
    {
        RcVec3f from = ToDetour(start);
        RcVec3f to = ToDetour(end);

        if (!_query.FindNearestPoly(from, SearchExtents, _filter, out long startRef, out RcVec3f startPoint, out _)
                .Succeeded()
            || startRef == 0)
        {
            return NavPath.None(PathResult.NoPolygon);
        }

        if (!_query.FindNearestPoly(to, SearchExtents, _filter, out long endRef, out RcVec3f endPoint, out _)
                .Succeeded()
            || endRef == 0)
        {
            return NavPath.None(PathResult.NoPolygon);
        }

        Span<long> polys = stackalloc long[MaxPathPolygons];

        if (!_query.FindPath(startRef, endRef, startPoint, endPoint, _filter, polys, out int polyCount,
                MaxPathPolygons).Succeeded()
            || polyCount == 0)
        {
            return NavPath.None(PathResult.NoPolygon);
        }

        // The corridor ending somewhere other than the destination's polygon is Detour saying the
        // destination could not be reached. The route it did find is still worth walking.
        bool complete = polys[polyCount - 1] == endRef;

        // A partial route ends wherever the corridor does, not at the unreachable destination —
        // string-pulling towards a point outside the last polygon walks a creature into a wall.
        RcVec3f target = complete
            ? endPoint
            : _query.ClosestPointOnPoly(polys[polyCount - 1], endPoint, out RcVec3f closest, out _).Succeeded()
                ? closest
                : endPoint;

        Span<DtStraightPath> corners = new DtStraightPath[MaxPathPoints];

        if (!_query.FindStraightPath(startPoint, target, polys, polyCount, corners, out int cornerCount,
                MaxPathPoints, 0).Succeeded()
            || cornerCount == 0)
        {
            return NavPath.None(PathResult.NoPolygon);
        }

        List<Position> points = [FromDetour(corners[0].pos)];
        long currentPoly = startRef;

        for (int i = 1; i < cornerCount && points.Count < MaxPathPoints; i++)
        {
            Resample(ref currentPoly, corners[i - 1].pos, corners[i].pos, points);
        }

        return new NavPath(complete ? PathResult.Complete : PathResult.Partial, points);
    }

    /// <summary>
    /// Walks one leg along the surface, adding a point every <see cref="StepSize"/> yards.
    /// </summary>
    /// <remarks>
    /// <c>MoveAlongSurface</c> rather than a plain interpolation: it slides along the mesh and stops
    /// at its edge, so a step that would leave the corridor is clamped to it rather than taken. The
    /// polygon it ends on is carried into the next step, which is what makes the height lookup cheap
    /// — no spatial query per sample.
    /// <para>
    /// The leg's own end corner is always added, whatever the sampling did. A leg shorter than one
    /// step produces no intermediate points at all, and dropping its end would lose the corner.
    /// </para>
    /// </remarks>
    private void Resample(ref long currentPoly, RcVec3f from, RcVec3f to, List<Position> into)
    {
        float length = RcVec3f.Distance(from, to);
        int steps = (int)(length / StepSize);

        RcVec3f position = from;
        Span<long> visited = stackalloc long[MaxVisitedPerStep];

        for (int step = 1; step <= steps && into.Count < MaxPathPoints - 1; step++)
        {
            RcVec3f target = RcVec3f.Lerp(from, to, step * StepSize / length);

            if (!_query.MoveAlongSurface(currentPoly, position, target, _filter, out RcVec3f moved,
                    visited, out int visitedCount, MaxVisitedPerStep).Succeeded())
            {
                break;
            }

            if (visitedCount > 0)
            {
                currentPoly = visited[visitedCount - 1];
            }

            if (_query.GetPolyHeight(currentPoly, moved, out float height).Succeeded())
            {
                moved.Y = height + HeightLift;
            }

            position = moved;
            into.Add(FromDetour(moved));
        }

        into.Add(FromDetour(to));
    }

    /// <summary>A world position in Detour's axes: <c>(y, z, x)</c>.</summary>
    public static RcVec3f ToDetour(Position position) => new(position.Y, position.Z, position.X);

    /// <summary>And back again.</summary>
    public static Position FromDetour(RcVec3f vector) => new(vector.Z, vector.X, vector.Y, 0f);

    /// <summary>
    /// Detour's default cost, plus a penalty for climbing.
    /// </summary>
    /// <remarks>
    /// Port of <c>dtQueryFilterExt::getCost</c>. Distance times <c>1 + slopeDegrees/100</c> times
    /// the area's own cost — so a route up a hillside is dearer than the same distance across a
    /// field, and a creature prefers to go round rather than straight over. Without it every path is
    /// the shortest line regardless of what it climbs.
    /// </remarks>
    private sealed class SlopeAwareFilter(NavArea allowed) : IDtQueryFilter
    {
        public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly) =>
            (poly.flags & (ushort)allowed) != 0;

        public float GetCost(
            RcVec3f pa,
            RcVec3f pb,
            long prevRef, DtMeshTile prevTile, DtPoly prevPoly,
            long curRef, DtMeshTile curTile, DtPoly curPoly,
            long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
        {
            float distance = RcVec3f.Distance(pa, pb);

            // Detour's axes again: the height difference is the second component, and the two that
            // make up the floor distance are the first and third.
            float floorDistance = MathF.Sqrt(
                ((pa.X - pb.X) * (pa.X - pb.X)) + ((pa.Z - pb.Z) * (pa.Z - pb.Z)));

            if (floorDistance <= 0f)
            {
                return distance;
            }

            float slopeDegrees =
                MathF.Atan(MathF.Abs(pb.Y - pa.Y) / floorDistance) * 180f / MathF.PI;

            float cost = slopeDegrees > 0f ? 1.0f + (slopeDegrees / 100f) : 1.0f;

            return distance * cost;
        }
    }
}

/// <summary>
/// Navigation meshes for every map, loaded tile by tile as they are asked for.
/// </summary>
/// <remarks>
/// The same shape as <see cref="TerrainManager"/> and <see cref="VmapManager"/>, and for the same
/// reason: a continent is 515 tiles and a query touches one part of it.
/// <para>
/// A map with no <c>.mmap</c> file has no mesh and no generator. That is not an error — 98 maps have
/// one out of the client's several hundred — and a caller must fall back to walking in a straight
/// line rather than refusing to move.
/// </para>
/// </remarks>
public sealed class NavMeshManager(string dataDirectory)
{
    private readonly ConcurrentDictionary<uint, PathGenerator?> _maps = new();
    private readonly ConcurrentDictionary<(uint Map, int GridX, int GridY), bool> _loadedTiles = new();
    private readonly string _directory = Path.Combine(dataDirectory, "mmaps");
    private readonly Lock _loadLock = new();

    /// <summary>Whether the mmaps directory exists at all.</summary>
    public bool IsAvailable => Directory.Exists(_directory);

    /// <summary>How many <c>.mmap</c> files are present, for the startup log.</summary>
    public int CountMapFiles() =>
        IsAvailable ? Directory.EnumerateFiles(_directory, "*.mmap").Count() : 0;

    /// <summary>The generator for a map, or null when it has no navmesh.</summary>
    public PathGenerator? For(uint mapId) => _maps.GetOrAdd(mapId, Load);

    /// <summary>
    /// Makes sure the tiles around a point are in the mesh.
    /// </summary>
    /// <remarks>
    /// Called before a path is asked for. A tile that is not loaded is not a hole in the world so
    /// much as an absence: Detour finds no polygon there and the path is refused, which looks
    /// exactly like being off the mesh.
    /// </remarks>
    public void EnsureLoaded(uint mapId, float x, float y)
    {
        if (For(mapId) is not { } generator)
        {
            return;
        }

        (int gridX, int gridY) = MapGeometry.GridFor(x, y);

        // A path can leave the tile it starts in, so the neighbours come too.
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                LoadTile(generator, mapId, gridX + dx, gridY + dy);
            }
        }
    }

    private void LoadTile(PathGenerator generator, uint mapId, int gridX, int gridY)
    {
        if ((uint)gridX >= MapGeometry.GridsPerAxis || (uint)gridY >= MapGeometry.GridsPerAxis
            || !_loadedTiles.TryAdd((mapId, gridX, gridY), true))
        {
            return;
        }

        // Named exactly as a terrain tile is — {map}{gridX}{gridY} — and NOT swapped the way a
        // .vmtile is. Three tile formats, two conventions: the vmap one is the odd one out, and
        // assuming they all agree with it finds a tile from elsewhere on the continent.
        string path = Path.Combine(_directory, $"{mapId:D3}{gridX:D2}{gridY:D2}.mmtile");

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            DetourTile tile = NavMeshFile.ReadTile(File.ReadAllBytes(path));

            lock (_loadLock)
            {
                generator.Mesh.AddTile(NavMesh.ToMeshData(tile), 0, 0, out _);
            }
        }
        catch (InvalidDataException)
        {
            // A corrupt tile costs its own corner of the map, not the whole mesh.
        }
    }

    private PathGenerator? Load(uint mapId)
    {
        string path = Path.Combine(_directory, $"{mapId:D3}.mmap");

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return new PathGenerator(NavMesh.Create(NavMeshFile.ReadParams(File.ReadAllBytes(path))));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
