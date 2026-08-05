using System.Numerics;
using WowEmu.Data.Client;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Ray/triangle intersection, on geometry whose answers are known exactly.
/// </summary>
/// <remarks>
/// Synthetic on purpose. Real models can only be checked against invariants — "it hit something
/// plausible" — whereas a unit triangle at a known distance has one right answer, so an error of a
/// hundredth shows up here and nowhere else.
/// </remarks>
public sealed class TriangleIntersectionTests
{
    private static readonly Vector3 A = new(0f, 0f, 0f);
    private static readonly Vector3 B = new(1f, 0f, 0f);
    private static readonly Vector3 C = new(0f, 1f, 0f);

    [Fact]
    public void ARayThroughTheTriangle_HitsAtTheRightDistance()
    {
        // Straight down the -Z axis at a point well inside the triangle.
        Ray ray = new(new Vector3(0.25f, 0.25f, 5f), new Vector3(0f, 0f, -1f));
        float distance = 100f;

        Assert.True(Collision.IntersectTriangle(ray, A, B, C, ref distance));
        Assert.Equal(5f, distance, 0.0001f);
    }

    [Fact]
    public void ARayOutsideTheTriangle_Misses()
    {
        // In the triangle's plane but past the hypotenuse: u + v > 1.
        Ray ray = new(new Vector3(0.9f, 0.9f, 5f), new Vector3(0f, 0f, -1f));
        float distance = 100f;

        Assert.False(Collision.IntersectTriangle(ray, A, B, C, ref distance));
        Assert.Equal(100f, distance);
    }

    [Fact]
    public void ARayPointingAway_Misses()
    {
        Ray ray = new(new Vector3(0.25f, 0.25f, 5f), new Vector3(0f, 0f, 1f));
        float distance = 100f;

        Assert.False(Collision.IntersectTriangle(ray, A, B, C, ref distance));
    }

    /// <summary>A ray in the triangle's own plane is degenerate and must be refused, not guessed at.</summary>
    [Fact]
    public void ARayParallelToThePlane_Misses()
    {
        Ray ray = new(new Vector3(-5f, 0.25f, 0f), new Vector3(1f, 0f, 0f));
        float distance = 100f;

        Assert.False(Collision.IntersectTriangle(ray, A, B, C, ref distance));
    }

    /// <summary>
    /// A hit farther than the distance already found is not a hit.
    /// </summary>
    /// <remarks>
    /// The in-and-out distance is what makes the traversal work: it holds the nearest hit so far,
    /// and every later test is measured against it. Without this, the last triangle visited would
    /// win rather than the closest one — and a wall behind you would block the view.
    /// </remarks>
    [Fact]
    public void ATriangleBeyondTheCurrentHit_DoesNotOverwriteIt()
    {
        Ray ray = new(new Vector3(0.25f, 0.25f, 5f), new Vector3(0f, 0f, -1f));

        // A nearer hit has already been recorded at 2 yards.
        float distance = 2f;

        Assert.False(Collision.IntersectTriangle(ray, A, B, C, ref distance));
        Assert.Equal(2f, distance);
    }

    [Fact]
    public void ANearerTriangle_NarrowsTheDistance()
    {
        Ray ray = new(new Vector3(0.25f, 0.25f, 5f), new Vector3(0f, 0f, -1f));
        float distance = 100f;

        // The far triangle first, then a nearer one: the nearer must win.
        Assert.True(Collision.IntersectTriangle(ray, A, B, C, ref distance));
        Assert.Equal(5f, distance, 0.0001f);

        Vector3 lift = new(0f, 0f, 3f);
        Assert.True(Collision.IntersectTriangle(ray, A + lift, B + lift, C + lift, ref distance));
        Assert.Equal(2f, distance, 0.0001f);
    }

    /// <summary>Hits exactly on an edge are accepted; the barycentric test is inclusive at 0 and 1.</summary>
    [Fact]
    public void ARayThroughAVertex_Hits()
    {
        Ray ray = new(new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, -1f));
        float distance = 100f;

