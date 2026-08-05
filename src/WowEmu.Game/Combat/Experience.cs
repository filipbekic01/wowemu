using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.Game.Combat;

/// <summary>What a level-up changed, so the client can animate it.</summary>
/// <param name="NewLevel">The level reached.</param>
/// <param name="HealthDelta">How much maximum health went up.</param>
/// <param name="ManaDelta">How much maximum mana went up.</param>
/// <param name="StatDeltas">Strength, agility, stamina, intellect, spirit.</param>
public readonly record struct LevelUp(uint NewLevel, int HealthDelta, int ManaDelta, int[] StatDeltas);

/// <summary>
/// Awarding experience, and what happens when enough of it accumulates.
/// </summary>
/// <remarks>
/// Port of <c>Player::GiveXP</c> and the stat half of <c>Player::GiveLevel</c>. No rested bonus, no
/// recruit-a-friend, no group rate — all three are multipliers on top of a figure this computes.
/// </remarks>
public static class Experience
{
    /// <summary>
    /// Awards experience, levelling the player up as many times as it can afford.
    /// </summary>
    /// <remarks>
    /// <b>A loop, not a single check.</b> One kill can cross more than one level at low level or
    /// with a high experience rate, and a single <c>if</c> would leave the surplus sitting above the
    /// threshold until the next kill.
    /// <para>
    /// The remainder carries forward: <c>newXp -= cost</c> per level rather than resetting to zero,
    /// so overshooting a level is not thrown away.
    /// </para>
    /// </remarks>
    /// <returns>Every level gained, in order. Empty when the experience was not enough.</returns>
    public static IReadOnlyList<LevelUp> Give(
        Player player, uint amount, PlayerXpStore xpTable, PlayerStatsStore stats)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(xpTable);
        ArgumentNullException.ThrowIfNull(stats);

        if (amount == 0 || !player.IsAlive)
        {
            return [];
        }

        // At the cap there is nothing to gain. Awarding it anyway would fill a bar the client draws
        // as full and never empties.
        if (!xpTable.CanLevelPast(player.Level))
        {
            return [];
        }

        List<LevelUp> gained = [];

        uint newXp = player.Xp + amount;
        uint cost = xpTable.XpToLeave(player.Level);

        while (cost > 0 && newXp >= cost)
        {
            newXp -= cost;

            if (LevelUpTo(player, (byte)(player.Level + 1), stats) is { } levelUp)
            {
                gained.Add(levelUp);
            }
            else
            {
                // No stats row for the next level, so the character cannot be built there. Stop
                // rather than levelling into a state with no health.
                break;
            }

            cost = xpTable.XpToLeave(player.Level);
        }

        player.Xp = newXp;
        player.NextLevelXp = xpTable.XpToLeave(player.Level);

        return gained;
    }

    /// <summary>
    /// Raises a player to a level and recomputes everything that depends on it.
    /// </summary>
    /// <remarks>
    /// <b>Health and mana are refilled, not scaled.</b> A level-up in this game restores you to
    /// full — that is what makes levelling mid-fight a real swing rather than a small one.
    /// </remarks>
    /// <returns>What changed, or null when there is no stats row for that level.</returns>
    public static LevelUp? LevelUpTo(Player player, byte level, PlayerStatsStore stats)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(stats);

        if (!stats.TryGet(player.Race, player.Class, level, out LevelStats levelStats, out ClassLevelStats classStats))
        {
            return null;
        }

        int healthDelta = (int)classStats.BaseHealth - (int)player.MaxHealth;
        int manaDelta = (int)classStats.BaseMana - (int)player.BaseMana;

        int[] statDeltas =
        [
            (int)levelStats.Strength - (int)player.GetStat(0),
            (int)levelStats.Agility - (int)player.GetStat(1),
            (int)levelStats.Stamina - (int)player.GetStat(2),
            (int)levelStats.Intellect - (int)player.GetStat(3),
            (int)levelStats.Spirit - (int)player.GetStat(4),
        ];

        player.Level = level;

        // The base, not the total: PlayerCombatStats.Apply below writes the fields, adding whatever
        // is worn. Writing them here as well would be overwritten a moment later anyway.
        player.BaseStats = player.BaseStats with
        {
            Strength = levelStats.Strength,
            Agility = levelStats.Agility,
            Stamina = levelStats.Stamina,
            Intellect = levelStats.Intellect,
            Spirit = levelStats.Spirit,
        };

        player.MaxHealth = classStats.BaseHealth;
        player.Health = classStats.BaseHealth;

        player.BaseMana = classStats.BaseMana;
        player.SetMaxPower(Unit.PowerMana, classStats.BaseMana);
        player.SetPower(Unit.PowerMana, classStats.BaseMana);

        // Attack power moves with both level and strength, and this is also what writes the five
        // stat fields. Without it a character hits for its level 1 damage forever.
        PlayerCombatStats.Apply(player);

        return new LevelUp(level, healthDelta, manaDelta, statDeltas);
    }
}
