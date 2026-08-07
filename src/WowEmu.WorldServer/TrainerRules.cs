using WowEmu.Data.Db;
using WowEmu.Game;

namespace WowEmu.WorldServer;

/// <summary>
/// Whether a trainer will teach a given line to a given character.
/// </summary>
/// <remarks>
/// One rule, used twice: the colour the client draws a line in and the check that refuses a
/// purchase are the same question, and answering it in two places is how a trainer ends up showing
/// something in green that it then declines to sell.
/// </remarks>
public static class TrainerRules
{
    /// <summary>
    /// Whether a line is teachable now, already known, or out of reach.
    /// </summary>
    /// <remarks>
    /// Grey covers every reason a line is not takeable — the client draws the requirement text from
    /// the columns it was sent rather than from a reason code, so there is nothing finer to say.
    /// <para>
    /// The skill check compares the required <b>rank</b> against the skill's value, and requires the
    /// skill to be present at all. Those are two conditions, not one: a rank of zero means only "you
    /// must have this skill", and comparing a rank of zero against a missing skill's zero would pass
    /// every line a character has never trained in — which is exactly the bug the check exists to
    /// prevent.
    /// </para>
    /// </remarks>
    public static byte StateOf(Player player, in TrainerSpell spell)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (player.Spells.Knows(spell.SpellId))
        {
            return TrainerSpellState.Red;
        }

        if (player.Level < spell.RequiredLevel)
        {
            return TrainerSpellState.Grey;
        }

        if (spell.RequiredSkill != 0
            && (!player.Skills.Has(spell.RequiredSkill)
                || player.Skills.Value(spell.RequiredSkill) < spell.RequiredSkillRank))
        {
            return TrainerSpellState.Grey;
        }

        return TrainerSpellState.Green;
    }
}
