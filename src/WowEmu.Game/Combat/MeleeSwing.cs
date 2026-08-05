namespace WowEmu.Game.Combat;

/// <summary>Why a swing did not land. <c>SMSG_ATTACKSWING_*</c>.</summary>
public enum SwingError : byte
{
    /// <summary>The swing landed, or there was nothing to swing at.</summary>
    None = 0,

    /// <summary>Too far away.</summary>
    NotInRange = 1,

    /// <summary>The target is behind the attacker.</summary>
    BadFacing = 2,
}

/// <summary>What one tick of auto-attack did.</summary>
/// <param name="Swung">Whether a swing actually landed.</param>
/// <param name="Damage">The swing's result. Meaningless unless <paramref name="Swung"/>.</param>
/// <param name="Error">Why nothing landed, if nothing did.</param>
public readonly record struct SwingResult(bool Swung, MeleeDamageInfo Damage, SwingError Error)
{
    /// <summary>Nothing happened this tick — the weapon is still on cooldown.</summary>
    public static SwingResult Waiting => default;
}

/// <summary>
/// Drives one unit's auto-attack: when it swings, and what stops it.
/// </summary>
/// <remarks>
/// Port of the melee block in <c>Player::Update</c> and <c>Creature::Update</c>.
/// <para>
/// <b>A failed swing does not spend the weapon's timer.</b> When the target is out of range or
/// behind the attacker, the timer is set to a short retry rather than the weapon speed. Resetting it
/// properly would mean chasing a fleeing target costs a full swing every time you fall behind,
/// which is the difference between combat that feels responsive and combat that feels broken.
/// </para>
/// </remarks>
public static class MeleeSwing
{
    /// <summary>
    /// The arc a target must be inside for the attacker to swing. 120°, as two thirds of π either
    /// side of straight ahead.
    /// </summary>
    public const float FacingArc = 2f * MathF.PI / 3f;

    /// <summary>
    /// Advances one weapon's auto-attack by a tick.
    /// </summary>
    /// <remarks>
    /// The timer is counted down by the caller, before this runs — <see cref="Unit.UpdateAttackTimers"/>
    /// — so that a unit's timers advance whether or not it is attacking anything.
    /// </remarks>
    /// <param name="attacker">Who is swinging.</param>
    /// <param name="attackType">Which weapon.</param>
    /// <param name="roll">The random source for the damage roll and the attack table.</param>
    public static SwingResult Advance(Unit attacker, WeaponAttackType attackType, Func<uint, uint, uint> roll)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(roll);

        if (!attacker.IsMeleeAttacking || attacker.Victim is not { } victim)
        {
            return SwingResult.Waiting;
        }

        if (!attacker.IsAlive || !victim.IsAlive)
        {
            return SwingResult.Waiting;
        }

        if (!attacker.IsAttackReady(attackType))
        {
            return SwingResult.Waiting;
        }

        if (!attacker.IsWithinMeleeRange(victim))
        {
            attacker.SetAttackTimer(attackType, UnitDefaults.SwingRetryDelayMs);

            return new SwingResult(false, default, SwingError.NotInRange);
        }

        if (!IsFacing(attacker, victim))
        {
            attacker.SetAttackTimer(attackType, UnitDefaults.SwingRetryDelayMs);

            return new SwingResult(false, default, SwingError.BadFacing);
        }

        MeleeDamageInfo damage = attacker.CalculateMeleeDamage(
            victim, attackType, roll, attackerIsBehindVictim: IsBehind(attacker, victim));

        attacker.ResetAttackTimer(attackType);

        // A melee swing spends the ranged timer too, so that swapping to a bow mid-fight does not
        // fire for free off a timer that has been ticking down all along.
        if (attackType == WeaponAttackType.BaseAttack)
        {
            attacker.ResetAttackTimer(WeaponAttackType.RangedAttack);
        }

        return new SwingResult(true, damage, SwingError.None);
    }

    /// <summary>
    /// Whether the attacker is pointed close enough at the target to swing.
    /// </summary>
    /// <remarks>
    /// Skipped entirely when the two are inside each other's bounding radius — at that distance the
    /// angle between them is dominated by noise, and enforcing it would make a large creature
    /// impossible to hit while standing on top of it.
    /// </remarks>
    public static bool IsFacing(Unit attacker, Unit target)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);

        return IsWithinBoundaryRadius(attacker, target) || HasInArc(attacker, target, FacingArc);
    }

    /// <summary>Whether the attacker is behind the target — outside the target's forward half.</summary>
    /// <remarks>
    /// The arc is π rather than 2π/3: "behind" for the purposes of losing a dodge means anywhere in
    /// the rear half, which is a wider region than the front cone an attacker has to swing from.
    /// </remarks>
    public static bool IsBehind(Unit attacker, Unit target)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);

        return !HasInArc(target, attacker, MathF.PI);
    }

    /// <summary>Whether <paramref name="target"/> lies within <paramref name="arc"/> of where the unit faces.</summary>
    public static bool HasInArc(Unit unit, Unit target, float arc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(target);

        // Half the arc either side of straight ahead.
        float halfArc = MathF.Abs(arc) / 2f;

        float angleToTarget = MathF.Atan2(
            target.Position.Y - unit.Position.Y,
            target.Position.X - unit.Position.X);

        float difference = NormalizeAngle(angleToTarget - unit.Position.Orientation);

        return MathF.Abs(difference) <= halfArc;
    }

    /// <summary>Whether the two are close enough that facing stops meaning anything.</summary>
    public static bool IsWithinBoundaryRadius(Unit unit, Unit target)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(target);

        float boundary = unit.BoundingRadius + target.BoundingRadius;

        float dx = unit.Position.X - target.Position.X;
        float dy = unit.Position.Y - target.Position.Y;

        return (dx * dx) + (dy * dy) < boundary * boundary;
    }

    /// <summary>Folds an angle into <c>[-π, π]</c>, so the comparison against a half-arc works.</summary>
    /// <remarks>
    /// Without this a target at 179° and an attacker facing -179° read as 358° apart rather than 2°,
    /// and the attacker refuses to swing at something directly in front of it.
    /// </remarks>
    private static float NormalizeAngle(float radians)
    {
        const float TwoPi = 2f * MathF.PI;

        radians %= TwoPi;

        if (radians > MathF.PI)
        {
            radians -= TwoPi;
        }
        else if (radians < -MathF.PI)
        {
            radians += TwoPi;
        }

        return radians;
    }
}
