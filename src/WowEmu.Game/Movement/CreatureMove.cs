using WowEmu.Core;

namespace WowEmu.Game.Movement;

/// <summary>
/// One straight-line move in progress: where it started, where it ends, and how far through it is.
/// </summary>
/// <remarks>
/// The subset of upstream's <c>MoveSpline</c> that a wandering creature needs. Upstream evaluates a
/// Catmull-Rom spline over an arbitrary path; this evaluates a line between two points. Every
/// generator this phase has produces exactly that, and a spline of two points <i>is</i> a line — so
/// the difference only becomes real when waypoints and flight paths arrive.
/// <para>
/// A struct because a creature holds one and it is rewritten on every new move. It is immutable and
/// the elapsed time lives on the creature, so evaluating it twice at the same moment gives the same
/// answer.
/// </para>
/// </remarks>
public readonly record struct CreatureMove(Position Start, Position Destination, uint DurationMs)
{
    /// <summary>Whether there is a move at all.</summary>
    public bool IsMoving => DurationMs > 0;

    /// <summary>
    /// Where the object is after <paramref name="elapsedMs"/> of the move.
    /// </summary>
    /// <remarks>
    /// The orientation faces along the path, computed once from the endpoints rather than per step:
    /// a straight line does not turn, and recomputing it from successive positions produces jitter
    /// as the two points converge at the end.
    /// </remarks>
    public Position At(uint elapsedMs)
    {
        if (!IsMoving || elapsedMs >= DurationMs)
        {
            return new Position(Destination.X, Destination.Y, Destination.Z, Facing);
        }

        float progress = elapsedMs / (float)DurationMs;

        return new Position(
            Start.X + ((Destination.X - Start.X) * progress),
            Start.Y + ((Destination.Y - Start.Y) * progress),
            Start.Z + ((Destination.Z - Start.Z) * progress),
            Facing);
    }

    /// <summary>Which way the object faces while moving: along the line it is walking.</summary>
    public float Facing => MathF.Atan2(Destination.Y - Start.Y, Destination.X - Start.X);

    /// <summary>
    /// Builds a move at a given speed, or nothing if the distance is not worth moving.
    /// </summary>
    /// <remarks>
    /// The floor matters. A destination a few centimetres away produces a duration of zero, and a
    /// zero-duration move tells the client to arrive instantly — which reads as a twitch, repeated
    /// every time the generator picks a nearby point.
    /// </remarks>
    public static CreatureMove? Create(Position start, Position destination, float speed)
    {
        if (speed <= 0f)
        {
            return null;
        }

        float distance = start.GetExactDist(destination);

        if (distance < MinimumDistance)
        {
            return null;
        }

        uint duration = (uint)MathF.Ceiling(distance / speed * 1000f);

        return duration == 0 ? null : new CreatureMove(start, destination, duration);
    }

    /// <summary>Shorter than this and the move is not worth making, in yards.</summary>
    public const float MinimumDistance = 0.5f;
}
