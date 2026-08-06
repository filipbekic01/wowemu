using System.Numerics;

namespace WowEmu.Data.Client;

/// <summary>A ray: where it starts and which way it goes.</summary>
/// <remarks>
/// <see cref="Direction"/> is expected to be a unit vector. Distances along the ray are then in the
/// same units as the world, which is what lets a caller compare a hit against a maximum distance
/// without rescaling.
/// </remarks>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction);

/// <summary>
/// Ray intersection against VMAP geometry.
/// </summary>
/// <remarks>
/// Port of <c>IntersectTriangle</c>, <c>BIH::intersectRay</c>, <c>GroupModel::IntersectRay</c>,
/// <c>WorldModel::IntersectRay</c> and <c>ModelInstance::intersectRay</c> — the chain a line-of-sight
/// query walks from a world position down to a triangle.
/// <para>
/// <b>Coordinates.</b> VMAP geometry is not in world coordinates. Everything here works in the
/// internal representation; convert at the boundary with <see cref="ToInternal"/>.
/// </para>
/// </remarks>
public static class Collision
{
    /// <summary>Half the world, in yards. <c>0.5 * MAX_NUMBER_OF_GRIDS * SIZE_OF_GRIDS</c>.</summary>
    public const float WorldMid = 0.5f * MapGeometry.GridsPerAxis * MapGeometry.GridSize;

    /// <summary>
    /// Converts a world position into the coordinate system VMAP geometry lives in.
    /// </summary>
    /// <remarks>
    /// Port of <c>VMapMgr2::convertPositionToInternalRep</c>. X and Y are <i>mirrored about the
    /// world's midpoint</i> and Z is left alone — the same inversion the terrain grid uses, and for
    /// the same reason. Passing world coordinates straight in puts every query on the opposite side
    /// of the map, where it finds nothing and reports clear line of sight.
    /// </remarks>
    public static Vector3 ToInternal(float x, float y, float z) => new(WorldMid - x, WorldMid - y, z);

    /// <summary>Below this, a determinant is treated as degenerate. Upstream's <c>EPS</c>.</summary>
    public const float Epsilon = 1e-5f;

    /// <summary>
    /// Möller–Trumbore ray/triangle intersection.
    /// </summary>
    /// <param name="ray">The ray, in the same space as the vertices.</param>
    /// <param name="a">First vertex.</param>
    /// <param name="b">Second vertex.</param>
    /// <param name="c">Third vertex.</param>
    /// <param name="distance">
    /// In: the closest hit so far. Out: the new distance, if this triangle is closer.
    /// </param>
    /// <returns>True when this triangle is hit <i>and</i> is closer than <paramref name="distance"/>.</returns>
    /// <remarks>
    /// Port of <c>IntersectTriangle</c>. The distance is both an input and an output on purpose: the
    /// traversal keeps the nearest hit in one variable and every test narrows it, so a triangle
    /// behind a wall already found is rejected by the same comparison that finds the wall.
    /// </remarks>
    public static bool IntersectTriangle(
        Ray ray,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        ref float distance)
    {
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(ray.Direction, edge2);
        float determinant = Vector3.Dot(edge1, p);

        // Ill-conditioned: the ray is parallel to the triangle's plane.
        if (MathF.Abs(determinant) < Epsilon)
        {
            return false;
        }

        float inverse = 1.0f / determinant;
        Vector3 s = ray.Origin - a;
        float u = inverse * Vector3.Dot(s, p);

        if (u < 0.0f || u > 1.0f)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(s, edge1);
        float v = inverse * Vector3.Dot(ray.Direction, q);

        if (v < 0.0f || u + v > 1.0f)
        {
            return false;
        }

        float t = inverse * Vector3.Dot(edge2, q);

        if (t <= 0.0f || t >= distance)
        {
            return false;
        }

        distance = t;
        return true;
    }

    /// <summary>
    /// Whether a ray reaches <paramref name="maxDistance"/> without meeting a triangle.
    /// </summary>
    /// <remarks>
    /// The whole point of the exercise. Note the sense: this returns <b>true when the view is
    /// clear</b>, so it is the negation of "did we hit something".
    /// </remarks>
    public static bool IsInLineOfSight(
        WorldModelGroup group,
        Ray ray,
        float maxDistance)
    {
        float distance = maxDistance;
        return !IntersectGroup(group, ray, ref distance, stopAtFirstHit: true);
    }

