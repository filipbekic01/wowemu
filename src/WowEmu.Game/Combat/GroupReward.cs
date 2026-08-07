namespace WowEmu.Game.Combat;

/// <summary>What one member of a group takes away from a kill.</summary>
public readonly record struct GroupShare(Player Member, uint Experience);

/// <summary>
/// Dividing a kill among a group.
/// </summary>
/// <remarks>
/// Port of <c>KillRewarder</c>. Three things about this are not what they look like:
/// <list type="bullet">
/// <item><b>The base experience is computed from the highest-level member for whom the victim is
/// not grey, not from whoever landed the killing blow.</b> A level 70 helping a level 20 does not
/// make the kill worth a level 70's XP, and the killer's own level is irrelevant to the base.</item>
/// <item><b>The share is weighted by level, not divided evenly.</b> Each member takes
/// <c>rate × their level ÷ the sum of alive levels</c>, so a level 60 in a group with a level 20
/// takes three times as much. An even split is the obvious reading and is wrong.</item>
/// <item><b>Grouping <i>increases</i> the total.</b> The rate runs to 1.4 for five, so a full party
/// earns forty percent more than one player would — the group is not simply sharing one kill's
/// worth between them.</item>
/// </list>
/// </remarks>
public static class GroupReward
{
    /// <summary>
    /// How far a member can be and still be paid. <c>MaxGroupXPDistance</c>.
    /// </summary>
    /// <remarks>
    /// Measured from the corpse for a dead member and from the player otherwise, so a corpse-run
    /// does not cost the group their share of a kill they were there for.
    /// </remarks>
    public const float RewardDistance = 74f;

    /// <summary>
    /// The group's experience multiplier, by how many members are within range.
    /// </summary>
    /// <remarks>
    /// Port of <c>Acore::XP::xp_in_group_rate</c>. <b>Flat at 1.0 up to two members</b> — a duo
    /// earns exactly what a solo player would, and only the third member starts paying a bonus.
    /// Raids get 1.0 outright; upstream marks scaling by raid size as unimplemented.
    /// </remarks>
    public static float RateFor(int count, bool isRaid) => isRaid
        ? 1.0f
        : count switch
        {
            <= 2 => 1.0f,
            3 => 1.166f,
            4 => 1.3f,
            _ => 1.4f,
        };

    /// <summary>
    /// Splits a kill's experience across a group.
    /// </summary>
    /// <param name="members">Everyone in the group who is on the same map, in any state.</param>
    /// <param name="victim">What was killed. A creature — player kills pay no experience.</param>
    /// <param name="isRaid">Whether the group is a raid.</param>
    /// <param name="contentLevel">The expansion's level cap, for the base formula.</param>
    /// <returns>What each member earns. Members out of range are absent.</returns>
    /// <remarks>
    /// A member whose own level is above the highest non-grey member's earns <b>nothing</b>: the
    /// kill is grey to them and paying them anything is the power-levelling hole this closes.
    /// <para>
    /// When any member is grey the whole group is paid half plus one, not merely the grey one —
    /// the penalty is collective, which is what stops a high-level friend inflating a group.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GroupShare> Split(
        IReadOnlyList<Player> members, Creature victim, bool isRaid, byte contentLevel)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(victim);

        Player? highestNotGrey = null;
        int inRange = 0;
        uint aliveSumLevel = 0;

        foreach (Player member in members)
        {
            if (!IsInRewardRange(member, victim))
            {
                continue;
            }

            inRange++;

            if (member.IsAlive)
            {
                aliveSumLevel += member.Level;
            }

            if (victim.Level > ExperienceFormula.GrayLevel(member.Level)
                && (highestNotGrey is null || highestNotGrey.Level < member.Level))
            {
                highestNotGrey = member;
            }
        }

        if (highestNotGrey is null || aliveSumLevel == 0)
        {
            return [];
        }

        // From the highest non-grey member, not the killer. This is the whole base figure.
        uint baseXp = ExperienceFormula.Gain(highestNotGrey, victim, contentLevel);

        if (baseXp == 0)
        {
            return [];
        }

        byte highestLevel = 0;

        foreach (Player member in members)
        {
            if (IsInRewardRange(member, victim) && member.Level > highestLevel)
            {
                highestLevel = member.Level;
            }
        }

        // Full experience only when nobody in range out-levels the highest member the victim is
        // still worth something to. Otherwise the whole group is halved.
        bool fullXp = highestLevel == highestNotGrey.Level;

        float rate = RateFor(inRange, isRaid);
        List<GroupShare> shares = [];

        foreach (Player member in members)
        {
            if (!IsInRewardRange(member, victim))
            {
                continue;
            }

            // Above the highest non-grey member is grey to this one, and grey pays nothing.
            if (member.IsAlive && highestNotGrey.Level < member.Level)
            {
                shares.Add(new GroupShare(member, 0));
                continue;
            }

            float share = rate * member.Level / aliveSumLevel;
            uint earned = fullXp
                ? (uint)(baseXp * share)
                : (uint)(baseXp * share / 2) + 1;

            shares.Add(new GroupShare(member, earned));
        }

        return shares;
    }

    /// <summary>
    /// Whether a member is close enough to be paid.
    /// </summary>
    /// <remarks>
    /// Same map first — a member on another continent is not "far away", they are not there at all,
    /// and a distance in coordinates that mean different things on different maps is meaningless.
    /// </remarks>
    public static bool IsInRewardRange(Player member, WorldObject victim)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(victim);

        return member.MapId == victim.MapId
            && member.Position.GetExactDist2dSq(victim.Position) <= RewardDistance * RewardDistance;
    }
}
