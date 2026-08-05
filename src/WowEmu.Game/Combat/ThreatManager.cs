using WowEmu.Core;

namespace WowEmu.Game.Combat;

/// <summary>One entry on a creature's threat list.</summary>
/// <param name="Target">Who is hated.</param>
/// <param name="Threat">How much.</param>
public readonly record struct ThreatEntry(Unit Target, float Threat);

/// <summary>
/// Who a creature hates, how much, and which of them it is currently fighting.
/// </summary>
/// <remarks>
/// Port of the parts of <c>ThreatMgr</c> and <c>ThreatContainer</c> that a straightforward fight
/// needs — no redirection, no taunt, no aura modifiers, none of which exist yet.
/// <para>
/// <b>The victim is sticky.</b> The highest-threat target does <i>not</i> automatically become the
/// victim: a challenger has to exceed the current victim by 10 % to take it while in melee range, or
/// by 30 % from further away. Without that margin a creature flips between two similar attackers
/// every swing, spinning on the spot and hitting neither — and the whole idea of a tank holding
/// aggro stops working, because holding it would require winning every single roll.
/// </para>
/// </remarks>
public sealed class ThreatManager(Unit owner)
{
    /// <summary>How far ahead a challenger in melee range must be to steal the victim slot.</summary>
    public const float MeleeThreatMargin = 1.1f;

    /// <summary>How far ahead a challenger out of melee range must be.</summary>
    /// <remarks>
    /// Higher than the melee margin on purpose: pulling a creature away from what it is standing
    /// next to should cost more than taking it from someone beside you.
    /// </remarks>
    public const float RangedThreatMargin = 1.3f;

    private readonly Dictionary<ObjectGuid, ThreatEntry> _threat = [];

    /// <summary>The creature whose list this is.</summary>
    public Unit Owner { get; } = owner;

    /// <summary>Who it is fighting right now, if anyone.</summary>
    public Unit? CurrentVictim { get; private set; }

    /// <summary>Whether anything is on the list.</summary>
    public bool IsEmpty => _threat.Count == 0;

    /// <summary>How many are on the list.</summary>
    public int Count => _threat.Count;

    /// <summary>The list, highest threat first.</summary>
    /// <remarks>
    /// Sorted on read rather than kept sorted. A threat list is a handful of entries and is written
    /// far more often than it is read — every point of damage is a write — so a sorted structure
    /// would pay for ordering on the common path to save it on the rare one.
    /// </remarks>
    public IReadOnlyList<ThreatEntry> Sorted =>
        [.. _threat.Values.OrderByDescending(entry => entry.Threat)];

    /// <summary>
    /// Adds threat, putting the attacker on the list if it is not already there.
    /// </summary>
    /// <remarks>
    /// One point per point of damage. Zero is meaningful and not a no-op — it is how a creature is
    /// put on the list by something that did no damage at all, which is what makes it fight back
    /// after a miss.
    /// </remarks>
    public void AddThreat(Unit target, float threat)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (ReferenceEquals(target, Owner))
        {
            return;
        }

        float existing = _threat.TryGetValue(target.Guid, out ThreatEntry entry) ? entry.Threat : 0f;

        _threat[target.Guid] = new ThreatEntry(target, MathF.Max(existing + threat, 0f));
    }

    /// <summary>How much the owner hates a target. Zero if it is not on the list at all.</summary>
    public float GetThreat(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return _threat.TryGetValue(target.Guid, out ThreatEntry entry) ? entry.Threat : 0f;
    }

    /// <summary>Whether a target is on the list, whatever its threat.</summary>
    public bool Contains(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return _threat.ContainsKey(target.Guid);
    }

    /// <summary>Takes a target off the list, and off the victim slot if it held it.</summary>
    public void Remove(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);

        _threat.Remove(target.Guid);

        if (ReferenceEquals(CurrentVictim, target))
        {
            CurrentVictim = null;
        }
    }

    /// <summary>Forgets everyone. What happens when a creature dies or evades.</summary>
    public void Clear()
    {
        _threat.Clear();
        CurrentVictim = null;
    }

    /// <summary>
    /// Picks who to fight, and remembers the choice.
    /// </summary>
    /// <remarks>
    /// Applies the sticky-victim margins described on the class. Dead and unreachable targets are
    /// dropped as they are found, which is what keeps the list from growing without bound over a
    /// long fight.
    /// </remarks>
    /// <returns>The victim, or null when there is nobody left to fight.</returns>
    public Unit? SelectVictim()
    {
        PruneInvalid();

        IReadOnlyList<ThreatEntry> sorted = Sorted;

        if (sorted.Count == 0)
        {
            CurrentVictim = null;
            return null;
        }

        // No incumbent: the top of the list takes it, with distance breaking a tie.
        if (CurrentVictim is null || !_threat.ContainsKey(CurrentVictim.Guid))
        {
            CurrentVictim = BreakTie(sorted);
            return CurrentVictim;
        }

        float victimThreat = GetThreat(CurrentVictim);

        foreach (ThreatEntry candidate in sorted)
        {
            if (ReferenceEquals(candidate.Target, CurrentVictim))
            {
                // Reached the incumbent without anything clearing the margin, so it keeps the slot.
                break;
            }

            // The list is sorted, so the first candidate that clears the bar wins outright.
            if (candidate.Threat > RangedThreatMargin * victimThreat)
            {
                CurrentVictim = candidate.Target;
                return CurrentVictim;
            }

            if (candidate.Threat > MeleeThreatMargin * victimThreat
                && Owner.IsWithinMeleeRange(candidate.Target))
            {
                CurrentVictim = candidate.Target;
                return CurrentVictim;
            }
        }

        return CurrentVictim;
    }

    /// <summary>
    /// Among everything tied for the top, takes the closest.
    /// </summary>
    /// <remarks>
    /// The tolerance is a hundredth of a point rather than exact equality: threat accumulates in
    /// floats, and two attackers who have dealt identical damage will not compare equal.
    /// </remarks>
    private Unit BreakTie(IReadOnlyList<ThreatEntry> sorted)
    {
        const float Tolerance = 0.01f;

        Unit best = sorted[0].Target;
        float bestThreat = sorted[0].Threat;
        float bestDistance = Owner.Position.GetExactDist2dSq(best.Position);

        foreach (ThreatEntry candidate in sorted.Skip(1))
        {
            if (bestThreat - candidate.Threat > Tolerance)
            {
                break;
            }

            float distance = Owner.Position.GetExactDist2dSq(candidate.Target.Position);

            if (distance < bestDistance)
            {
                best = candidate.Target;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Drops anything dead or gone from another map.</summary>
    private void PruneInvalid()
    {
        List<ObjectGuid>? gone = null;

        foreach ((ObjectGuid guid, ThreatEntry entry) in _threat)
        {
            if (!entry.Target.IsAlive || entry.Target.MapId != Owner.MapId)
            {
                (gone ??= []).Add(guid);
            }
        }

        if (gone is null)
        {
            return;
        }

        foreach (ObjectGuid guid in gone)
        {
            _threat.Remove(guid);

            if (CurrentVictim?.Guid == guid)
            {
                CurrentVictim = null;
            }
        }
    }
}