        Assert.True(Collision.IntersectTriangle(ray, A, B, C, ref distance));
        Assert.Equal(5f, distance, 0.0001f);
    }
}

/// <summary>The coordinate and rotation transforms a placed model needs.</summary>
public sealed class ModelTransformTests
{
    /// <summary>
    /// The world-to-vmap conversion mirrors X and Y about the world's midpoint.
    /// </summary>
    /// <remarks>
    /// Getting this wrong is silent in the worst way: every query lands on the opposite side of the
    /// map, finds no geometry there, and reports clear line of sight through solid rock.
    /// </remarks>
    [Fact]
    public void ToInternal_MirrorsXAndYAndLeavesZ()
    {
        Vector3 internalPosition = Collision.ToInternal(100f, 200f, 50f);

        Assert.Equal(Collision.WorldMid - 100f, internalPosition.X, 0.001f);
        Assert.Equal(Collision.WorldMid - 200f, internalPosition.Y, 0.001f);
        Assert.Equal(50f, internalPosition.Z, 0.001f);
    }

    [Fact]
    public void ToInternal_IsItsOwnInverse()
    {
        Vector3 once = Collision.ToInternal(1234.5f, -678.9f, 42f);
        Vector3 twice = Collision.ToInternal(once.X, once.Y, once.Z);

        Assert.Equal(1234.5f, twice.X, 0.01f);
        Assert.Equal(-678.9f, twice.Y, 0.01f);
        Assert.Equal(42f, twice.Z, 0.01f);
    }

    /// <summary>The world's midpoint maps to the origin, which is what makes it a mirror.</summary>
    [Fact]
    public void TheWorldMidpoint_MapsToZero()
    {
        Vector3 origin = Collision.ToInternal(Collision.WorldMid, Collision.WorldMid, 0f);

        Assert.Equal(0f, origin.X, 0.001f);
        Assert.Equal(0f, origin.Y, 0.001f);
    }

    /// <summary>An unrotated model's inverse rotation is the identity.</summary>
    [Fact]
    public void NoRotation_GivesTheIdentity()
    {
        Matrix4x4 inverse = Collision.InverseRotation(Spawn(0f, 0f, 0f));

        Assert.True(Matrix4x4.Identity.Equals(inverse) || IsNearlyIdentity(inverse));
    }

    /// <summary>
    /// The inverse really is an inverse: rotating a point and un-rotating it returns it.
    /// </summary>
    /// <remarks>
    /// This is what proves the transpose shortcut is legitimate. A rotation matrix is orthonormal so
    /// its transpose is its inverse — exactly, and without a general inversion's conditioning
    /// worries — but only if what was built really is a rotation.
    /// </remarks>
    [Fact]
    public void TheInverse_UndoesTheRotation()
    {
        ModelSpawn spawn = Spawn(30f, 45f, 60f);

        Matrix4x4 inverse = Collision.InverseRotation(spawn);
        Matrix4x4 forward = Matrix4x4.Transpose(inverse);

        Vector3 point = new(3f, -7f, 11f);
        Vector3 roundTripped = Vector3.Transform(Vector3.Transform(point, forward), inverse);

        Assert.Equal(point.X, roundTripped.X, 0.001f);
        Assert.Equal(point.Y, roundTripped.Y, 0.001f);
        Assert.Equal(point.Z, roundTripped.Z, 0.001f);
    }

