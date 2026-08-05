namespace WowEmu.Game.Combat;

/// <summary>How a melee swing turned out. <c>MeleeHitOutcome</c>, in upstream's order.</summary>
/// <remarks>
/// The numeric order is upstream's declaration order, not the order the table tests them in — those
/// differ, and the table's order is the one that matters. See <see cref="MeleeAttackTable"/>.
/// </remarks>
public enum MeleeHitOutcome : byte
{
    Evade = 0,
    Miss = 1,
    Dodge = 2,
    Block = 3,
    Parry = 4,
    Glancing = 5,
    Crit = 6,
    Crushing = 7,
    Normal = 8,
}

/// <summary>
/// Everything the attack table needs to know about one swing.
/// </summary>
/// <remarks>
/// A plain record rather than two <c>Unit</c> references, so the table is a pure function of its
/// inputs and can be rolled a million times in a test without a world behind it.
/// <para>
/// <b>Every chance is in hundredths of a percent.</b> Upstream multiplies its percentages by 100
/// before calling, so 5 % arrives as 500 and the roll is over <c>[0, 10000]</c>. Passing plain
/// percentages makes every defensive outcome a hundred times too rare, which reads as "the table
/// works" until someone counts.
/// </para>
/// </remarks>
public readonly record struct MeleeAttack(
    int AttackerLevel,
    int VictimLevel,
    int AttackerWeaponSkill,
    int AttackerMaxSkill,
    int VictimDefenseSkill,
    int VictimMaxSkill,
    int MissChance,
    int DodgeChance,
    int ParryChance,
    int BlockChance,
    int CritChance,
    bool VictimIsPlayer,
    bool AttackerIsPlayerControlled,
    bool AttackerIsBehindVictim,
    WeaponAttackType AttackType,
    bool VictimCanDodge = true,
    bool VictimCanParry = true,
    bool VictimCanBlock = true,
    bool AttackerCanCrush = true,
    bool AttackerCanCrit = true);

/// <summary>
/// Which of the eight things happens when a melee swing lands.
/// </summary>
/// <remarks>
/// Port of <c>Unit::RollMeleeOutcomeAgainst</c>.
/// <para>
/// <b>One roll, not eight.</b> A single <c>urand(0, 10000)</c> is compared against a running sum in
/// a fixed order: miss, dodge, parry, block, glancing, crushing, crit, and normal if nothing else
/// claimed it. Rolling separately per outcome gives a completely different distribution — outcomes
/// stop being mutually exclusive and the total can exceed certainty.
/// </para>
/// <para>
/// <b>The roll is inclusive at both ends</b>, so there are 10001 outcomes and not 10000. PLAN.md §6
/// records that the table depends on it.
/// </para>
/// <para>
/// The structure is reproduced rather than tidied. Upstream mutates <c>tmp</c> inside the branch
/// conditions — <c>(tmp -= skillBonus) &gt; 0</c> — which is harmless only because each block
/// reassigns <c>tmp</c> first, and rewriting it into something cleaner is how that stops being true.
/// </para>
/// <para>
/// <b>What this deliberately leaves out.</b> Upstream returns <see cref="MeleeHitOutcome.Evade"/>
/// before rolling anything when the victim is a creature that is evading; the enum member exists,
/// but the check needs creature evade state and belongs to the caller once there is any. The chance
/// figures themselves — the aura modifiers, expertise, and the "cannot dodge while casting or
/// stunned" rules that upstream applies inline — are the caller's to compute too, which is what
/// makes this a pure function of its inputs.
/// </para>
/// <para>
/// Upstream also excludes a <i>pet</i> victim from glancing blows alongside a player one. Pets do
/// not exist here yet, so only the player half is modelled; the condition needs widening when they
/// arrive.
/// </para>
/// </remarks>
public static class MeleeAttackTable
{
    /// <summary>The roll's upper bound, inclusive. 10000 hundredths of a percent is certainty.</summary>
    public const int RollMax = 10000;

    /// <summary>A glancing blow can never exceed 40 %.</summary>
    public const int MaxGlancingChance = 4000;

    /// <summary>How many levels above its victim a creature must be to land a crushing blow.</summary>
    public const int CrushingLevelGap = 4;

