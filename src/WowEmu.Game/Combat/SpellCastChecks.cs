using WowEmu.Data.Client;
using WowEmu.Protocol;

namespace WowEmu.Game.Combat;

/// <summary>
/// Whether a cast is allowed, and what to tell the client when it is not.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Spell::CheckCast</c> a plain damage spell reaches. No reagents, no
/// stances, no aura states, no line-of-sight against dynamic objects.
/// <para>
/// <b>The order matters and is upstream's.</b> The client shows only the first failure, so checking
/// range before target validity tells someone their target is too far away when the real problem is
/// that it is dead.
/// </para>
/// </remarks>
public static class SpellCastChecks
{
    /// <summary>
    /// How much slack is allowed on the range check.
    /// </summary>
    /// <remarks>
    /// The client and server disagree about position by however far the player moved since the last
    /// heartbeat. Without slack an honest cast at the edge of range is refused often enough to feel
    /// broken; upstream adds the caster's combat reach for the same reason.
    /// </remarks>
    public const float RangeTolerance = 1.0f;

    /// <summary>
    /// Checks a cast.
    /// </summary>
    /// <param name="caster">Who is casting.</param>
    /// <param name="spell">What.</param>
    /// <param name="target">At whom, or null for a self-cast.</param>
    /// <param name="stores">Resolves the spell's range, which is an index rather than a value.</param>
    /// <param name="casting">The caster's cooldowns and cast in progress.</param>
    /// <param name="hasLineOfSight">
    /// Supplied rather than reached for, so this stays a pure function and can be tested without a map.
    /// </param>
    public static SpellCastResult Check(
        Unit caster,
        SpellEntry spell,
        Unit? target,
        SpellStores stores,
        SpellCastState casting,
        Func<bool> hasLineOfSight)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(casting);
        ArgumentNullException.ThrowIfNull(hasLineOfSight);

        if (casting.State == CastState.Casting)
        {
            return SpellCastResult.SpellInProgress;
        }

        if (!casting.IsReady(spell.Id))
        {
            return SpellCastResult.NotReady;
        }

        // The global cooldown only blocks spells that would themselves start one. An ability with no
        // StartRecoveryTime is usable during it, which is what lets Heroic Strike be queued mid-GCD.
        if (!casting.IsGlobalCooldownReady && spell.StartRecoveryTime > 0)
        {
            return SpellCastResult.NotReady;
        }

        if (!caster.IsAlive)
        {
            return SpellCastResult.BadTargets;
        }

        if (!HasEnoughPower(caster, spell))
        {
            return SpellCastResult.NoPower;
        }

        // A spell with no target is a self-cast, and everything below is about someone else.
        if (target is null || ReferenceEquals(target, caster))
        {
            return SpellCastResult.Ok;
        }

        if (!target.IsAlive)
        {
            return SpellCastResult.TargetsDead;
        }

        if (caster.MapId != target.MapId)
        {
            return SpellCastResult.OutOfRange;
        }

        float maxRange = stores.MaxRange(spell) + RangeTolerance;
        float minRange = stores.MinRange(spell);
        float distanceSquared = caster.Position.GetExactDist2dSq(target.Position);

        // A zero maximum means "melee range" rather than "cannot be cast" — several abilities carry
        // range index 1, whose maximum is 0 and whose meaning is the caster's own reach.
        if (maxRange > RangeTolerance && distanceSquared > maxRange * maxRange)
        {
            return SpellCastResult.OutOfRange;
        }

        // Minimum range is a real constraint, not a formality: a hunter cannot shoot something
        // standing on top of them.
        if (minRange > 0f && distanceSquared < minRange * minRange)
        {
            return SpellCastResult.TooClose;
        }

        return hasLineOfSight() ? SpellCastResult.Ok : SpellCastResult.LineOfSight;
    }

    /// <summary>
    /// What a spell costs the caster.
    /// </summary>
    /// <remarks>
    /// Two columns, and which one applies depends on the spell rather than the class. Wrath moved
    /// caster spells to a percentage of <i>base</i> mana and left rage and energy flat, so reading
    /// only the flat column makes every mage spell free.
    /// <para>
    /// The percentage is of base mana — the class's mana before gear — not of the caster's current
    /// maximum. Taking it from the maximum makes spells cost more as a character gears up.
    /// </para>
    /// </remarks>
    public static uint PowerCost(Unit caster, SpellEntry spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);

        uint cost = spell.ManaCost;

        if (spell.ManaCostPercentage > 0)
        {
            cost += spell.ManaCostPercentage * caster.BasePowerFor((byte)spell.PowerType) / 100;
        }

        return cost;
    }

    /// <summary>Whether the caster can pay for a spell.</summary>
    /// <remarks>
    /// Checked against the <b>spell's</b> power type, not the caster's displayed one. A unit carries
    /// all seven values at once, so a mana-priced spell is checked against mana even when the
    /// caster's own bar is rage — which is what stops a warrior casting Fireball for free.
    /// </remarks>
    public static bool HasEnoughPower(Unit caster, SpellEntry spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);

        uint cost = PowerCost(caster, spell);

        return cost == 0 || caster.GetPower((byte)spell.PowerType) >= cost;
    }
}