    /// <summary>
    /// The stored rotation components are consumed in upstream's swapped order.
    /// </summary>
    /// <remarks>
    /// <c>fromEulerAnglesZYX(rot.y, rot.x, rot.z)</c> — the stored Y becomes the Z angle and the
    /// stored X becomes the Y angle. Feeding them in the order they are stored produces a rotation
    /// about the wrong axes, which looks fine on a symmetrical model and wrong on a wall.
    /// <para>
    /// Pinned by construction: a spawn rotated only about its stored <c>RotationY</c> must move a
    /// point in the plane a Z rotation moves it in.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyTheStoredY_ProducesARotationAboutZ()
    {
        // 90 degrees in the stored Y slot, which upstream feeds in as the Z angle.
        Matrix4x4 forward = Matrix4x4.Transpose(Collision.InverseRotation(Spawn(0f, 90f, 0f)));

        Vector3 alongX = Vector3.Transform(new Vector3(1f, 0f, 0f), forward);

        // A rotation about Z takes +X to +Y and leaves Z alone.
        Assert.Equal(0f, alongX.X, 0.001f);
        Assert.Equal(1f, alongX.Y, 0.001f);
        Assert.Equal(0f, alongX.Z, 0.001f);
    }

    [Fact]
    public void OnlyTheStoredX_ProducesARotationAboutY()
    {
        Matrix4x4 forward = Matrix4x4.Transpose(Collision.InverseRotation(Spawn(90f, 0f, 0f)));

        Vector3 alongZ = Vector3.Transform(new Vector3(0f, 0f, 1f), forward);

        // A rotation about Y takes +Z to +X.
        Assert.Equal(1f, alongZ.X, 0.001f);
        Assert.Equal(0f, alongZ.Y, 0.001f);
        Assert.Equal(0f, alongZ.Z, 0.001f);
    }

    private static bool IsNearlyIdentity(Matrix4x4 m) =>
        MathF.Abs(m.M11 - 1f) < 1e-5f && MathF.Abs(m.M22 - 1f) < 1e-5f && MathF.Abs(m.M33 - 1f) < 1e-5f
        && MathF.Abs(m.M12) < 1e-5f && MathF.Abs(m.M13) < 1e-5f && MathF.Abs(m.M21) < 1e-5f;

    private static ModelSpawn Spawn(float rotX, float rotY, float rotZ) => new(
        ModelSpawnFlags.None, 0, 0,
        0f, 0f, 0f,
        rotX, rotY, rotZ,
        1f,
        0f, 0f, 0f, 0f, 0f, 0f,
        "test", false);
}

/// <summary>
/// Line of sight against real extracted geometry.
/// </summary>
/// <remarks>
/// These cannot assert an exact distance — nobody knows where a particular wall is without looking.
/// What they can assert is the property that matters: a ray fired through the middle of a solid
/// model hits it, and the same ray fired well away from it does not.
/// </remarks>
public sealed class LineOfSightTests(ITestOutputHelper output)
{
    /// <summary>
    /// A ray through the middle of a solid model usually hits it.
    /// </summary>
    /// <remarks>
    /// A heuristic, and asserted as one. A group is not always a closed solid — a curved wall or an
    /// overhanging roof can sit entirely off to one side of its own bounding box, so a ray through
    /// the box's centre genuinely misses it on every axis. What would be alarming is that happening
    /// <i>often</i>, so the assertion is on the proportion.
    /// <para>
    /// The exact check lives in <see cref="TheTreeFilter_AgreesWithBruteForce"/>, which compares
    /// against testing every triangle and needs no assumption about shape.
    /// </para>
    /// </remarks>
    [RequiresVmapFact]
    public void ARayThroughAModel_UsuallyHitsIt()
    {
        int tested = 0, hitFromEveryAxis = 0, missedEntirely = 0;

        foreach (WorldModel model in SolidModels(40))
        {
            WorldModelGroup group = model.Groups.First(g => g.HasGeometry && g.HasBounds);

            Vector3 centre = new(
                (group.BoundsMinX + group.BoundsMaxX) / 2f,
                (group.BoundsMinY + group.BoundsMaxY) / 2f,
                (group.BoundsMinZ + group.BoundsMaxZ) / 2f);

            float span = MathF.Max(
                group.BoundsMaxX - group.BoundsMinX,
                MathF.Max(group.BoundsMaxY - group.BoundsMinY, group.BoundsMaxZ - group.BoundsMinZ));

            if (span < 1f)
            {
                continue;
            }

            tested++;
            int axesHit = 0;

            foreach (Vector3 direction in Axes)
            {
                // Start outside the model and fire through its centre.
                Ray ray = new(centre + (direction * span * 2f), -direction);
                float distance = span * 4f;

                if (Collision.IntersectGroup(group, ray, ref distance, stopAtFirstHit: true))
                {
                    axesHit++;
                }
            }

            if (axesHit == Axes.Length)
            {
                hitFromEveryAxis++;
            }
            else if (axesHit == 0)
            {
                missedEntirely++;
            }
        }

        Assert.True(tested > 10, $"only {tested} models were big enough to test");

        // Most models are hit through their centre. A handful legitimately are not; a majority not
        // being hit would mean the intersection itself is broken.
        Assert.True(
            missedEntirely * 4 < tested,
            $"{missedEntirely} of {tested} models were missed from every axis — too many to be shape");

        output.WriteLine(
            $"{tested} models tested: {hitFromEveryAxis} hit from all three axes, " +
            $"{missedEntirely} missed from every axis");
    }

