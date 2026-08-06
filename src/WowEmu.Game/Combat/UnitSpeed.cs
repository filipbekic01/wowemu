using WowEmu.Protocol;

namespace WowEmu.Game.Combat;

/// <summary>Which speed is being asked about. <c>UnitMoveType</c>, in the client's order.</summary>
/// <remarks>
/// The order matters twice over: it is the order the nine floats appear in a create block, and it
/// indexes the opcode tables the client is told about changes on.
/// </remarks>
public enum UnitMoveType
{
    Walk = 0,
    Run = 1,
    RunBack = 2,
    Swim = 3,
    SwimBack = 4,
    TurnRate = 5,
    Flight = 6,
    FlightBack = 7,
    PitchRate = 8,
}

/// <summary>
/// How fast a unit moves, once its auras have had their say.
/// </summary>
/// <remarks>
/// Port of the reachable part of <c>Unit::UpdateSpeed</c>. Mounts, vehicles, pets and flight forms
/// are all absent, which removes most of the switch — what is left is the part every unit goes
/// through, and it is the part with the two traps in it.
/// <para>
/// <b>Speed buffs do not stack and slows do not either.</b> Upstream takes the single strongest of
/// each — <c>GetMaxPositiveAuraModifier</c> and <c>GetMaxNegativeAuraModifier</c> — rather than
/// adding them up. Summing is the obvious reading and gives a rogue with two slows on them a
/// negative speed.
/// <para>
/// <b>The buff is applied before the slow, and both are percentages of the running total.</b> A 50%
/// buff and a 50% slow do not cancel: <c>1.5 × 0.5 = 0.75</c>. Applying them in one step, or against
/// the base rather than the total, gives 1.0 and a noticeably different game.
/// </para>
/// </para>
/// </remarks>
public static class UnitSpeed
{
    /// <summary>
    /// The speeds a unit starts from, before anything modifies them. <c>baseMoveSpeed</c>.
    /// </summary>
    /// <remarks>
    /// Upstream keeps a second table for player-controlled units, and in 3.3.5a every entry is
    /// identical to this one — so there is one table here, and a comment rather than a copy.
    /// </remarks>
    public const float BaseWalk = 2.5f;

    /// <inheritdoc cref="BaseWalk"/>
    public const float BaseRun = 7.0f;

    /// <inheritdoc cref="BaseWalk"/>
    public const float BaseRunBack = 4.5f;

    /// <inheritdoc cref="BaseWalk"/>
    public const float BaseSwim = 4.722222f;

    /// <inheritdoc cref="BaseWalk"/>
    public const float BaseSwimBack = 2.5f;

    /// <inheritdoc cref="BaseWalk"/>
    public const float BaseFlight = 7.0f;

    /// <inheritdoc cref="BaseWalk"/>
    public const float BaseFlightBack = 4.5f;

    /// <summary>The base speed for a move type.</summary>
    public static float BaseFor(UnitMoveType type) => type switch
    {
        UnitMoveType.Walk => BaseWalk,
        UnitMoveType.Run => BaseRun,
        UnitMoveType.RunBack => BaseRunBack,
        UnitMoveType.Swim => BaseSwim,
        UnitMoveType.SwimBack => BaseSwimBack,
        UnitMoveType.Flight => BaseFlight,
        UnitMoveType.FlightBack => BaseFlightBack,
        _ => 1.0f,
    };

