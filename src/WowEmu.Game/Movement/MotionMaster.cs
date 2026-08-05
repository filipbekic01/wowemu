using WowEmu.Core;

namespace WowEmu.Game.Movement;

/// <summary>
/// The kinds of movement a creature can be doing, from <c>MotionMaster.h</c>.
/// </summary>
/// <remarks>
/// The numbers are the <c>creature.MovementType</c> column's, so a spawn row's value maps straight
/// onto this. Only the first three are produced; the rest are named so that a row carrying one is
/// recognisable in a log rather than an unexplained integer.
/// </remarks>
public enum MovementGeneratorType : byte
{
    Idle = 0,
    Random = 1,
    Waypoint = 2,
    Confused = 4,
    Chase = 5,
    Home = 6,
    Flight = 7,
    Point = 8,
    Fleeing = 9,
    Distract = 10,
    Follow = 12,
}

/// <summary>
/// Decides where a creature is trying to go.
/// </summary>
/// <remarks>
/// Port of the part of <c>MotionMaster</c> that M4 needs. Upstream keeps a stack of generators in
/// numbered slots, so that a creature fleeing on top of a waypoint route resumes the route when the
/// fear ends. There is nothing yet that can interrupt anything, so this holds one generator and the
/// default it falls back to — but it is shaped as a stack, because the first thing that pushes onto
/// it is combat, and retrofitting the stack later means revisiting every generator.
/// <para>
/// A generator is asked for a destination and says whether it has one. Everything about actually
/// getting there — the move, the packet, the position updates — belongs to the creature, so a
/// generator stays a decision and never a side effect.
/// </para>
/// </remarks>
public sealed class MotionMaster(MovementGeneratorType defaultType)
{
    private readonly Stack<IMovementGenerator> _slots = [];

    /// <summary>What the creature does when nothing else is asking for its attention.</summary>
    public MovementGeneratorType DefaultType { get; } = defaultType;

    /// <summary>What is currently deciding where the creature goes.</summary>
    public MovementGeneratorType CurrentType => _slots.Count > 0 ? _slots.Peek().Type : DefaultType;

    /// <summary>Pushes a generator on top of whatever is running.</summary>
    public void Push(IMovementGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        _slots.Push(generator);
    }

    /// <summary>Removes the top generator, revealing whatever it interrupted.</summary>
    public void Pop()
    {
        if (_slots.Count > 0)
        {
            _slots.Pop();
        }
    }

    /// <summary>Sets the generator for the creature's default behaviour.</summary>
    public void Initialize(IMovementGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);

        _slots.Clear();
        _slots.Push(generator);
    }

    /// <summary>
    /// Asks the current generator where the creature should go next.
    /// </summary>
    /// <returns>False when it has nowhere for the creature to be, which is the usual answer.</returns>
    public bool TryGetDestination(Creature creature, uint diffMs, out Position destination)
    {
        destination = default;

        return _slots.Count > 0 && _slots.Peek().TryGetDestination(creature, diffMs, out destination);
    }
}

/// <summary>Decides where a creature goes next.</summary>
public interface IMovementGenerator
{
    /// <summary>Which kind this is, for logs and for <see cref="MotionMaster.CurrentType"/>.</summary>
    MovementGeneratorType Type { get; }

    /// <summary>
    /// Where the creature should head, if anywhere.
    /// </summary>
    /// <param name="creature">The creature deciding. Read, never moved.</param>
    /// <param name="diffMs">Milliseconds since the last call, for generators that wait.</param>
    /// <param name="destination">Where to go.</param>
    /// <returns>False to leave the creature where it is.</returns>
    bool TryGetDestination(Creature creature, uint diffMs, out Position destination);
}

/// <summary>A creature that stays exactly where it was put.</summary>
/// <remarks>
/// The default for the ~68,000 spawns whose <c>MovementType</c> is 0 — guards at posts, vendors
/// behind counters, and everything scripted to stand still.
/// </remarks>
public sealed class IdleMovementGenerator : IMovementGenerator
{
    /// <summary>There is no state, so one instance serves every idle creature.</summary>
    public static IdleMovementGenerator Instance { get; } = new();

    public MovementGeneratorType Type => MovementGeneratorType.Idle;

    public bool TryGetDestination(Creature creature, uint diffMs, out Position destination)
    {
        destination = default;
        return false;
    }
}

/// <summary>
/// A creature that wanders around where it spawned.
/// </summary>
/// <remarks>
/// Port of <c>RandomMovementGenerator</c>, which 77,138 spawns use. It picks a point inside the
/// spawn's wander radius, walks there, then waits before choosing again.
/// <para>
/// The pause is what makes it read as an animal rather than a machine. Upstream draws it from
/// <c>urand(500, 10000)</c> once the creature arrives; without it a creature walks continuously and
/// looks driven.
/// </para>
/// </remarks>
public sealed class RandomMovementGenerator(float wanderDistance) : IMovementGenerator
{
    private uint _waitRemainingMs;

    /// <summary>How far from home it may stray, in yards.</summary>
    public float WanderDistance { get; } = wanderDistance;

    public MovementGeneratorType Type => MovementGeneratorType.Random;

    public bool TryGetDestination(Creature creature, uint diffMs, out Position destination)
    {
        ArgumentNullException.ThrowIfNull(creature);

        destination = default;

        if (_waitRemainingMs > diffMs)
        {
            _waitRemainingMs -= diffMs;
            return false;
        }

        _waitRemainingMs = 0;

        if (WanderDistance <= 0f)
        {
            return false;
        }

        // Uniform over the disc, not over (angle, radius): drawing the radius uniformly clusters
        // creatures around their spawn point, because the same radius range covers less area near
        // the centre than at the edge.
        float angle = GameRandom.Frand(0f, MathF.Tau);
        float radius = WanderDistance * MathF.Sqrt(GameRandom.Frand(0f, 1f));

        Position home = creature.HomePosition;

        destination = new Position(
            home.X + (radius * MathF.Cos(angle)),
            home.Y + (radius * MathF.Sin(angle)),

            // Terrain height is not consulted. Without vmaps a creature on a bridge or indoors would
            // be dropped through the floor, which is the same reason movement validation does not
            // check height yet — see MovementValidator and TODO.md.
            home.Z,
            0f);

        // Drawn now rather than on arrival, so one draw covers the whole walk-and-wait cycle and the
        // generator needs no arrival callback to stay in step.
        _waitRemainingMs = GameRandom.Urand(MinWaitMs, MaxWaitMs);

        return true;
    }

    /// <summary>Upstream's <c>urand(500, 10000)</c> pause between wanders, in milliseconds.</summary>
    public const uint MinWaitMs = 500;

    /// <inheritdoc cref="MinWaitMs"/>
    public const uint MaxWaitMs = 10_000;
}
