using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game.Maps;
using WowEmu.Protocol;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The floor under a point, from terrain and models together.
/// </summary>
/// <remarks>
/// Neither source is complete: terrain knows the ground and nothing else, vmaps know buildings and
/// nothing about the ground beneath them. The floor is the higher of the two, and either may
/// legitimately be absent.
/// </remarks>
public sealed class WorldHeightTests(ITestOutputHelper output)
{
    private const float StartX = -8949.95f;
    private const float StartY = -132.493f;
    private const float StartZ = 83.53f;

    /// <summary>Neither source knowing is a real answer, not a failure.</summary>
    /// <remarks>
    /// A caller that reads null as "the floor is at zero" concludes that every player standing over
    /// a terrain hole is a hundred yards in the air.
    /// </remarks>
    [Fact]
    public void WithNeitherSource_TheFloorIsUnknown()
    {
        Assert.Null(WorldHeight.GetFloor(terrain: null, vmaps: null, 0f, 0f, 0f));
    }

    [RequiresMapsFact]
    public void OnOpenGround_TheFloorComesFromTerrain()
    {
        TerrainMap terrain = new(0, Path.Combine(ClientData.DataDirectory, "maps"));

        float? floor = WorldHeight.GetFloor(terrain, vmaps: null, StartX, StartY, StartZ);

        Assert.NotNull(floor);

        // The human start position stands on the ground, so the two agree within a couple of yards.
        Assert.InRange(floor.Value, StartZ - 3f, StartZ + 3f);

        output.WriteLine($"terrain floor at the human start: {floor.Value:F2} (character at {StartZ:F2})");
    }

    /// <summary>
    /// Where a building stands, the model surface is above the ground and wins.
    /// </summary>
    /// <remarks>
    /// This is the case the whole exercise exists for. Terrain alone would put a player standing on
    /// a bridge or an upper floor tens of yards below where they are, and any height check built on
    /// it would refuse them.
    /// </remarks>
    [RequiresVmapFact]
    public void WhereAModelStands_TheFloorIsHigherThanTheGround()
    {
        TerrainMap terrain = new(0, Path.Combine(ClientData.DataDirectory, "maps"));
        StaticMapTree vmaps = new(0, VmapData.Directory);

        int raised = 0, sampled = 0;
        float biggestLift = 0f;

        // Stormwind, which is dense with buildings on sloping ground.
        for (float x = -8900f; x <= -8500f; x += 20f)
        {
            for (float y = 400f; y <= 800f; y += 20f)
            {
                float ground = terrain.GetHeight(x, y);

                if (ground <= MapGeometry.InvalidHeight)
                {
                    continue;
                }

                sampled++;

                // Start well above so a tall building is found from outside it.
                float? floor = WorldHeight.GetFloor(terrain, vmaps, x, y, ground + 40f);

                Assert.NotNull(floor);
                Assert.True(floor.Value >= ground - 0.01f, "the combined floor came out below the ground");

                if (floor.Value > ground + 1f)
                {
                    raised++;
                    biggestLift = MathF.Max(biggestLift, floor.Value - ground);
                }
            }
        }

        Assert.True(sampled > 100, $"only {sampled} points had terrain");
        Assert.True(raised > 0, "no point in Stormwind had a model above the ground");

        output.WriteLine(
            $"{raised} of {sampled} sampled points stand on a model rather than the ground; " +
            $"the tallest is {biggestLift:F1} yards above it");
    }

    /// <summary>The combined floor is never below what terrain alone reports.</summary>
    [RequiresVmapFact]
    public void TheCombinedFloor_IsNeverBelowTheGround()
    {
        TerrainMap terrain = new(0, Path.Combine(ClientData.DataDirectory, "maps"));
        StaticMapTree vmaps = new(0, VmapData.Directory);

        for (float x = -9000f; x <= -8400f; x += 37f)
        {
            for (float y = -300f; y <= 900f; y += 37f)
            {
                float ground = terrain.GetHeight(x, y);

                if (ground <= MapGeometry.InvalidHeight)
                {
                    continue;
                }

                float? floor = WorldHeight.GetFloor(terrain, vmaps, x, y, ground + 5f);

                Assert.NotNull(floor);
                Assert.True(floor.Value >= ground - 0.01f);
            }
        }
    }
}

