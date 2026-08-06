using System.Numerics;

namespace WowEmu.Data.Client;

/// <summary>
/// Where a point sits relative to one group of a model. <c>GroupModel::InsideResult</c>.
/// </summary>
/// <remarks>
/// The three non-negative values are array indices upstream, and the distinction between them is
/// what lets a room with no floor still be identified. <see cref="MaybeInside"/> means the point is
/// within the group's mesh bounds but no downward ray met a triangle — true of a room whose floor
/// belongs to the group below it.
/// </remarks>
public enum GroupInside
{
    OutOfBounds = -1,
    Inside = 0,
    MaybeInside = 1,
    Above = 2,
}

/// <summary>The group a point was found in, and the surface under it.</summary>
/// <param name="Group">The group itself — its liquid and its flags hang off this.</param>
/// <param name="RootWmoId">The model's root WMO id.</param>
/// <param name="GroundZ">The world height of the floor found beneath the point.</param>
public readonly record struct ModelLocation(WorldModelGroup Group, uint RootWmoId, float GroundZ);

/// <summary>
/// Finds which part of a model a point is inside, and what liquid is there.
/// </summary>
/// <remarks>
/// Port of the <c>GetLocationInfo</c> chain — <c>StaticMapTree</c> down through
/// <c>ModelInstance</c> and <c>WorldModel</c> to <c>GroupModel</c> — plus
/// <c>WmoLiquid::GetLiquidHeight</c>. This is what makes indoor water exist: terrain liquid covers
/// lakes and oceans, and every fountain, canal and flooded dungeon room is a WMO's own liquid grid
/// instead.
/// <para>
/// Separate from <see cref="Collision"/> because the question is different. Collision asks what a
/// ray hits; this asks which enclosed volume a point is in, which needs the group tree walked as a
/// containment test rather than as a visibility one.
/// </para>
/// </remarks>
public static class ModelLocator
{
    /// <summary>One liquid tile of a WMO's grid, in yards. <c>LIQUID_TILE_SIZE</c>.</summary>
    public const float LiquidTileSize = 533.333f / 128f;

    /// <summary>
    /// A tile flagged as carrying no liquid.
    /// </summary>
    /// <remarks>
    /// Upstream's comment is worth keeping: checking for <c>0x08</c> alone might be enough, but
    /// disabled tiles are always <c>0x?F</c>, so the low nibble is compared whole.
    /// </remarks>
    private const byte LiquidTileDisabled = 0x0F;

    /// <summary>
    /// Where a point sits relative to one group, by casting a ray down through it.
    /// </summary>
    /// <remarks>
    /// Port of <c>GroupModel::IsInsideObject</c>. The bound test is deliberately not a containment
    /// test — it omits the upper Z bound, so a point above the group still qualifies, which is what
    /// <see cref="GroupInside.Above"/> exists to report.
    /// </remarks>
    public static GroupInside IsInside(WorldModelGroup group, Ray ray, out float zDistance)
    {
        ArgumentNullException.ThrowIfNull(group);

        zDistance = 0f;

        if (group.Triangles.Length == 0 || !IsInsideOrAboveBound(group, ray.Origin))
        {
            return GroupInside.OutOfBounds;
        }

        (float meshMinZ, float meshMaxZ) = MeshBoundsZ(group);

        if (meshMaxZ >= ray.Origin.Z)
        {
            float distance = float.PositiveInfinity;

            if (Collision.IntersectGroup(group, ray, ref distance, stopAtFirstHit: false))
            {
                // The 0.1 is upstream's, and pairs with the 0.1 the ray origin was raised by:
                // together they put the reported floor back at the triangle it was found on.
                zDistance = distance - 0.1f;
                return GroupInside.Inside;
            }

            if (ContainsPoint(group, ray.Origin, meshMinZ, meshMaxZ))
            {
                return GroupInside.MaybeInside;
            }
        }
        else
        {
            // The point is above everything in this group, so a ray from here would start past the
            // geometry. Upstream bumps the ray down to the top of the mesh and adds the bump back
            // on, which is how a group with no floor of its own finds the one belonging to the
            // group below it.
            float delta = ray.Origin.Z - meshMaxZ;
            float distance = float.PositiveInfinity;

            Ray bumped = new(ray.Origin + (ray.Direction * delta), ray.Direction);

            if (Collision.IntersectGroup(group, bumped, ref distance, stopAtFirstHit: false))
            {
                zDistance = distance - 0.1f + delta;
                return GroupInside.Above;
            }
        }

        return GroupInside.OutOfBounds;
    }

