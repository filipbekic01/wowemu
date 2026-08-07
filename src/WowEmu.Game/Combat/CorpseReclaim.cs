namespace WowEmu.Game.Combat;

/// <summary>
/// How long a ghost must wait before it may take its body back.
/// </summary>
/// <remarks>
/// Port of <c>Player::GetCorpseReclaimDelay</c>, <c>CalculateCorpseReclaimDelay</c> and
/// <c>UpdateCorpseReclaimDelay</c>. The number was already being sent to the client and never
/// enforced, which is the worst of both: the client counts down a timer the server ignores, so the
/// honest player waits and anyone who clicks through does not.
/// <para>
/// <b>The delay escalates with repeated deaths and decays with time.</b> That is the whole
/// mechanic, and it is easy to implement as a flat thirty seconds and call it done — which removes
/// the only cost of dying repeatedly in the same fight.
/// </para>
/// </remarks>
public static class CorpseReclaim
{
    /// <summary>
    /// The three delays, in seconds. <c>copseReclaimDelay</c>, upstream's spelling and all.
    /// </summary>
    /// <remarks>
    /// Thirty seconds, then a minute, then two — but <b>not as a simple ladder</b>. A second death
    /// with the whole first window still open lands on the third rung, not the second; the middle
    /// one is reached only by dying again late in the window, when most of it has already decayed.
    /// Reading the table as 1st/2nd/3rd death halves the penalty for chain-dying.
    /// </remarks>
    public static readonly int[] Delays = [30, 60, 120];

    /// <summary>
    /// How long one death's worth of penalty takes to decay. <c>DEATH_EXPIRE_STEP</c>.
    /// </summary>
    /// <remarks>
    /// Five minutes. The escalation is not a death counter — it is a window, so the penalty fades
    /// on its own and nothing has to remember to reset it.
    /// </remarks>
    public const long ExpireStepSeconds = 5 * 60;

    /// <summary>How many steps the penalty can stack to. <c>MAX_DEATH_COUNT</c>.</summary>
    public const int MaxDeathCount = 3;

    /// <summary>
    /// Records a death, pushing the penalty window further out.
    /// </summary>
    /// <remarks>
    /// Port of <c>UpdateCorpseReclaimDelay</c>. The arithmetic is worth reading slowly: while the
    /// window is still open, the new expiry is <i>one more step than however many are left</i>, so
    /// dying again during the penalty escalates it rather than merely refreshing it. Once the
    /// window has closed the count starts again at one step.
    /// </remarks>
    public static void RecordDeath(Player player, long now)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (now < player.DeathExpireTime)
        {
            long remaining = ((player.DeathExpireTime - now) / ExpireStepSeconds) + 1;

            player.DeathExpireTime = remaining < MaxDeathCount
                ? now + ((remaining + 1) * ExpireStepSeconds)
                : now + (MaxDeathCount * ExpireStepSeconds);

            return;
        }

        player.DeathExpireTime = now + ExpireStepSeconds;
    }

    /// <summary>
    /// The delay this death earns, in seconds.
    /// </summary>
    /// <remarks>
    /// Port of <c>GetCorpseReclaimDelay</c>. The <c>- 1</c> is upstream's, and its own comment says
    /// what it is for: the index should be a ceiling minus one rather than a floor, so a window with
    /// exactly one step left counts as one step rather than none.
    /// </remarks>
    public static int DelayFor(Player player, long now)
    {
        ArgumentNullException.ThrowIfNull(player);

        long steps = now < player.DeathExpireTime - 1
            ? (player.DeathExpireTime - 1 - now) / ExpireStepSeconds
            : 0;

        return Delays[Math.Clamp(steps, 0, Delays.Length - 1)];
    }

    /// <summary>
    /// Whether enough time has passed since this death.
    /// </summary>
    /// <remarks>
    /// Measured from when the player became a ghost, not from when they released — a player who
    /// stares at the release screen for a minute has already served the wait.
    /// </remarks>
    public static bool CanReclaim(Player player, long now)
    {
        ArgumentNullException.ThrowIfNull(player);

        return now >= player.GhostTime + player.ReclaimDelaySeconds;
    }

    /// <summary>How much longer, in seconds. Zero once the wait is over.</summary>
    public static long RemainingSeconds(Player player, long now)
    {
        ArgumentNullException.ThrowIfNull(player);

        return Math.Max(player.GhostTime + player.ReclaimDelaySeconds - now, 0);
    }
}
