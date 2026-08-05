namespace WowEmu.Game.Combat;

/// <summary>
/// How much experience a kill is worth.
/// </summary>
/// <remarks>
/// Port of <c>Acore::XP::BaseGain</c>, <c>Gain</c>, <c>GetGrayLevel</c> and
/// <c>GetZeroDifference</c>.
/// </remarks>
public static class ExperienceFormula
{
    /// <summary>The base figure for content up to level 60.</summary>
    public const uint BaseExpClassic = 45;

    /// <summary>The base figure for Outland, which is five times classic's.</summary>
    /// <remarks>
    /// The jump is what makes questing through the Dark Portal at 58 worth more than finishing
    /// Azeroth. It comes from the <i>zone</i>, not from the creature's level.
    /// </remarks>
    public const uint BaseExpBurningCrusade = 235;

    /// <summary>The base figure for Northrend.</summary>
    public const uint BaseExpWrath = 580;

    /// <summary>An elite is worth twice an ordinary creature.</summary>
    public const float EliteMultiplier = 2.0f;

    /// <summary>
    /// The lowest level worth any experience at all.
    /// </summary>
    /// <remarks>
    /// Below this a kill is worth nothing — the "grey" creature. The curve has three separate
    /// segments and a flat floor under level 6, none of which is derivable from the others.
    /// </remarks>
    public static byte GrayLevel(byte playerLevel) => playerLevel switch
    {
        <= 5 => 0,
        <= 39 => (byte)(playerLevel - 5 - (playerLevel / 10)),
        <= 59 => (byte)(playerLevel - 1 - (playerLevel / 5)),
        _ => (byte)(playerLevel - 9),
    };

    /// <summary>
    /// How many levels below the player a kill stays worth something.
    /// </summary>
    /// <remarks>
    /// A step function with nine steps, widening as the player levels. It is the denominator of the
    /// below-level falloff, so a wrong value here changes how fast low-level kills stop paying
    /// without changing anything at or above the player's level.
    /// </remarks>
    public static byte ZeroDifference(byte playerLevel) => playerLevel switch
    {
        < 8 => 5,
        < 10 => 6,
        < 12 => 7,
        < 16 => 8,
        < 20 => 9,
        < 30 => 11,
        < 40 => 12,
        < 45 => 13,
        < 50 => 14,
        < 55 => 15,
        < 60 => 16,
        _ => 17,
    };

    /// <summary>The base figure for an expansion's content. <c>ContentLevels</c>.</summary>
    /// <remarks>
    /// Taken from where the creature <i>is</i>, not from what level it is. A level 60 creature in
    /// Northrend pays Northrend rates.
    /// </remarks>
    public static uint BaseExpFor(byte contentLevel) => contentLevel switch
    {
        1 => BaseExpBurningCrusade,
        2 => BaseExpWrath,
        _ => BaseExpClassic,
    };

    /// <summary>
    /// The experience one kill is worth before any multipliers.
    /// </summary>
    /// <remarks>
    /// Two quite different curves either side of the player's own level.
    /// <list type="bullet">
    /// <item><b>At or above</b>: <c>(level × 5 + base) × (20 + diff) / 10 + 1) / 2</c>, with the
    /// difference capped at 4. The integer division is load-bearing — the <c>+ 1) / 2</c> is a
    /// round-half-up that a float version would get wrong on odd values.</item>
    /// <item><b>Below</b>: a linear falloff to zero at the grey level, over
    /// <see cref="ZeroDifference"/> levels.</item>
    /// </list>
    /// </remarks>
    public static uint BaseGain(byte playerLevel, byte creatureLevel, byte contentLevel)
    {
        uint baseExp = BaseExpFor(contentLevel);

        if (creatureLevel >= playerLevel)
        {
            // Past four levels up a creature is worth no more, however far above it is.
            uint levelDifference = Math.Min((uint)(creatureLevel - playerLevel), 4u);

            return ((((playerLevel * 5u) + baseExp) * (20 + levelDifference) / 10) + 1) / 2;
        }

        byte grayLevel = GrayLevel(playerLevel);

        if (creatureLevel <= grayLevel)
        {
            return 0;
        }

        uint zeroDifference = ZeroDifference(playerLevel);

        return ((playerLevel * 5u) + baseExp) * (uint)(zeroDifference + creatureLevel - playerLevel) / zeroDifference;
    }

    /// <summary>
    /// What a player actually gains for killing a creature.
    /// </summary>
    /// <remarks>
    /// <b>Critters and totems are worth nothing</b>, as is anything carrying the no-experience flag.
    /// Without that check a field of rabbits is a levelling strategy.
    /// </remarks>
    /// <param name="rate">The server's experience multiplier. 1.0 is Blizzard's own rate.</param>
    public static uint Gain(Player killer, Creature victim, byte contentLevel, float rate = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(killer);
        ArgumentNullException.ThrowIfNull(victim);

        if (victim.CreatureType == CritterCreatureType || victim.FlagsExtra.HasFlag(CreatureFlagsExtra.NoExperience))
        {
            return 0;
        }

        uint gain = BaseGain(killer.Level, victim.Level, contentLevel);

        if (gain == 0)
        {
            return 0;
        }

        float multiplier = rate;

        if (IsElite(victim.Rank))
        {
            multiplier *= EliteMultiplier;
        }

        return (uint)(gain * multiplier);
    }

    /// <summary><c>CREATURE_TYPE_CRITTER</c>.</summary>
    public const byte CritterCreatureType = 8;

    /// <summary>
    /// Whether a rank counts as elite for experience.
    /// </summary>
    /// <remarks>
    /// Rare is <i>not</i> elite. Rank 4 sits above world boss numerically but is an ordinary
    /// creature that happens to be uncommon, so a numeric <c>&gt;= 1</c> test pays double for it.
    /// </remarks>
    public static bool IsElite(byte rank) =>
        rank is Creature.RankElite or Creature.RankRareElite or Creature.RankWorldBoss;
}