    /// <summary>
    /// Finds the group of a model that contains a point, in the model's own coordinates.
    /// </summary>
    /// <remarks>
    /// Port of <c>WorldModel::GetLocationInfo</c>. An <see cref="GroupInside.Inside"/> hit wins
    /// outright; failing that, a <see cref="GroupInside.MaybeInside"/> group is accepted only when
    /// some other group was found <see cref="GroupInside.Above"/> — that pair is the signature of a
    /// floorless room sitting on top of one with a floor, and either alone is not enough to
    /// conclude the point is indoors.
    /// <para>
    /// The candidate list is a superset of what upstream's callback visits, because its BIH walk
    /// tightens the search distance as it goes and ours collects first. The running-minimum test
    /// below reproduces the same choice, and the extra candidates are rejected by it.
    /// </para>
    /// </remarks>
    public static WorldModelGroup? Locate(WorldModel model, Vector3 point, Vector3 down, out float zDistance)
    {
        ArgumentNullException.ThrowIfNull(model);

        zDistance = 0f;

        if (model.Groups.Length == 0)
        {
            return null;
        }

        // Raised slightly so that a point resting exactly on a floor is above it rather than in it.
        Ray ray = new(point - (down * 0.1f), down);

        float distance = GroupTreeExtent(model);

        WorldModelGroup? inside = null;
        WorldModelGroup? maybeInside = null;
        WorldModelGroup? above = null;

        foreach (WorldModelGroup group in CandidateGroups(model, ray, distance))
        {
            GroupInside result = IsInside(group, ray, out float groupZ);

            switch (result)
            {
                case GroupInside.MaybeInside:
                    maybeInside = group;
                    break;

                case GroupInside.Inside or GroupInside.Above when groupZ < distance:
                    distance = groupZ;

                    if (result == GroupInside.Inside)
                    {
                        inside = group;
                    }
                    else
                    {
                        above = group;
                    }

                    break;

                default:
                    break;
            }
        }

        if (inside is not null)
        {
            zDistance = distance;
            return inside;
        }

        if (maybeInside is not null && above is not null)
        {
            zDistance = distance;
            return maybeInside;
        }

        return null;
    }

    /// <summary>
    /// Finds the group of a placed model that contains a world point.
    /// </summary>
    /// <remarks>
    /// Port of <c>ModelInstance::GetLocationInfo</c>. The point is moved into the model's own space
    /// rather than the model into the world, exactly as the ray tests do, and the floor that comes
    /// back is moved out again.
    /// </remarks>
    public static bool TryLocate(
        ModelSpawn spawn,
        WorldModel model,
        Vector3 worldPoint,
        out ModelLocation location)
    {
        ArgumentNullException.ThrowIfNull(model);

        location = default;

        if (spawn.Scale <= 0f)
        {
            return false;
        }

        // An M2 is a doodad — a tree, a barrel. Upstream refuses them here because they carry no
        // area or liquid information, only collision geometry.
        if (spawn.IsM2)
        {
            return false;
        }

        if (spawn.HasBound && !ContainsBound(spawn, worldPoint))
        {
            return false;
        }

        Vector3 localPoint = ToModel(spawn, worldPoint);
        Vector3 localDown = Vector3.Transform(new Vector3(0f, 0f, -1f), Collision.InverseRotation(spawn));

        if (Locate(model, localPoint, localDown, out float zDistance) is not { } group)
        {
            return false;
        }

        Vector3 localGround = localPoint + (zDistance * localDown);

        location = new ModelLocation(group, model.RootWmoId, ToWorld(spawn, localGround).Z);
        return true;
    }

