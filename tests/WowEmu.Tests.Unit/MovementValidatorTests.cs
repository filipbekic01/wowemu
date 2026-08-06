using WowEmu.Core;
using WowEmu.Game.Maps;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Movement plausibility checks.
/// </summary>
/// <remarks>
/// These exist because the client computes its own movement and the server only sees the result —
/// every teleport and speed hack lives in the gap between "the client says so" and "the server
/// checked". The checks here are deliberately the ones that are <i>exactly</i> right rather than
/// nearly right; see <see cref="MovementValidator"/> for what is left out and why.
/// </remarks>
public sealed class MovementValidatorTests
{
    private static readonly Position Origin = new(-8949.95f, -132.493f, 83.5312f, 0f);

    [Fact]
    public void NormalWalking_IsAccepted()
    {
        // 7 yards in a second: base run speed exactly.
        MovementVerdict verdict = Validate(Origin, Offset(Origin, 7f, 0f), elapsed: 1000);

        Assert.True(verdict.Accepted);
        Assert.Equal(MovementRejection.None, verdict.Rejection);
    }

    [Fact]
    public void MountedSpeed_IsAccepted()
    {
        // The fastest flying mount is 31.5 yards/second.
        MovementVerdict verdict = Validate(Origin, Offset(Origin, 31.5f, 0f), elapsed: 1000);

        Assert.True(verdict.Accepted);
    }

