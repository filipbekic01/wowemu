using WowEmu.Data.Client;

namespace WowEmu.Game;

/// <summary>
/// How a player comes to have a skill, and what it starts at.
/// </summary>
/// <remarks>
/// Port of <c>Player::LearnDefaultSkill</c> and <c>Player::UpdateSkillsForLevel</c>. Separate from
/// <see cref="PlayerSkills"/> because the storage does not need the DBCs and this does nothing
/// else: everything here is a decision about a starting value, taken from the tables.
/// <para>
/// <b>Nothing grants a skill directly.</b> Skills arrive as a side effect of learning a spell —
/// <c>SkillLineAbility</c> ties the two together — which is why a fresh warrior has Swords and
/// Defense without any table listing them as starting skills.
/// </para>
/// </remarks>
public static class SkillLearning
{
    /// <summary>
    /// Death knights are the one class with starting-value rules of their own.
    /// </summary>
    /// <remarks>
    /// A local constant rather than a shared enum, matching how the other files that need one class
    /// number spell it (<c>PlayerCombatStats</c>, <c>Creature</c>).
    /// </remarks>
    private const byte ClassDeathKnight = 6;

    /// <summary>
    /// The highest a level-scaled skill can go: five points per level.
    /// </summary>
    /// <remarks>
    /// <c>Unit::GetMaxSkillValueForLevel</c>. The same number <see cref="Unit.WeaponSkillValue"/>
    /// uses for creatures, which is why an evenly-matched fight is one where both sides are capped.
    /// </remarks>
    public static ushort MaxValueForLevel(byte level) => (ushort)(level * 5);

    /// <summary>
    /// Gives a player a skill at whatever value the tables say it should start at.
    /// </summary>
    /// <param name="rank">
    /// Which tier of a ranked skill to grant. Ignored by everything else, and a rank of zero on a
    /// ranked skill grants nothing at all — there is no "apprentice zero".
    /// </param>
    /// <returns>False when the character's race and class may not have this skill.</returns>
    /// <remarks>
    /// Port of <c>Player::LearnDefaultSkill</c>. The starting value differs per range, and the two
    /// named exceptions are worth keeping:
    /// <list type="bullet">
    /// <item>
    /// A death knight starts level-scaled skills at <c>(level - 1) * 5</c> rather than at 1. They
    /// begin at 55, and a level-55 character with 1 weapon skill misses almost everything — the
    /// starting zone would be unplayable.
    /// </item>
    /// <item>
    /// Fist Weapons inherits Unarmed's current value. It is the same skill as far as the fantasy is
    /// concerned, and learning it fresh would reset a character to 1.
    /// </item>
    /// </list>
    /// </remarks>
    public static bool LearnDefault(Player player, SkillLines skills, uint skillId, ushort rank = 0)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(skills);

        if (skills.RaceClassInfo(skillId, player.Race, player.Class) is not { } info)
        {
            return false;
        }

        ushort maxForLevel = MaxValueForLevel(player.Level);