/// <summary>
/// The under-the-world check, which is the only height test that is safe to make.
/// </summary>
/// <remarks>
/// Deliberately one-sided. Below the floor is unambiguous — there is nothing down there to stand on.
/// Above it is ordinary, and a check in that direction would have to know about lifts, transports
/// and the moment between leaving a surface and the fall being reported.
/// </remarks>
public sealed class UnderTheWorldTests
{
    [Fact]
    public void WithNoFloorProvider_TheHeightCheckIsSkipped()
    {
        // Deep under the world, but arrived there plausibly — the earlier checks must not be the
        // reason this passes.
        MovementInfo movement = At(0f, 0f, -1000f);

        Assert.True(MovementValidator.Validate(new Position(0f, 0f, -998f, 0f), movement, 1000).Accepted);
    }

    /// <summary>An unknown floor is not treated as a floor at zero.</summary>
    [Fact]
    public void WhereTheFloorIsUnknown_NothingIsRefused()
    {
        MovementInfo movement = At(0f, 0f, -1000f);

        MovementVerdict verdict = MovementValidator.Validate(
            new Position(0f, 0f, -998f, 0f), movement, 1000, (_, _, _) => null);

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void FarBelowTheFloor_IsRefused()
    {
        MovementInfo movement = At(0f, 0f, -100f);

        MovementVerdict verdict = MovementValidator.Validate(
            new Position(0f, 0f, -98f, 0f), movement, 1000, (_, _, _) => 0f);

        Assert.False(verdict.Accepted);
        Assert.Equal(MovementRejection.UnderTheWorld, verdict.Rejection);
    }

    /// <summary>
    /// Being above the floor is never refused, however far.
    /// </summary>
    /// <remarks>
    /// Jumping, falling, a flying mount and a lift all put a player above the floor. Refusing any of
    /// them disconnects an honest player, which is worse than missing a cheat.
    /// </remarks>
    [Theory]
    [InlineData(1f)]
    [InlineData(50f)]
    [InlineData(500f)]
    public void AboveTheFloor_IsAlwaysAccepted(float height)
    {
        MovementInfo movement = At(0f, 0f, height);

        MovementVerdict verdict = MovementValidator.Validate(
            new Position(0f, 0f, height, 0f), movement, 1000, (_, _, _) => 0f);

        Assert.True(verdict.Accepted);
    }

    /// <summary>
    /// A little below the floor is tolerated, because real geometry sits above walkable places.
    /// </summary>
    /// <remarks>
    /// Cave mouths, dungeon entrances and the underside of a city all put a model surface above a
    /// position a player can legitimately occupy. The threshold is sized to catch someone who has
    /// left the world, not to measure their feet.
    /// </remarks>
    [Fact]
    public void JustBelowTheFloor_IsTolerated()
    {
        float justAbove = -MovementValidator.MaxDepthBelowFloor + 1f;
        MovementInfo movement = At(0f, 0f, justAbove);

        MovementVerdict verdict = MovementValidator.Validate(
            new Position(0f, 0f, justAbove, 0f), movement, 1000, (_, _, _) => 0f);

        Assert.True(verdict.Accepted);
    }

    /// <summary>The cheap checks run first; a teleport is refused as a teleport, not a depth.</summary>
    [Fact]
    public void ACheaperRejection_WinsOverTheHeightCheck()
    {
        MovementInfo movement = At(5000f, 0f, -1000f);

        MovementVerdict verdict = MovementValidator.Validate(
            new Position(0f, 0f, 0f, 0f), movement, 1000, (_, _, _) => 0f);

        Assert.False(verdict.Accepted);
        Assert.Equal(MovementRejection.Teleport, verdict.Rejection);
    }

    private static MovementInfo At(float x, float y, float z) =>
        new() { Position = new Position(x, y, z, 0f) };
}