    /// <summary>
    /// The rate a unit moves at, as a multiple of the base speed.
    /// </summary>
    /// <param name="auras">What is on the unit.</param>
    /// <param name="type">Which speed.</param>
    /// <remarks>
    /// Only forward run and swim take speed <i>buffs</i>. Walking and the three backwards speeds
    /// take debuffs only — upstream's switch falls straight through for them — so a Sprinting player
    /// walks at the ordinary pace and a slowed one walks slowly.
    /// </remarks>
    public static float RateFor(AuraContainer auras, UnitMoveType type)
    {
        ArgumentNullException.ThrowIfNull(auras);

        float rate = 1.0f;

        // Buffs reach forward movement only. A backwards or walking speed is never increased by
        // one, which is upstream's behaviour and not an omission.
        if (type is UnitMoveType.Run or UnitMoveType.Swim or UnitMoveType.Flight)
        {
            int increase = auras.MaxPositive(AuraType.ModIncreaseSpeed);

            if (increase != 0)
            {
                rate += rate * (increase / 100f);
            }
        }

        int slow = auras.MaxNegative(AuraType.ModDecreaseSpeed);

        if (slow != 0)
        {
            rate += rate * (slow / 100f);
        }

        // A slow past 100% would send the unit backwards. Upstream clamps in SetSpeed for the same
        // reason, and the client draws a negative speed as an immediate desync.
        return MathF.Max(rate, 0f);
    }

    /// <summary>Reads one of the nine speeds off a set.</summary>
    public static float Read(MovementSpeeds speeds, UnitMoveType type)
    {
        ArgumentNullException.ThrowIfNull(speeds);

        return type switch
        {
            UnitMoveType.Walk => speeds.Walk,
            UnitMoveType.Run => speeds.Run,
            UnitMoveType.RunBack => speeds.RunBack,
            UnitMoveType.Swim => speeds.Swim,
            UnitMoveType.SwimBack => speeds.SwimBack,
            UnitMoveType.Flight => speeds.Flight,
            UnitMoveType.FlightBack => speeds.FlightBack,
            UnitMoveType.TurnRate => speeds.TurnRate,
            _ => speeds.PitchRate,
        };
    }

    private static void Write(MovementSpeeds speeds, UnitMoveType type, float value)
    {
        switch (type)
        {
            case UnitMoveType.Walk: speeds.Walk = value; break;
            case UnitMoveType.Run: speeds.Run = value; break;
            case UnitMoveType.RunBack: speeds.RunBack = value; break;
            case UnitMoveType.Swim: speeds.Swim = value; break;
            case UnitMoveType.SwimBack: speeds.SwimBack = value; break;
            case UnitMoveType.Flight: speeds.Flight = value; break;
            case UnitMoveType.FlightBack: speeds.FlightBack = value; break;
            default: break;
        }
    }

    /// <summary>The seven speeds auras can touch. Turn and pitch rates are not speeds.</summary>
    public static IReadOnlyList<UnitMoveType> Modifiable { get; } =
    [
        UnitMoveType.Walk,
        UnitMoveType.Run,
        UnitMoveType.RunBack,
        UnitMoveType.Swim,
        UnitMoveType.SwimBack,
        UnitMoveType.Flight,
        UnitMoveType.FlightBack,
    ];

    /// <summary>
    /// Rewrites a unit's speeds from its unmodified ones and its auras, and says which moved.
    /// </summary>
    /// <param name="speeds">The live speeds, as sent to clients. Overwritten.</param>
    /// <param name="baseSpeeds">
    /// What the unit moves at with nothing on it. <b>Not the global base speeds</b> — a creature's
    /// template scales its walk and run before anything else does, so recomputing from the global
    /// values would give every wolf in the game a human's pace the first time it was slowed.
    /// </param>
    /// <remarks>
    /// Returns the changed ones rather than all seven so the caller sends packets only for what
    /// actually differs. A recompute runs on every aura application and most change nothing.
    /// </remarks>
    public static IReadOnlyList<UnitMoveType> Refresh(
        MovementSpeeds speeds,
        MovementSpeeds baseSpeeds,
        AuraContainer auras)
    {
        ArgumentNullException.ThrowIfNull(speeds);
        ArgumentNullException.ThrowIfNull(baseSpeeds);
        ArgumentNullException.ThrowIfNull(auras);

        List<UnitMoveType> changed = [];

        foreach (UnitMoveType type in Modifiable)
        {
            float updated = Read(baseSpeeds, type) * RateFor(auras, type);

            if (MathF.Abs(Read(speeds, type) - updated) > 0.0001f)
            {
                Write(speeds, type, updated);
                changed.Add(type);
            }
        }

        return changed;
    }
}