    /// <summary>
    /// The world height of a group's liquid surface under a world point.
    /// </summary>
    /// <remarks>
    /// Port of <c>ModelInstance::GetLiquidLevel</c> over <c>WmoLiquid::GetLiquidHeight</c>. Note
    /// what goes back through the transform: the point's own x and y with the liquid's height
    /// substituted for z, not the point itself — the surface is being sampled, not the position.
    /// </remarks>
    public static bool TryGetLiquidLevel(
        ModelSpawn spawn,
        WorldModelGroup group,
        Vector3 worldPoint,
        out float level)
    {
        ArgumentNullException.ThrowIfNull(group);

        level = 0f;

        if (group.Liquid is not { } liquid || spawn.Scale <= 0f)
        {
            return false;
        }

        Vector3 localPoint = ToModel(spawn, worldPoint);

        if (!TryGetLiquidHeight(liquid, localPoint, out float localHeight))
        {
            return false;
        }

        level = ToWorld(spawn, new Vector3(localPoint.X, localPoint.Y, localHeight)).Z;
        return true;
    }

    /// <summary>
    /// The height of a model's liquid surface at a point, in the model's own coordinates.
    /// </summary>
    /// <remarks>
    /// Port of <c>WmoLiquid::GetLiquidHeight</c>. The grid is one larger than the tile count in each
    /// axis because heights sit at tile corners, and each tile is split into two triangles whose
    /// shared edge runs from its origin corner — which is why the interpolation picks a case on
    /// <c>dx &gt; dy</c> and reads a different pair of neighbours in each.
    /// <para>
    /// A liquid with no flags array is a single height covering the whole thing, and answers
    /// everywhere without any of this.
    /// </para>
    /// </remarks>
    public static bool TryGetLiquidHeight(WmoLiquid liquid, Vector3 point, out float height)
    {
        ArgumentNullException.ThrowIfNull(liquid);

        height = 0f;

        if (liquid.Flags.Length == 0)
        {
            if (liquid.Heights.Length == 0)
            {
                return false;
            }

            height = liquid.Heights[0];
            return true;
        }

        float tileXf = (point.X - liquid.CornerX) / LiquidTileSize;
        float tileYf = (point.Y - liquid.CornerY) / LiquidTileSize;

        if (tileXf < 0f || tileYf < 0f)
        {
            return false;
        }

        uint tileX = (uint)tileXf;
        uint tileY = (uint)tileYf;

        if (tileX >= liquid.TilesX || tileY >= liquid.TilesY)
        {
            return false;
        }

        if ((liquid.Flags[(int)(tileX + (tileY * liquid.TilesX))] & 0x0F) == LiquidTileDisabled)
        {
            return false;
        }

        float dx = tileXf - tileX;
        float dy = tileYf - tileY;

        uint rowOffset = liquid.TilesX + 1;
        uint origin = tileX + (tileY * rowOffset);

        if (origin + rowOffset + 1 >= (uint)liquid.Heights.Length)
        {
            return false;
        }

        float slopeX;
        float slopeY;

        if (dx > dy)
        {
            slopeX = liquid.Heights[origin + 1] - liquid.Heights[origin];
            slopeY = liquid.Heights[origin + 1 + rowOffset] - liquid.Heights[origin + 1];
        }
        else
        {
            slopeX = liquid.Heights[origin + 1 + rowOffset] - liquid.Heights[origin + rowOffset];
            slopeY = liquid.Heights[origin + rowOffset] - liquid.Heights[origin];
        }

        height = liquid.Heights[origin] + (dx * slopeX) + (dy * slopeY);
        return true;
    }

    /// <summary>Moves a world point into a model's own coordinates.</summary>
    /// <remarks>
    /// The same expression <c>Collision.IntersectInstance</c> uses for a ray's origin, and it has to
    /// stay the same: the two are asking about the same geometry and would otherwise disagree about
    /// where it is.
    /// </remarks>
    public static Vector3 ToModel(ModelSpawn spawn, Vector3 worldPoint)
    {
        Vector3 position = new(spawn.PositionX, spawn.PositionY, spawn.PositionZ);

        return Vector3.Transform(worldPoint - position, Collision.InverseRotation(spawn)) / spawn.Scale;
    }