    /// <summary>A ray nowhere near a model does not hit it.</summary>
    /// <remarks>
    /// The other half. A test that only checks for hits passes just as well against an intersection
    /// routine that always returns true.
    /// </remarks>
    [RequiresVmapFact]
    public void ARayFarFromAModel_MissesIt()
    {
        int tested = 0;

        foreach (WorldModel model in SolidModels(40))
        {
            WorldModelGroup group = model.Groups.First(g => g.HasGeometry && g.HasBounds);

            float span = MathF.Max(
                group.BoundsMaxX - group.BoundsMinX,
                MathF.Max(group.BoundsMaxY - group.BoundsMinY, group.BoundsMaxZ - group.BoundsMinZ));

            if (span < 1f)
            {
                continue;
            }

            // Well outside the bounding box, travelling away from it.
            Vector3 origin = new(
                group.BoundsMaxX + (span * 10f),
                group.BoundsMaxY + (span * 10f),
                group.BoundsMaxZ + (span * 10f));

            Ray ray = new(origin, Vector3.Normalize(new Vector3(1f, 1f, 1f)));
            float distance = span * 100f;

            Assert.False(
                Collision.IntersectGroup(group, ray, ref distance, stopAtFirstHit: true),
                "a ray travelling away from a model hit it");

            tested++;
        }

        Assert.True(tested > 10);
        output.WriteLine($"{tested} models correctly missed");
    }

    /// <summary>
    /// Line of sight is the negation of a hit, and reports clear when nothing is in the way.
    /// </summary>
    [RequiresVmapFact]
    public void LineOfSight_IsClearWhenNothingBlocks()
    {
        WorldModel model = SolidModels(1).First();
        WorldModelGroup group = model.Groups.First(g => g.HasGeometry && g.HasBounds);

        float span = MathF.Max(
            group.BoundsMaxX - group.BoundsMinX,
            MathF.Max(group.BoundsMaxY - group.BoundsMinY, group.BoundsMaxZ - group.BoundsMinZ));

        Vector3 centre = new(
            (group.BoundsMinX + group.BoundsMaxX) / 2f,
            (group.BoundsMinY + group.BoundsMaxY) / 2f,
            (group.BoundsMinZ + group.BoundsMaxZ) / 2f);

        // Through the model: blocked.
        Ray through = new(centre + new Vector3(0f, 0f, span * 2f), new Vector3(0f, 0f, -1f));
        Assert.False(Collision.IsInLineOfSight(group, through, span * 4f));

        // Far to one side, travelling away: clear.
        Vector3 outside = new(group.BoundsMaxX + (span * 20f), group.BoundsMaxY + (span * 20f), centre.Z);
        Ray away = new(outside, Vector3.Normalize(new Vector3(1f, 1f, 0f)));
        Assert.True(Collision.IsInLineOfSight(group, away, span * 50f));
    }

