namespace WowEmu.Game.Combat;

/// <summary>
/// The debuff a spirit healer leaves you with.
/// </summary>
/// <remarks>
/// Port of the <c>applySickness</c> half of <c>Player::ResurrectPlayer</c>. It is what makes the
/// two ways back from death different from each other: walking to your corpse is free and slow, and
/// the spirit healer is instant and costs you ten minutes of halved stats plus a quarter of your
/// durability. Without it the corpse run has no reason to exist.
/// </remarks>
public static class ResurrectionSickness
{
    /// <summary>The spell itself. <c>15007</c>, "Resurrection Sickness".</summary>
    public const uint SpellId = 15007;

    /// <summary>
    /// The level below which the dead get off free. <c>Death.SicknessLevel</c>.
    /// </summary>
    /// <remarks>
    /// Eleven. A character in their starting zone dies often and has nothing to lose by it, and ten
    /// minutes of halved stats at level 3 is most of a play session.
    /// </remarks>
    public const byte StartLevel = 11;

    /// <summary>How long the full debuff lasts.</summary>
    public const int FullDurationMs = 10 * 60 * 1000;

    /// <summary>
    /// How long the sickness should last for a character of this level, or zero for none.
    /// </summary>
    /// <remarks>
    /// Three bands, and the middle one is the fiddly one: from <see cref="StartLevel"/> a character
    /// suffers one minute per level above ten, reaching the full ten minutes at level 20. Written as
    /// a flat ten minutes from level 11 it takes a level-11 character out of the game for the same
    /// time as a level-80 raider, which is a much heavier penalty at the level that can least
    /// afford it.
    /// </remarks>
    public static int DurationMsFor(byte level)
    {
        if (level < StartLevel)
        {
            return 0;
        }

        // Nine bands above the start level, which is levels 11..19; 20 and up take the full time.
        if (level >= StartLevel + 9)
        {
            return FullDurationMs;
        }

        return (level - StartLevel + 1) * 60 * 1000;
    }

    /// <summary>Whether a character of this level suffers it at all.</summary>
    public static bool Applies(byte level) => level >= StartLevel;
}