    /// <summary>Skill points of advantage a crushing blow needs before it can happen at all.</summary>
    public const int CrushingSkillGap = 15;

    /// <summary>
    /// Rolls one swing.
    /// </summary>
    /// <param name="attack">The swing's inputs, all chances in hundredths of a percent.</param>
    /// <param name="roll">
    /// Draws the roll. Takes <c>[0, 10000]</c> inclusive, matching <c>urand</c>. Injected so a test
    /// can walk every boundary rather than sample around them.
    /// </param>
    public static MeleeHitOutcome Roll(MeleeAttack attack, Func<uint, uint, uint> roll)
    {
        ArgumentNullException.ThrowIfNull(roll);

        // Skill difference is worth 0.04 % per point, which is 4 in hundredths.
        int skillBonus = 4 * (attack.AttackerWeaponSkill - attack.VictimMaxSkill);

        int sum = 0;
        int tmp;
        int rolled = (int)roll(0, RollMax);

        tmp = attack.MissChance;

        if (tmp > 0 && rolled < (sum += tmp))
        {
            return MeleeHitOutcome.Miss;
        }

        // Only a *player* victim loses its dodge to an attack from behind. A creature dodges from
        // any direction, which is not an oversight — it is what makes tanking a boss survivable.
        if (!(attack.VictimIsPlayer && attack.AttackerIsBehindVictim) && attack.VictimCanDodge)
        {
            tmp = attack.DodgeChance;

            if (tmp > 0 && (tmp -= skillBonus) > 0 && rolled < (sum += tmp))
            {
                return MeleeHitOutcome.Dodge;
            }
        }

        // Parry and block, unlike dodge, are lost by *anyone* attacked from behind.
        if (!attack.AttackerIsBehindVictim)
        {
            if (attack.VictimCanParry)
            {
                tmp = attack.ParryChance;

                if (tmp > 0 && (tmp -= skillBonus) > 0 && rolled < (sum += tmp))
                {
                    return MeleeHitOutcome.Parry;
                }
            }

            if (attack.VictimCanBlock)
            {
                tmp = attack.BlockChance;

                if (tmp > 0 && (tmp -= skillBonus) > 0 && rolled < (sum += tmp))
                {
                    return MeleeHitOutcome.Block;
                }
            }
        }

        // Glancing: only a player or pet, only against something bigger than itself, never ranged.
        if (attack.AttackType != WeaponAttackType.RangedAttack
            && attack.AttackerIsPlayerControlled
            && !attack.VictimIsPlayer
            && attack.AttackerLevel < attack.VictimLevel)
        {
            // Skill above the level cap does not count.
            int skill = Math.Min(attack.AttackerWeaponSkill, attack.AttackerMaxSkill);

            tmp = (10 + (attack.VictimDefenseSkill - skill)) * 100;
            tmp = Math.Min(tmp, MaxGlancingChance);

            if (rolled < (sum += tmp))
            {
                return MeleeHitOutcome.Glancing;
            }
        }

        // Crushing: only something not driven by a player, and only four levels up or more.
        if (attack.AttackerLevel >= attack.VictimLevel + CrushingLevelGap
            && !attack.AttackerIsPlayerControlled
            && attack.AttackerCanCrush)
        {
            // Defence above the victim's own cap has no effect.
            tmp = Math.Min(attack.VictimDefenseSkill, attack.VictimMaxSkill);
            tmp = attack.AttackerMaxSkill - tmp;

            if (tmp >= CrushingSkillGap)
            {
                // Two percent per lacking point, starting from fifteen.
                tmp = (tmp * 200) - 1500;

                if (rolled < (sum += tmp))
                {
                    return MeleeHitOutcome.Crushing;
                }
            }
        }

        tmp = attack.CritChance;

        if (tmp > 0 && rolled < (sum += tmp))
        {
            // The no-crit flag is tested *after* the roll lands in the crit range, not before it.
            // A creature that cannot crit therefore takes a normal hit — it does not get the crit
            // range redistributed to something else, because crit is the last outcome anyway.
            if (attack.AttackerCanCrit)
            {
                return MeleeHitOutcome.Crit;
            }
        }

        return MeleeHitOutcome.Normal;
    }
}
