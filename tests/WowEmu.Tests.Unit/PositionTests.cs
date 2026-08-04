using WowEmu.Core;

namespace WowEmu.Tests.Unit;

/// <summary>Positions, distances and the orientation normalization that trips everyone up once.</summary>
public sealed class PositionTests
{
    private const float Tolerance = 0.0001f;

    [Fact]
    public void Constructor_NormalizesOrientation()
    {
        Position position = new(0, 0, 0, 3f * MathF.PI);

        Assert.Equal(MathF.PI, position.Orientation, Tolerance);
    }

    [Fact]
    public void Orientation_IsRenormalizedOnEveryAssignment()
    {
        Position position = new();

        position.Orientation = 7f;

        Assert.InRange(position.Orientation, 0f, 2f * MathF.PI);
        Assert.Equal(7f - (2f * MathF.PI), position.Orientation, Tolerance);
    }

    /// <summary>
    /// Negative angles are folded by mirroring rather than a single <c>fmod</c>, because C's
    /// <c>fmod</c> keeps its argument's sign. Getting this wrong points units backwards.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(-1f, 5.2831855f)]
    [InlineData(-3.14159265f, 3.14159265f)]
    [InlineData(7f, 0.71681476f)]
    public void NormalizeOrientation_MatchesUpstream(float input, float expected)
    {
        Assert.Equal(expected, Position.NormalizeOrientation(input), Tolerance);
    }

    [Fact]
    public void NormalizeOrientation_NeverReturnsNegative()
    {
        for (float angle = -20f; angle < 20f; angle += 0.37f)
        {
            Assert.True(Position.NormalizeOrientation(angle) >= 0f, $"{angle} normalized negative");
        }
    }

    [Fact]
    public void Distances_UseTheRightNumberOfDimensions()
    {
        Position from = new(0, 0, 0);
        Position to = new(3, 4, 12);

        Assert.Equal(5f, from.GetExactDist2d(to), Tolerance);
        Assert.Equal(25f, from.GetExactDist2dSq(to), Tolerance);
        Assert.Equal(13f, from.GetExactDist(to), Tolerance);
        Assert.Equal(169f, from.GetExactDistSq(to), Tolerance);
    }

    /// <summary>Upstream compares with a strict <c>&lt;</c>: a target exactly at range is out of range.</summary>
    [Fact]
    public void IsInDist_ExcludesTheBoundary()
    {
        Position origin = new(0, 0, 0);

        Assert.True(origin.IsInDist2d(new Position(3, 4, 0), 5.001f));
        Assert.False(origin.IsInDist2d(new Position(3, 4, 0), 5f));
    }

    [Fact]
    public void GetAngle_IsMeasuredFromPositiveX_AndNormalized()
    {
        Position origin = new(0, 0, 0);

        Assert.Equal(0f, origin.GetAngle(1, 0), Tolerance);
        Assert.Equal(MathF.PI / 2f, origin.GetAngle(0, 1), Tolerance);
        Assert.Equal(MathF.PI, origin.GetAngle(-1, 0), Tolerance);

        // Straight down: atan2 gives -π/2, which must come back as 3π/2.
        Assert.Equal(3f * MathF.PI / 2f, origin.GetAngle(0, -1), Tolerance);
    }

    [Fact]
    public void GetRelativeAngle_IsRelativeToFacing()
    {
        Position facingNorth = new(0, 0, 0, MathF.PI / 2f);

        Assert.Equal(0f, facingNorth.GetRelativeAngle(0, 1), Tolerance);
        Assert.Equal(3f * MathF.PI / 2f, facingNorth.GetRelativeAngle(1, 0), Tolerance);
    }

    [Fact]
    public void HasInArc_CoversTheFacingAndExcludesBehind()
    {
        Position facingEast = new(0, 0, 0, 0f);

        Assert.True(facingEast.HasInArc(MathF.PI, new Position(10, 0, 0)));
        Assert.True(facingEast.HasInArc(MathF.PI, new Position(10, 4, 0)));
        Assert.False(facingEast.HasInArc(MathF.PI, new Position(-10, 0, 0)));
    }

    [Fact]
    public void RelocateOffset_ThenOffsetTo_RoundTrips()
    {
        Position origin = new(100, 200, 50, 1.2f);
        Position offset = new(5, -3, 2, 0.4f);

        Position moved = origin;
        moved.RelocateOffset(offset);

        Position recovered = origin.GetPositionOffsetTo(moved);

        Assert.Equal(offset.X, recovered.X, 0.001f);
        Assert.Equal(offset.Y, recovered.Y, 0.001f);
        Assert.Equal(offset.Z, recovered.Z, 0.001f);
    }

    [Fact]
    public void Relocate_ReplacesTheCoordinates()
    {
        Position position = new(1, 2, 3, 0.5f);
        position.Relocate(10, 20, 30, 1.5f);

        Assert.Equal(10f, position.X);
        Assert.Equal(20f, position.Y);
        Assert.Equal(30f, position.Z);
        Assert.Equal(1.5f, position.Orientation, Tolerance);
    }

    [Fact]
    public void WorldLocation_DefaultsToTheInvalidMap()
    {
        WorldLocation location = new();

        Assert.Equal(0xFFFFFFFFu, location.MapId);
        Assert.Equal(WorldLocation.InvalidMapId, location.MapId);
    }

    [Fact]
    public void WorldLocation_ComparesMapAndPosition()
    {
        WorldLocation first = new(0, new Position(1, 2, 3, 0));
        WorldLocation same = new(0, new Position(1, 2, 3, 0));
        WorldLocation otherMap = new(1, new Position(1, 2, 3, 0));

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherMap);
    }
}
