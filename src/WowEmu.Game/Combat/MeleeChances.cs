namespace WowEmu.Game.Combat;

/// <summary>
/// The five chances the attack table rolls against, before they are scaled to hundredths.
/// </summary>
/// <remarks>
/// Percentages here, not hundredths — <see cref="MeleeChances.For"/> does the conversion when it
/// builds the <see cref="MeleeAttack"/>. Keeping them as plain percentages up to that point matches
/// upstream, where every one of these functions returns a float percentage and the single
/// <c>int32(x * 100)</c> happens at the call site.
/// </remarks>
public readonly record struct MeleeChancePercentages(
    float Miss,
    float Dodge,
    float Parry,
    float Block,
    float Crit);

/// <summary>
/// Works out an attacker's odds against a victim.
/// </summary>
/// <remarks>
/// Ports <c>GetUnitMissChance</c>, <c>MeleeSpellMissChance</c>, <c>GetUnitDodgeChance</c>,
/// <c>GetUnitParryChance</c>, <c>GetUnitBlockChance</c> and <c>GetUnitCriticalChance</c> — the
/// aura-free, item-free parts of each.
/// <para>
/// <b>What is missing, and why it is missing.</b> Every one of these functions ends by adding aura
/// modifiers, and the player branches read equipment. Neither exists yet. The base values are the
/// whole calculation for a creature and the correct starting point for a player, so what is here is
/// right rather than partial — but a player's dodge, parry, block and crit come from their character
/// sheet, which is why those are read from update fields instead of computed.
/// </para>
/// </remarks>
public static class MeleeChances
{
    /// <summary>Everything has a 5 % base chance to be missed.</summary>
    public const float BaseMissChance = 5.0f;

    /// <summary>Miss is clamped to this, so no amount of skill difference makes an attacker useless.</summary>
    public const float MaxMissChance = 60.0f;

    /// <summary>An ordinary creature's dodge.</summary>
    public const float CreatureDodgeChance = 5.0f;

    /// <summary>
    /// A world boss's dodge.
    /// </summary>
    /// <remarks>
    /// 5.85 rather than 5, so that with the 0.65 a boss gains from the skill difference against a
    /// level-80 attacker it lands on the 6.5 % the encounter maths assumes.
    /// </remarks>
    public const float BossDodgeChance = 5.85f;

    /// <summary>A humanoid creature's parry. Anything non-humanoid parries nothing.</summary>
    public const float HumanoidParryChance = 5.0f;

    /// <summary>A world boss's parry — high enough that attacking one from the front is a mistake.</summary>
    public const float BossParryChance = 13.4f;

    /// <summary>A creature's block.</summary>
    public const float CreatureBlockChance = 5.0f;

    /// <summary>A creature's crit.</summary>
    public const float CreatureCritChance = 5.0f;

    /// <summary>Each point of skill difference is worth this much crit.</summary>
    public const float CritPerSkillPoint = 0.04f;

    /// <summary><c>CREATURE_TYPE_HUMANOID</c>.</summary>
    public const byte HumanoidCreatureType = 7;

    /// <summary>The rank a world boss carries in <c>creature_template</c>.</summary>
    public const byte WorldBossRank = 3;

    /// <summary>
    /// How likely the attacker is to miss.
    /// </summary>
    /// <remarks>
    /// The skill term is <b>asymmetric in two directions at once</b>, and this is where the numbers
    /// go wrong if it is simplified.
    /// <list type="bullet">
    /// <item>Against a <i>player</i>, being under-skilled costs twice what being over-skilled saves —
    /// 0.04 per point against 0.02.</item>
    /// <item>Against a <i>creature</i>, the first ten points are worth 0.1 each and every point after
    /// that is worth 0.4 — the cliff that makes attacking something more than two levels up feel
    /// suddenly hopeless rather than gradually harder.</item>
    /// </list>
    /// </remarks>
    /// <param name="skillDifference">Attacker's weapon skill minus the victim's skill cap.</param>
    /// <param name="victimIsPlayer">Which of the two curves applies.</param>
    public static float MissChance(int skillDifference, bool victimIsPlayer)
    {
        float missChance = BaseMissChance;

        // Upstream negates before branching, so the sign convention is "how far the attacker is
        // *behind*". Skipping the negation flips both curves.
        int diff = -skillDifference;

        missChance += victimIsPlayer
            ? (diff > 0 ? diff * 0.04f : diff * 0.02f)
            : (diff > 10 ? 1 + ((diff - 10) * 0.4f) : diff * 0.1f);

        return Math.Clamp(missChance, 0f, MaxMissChance);
    }