        switch (skills.RangeOf(info))
        {
            case SkillRange.Language:
                return player.Skills.Set(skillId, 0, SkillLines.LanguageValue, SkillLines.LanguageValue);

            case SkillRange.Mono:
                return player.Skills.Set(skillId, 0, 1, 1);

            case SkillRange.Level:
                return player.Skills.Set(
                    skillId, 0, StartingValue(player, info, skillId, maxForLevel), maxForLevel);

            case SkillRange.Rank:
            {
                if (rank == 0 || skills.Tier(info.SkillTierId) is not { } tier)
                {
                    return false;
                }

                ushort max = (ushort)tier.MaxAt(rank);
                ushort value = 1;

                if ((info.Flags & SkillRaceClassInfoEntry.AlwaysMaxValue) != 0)
                {
                    value = max;
                }
                else if (player.Class == ClassDeathKnight)
                {
                    value = Math.Min(DeathKnightStart(player.Level), max);
                }

                return player.Skills.Set(skillId, rank, value, max);
            }

            default:
                return false;
        }
    }

    /// <summary>Where a level-scaled skill starts, which is 1 unless something says otherwise.</summary>
    private static ushort StartingValue(
        Player player, SkillRaceClassInfoEntry info, uint skillId, ushort maxForLevel)
    {
        if ((info.Flags & SkillRaceClassInfoEntry.AlwaysMaxValue) != 0)
        {
            return maxForLevel;
        }

        if (player.Class == ClassDeathKnight)
        {
            return Math.Min(DeathKnightStart(player.Level), maxForLevel);
        }

        if (skillId == SkillType.FistWeapons)
        {
            // Fists are unarmed with gloves on. Starting them at 1 would undo every point a monk-ish
            // character had already put into Unarmed.
            return Math.Max((ushort)1, player.Skills.Value(SkillType.Unarmed));
        }

        return 1;
    }

    /// <summary>A death knight's floor — one level's worth below their own cap, but never zero.</summary>
    private static ushort DeathKnightStart(byte level) =>
        Math.Max((ushort)1, (ushort)((level - 1) * 5));

    /// <summary>
    /// Raises the ceiling on every level-scaled skill after a level-up.
    /// </summary>
    /// <remarks>
    /// Port of <c>Player::UpdateSkillsForLevel</c>. Only the ceiling moves — the value stays where
    /// practice left it, which is what makes weapon skill something you keep up rather than
    /// something you are given.
    /// <para>
    /// <b>A maximum of 1 is left alone.</b> That is the mono bar — armour proficiencies and
    /// runeforging — and it is how upstream tells them apart here without re-deriving the range.
    /// Raising it would draw a progress bar on a skill that has no progress.
    /// </para>
    /// </remarks>
    public static void UpdateForLevel(Player player, SkillLines skills)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(skills);

        ushort maxForLevel = MaxValueForLevel(player.Level);

        // Materialised, because granting or clearing a skill inside the loop would invalidate it.
        foreach (uint skillId in player.Skills.Known.ToArray())
        {
            if (skills.RaceClassInfo(skillId, player.Race, player.Class) is not { } info
                || skills.RangeOf(info) != SkillRange.Level)
            {
                continue;
            }

            ushort max = player.Skills.PureMaxValue(skillId);

            if (max == 1)
            {
                continue;
            }

            ushort value = player.Skills.PureValue(skillId);

            if ((info.Flags & SkillRaceClassInfoEntry.AlwaysMaxValue) != 0)
            {
                value = maxForLevel;
            }

            player.Skills.Set(skillId, player.Skills.Step(skillId), value, maxForLevel);
        }
    }

    /// <summary>
    /// Grants whatever skills a newly learned spell carries with it.
    /// </summary>
    /// <remarks>
    /// Port of the non-ranked branch of <c>Player::addSpell</c>. A spell grants its skill when the
    /// ability row says the two live and die together, which is <c>AcquireMethod</c> 2.
    /// <para>
    /// Lockpicking and Runeforging are named separately, and the condition is not the acquire method
    /// but a <c>TrivialSkillLineRankHigh</c> of zero. Both are granted by learning the spell rather
    /// than at character creation — upstream's note says the runeforging case was confirmed by
    /// sniffing the real server, which is the sort of thing worth not quietly tidying away.
    /// </para>
    /// </remarks>
    public static void LearnSkillsFromSpell(Player player, SkillLines skills, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(skills);

        foreach (SkillLineAbilityEntry ability in skills.AbilitiesOf(spellId))
        {
            if (skills.Line(ability.SkillLine) is null)
            {
                continue;
            }

            bool comesWithTheSkill =
                ability.AcquireMethod == SkillLineAbilityEntry.LearnedOnSkillLearn
                && !player.Skills.Has(ability.SkillLine);

            bool grantedByTheSpell =
                (ability.SkillLine is SkillType.Lockpicking or SkillType.Runeforging)
                && ability.TrivialSkillLineRankHigh == 0;

            if (comesWithTheSkill || grantedByTheSpell)
            {
                LearnDefault(player, skills, ability.SkillLine);
            }
        }
    }
}