    /// <summary>
    /// A NaN coordinate would poison the cell arithmetic and then be written to the database, so it
    /// is refused before anything touches it.
    /// </summary>
    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.NaN, 0f)]
    [InlineData(0f, 0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(float.NegativeInfinity, 0f, 0f)]
    public void NonFiniteCoordinates_AreRefused(float x, float y, float z)
    {
        MovementInfo movement = new() { Position = new Position(x, y, z, 0f) };

        MovementVerdict verdict = MovementValidator.Validate(Origin, movement, 100);

        Assert.False(verdict.Accepted);
        Assert.Equal(MovementRejection.InvalidCoordinate, verdict.Rejection);
    }

    [Fact]
    public void CoordinatesBeyondTheMap_AreRefused()
    {
        MovementInfo movement = new() { Position = new Position(999_999f, 0f, 0f, 0f) };

        MovementVerdict verdict = MovementValidator.Validate(Origin, movement, 100);

        Assert.Equal(MovementRejection.InvalidCoordinate, verdict.Rejection);
    }

    /// <summary>
    /// Orientation gets the same treatment as the coordinates. Normalizing NaN yields NaN, so a
    /// hostile value survives the setter and has to be caught here.
    /// </summary>
    [Fact]
    public void NonFiniteOrientation_IsRefused()
    {
        MovementInfo movement = new()
        {
            Position = new Position(Origin.X, Origin.Y, Origin.Z, float.NaN),
        };

        Assert.False(float.IsFinite(movement.Position.Orientation));

        MovementVerdict verdict = MovementValidator.Validate(Origin, movement, 100);

        Assert.False(verdict.Accepted);
        Assert.Equal(MovementRejection.InvalidCoordinate, verdict.Rejection);
    }

    /// <summary>The blatant case: a jump no elapsed time can explain.</summary>
    [Fact]
    public void LongTeleport_IsRefused()
    {
        MovementVerdict verdict = Validate(Origin, Offset(Origin, 500f, 0f), elapsed: 100);

        Assert.Equal(MovementRejection.Teleport, verdict.Rejection);
        Assert.Contains("yards in one packet", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The step cap is a backstop against claiming a long elapsed time: without it, a hacked client
    /// could justify any distance by asserting enough time had passed.
    /// </summary>
    [Fact]
    public void LongTeleport_IsRefusedEvenWithAGenerousElapsedTime()
    {
        MovementVerdict verdict = Validate(Origin, Offset(Origin, 500f, 0f), elapsed: 9000);

        Assert.Equal(MovementRejection.Teleport, verdict.Rejection);
    }

    [Fact]
    public void ImpossibleSpeed_IsRefused()
    {
        // 100 yards in 100 ms is 1000 yards/second.
        MovementVerdict verdict = Validate(Origin, Offset(Origin, 100f, 0f), elapsed: 100);

        Assert.Equal(MovementRejection.ImpossibleSpeed, verdict.Rejection);
        Assert.Contains("yards/second", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the player's real speed known, the ceiling is that rather than the flat one.
    /// </summary>
    /// <remarks>
    /// 30 yards a second is nowhere near the 60 the flat ceiling allows, so before the applied speed
    /// was passed in this went through unchallenged. A player at base run speed is now held to
    /// double it.
    /// </remarks>
    [Fact]
    public void AKnownSpeed_TightensTheCeiling()
    {
        // 3 yards in 100 ms is 30 yards/second: under the flat ceiling, far over a runner's.
        MovementInfo claimed = Offset(Origin, 3f, 0f);

        Assert.True(MovementValidator.Validate(Origin, claimed, 100).Accepted);

        MovementVerdict verdict = MovementValidator.Validate(
            Origin, claimed, 100, floorAt: null, allowedSpeed: 7.0f);

        Assert.Equal(MovementRejection.ImpossibleSpeed, verdict.Rejection);
        Assert.Contains("ceiling", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tolerance is real headroom, not a rounding allowance.
    /// </summary>
    /// <remarks>
    /// The elapsed time is the server's, so a stalled tick or a burst of queued packets inflates the
    /// measured speed over a short window. A player genuinely running at 7 has to survive measuring
    /// at 13.
    /// </remarks>
    [Fact]
    public void AClientJustOverItsSpeed_IsStillAccepted()
    {
        // 1.3 yards in 100 ms is 13 yards/second — nearly double a runner's 7, and inside tolerance.
        MovementVerdict verdict = MovementValidator.Validate(
            Origin, Offset(Origin, 1.3f, 0f), 100, floorAt: null, allowedSpeed: 7.0f);

        Assert.True(verdict.Accepted, verdict.Detail);
    }

    /// <summary>
    /// With no known speed the flat ceiling applies, which is what an unacknowledged change gives.
    /// </summary>
    /// <remarks>
    /// Between ordering a speed and the client confirming it, the client is legitimately at either
    /// the old value or the new one. Holding it to either would refuse an honest client half the
    /// time, so the check loosens for exactly as long as the ambiguity lasts.
    /// </remarks>
    [Fact]
    public void WithNoKnownSpeed_TheFlatCeilingApplies()
    {
        // 50 yards/second: over any real speed, under the flat ceiling.
        MovementVerdict verdict = MovementValidator.Validate(
            Origin, Offset(Origin, 5f, 0f), 100, floorAt: null, allowedSpeed: null);

        Assert.True(verdict.Accepted, verdict.Detail);
    }

    [Fact]
    public void ContradictoryFlags_AreRefused()
    {
        MovementInfo movement = Offset(Origin, 1f, 0f);
        movement.Flags = MovementFlag.Falling | MovementFlag.Swimming;

        MovementVerdict verdict = MovementValidator.Validate(Origin, movement, 1000);

        Assert.Equal(MovementRejection.ContradictoryFlags, verdict.Rejection);
    }

    /// <summary>
    /// A first packet has no previous timestamp, and a client that stalled can report a long gap.
    /// Neither is cheating, so the speed check stands down — the step cap still applies.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(60_000u)]
    public void WithoutAUsableElapsedTime_SpeedIsNotJudged(uint elapsed)
    {
        // 100 yards would be an impossible speed over a normal interval.
        MovementVerdict verdict = Validate(Origin, Offset(Origin, 100f, 0f), elapsed);

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void SwimmingAlone_IsFine()
    {
        MovementInfo movement = Offset(Origin, 3f, 0f);
        movement.Flags = MovementFlag.Swimming;

        Assert.True(MovementValidator.Validate(Origin, movement, 1000).Accepted);
    }

    [Fact]
    public void FallingAlone_IsFine()
    {
        MovementInfo movement = Offset(Origin, 0f, 0f);
        movement.Flags = MovementFlag.Falling;
        movement.FallTime = 1200;

        Assert.True(MovementValidator.Validate(Origin, movement, 1000).Accepted);
    }

    /// <summary>Standing still is the most common packet of all.</summary>
    [Fact]
    public void NotMovingAtAll_IsAccepted()
    {
        Assert.True(Validate(Origin, Offset(Origin, 0f, 0f), 500).Accepted);
    }

    private static MovementVerdict Validate(Position from, MovementInfo movement, uint elapsed) =>
        MovementValidator.Validate(from, movement, elapsed);

    private static MovementInfo Offset(Position from, float dx, float dy) => new()
    {
        Position = new Position(from.X + dx, from.Y + dy, from.Z, 0f),
    };
}
