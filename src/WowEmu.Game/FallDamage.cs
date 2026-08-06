namespace WowEmu.Game;

/// <summary>
/// What a long drop costs.
/// </summary>
/// <remarks>
/// Port of <c>Player::HandleFall</c>. A straight line in the fall distance: nothing below about
/// thirteen and a half yards hurts at all, and the fraction of maximum health lost rises by 1.8% per
/// yard after that. Around 69 yards it reaches 100% and the fall is fatal from full health.
/// <para>
/// <b>The distance is measured by the server, from the last height the player was known to be
/// standing at.</b> The client sends its own fall time and start position, and both are exactly what
/// a client avoiding fall damage would understate.
/// </para>
/// </remarks>
public static class FallDamage
{
    /// <summary>Fraction of maximum health lost per yard fallen. <c>FALL_DMG_EQU_SLOPE</c>.</summary>
    public const float Slope = 0.018f;

    /// <summary>Where the line crosses zero. <c>FALL_DMG_EQU_INTERCEPT</c>.</summary>
    public const float Intercept = -0.2426f;

    /// <summary>Shortest fall that hurts, in yards. <c>MIN_FALL_DMG_DIST</c>.</summary>
    public const float MinimumDistance = 13.48f;

    /// <summary>
    /// The damage a fall of <paramref name="distance"/> yards deals, or zero.
    /// </summary>
    /// <param name="distance">How far the player dropped. Negative means they ended up higher.</param>
    /// <param name="maxHealth">The player's maximum health; the loss is a fraction of it.</param>
    /// <param name="safeFallReduction">
    /// Yards ignored before the fall is measured — Safe Fall and Slow Fall. Zero until auras with
    /// that effect exist.
    /// </param>
    /// <param name="rate">The server's fall-damage multiplier. 1.0 is Blizzard's own.</param>
    /// <remarks>
    /// Capped at maximum health, so a very long fall kills exactly once rather than reporting more
    /// damage than the player has — the number is shown to the client, and an absurd one is visible.
    /// </remarks>
    public static uint Calculate(
        float distance,
        uint maxHealth,
        int safeFallReduction = 0,
        float rate = 1.0f)
    {
        if (distance < MinimumDistance)
        {
            return 0;
        }

        float fraction = (Slope * (distance - safeFallReduction)) + Intercept;

        if (fraction <= 0f)
        {
            return 0;
        }

        uint damage = (uint)(fraction * maxHealth * rate);

        return Math.Min(damage, maxHealth);
    }
}