    /// <summary>
    /// Intersects a ray with one group's mesh, using its BIH to skip triangles.
    /// </summary>
    /// <remarks>
    /// Port of <c>GroupModel::IntersectRay</c>. The tree is a filter, not an oracle: a leaf it hands
    /// back is a <i>candidate</i>, and every candidate is still tested against the real triangle.
    /// </remarks>
    public static bool IntersectGroup(
        WorldModelGroup group,
        Ray ray,
        ref float distance,
        bool stopAtFirstHit)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (group.Triangles.Length == 0)
        {
            return false;
        }

        bool hit = false;

        // Without a tree there is nothing to skip with, so every triangle is a candidate. That is
        // slow but correct, and it keeps a group whose BIH failed to read from silently vanishing.
        if (group.MeshTree is not { } tree)
        {
            for (int i = 0; i < group.Triangles.Length; i++)
            {
                if (IntersectTriangleAt(group, i, ray, ref distance))
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

        List<uint> candidates = [];
        CollectCandidates(tree, ray, distance, candidates);

        foreach (uint candidate in candidates)
        {
            if (candidate >= (uint)group.Triangles.Length)
            {
                continue;
            }

            if (IntersectTriangleAt(group, (int)candidate, ray, ref distance))
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

    /// <summary>
    /// Intersects a ray with a whole model, descending its group tree.
    /// </summary>
    /// <remarks>
    /// Port of <c>WorldModel::IntersectRay</c>, including its shortcut: a model with a single group
    /// skips the tree entirely, because a tree over one item can only say "yes".
    /// </remarks>
    public static bool IntersectModel(
        WorldModel model,
        Ray ray,
        ref float distance,
        bool stopAtFirstHit)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.Groups.Length == 0)
        {
            return false;
        }

        if (model.Groups.Length == 1)
        {
            return IntersectGroup(model.Groups[0], ray, ref distance, stopAtFirstHit);
        }

        bool hit = false;

        if (model.GroupTree is not { } tree)
        {
            foreach (WorldModelGroup group in model.Groups)
            {
                if (IntersectGroup(group, ray, ref distance, stopAtFirstHit))
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

        List<uint> candidates = [];
        CollectCandidates(tree, ray, distance, candidates);

        foreach (uint candidate in candidates)
        {
            if (candidate >= (uint)model.Groups.Length)
            {
                continue;
            }

            if (IntersectGroup(model.Groups[candidate], ray, ref distance, stopAtFirstHit))
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

    /// <summary>
    /// Intersects a ray with a model placed in the world.
    /// </summary>
    /// <remarks>
    /// Port of <c>ModelInstance::intersectRay</c>. The ray is transformed <i>into</i> the model's own
    /// space rather than the model being transformed into the world: one ray is cheaper to move than
    /// thousands of triangles, and the model's BIH is built in its own coordinates and would be
    /// useless otherwise.
    /// <para>
    /// The distance is scaled on the way in and back out again. Skipping either half gives hits at
    /// the wrong range for every model whose scale is not 1.
    /// </para>
    /// </remarks>
    public static bool IntersectInstance(
        ModelSpawn spawn,
        WorldModel model,
        Ray ray,
        ref float maxDistance,
        bool stopAtFirstHit)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (spawn.Scale <= 0f)
        {
            return false;
        }

        // The precomputed box is a cheap rejection: most models a ray passes near are not hit.
        if (spawn.HasBound && !IntersectsBox(ray, spawn, maxDistance))
        {
            return false;
        }

        Matrix4x4 inverseRotation = InverseRotation(spawn);
        float inverseScale = 1.0f / spawn.Scale;

        Vector3 position = new(spawn.PositionX, spawn.PositionY, spawn.PositionZ);
        Vector3 localOrigin = Vector3.Transform(ray.Origin - position, inverseRotation) * inverseScale;
        Vector3 localDirection = Vector3.Transform(ray.Direction, inverseRotation);

        float distance = maxDistance * inverseScale;

        if (!IntersectModel(model, new Ray(localOrigin, localDirection), ref distance, stopAtFirstHit))
        {
            return false;
        }

        maxDistance = distance * spawn.Scale;
        return true;
    }

    /// <summary>
    /// The inverse of a spawn's rotation.
    /// </summary>
    /// <remarks>
    /// Port of <c>ModelInstance</c>'s constructor, and its argument order is a genuine trap:
    /// <c>Matrix3::fromEulerAnglesZYX(rot.y, rot.x, rot.z)</c> — the stored <b>Y</b> rotation is
    /// passed as the Z angle and the stored <b>X</b> as the Y angle. Passing them in the order they
    /// are stored rotates buildings about the wrong axes, which looks plausible on a symmetrical
    /// model and badly wrong on everything else.
    /// <para>
    /// Angles are stored in degrees and the maths wants radians.
    /// </para>
    /// </remarks>
    public static Matrix4x4 InverseRotation(ModelSpawn spawn)
    {
        float z = float.DegreesToRadians(spawn.RotationY);
        float y = float.DegreesToRadians(spawn.RotationX);
        float x = float.DegreesToRadians(spawn.RotationZ);

        // ZYX order: the Z rotation is applied first and the X last.
        Matrix4x4 rotation =
            Matrix4x4.CreateRotationZ(z) *
            Matrix4x4.CreateRotationY(y) *
            Matrix4x4.CreateRotationX(x);

        // A rotation matrix is orthonormal, so its transpose is its inverse — exact, and without the
        // conditioning worries of a general inversion.
        return Matrix4x4.Transpose(rotation);
    }

    /// <summary>Whether a ray meets a spawn's precomputed bounding box within a distance.</summary>
    /// <remarks>
    /// A slab test. Rays parallel to an axis are handled by the infinities that division by zero
    /// produces, which compare correctly — the only case needing care is a direction component of
    /// exactly zero with an origin outside the slab, and that yields NaN rather than a false hit.
    /// </remarks>
    public static bool IntersectsBox(Ray ray, ModelSpawn spawn, float maxDistance)
    {
        float near = 0f;
        float far = maxDistance;

        if (!Slab(ray.Origin.X, ray.Direction.X, spawn.BoundsMinX, spawn.BoundsMaxX, ref near, ref far) ||
            !Slab(ray.Origin.Y, ray.Direction.Y, spawn.BoundsMinY, spawn.BoundsMaxY, ref near, ref far) ||
            !Slab(ray.Origin.Z, ray.Direction.Z, spawn.BoundsMinZ, spawn.BoundsMaxZ, ref near, ref far))
        {
            return false;
        }

        return near <= far;
    }

    private static bool Slab(float origin, float direction, float low, float high, ref float near, ref float far)
    {
        if (MathF.Abs(direction) < 1e-9f)
        {
            // Parallel to this slab: it can only miss if it starts outside.
            return origin >= low && origin <= high;
        }

        float inverse = 1.0f / direction;
        float t0 = (low - origin) * inverse;
        float t1 = (high - origin) * inverse;

        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        near = MathF.Max(near, t0);
        far = MathF.Min(far, t1);

        return near <= far;
    }

    private static bool IntersectTriangleAt(WorldModelGroup group, int index, Ray ray, ref float distance)
    {
        MeshTriangle triangle = group.Triangles[index];

        return IntersectTriangle(
            ray,
            VertexAt(group, triangle.Index0),
            VertexAt(group, triangle.Index1),
            VertexAt(group, triangle.Index2),
            ref distance);
    }

    private static Vector3 VertexAt(WorldModelGroup group, uint index) => new(
        group.Vertices[(index * 3) + 0],
        group.Vertices[(index * 3) + 1],
        group.Vertices[(index * 3) + 2]);

    /// <summary>
    /// Appends the primitives a ray might hit to <paramref name="into"/>.
    /// </summary>
    /// <remarks>
    /// Port of <c>BIH::intersectRay</c>. The tree is a filter, not an oracle: what comes back is a
    /// superset of what the ray really meets, and every candidate is still tested against real
    /// geometry.
    /// <para>
    /// The traversal narrows an interval <c>[min, max]</c> along the ray as it descends, and visits
    /// the near child before the far one so that the interval tightens as early as possible. Which
    /// child is "near" depends on the <b>sign of the ray's direction on the split axis</b> — that is
    /// what upstream's <c>offsetFront</c>/<c>offsetBack</c> tables encode, derived from the
    /// direction's sign bit. A traversal that always takes the lower child first still returns
    /// correct results but stops being a filter, because the interval never tightens.
    /// </para>
    /// <para>
    /// Fills a caller-supplied list rather than yielding: the traversal needs stack-allocated
    /// per-axis tables, and a <c>Span</c> cannot live across a <c>yield</c>. The list is also the
    /// better shape for reuse when this becomes hot — see TODO.md.
    /// </para>
    /// </remarks>
    public static void CollectCandidates(BihTree tree, Ray ray, float maxDistance, List<uint> into)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(into);

        if (tree.Nodes.Length == 0 || tree.Objects.Length == 0)
        {
            return;
        }

        if (!ClipToBounds(tree, ray, maxDistance, out float intervalMin, out float intervalMax))
        {
            return;
        }

        Vector3 origin = ray.Origin;
        Vector3 direction = ray.Direction;

        Span<float> org = [origin.X, origin.Y, origin.Z];
        Span<float> invDir = [1f / direction.X, 1f / direction.Y, 1f / direction.Z];
        Span<float> dir = [direction.X, direction.Y, direction.Z];

        // Which of the two split planes is nearer depends on the direction's sign, per axis.
        Span<int> front = stackalloc int[3];
        Span<int> back = stackalloc int[3];
        Span<int> front3 = stackalloc int[3];
        Span<int> back3 = stackalloc int[3];

        for (int i = 0; i < 3; i++)
        {
            int negative = BitConverter.SingleToUInt32Bits(dir[i]) >> 31 == 1 ? 1 : 0;

            front[i] = negative + 1;
            back[i] = (negative ^ 1) + 1;
            front3[i] = negative * BihTree.WordsPerNode;
            back3[i] = (negative ^ 1) * BihTree.WordsPerNode;
        }

        Stack<(int Node, float Near, float Far)> pending = new();
        int node = 0;

        while (true)
        {
            bool descend = true;

            while (descend)
            {
                if (node < 0 || node + 1 >= tree.Nodes.Length)
                {
                    break;
                }

                uint word = tree.Nodes[node];
                int axis = (int)BihTree.NodeAxis(word);
                bool bvh2 = BihTree.NodeIsBvh2(word);
                int offset = (int)BihTree.NodeOffset(word);

                if (!bvh2)
                {
                    if (axis < 3)
                    {
                        if (node + Math.Max(front[axis], back[axis]) >= tree.Nodes.Length)
                        {
                            break;
                        }

                        float tf = (AsFloat(tree.Nodes[node + front[axis]]) - org[axis]) * invDir[axis];
                        float tb = (AsFloat(tree.Nodes[node + back[axis]]) - org[axis]) * invDir[axis];

                        // The ray threads between the two half-spaces and misses both children.
                        if (tf < intervalMin && tb > intervalMax)
                        {
                            break;
                        }

                        int farChild = offset + back3[axis];
                        node = farChild;

                        if (tf < intervalMin)
                        {
                            intervalMin = tb >= intervalMin ? tb : intervalMin;
                            continue;
                        }

                        node = offset + front3[axis];

                        if (tb > intervalMax)
                        {
                            intervalMax = tf <= intervalMax ? tf : intervalMax;
                            continue;
                        }

                        pending.Push((farChild, tb >= intervalMin ? tb : intervalMin, intervalMax));
                        intervalMax = tf <= intervalMax ? tf : intervalMax;
                        continue;
                    }

                    // A leaf: a run of the object array.
                    uint count = tree.Nodes[node + 1];

                    for (uint i = 0; i < count; i++)
                    {
                        uint slot = (uint)offset + i;

                        if (slot < (uint)tree.Objects.Length)
                        {
                            into.Add(tree.Objects[slot]);
                        }
                    }

                    break;
                }

                // A single-child node: both planes clip the same subtree.
                if (axis > 2 || node + Math.Max(front[axis], back[axis]) >= tree.Nodes.Length)
                {
                    return;
                }

                float nearPlane = (AsFloat(tree.Nodes[node + front[axis]]) - org[axis]) * invDir[axis];
                float farPlane = (AsFloat(tree.Nodes[node + back[axis]]) - org[axis]) * invDir[axis];

                node = offset;
                intervalMin = nearPlane >= intervalMin ? nearPlane : intervalMin;
                intervalMax = farPlane <= intervalMax ? farPlane : intervalMax;

                if (intervalMin > intervalMax)
                {
                    break;
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            (int resumedNode, float near, float far) = pending.Pop();

            if (maxDistance < near)
            {
                continue;
            }

            node = resumedNode;
            intervalMin = near;
            intervalMax = far;
        }
    }

    /// <summary>
    /// Appends the primitives whose subtree contains a point to <paramref name="into"/>.
    /// </summary>
    /// <remarks>
    /// Port of <c>BIH::intersectPoint</c>. The same tree as the ray walk and the same node layout,
    /// but there is no interval to narrow and no near/far ordering to get right — a point is simply
    /// on one side of each split plane, or between them, in which case it is in neither child and
    /// the descent stops.
    /// <para>
    /// Like the ray walk, this is a filter: a returned candidate is a primitive whose <i>subtree</i>
    /// the point falls in, not one the point is inside. Callers still test the real geometry.
    /// </para>
    /// </remarks>
    public static void CollectCandidatesAtPoint(BihTree tree, Vector3 point, List<uint> into)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(into);

        if (tree.Nodes.Length == 0 || tree.Objects.Length == 0)
        {
            return;
        }

        if (point.X < tree.BoundsMinX || point.X > tree.BoundsMaxX ||
            point.Y < tree.BoundsMinY || point.Y > tree.BoundsMaxY ||
            point.Z < tree.BoundsMinZ || point.Z > tree.BoundsMaxZ)
        {
            return;
        }

        Span<float> at = [point.X, point.Y, point.Z];

        Stack<int> pending = new();
        int node = 0;

        while (true)
        {
            bool descend = true;

            while (descend)
            {
                if (node < 0 || node + 2 >= tree.Nodes.Length)
                {
                    break;
                }

                uint word = tree.Nodes[node];
                int axis = (int)BihTree.NodeAxis(word);
                bool bvh2 = BihTree.NodeIsBvh2(word);
                int offset = (int)BihTree.NodeOffset(word);

                if (!bvh2)
                {
                    if (axis < 3)
                    {
                        float left = AsFloat(tree.Nodes[node + 1]);
                        float right = AsFloat(tree.Nodes[node + 2]);

                        // Between the two half-spaces: the point is in the empty gap the split
                        // carved out, so neither child can contain it.
                        if (left < at[axis] && right > at[axis])
                        {
                            break;
                        }

                        int rightChild = offset + BihTree.WordsPerNode;
                        node = rightChild;

                        if (left < at[axis])
                        {
                            continue;
                        }

                        node = offset;

                        if (right > at[axis])
                        {
                            continue;
                        }

                        // In both — the halves overlap here, so the far one is deferred.
                        pending.Push(rightChild);
                        continue;
                    }

                    uint count = tree.Nodes[node + 1];

                    for (uint i = 0; i < count; i++)
                    {
                        uint slot = (uint)offset + i;

                        if (slot < (uint)tree.Objects.Length)
                        {
                            into.Add(tree.Objects[slot]);
                        }
                    }

                    break;
                }

                if (axis > 2)
                {
                    return;
                }

                float low = AsFloat(tree.Nodes[node + 1]);
                float high = AsFloat(tree.Nodes[node + 2]);

                node = offset;

                if (low > at[axis] || high < at[axis])
                {
                    break;
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            node = pending.Pop();
        }
    }

    /// <summary>
    /// Clips the ray to the tree's bounding box, giving the interval the walk starts from.
    /// </summary>
    /// <remarks>
    /// An axis the ray is parallel to contributes no constraint — upstream skips it rather than
    /// dividing by zero, and so does this. A ray parallel to an axis and outside the box on it is
    /// caught by the other two.
    /// </remarks>
    private static bool ClipToBounds(
        BihTree tree,
        Ray ray,
        float maxDistance,
        out float intervalMin,
        out float intervalMax)
    {
        intervalMin = 0f;
        intervalMax = maxDistance;

        Span<float> org = [ray.Origin.X, ray.Origin.Y, ray.Origin.Z];
        Span<float> dir = [ray.Direction.X, ray.Direction.Y, ray.Direction.Z];
        Span<float> low = [tree.BoundsMinX, tree.BoundsMinY, tree.BoundsMinZ];
        Span<float> high = [tree.BoundsMaxX, tree.BoundsMaxY, tree.BoundsMaxZ];

        for (int i = 0; i < 3; i++)
        {
            if (MathF.Abs(dir[i]) < 1e-9f)
            {
                continue;
            }

            float inverse = 1f / dir[i];
            float t1 = (low[i] - org[i]) * inverse;
            float t2 = (high[i] - org[i]) * inverse;

            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            if (t1 > intervalMin)
            {
                intervalMin = t1;
            }

            if (t2 < intervalMax)
            {
                intervalMax = t2;
            }

            if (intervalMax <= 0f || intervalMin >= maxDistance)
            {
                return false;
            }
        }

        if (intervalMin > intervalMax)
        {
            return false;
        }

        intervalMin = MathF.Max(intervalMin, 0f);
        intervalMax = MathF.Min(intervalMax, maxDistance);

        return true;
    }

    /// <summary>Reinterprets a tree word as the float split plane it is.</summary>
    private static float AsFloat(uint word) => BitConverter.UInt32BitsToSingle(word);
}