    /// <summary>
    /// The tree filter never loses a hit that brute force would find.
    /// </summary>
    /// <remarks>
    /// The BIH is an optimisation, so the only thing that can go wrong is it discarding geometry the
    /// ray really meets — and that failure is silent, appearing as a wall you can see through. This
    /// compares the filtered answer against testing every triangle, which is slow and obviously
    /// correct.
    /// </remarks>
    [RequiresVmapFact]
    public void TheTreeFilter_AgreesWithBruteForce()
    {
        int compared = 0, hits = 0;

        foreach (WorldModel model in SolidModels(25))
        {
            foreach (WorldModelGroup group in model.Groups.Where(g => g.HasGeometry && g.HasBounds))
            {
                Vector3 centre = new(
                    (group.BoundsMinX + group.BoundsMaxX) / 2f,
                    (group.BoundsMinY + group.BoundsMaxY) / 2f,
                    (group.BoundsMinZ + group.BoundsMaxZ) / 2f);

                float span = MathF.Max(
                    group.BoundsMaxX - group.BoundsMinX,
                    MathF.Max(group.BoundsMaxY - group.BoundsMinY, group.BoundsMaxZ - group.BoundsMinZ));

                if (span < 1f)
                {
                    continue;
                }

                foreach (Vector3 direction in Axes)
                {
                    Ray ray = new(centre + (direction * span * 2f), -direction);

                    float viaTree = span * 4f;
                    bool treeHit = Collision.IntersectGroup(group, ray, ref viaTree, stopAtFirstHit: false);

                    float viaBrute = span * 4f;
                    bool bruteHit = BruteForce(group, ray, ref viaBrute);

                    Assert.Equal(bruteHit, treeHit);

                    if (bruteHit)
                    {
                        // And the same nearest distance, not merely the same answer.
                        Assert.Equal(viaBrute, viaTree, 0.001f);
                        hits++;
                    }

                    compared++;
                }
            }
        }

        Assert.True(compared > 50, $"only {compared} rays compared");
        output.WriteLine($"{compared} rays agree between tree and brute force ({hits} hits)");
    }

    private static bool BruteForce(WorldModelGroup group, Ray ray, ref float distance)
    {
        bool hit = false;

        for (int i = 0; i < group.Triangles.Length; i++)
        {
            MeshTriangle triangle = group.Triangles[i];

            Vector3 a = new(
                group.Vertices[(triangle.Index0 * 3) + 0],
                group.Vertices[(triangle.Index0 * 3) + 1],
                group.Vertices[(triangle.Index0 * 3) + 2]);
            Vector3 b = new(
                group.Vertices[(triangle.Index1 * 3) + 0],
                group.Vertices[(triangle.Index1 * 3) + 1],
                group.Vertices[(triangle.Index1 * 3) + 2]);
            Vector3 c = new(
                group.Vertices[(triangle.Index2 * 3) + 0],
                group.Vertices[(triangle.Index2 * 3) + 1],
                group.Vertices[(triangle.Index2 * 3) + 2]);

            if (Collision.IntersectTriangle(ray, a, b, c, ref distance))
            {
                hit = true;
            }
        }

        return hit;
    }

    private static readonly Vector3[] Axes =
    [
        new(1f, 0f, 0f),
        new(0f, 1f, 0f),
        new(0f, 0f, 1f),
    ];

    private static IEnumerable<WorldModel> SolidModels(int limit)
    {
        int found = 0;

        foreach (string path in Directory.EnumerateFiles(VmapData.Directory, "*.vmo").Order(StringComparer.Ordinal))
        {
            if (found >= limit)
            {
                yield break;
            }

            WorldModel model = WorldModelFile.Read(File.ReadAllBytes(path));

            if (!model.Groups.Any(g => g.HasGeometry && g.HasBounds && g.Triangles.Length > 8))
            {
                continue;
            }

            found++;
            yield return model;
        }
    }
}