    /// <summary>Moves a point in a model's own coordinates back into the world.</summary>
    /// <remarks>
    /// The exact inverse of <see cref="ToModel"/>: the rotation is the transpose of the inverse
    /// rotation, which for an orthonormal matrix is the rotation itself.
    /// </remarks>
    public static Vector3 ToWorld(ModelSpawn spawn, Vector3 modelPoint)
    {
        Vector3 position = new(spawn.PositionX, spawn.PositionY, spawn.PositionZ);
        Matrix4x4 rotation = Matrix4x4.Transpose(Collision.InverseRotation(spawn));

        return (Vector3.Transform(modelPoint, rotation) * spawn.Scale) + position;
    }

    /// <summary>The groups whose subtree a downward ray passes through.</summary>
    private static IEnumerable<WorldModelGroup> CandidateGroups(WorldModel model, Ray ray, float distance)
    {
        if (model.Groups.Length == 1 || model.GroupTree is not { } tree)
        {
            return model.Groups;
        }

        List<uint> candidates = [];
        Collision.CollectCandidates(tree, ray, distance, candidates);

        return candidates
            .Where(candidate => candidate < (uint)model.Groups.Length)
            .Select(candidate => model.Groups[candidate]);
    }

    /// <summary>
    /// How far the walk down the group tree may reach.
    /// </summary>
    /// <remarks>
    /// Upstream uses the diagonal of the group tree's bounding box. Without a tree there is no such
    /// box, so the search is unbounded — which is correct rather than lazy: the ray is only used to
    /// find a floor beneath the point, and a model small enough to have no tree cannot produce a
    /// distant false hit.
    /// </remarks>
    private static float GroupTreeExtent(WorldModel model)
    {
        if (model.GroupTree is not { } tree)
        {
            return float.PositiveInfinity;
        }

        Vector3 extent = new(
            tree.BoundsMaxX - tree.BoundsMinX,
            tree.BoundsMaxY - tree.BoundsMinY,
            tree.BoundsMaxZ - tree.BoundsMinZ);

        return extent.Length();
    }

    /// <summary>
    /// Whether a point is inside a group's box, or directly above it.
    /// </summary>
    /// <remarks>
    /// <b>The upper Z bound is deliberately not tested.</b> Upstream's
    /// <c>IsInsideOrAboveBound</c> checks five of the six faces, and the missing one is what lets a
    /// point above a group be reported as <see cref="GroupInside.Above"/> instead of being rejected.
    /// Adding the sixth test looks like a tidy-up and removes the floorless-room case entirely.
    /// </remarks>
    private static bool IsInsideOrAboveBound(WorldModelGroup group, Vector3 point) =>
        point.X >= group.BoundsMinX && point.X <= group.BoundsMaxX &&
        point.Y >= group.BoundsMinY && point.Y <= group.BoundsMaxY &&
        point.Z >= group.BoundsMinZ;

    /// <summary>Whether a point is inside the group's mesh box on all three axes.</summary>
    private static bool ContainsPoint(WorldModelGroup group, Vector3 point, float minZ, float maxZ) =>
        point.X >= group.BoundsMinX && point.X <= group.BoundsMaxX &&
        point.Y >= group.BoundsMinY && point.Y <= group.BoundsMaxY &&
        point.Z >= minZ && point.Z <= maxZ;

    /// <summary>Whether a world point is inside a spawn's precomputed box.</summary>
    private static bool ContainsBound(ModelSpawn spawn, Vector3 point) =>
        point.X >= spawn.BoundsMinX && point.X <= spawn.BoundsMaxX &&
        point.Y >= spawn.BoundsMinY && point.Y <= spawn.BoundsMaxY &&
        point.Z >= spawn.BoundsMinZ && point.Z <= spawn.BoundsMaxZ;

    /// <summary>The vertical extent of a group's mesh tree, falling back to its own box.</summary>
    private static (float MinZ, float MaxZ) MeshBoundsZ(WorldModelGroup group) =>
        group.MeshTree is { } tree
            ? (tree.BoundsMinZ, tree.BoundsMaxZ)
            : (group.BoundsMinZ, group.BoundsMaxZ);
}
