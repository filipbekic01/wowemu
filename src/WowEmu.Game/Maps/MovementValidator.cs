using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Protocol;

namespace WowEmu.Game.Maps;

/// <summary>Why a movement packet was refused.</summary>
public enum MovementRejection
{
    None = 0,

    /// <summary>A coordinate was NaN, infinite, or outside the map.</summary>
    InvalidCoordinate,

    /// <summary>Flags that cannot both be true at once.</summary>
    ContradictoryFlags,

    /// <summary>Moved further than any speed could carry the player in the elapsed time.</summary>
    ImpossibleSpeed,

    /// <summary>Jumped further in one packet than the protocol can explain.</summary>
    Teleport,

    /// <summary>Reported a position far below any ground or model at that spot.</summary>
    UnderTheWorld,
}

/// <summary>The verdict on one movement packet.</summary>
public readonly record struct MovementVerdict(MovementRejection Rejection, string? Detail)
{
    public bool Accepted => Rejection == MovementRejection.None;

    public static MovementVerdict Accept() => new(MovementRejection.None, null);

    public static MovementVerdict Reject(MovementRejection rejection, string detail) => new(rejection, detail);
}

/// <summary>
/// Checks that a movement packet could plausibly have happened.
/// </summary>
/// <remarks>
/// The client computes its own movement against its own copy of the terrain, so the server's job is
/// not to move the player — it is to notice when the client is lying. Every teleport, speed and fly
/// hack lives in the gap between "the client says so" and "the server checked".
/// <para>
/// <b>Height is checked in one direction only, on purpose.</b> Being far <i>below</i> the floor is
/// unambiguous — there is nothing to stand on down there. Being above it is ordinary: jumping,
/// falling, a flying mount, a lift. So this refuses a player under the world and says nothing about
/// one in the air.
/// </para>
/// <para>
/// The symmetric test — does the reported Z <i>match</i> the floor? — is still not made, and vmaps
/// did not make it safe. It would have to know about transports, lifts, and the moment between
/// leaving a surface and the fall being reported; each of those is an honest player disconnected.
/// </para>
/// <para>
/// Swimming is still not checked here, and this one is now a choice rather than a blocker. Both
/// kinds of water can be asked about — <see cref="WorldLiquid"/> answers for terrain and models
/// together — so "claims to be swimming where there is no liquid" is finally a question with a real
/// answer. What is missing is the margin: a client reports its position on its own schedule, and a
/// player who has just jumped clear of a lake is legitimately dry at a position it sends while still
/// flagged as swimming. Refusing that is a disconnect for playing normally. The rule wants a
/// tolerance in time and distance, which is a piece of design rather than a lookup, and it belongs
/// with the fall-damage work that needs the same state.
/// </para>
/// <para>
/// So the checks here are the ones that are exactly right rather than nearly right: coordinates
/// that cannot exist, flag combinations that contradict themselves, distances that no speed
/// explains, and positions with the whole world above them.
/// </para>
/// </remarks>
public sealed class MovementValidator
{
    /// <summary>Half the map's width, less a margin. Upstream's bound.</summary>
    public const float MapHalfSize = (MapGeometry.GridSize * MapGeometry.GridsPerAxis / 2f) - 0.5f;

    /// <summary>
    /// Fastest anything moves in 3.3.5a, with headroom.
    /// </summary>
    /// <remarks>
    /// Base run is 7 yards/second; the fastest flying mount is 31.5. The ceiling here is far above
    /// both because it exists to catch teleports, not to police speed buffs — that needs the
    /// server to track applied auras, which no phase has built.
    /// </remarks>
    public const float MaxPlausibleSpeed = 60.0f;

    /// <summary>
    /// Largest jump accepted in one packet regardless of elapsed time.
    /// </summary>
    /// <remarks>
    /// A client that has been asleep can legitimately report a long gap, so the speed check alone
    /// would let an arbitrarily large jump through by claiming enough time passed. This is the
    /// backstop.
    /// </remarks>
    public const float MaxSingleStep = 150.0f;

    /// <summary>
    /// How far below the floor a position may be before it is refused.
    /// </summary>
    /// <remarks>
    /// Generous, and deliberately so. The floor is the higher of terrain and models, and there are
    /// real places — cave mouths, dungeon entrances, the underside of a city — where geometry sits
    /// above a position a player can legitimately occupy. This is sized to catch a player who has
    /// left the world entirely, not to measure their feet.
    /// </remarks>
    public const float MaxDepthBelowFloor = 25.0f;

    /// <summary>Flags that mean the player is moving under its own power.</summary>
    private const MovementFlag MovingFlags =
        MovementFlag.Forward | MovementFlag.Backward |
        MovementFlag.StrafeLeft | MovementFlag.StrafeRight;

    /// <summary>
    /// Judges a movement packet against the player's last known state.
    /// </summary>
    /// <param name="previous">Where the player was.</param>
    /// <param name="movement">What the client now claims.</param>
    /// <param name="elapsedMilliseconds">
    /// Time since the last accepted packet, by the server's clock. Server-side on purpose: the
    /// client's own timestamp is exactly the thing a speed hack would forge.
    /// </param>
    /// <param name="floorAt">
    /// The surface under a position, or null where nothing is known. Optional: without it the height
    /// check is skipped rather than guessed at.
    /// </param>
    public static MovementVerdict Validate(
        Position previous,
        MovementInfo movement,
        uint elapsedMilliseconds,
        Func<float, float, float, float?>? floorAt = null)
    {
        ArgumentNullException.ThrowIfNull(movement);

        Position position = movement.Position;

        if (!IsValidCoordinate(position.X) || !IsValidCoordinate(position.Y) ||
            !IsValidCoordinate(position.Z) || !float.IsFinite(position.Orientation))
        {
            // A NaN here would poison the cell arithmetic and then get written to the database.
            return MovementVerdict.Reject(
                MovementRejection.InvalidCoordinate,
                $"({position.X}, {position.Y}, {position.Z})");
        }

        if (movement.Flags.HasFlag(MovementFlag.Falling) && movement.Flags.HasFlag(MovementFlag.Swimming))
        {
            return MovementVerdict.Reject(MovementRejection.ContradictoryFlags, "falling while swimming");
        }

        float distance = previous.GetExactDist(position);

        if (distance > MaxSingleStep)
        {
            return MovementVerdict.Reject(
                MovementRejection.Teleport,
                $"{distance:F1} yards in one packet");
        }

        // A first packet, or one after a long stall, has no meaningful elapsed time to judge.
        if (elapsedMilliseconds is > 0 and < 10_000)
        {
            float seconds = elapsedMilliseconds / 1000f;
            float speed = distance / seconds;

            if (speed > MaxPlausibleSpeed)
            {
                return MovementVerdict.Reject(
                    MovementRejection.ImpossibleSpeed,
                    $"{speed:F1} yards/second over {seconds:F2}s");
            }
        }

        // Last, because it is the only check that reads a file. A packet that fails one of the
        // cheap tests above never reaches it.
        if (floorAt is not null && floorAt(position.X, position.Y, position.Z) is { } floor)
        {
            float depth = floor - position.Z;

            if (depth > MaxDepthBelowFloor)
            {
                return MovementVerdict.Reject(
                    MovementRejection.UnderTheWorld,
                    $"{depth:F1} yards below the floor at {floor:F1}");
            }
        }

        return MovementVerdict.Accept();
    }

    private static bool IsValidCoordinate(float value) =>
        float.IsFinite(value) && Math.Abs(value) <= MapHalfSize;
}