    /// <summary>A creature's dodge.</summary>
    public static float CreatureDodge(bool isWorldBoss) => isWorldBoss ? BossDodgeChance : CreatureDodgeChance;

    /// <summary>
    /// A creature's parry.
    /// </summary>
    /// <remarks>
    /// Only humanoids parry. A wolf has nothing to parry with, and giving every creature the humanoid
    /// value makes early levelling noticeably slower than it should be.
    /// </remarks>
    public static float CreatureParry(bool isWorldBoss, byte creatureType)
    {
        if (isWorldBoss)
        {
            return BossParryChance;
        }

        return creatureType == HumanoidCreatureType ? HumanoidParryChance : 0f;
    }

    /// <summary>
    /// The attacker's crit, adjusted for the skill difference.
    /// </summary>
    /// <param name="baseCritChance">5 % for a creature; the character sheet's value for a player.</param>
    /// <param name="attackerMaxSkill">The attacker's skill cap — level × 5.</param>
    /// <param name="victimDefenseSkill">The victim's defence.</param>
    public static float CritChance(float baseCritChance, int attackerMaxSkill, int victimDefenseSkill)
    {
        float crit = baseCritChance + ((attackerMaxSkill - victimDefenseSkill) * CritPerSkillPoint);

        return MathF.Max(crit, 0f);
    }

    /// <summary>
    /// Assembles a swing between two units, ready to roll.
    /// </summary>
    /// <remarks>
    /// The single place percentages become hundredths, as <c>int32(chance * 100)</c> — the same
    /// expression upstream uses at its own call site.
    /// <para>
    /// <b>The multiplication stays in float.</b> It is a truncation of a float product, not of the
    /// decimal you would write down, and the two differ: 13.4 is not representable, but 13.4f × 100
    /// rounds to exactly 1340.0f, so a boss's parry is 1340 where decimal arithmetic says 1339.
    /// Widening to double here would be more accurate and less correct.
    /// </para>
    /// </remarks>
    public static MeleeAttack For(
        Unit attacker,
        Unit victim,
        WeaponAttackType attackType,
        bool attackerIsBehindVictim = false)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);

        int attackerMaxSkill = attacker.Level * 5;
        int victimMaxSkill = victim.Level * 5;
        int attackerWeaponSkill = attacker.WeaponSkillValue;
        int victimDefenseSkill = victim.DefenseSkillValue;

        MeleeChancePercentages chances = Percentages(attacker, victim, attackType, attackerWeaponSkill, victimMaxSkill);

        return new MeleeAttack(
            AttackerLevel: attacker.Level,
            VictimLevel: victim.Level,
            AttackerWeaponSkill: attackerWeaponSkill,
            AttackerMaxSkill: attackerMaxSkill,
            VictimDefenseSkill: victimDefenseSkill,
            VictimMaxSkill: victimMaxSkill,
            MissChance: (int)(chances.Miss * 100),
            DodgeChance: (int)(chances.Dodge * 100),
            ParryChance: (int)(chances.Parry * 100),
            BlockChance: (int)(chances.Block * 100),
            CritChance: (int)(chances.Crit * 100),
            VictimIsPlayer: victim.IsPlayerControlled,
            AttackerIsPlayerControlled: attacker.IsPlayerControlled,
            AttackerIsBehindVictim: attackerIsBehindVictim,
            AttackType: attackType,
            VictimCanDodge: victim.CanDodge,
            VictimCanParry: victim.CanParry,
            VictimCanBlock: victim.CanBlock,
            AttackerCanCrush: attacker.CanCrush,
            AttackerCanCrit: attacker.CanCrit);
    }

    /// <summary>The five percentages for a swing, before they are scaled.</summary>
    public static MeleeChancePercentages Percentages(
        Unit attacker,
        Unit victim,
        WeaponAttackType attackType,
        int attackerWeaponSkill,
        int victimMaxSkill)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);

        return new MeleeChancePercentages(
            Miss: MissChance(attackerWeaponSkill - victimMaxSkill, victim.IsPlayerControlled),
            Dodge: victim.DodgeChance,
            Parry: victim.ParryChance,
            Block: victim.BlockChance,
            Crit: CritChance(attacker.CritChanceFor(attackType), attacker.Level * 5, victim.DefenseSkillValue));
    }
}
