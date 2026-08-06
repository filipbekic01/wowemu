using WowEmu.Core;
using WowEmu.Data.Db;

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
/// Where a generator wants the creature to go, and how fast.
/// </summary>
/// <param name="Destination">The point to head for.</param>
/// <param name="Run">
/// Whether to run rather than walk. A wandering animal ambles; a patrol on a run-flagged leg does
/// not, and 7,299 of the 112,797 stored waypoints are flagged that way.
/// </param>
public readonly record struct MovementDecision(Position Destination, bool Run);

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
    public bool TryGetDestination(Creature creature, uint diffMs, out MovementDecision decision)
    {
        decision = default;

        return _slots.Count > 0 && _slots.Peek().TryGetDestination(creature, diffMs, out decision);
    }

    /// <summary>
    /// Tells the top generator the creature has arrived, and removes it if it is finished.
    /// </summary>
    /// <remarks>
    /// The only way a pushed generator ever comes off the stack. Going home is the first thing that
    /// needs it: the creature heads for its spawn point, reaches it, and the route or wander it was
    /// doing before the fight resumes on its own.
    /// </remarks>
    public void NotifyArrived(Creature creature)
    {
        if (_slots.Count > 0 && _slots.Peek().OnArrived(creature))
        {
            _slots.Pop();
        }
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
    /// <param name="decision">Where to go, and whether to run.</param>
    /// <returns>False to leave the creature where it is.</returns>
    bool TryGetDestination(Creature creature, uint diffMs, out MovementDecision decision);

    /// <summary>
    /// Called when the creature finishes a move this generator asked for.
    /// </summary>
    /// <returns>True when the generator is done and should be taken off the stack.</returns>
    /// <remarks>
    /// Default false: a generator that runs forever — idling, wandering, patrolling — is never
    /// finished, and only a temporary one pushed on top of it has anywhere to go afterwards.
    /// </remarks>
    bool OnArrived(Creature creature) => false;
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

    public bool TryGetDestination(Creature creature, uint diffMs, out MovementDecision decision)
    {
        decision = default;
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

    public bool TryGetDestination(Creature creature, uint diffMs, out MovementDecision decision)
    {
        ArgumentNullException.ThrowIfNull(creature);

        decision = default;

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

        float x = home.X + (radius * MathF.Cos(angle));
        float y = home.Y + (radius * MathF.Sin(angle));

        // Drawn now rather than on arrival, so one draw covers the whole walk-and-wait cycle and the
        // generator needs no arrival callback to stay in step.
        _waitRemainingMs = GameRandom.Urand(MinWaitMs, MaxWaitMs);

        float z = GroundAt(creature, x, y, home.Z);

        decision = new MovementDecision(new Position(x, y, z, 0f), Run: false);
        return true;
    }

    /// <summary>
    /// The height a creature would stand at over a point, if it may stand there at all.
    /// </summary>
    /// <remarks>
    /// The spawn's own height is not good enough. A wander radius is small — 52,426 of the 77,138
    /// wandering spawns stay within five yards — but 1,946 of them roam more than twenty, and on any
    /// slope reusing the spawn's Z buries the creature in the hillside or floats it above one by the
    /// full height difference.
    /// <para>
    /// <b>No answer falls back to the spawn height rather than refusing.</b> That was not the first
    /// choice: refusing looks safer, and it is — right up until the server is run without extracted
    /// client data, which a fresh clone always is, because <c>data/</c> is three gigabytes and not
    /// committed. Every wandering creature in the world would then stand still, silently. The rest
    /// of this codebase makes the same call in the same direction — a missing collision file leaves
    /// line of sight clear rather than blinding the world — so a missing height leaves the creature
    /// wandering at its spawn height, exactly as it did before heights were consulted at all.
    /// </para>
    /// <para>
    /// The cost is that a genuine hole in the terrain is indistinguishable from an absent tile, and
    /// a creature wandering over one keeps its spawn height. That is the old behaviour and no worse.
    /// </para>
    /// </remarks>
    private static float GroundAt(Creature creature, float x, float y, float fallbackZ)
    {
        // Searched from the spawn's own height: terrain answers regardless of where the search
        // starts, and a model's floor is only found within a few yards of it — which is all a
        // wander radius covers.
        return creature.FloorAt?.Invoke(x, y, fallbackZ) ?? fallbackZ;
    }

    /// <summary>Upstream's <c>urand(500, 10000)</c> pause between wanders, in milliseconds.</summary>
    public const uint MinWaitMs = 500;

    /// <inheritdoc cref="MinWaitMs"/>
    public const uint MaxWaitMs = 10_000;
}

/// <summary>
/// A creature that walks a fixed route, over and over.
/// </summary>
/// <remarks>
/// Port of <c>WaypointMovementGenerator&lt;Creature&gt;</c>. 5,290 spawns use it — the guards
/// pacing a wall, the patrols circling a camp, the boats and zeppelins' crews. Before this they
/// stood exactly where the database put them.
/// <para>
/// The route is shared and never mutated: one path can be walked by several spawns at once, and the
/// only per-creature state is where along it this one has got to.
/// </para>
/// </remarks>
public sealed class WaypointMovementGenerator(IReadOnlyList<Waypoint> path) : IMovementGenerator
{
    private int _next;
    private uint _pauseRemainingMs;

    /// <summary>The route, in the order it is walked.</summary>
    public IReadOnlyList<Waypoint> Path { get; } = path;

    /// <summary>Which point the creature is heading for, or has just reached.</summary>
    public int NextIndex => _next;

    public MovementGeneratorType Type => MovementGeneratorType.Waypoint;

    /// <summary>
    /// Picks the next point, unless the creature is still waiting at the last one.
    /// </summary>
    /// <remarks>
    /// <b>The wait is armed when the move is issued, not when it completes.</b> This is only called
    /// once a move has finished — a creature mid-walk never reaches here — so a pause armed at issue
    /// time is first counted down on the call after arrival, which is exactly when it should be.
    /// <para>
    /// Starts at point 0 wherever the creature happens to be standing, so its first leg is a walk
    /// from its spawn point to the start of the route. Upstream instead resumes from the
    /// <c>creature.currentwaypoint</c> column, which we do not read; the difference is one leg, once,
    /// at startup.
    /// </para>
    /// </remarks>
    public bool TryGetDestination(Creature creature, uint diffMs, out MovementDecision decision)
    {
        decision = default;

        if (Path.Count == 0)
        {
            return false;
        }

        // The same shape the wander generator uses, and upstream's: the tick that exhausts the wait
        // is the tick that moves. Burning a further call to notice the wait had ended would add a
        // whole tick of standing still to every pause on every patrol.
        if (_pauseRemainingMs > diffMs)
        {
            _pauseRemainingMs -= diffMs;
            return false;
        }

        _pauseRemainingMs = 0;

        Waypoint point = Path[_next];

        // Wraps, because a patrol is a loop: upstream's creature paths are all repeating, and one
        // that stopped at the end would leave a guard standing at the far end of its beat forever.
        _next = (_next + 1) % Path.Count;

        _pauseRemainingMs = point.DelayMs;

        decision = new MovementDecision(point.Position, point.IsRun);
        return true;
    }
}

/// <summary>
/// A creature walking back to where it spawned after giving up on a fight.
/// </summary>
/// <remarks>
/// Port of <c>HomeMovementGenerator</c>. Pushed on top of whatever the creature was doing, and pops
/// itself the moment it arrives — so the wander or the patrol it was interrupted from resumes on its
/// own, with no one having to remember what it was.
/// <para>
/// It runs. A creature that has just disengaged jogs back rather than ambling, which is both what
/// upstream does and what stops a player kiting it away and strolling alongside it.
/// </para>
/// </remarks>
public sealed class HomeMovementGenerator : IMovementGenerator
{
    private bool _issued;

    public MovementGeneratorType Type => MovementGeneratorType.Home;

    public bool TryGetDestination(Creature creature, uint diffMs, out MovementDecision decision)
    {
        ArgumentNullException.ThrowIfNull(creature);

        decision = default;

        if (_issued)
        {
            return false;
        }

        _issued = true;
        decision = new MovementDecision(creature.HomePosition, Run: true);

        return true;
    }

    /// <summary>
    /// Arriving home ends the generator — but only once it has actually set off.
    /// </summary>
    /// <remarks>
    /// The <c>_issued</c> guard matters: a creature that evades while standing on its own spawn
    /// point has no distance to cover, so the move is refused as pointless and the arrival fires
    /// against a generator that never issued anything. Popping on that would be right; popping on an
    /// unrelated earlier arrival would leave the creature never going home at all.
    /// </remarks>
    public bool OnArrived(Creature creature) => _issued;
}
