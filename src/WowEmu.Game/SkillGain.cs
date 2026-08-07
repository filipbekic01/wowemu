using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Game.Combat;

namespace WowEmu.Game;

/// <summary>
/// Raising a skill by using it.
/// </summary>
/// <remarks>
/// Port of <c>Player::UpdateSkill</c>, <c>UpdateSkillPro</c>, <c>UpdateCombatSkills</c> and
/// <c>UpdateWeaponSkill</c>. Without this the skill system is a set of numbers handed out at
/// creation and never touched again — weapon skill sits at 1 forever, and a profession can never
/// leave the value its trainer granted.
/// <para>
/// <b>Only combat is wired up.</b> The crafting, gathering and fishing gains have nowhere to be
/// called from yet, so they are not written: a chance formula with no caller is a formula nobody
/// can tell is wrong.
/// </para>
/// </remarks>
public static class SkillGain
{
    /// <summary>
    /// How much one successful gain is worth. <c>CONFIG_SKILL_GAIN_WEAPON</c> and its siblings.
    /// </summary>
    /// <remarks>
    /// One point, which is the retail rate. Upstream makes it configurable per category so a server
    /// can multiply it; there is nowhere to configure anything yet, so the default stands alone.
    /// </remarks>
    public const ushort Step = 1;

    /// <summary>
    /// Raises a skill by a flat step, up to its ceiling.
    /// </summary>
    /// <remarks>
    /// Port of <c>Player::UpdateSkill</c>. Three separate reasons to do nothing, and the middle one
    /// — <b>a value of zero is never raised</b> — is upstream's and is kept for the same reason it
    /// is there: it is an invariant, not a live branch. Nothing can currently produce a known skill
    /// sitting at zero, because <see cref="PlayerSkills.Set"/> reads a value of zero as "forget this
    /// skill" on every path including the restore. Worth keeping anyway; worth not mistaking for
    /// load-bearing.
    /// </remarks>
    /// <returns>Whether the value actually moved.</returns>
    public static bool Raise(Player player, uint skillId, ushort step = Step)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (skillId == 0 || !player.Skills.Has(skillId))
        {
            return false;
        }

        ushort value = player.Skills.PureValue(skillId);
        ushort max = player.Skills.PureMaxValue(skillId);

        if (max == 0 || value == 0 || value >= max)
        {
            return false;
        }

        ushort raised = (ushort)Math.Min(value + step, max);

        player.Skills.Set(skillId, player.Skills.Step(skillId), raised, max);

        return true;
    }

    /// <summary>
    /// One melee exchange's chance to raise the attacker's weapon skill or the victim's defence.
    /// </summary>
    /// <param name="player">Whose skill might go up.</param>
    /// <param name="opponent">The other side, whose level sets how much there is to learn.</param>
    /// <param name="defending">
    /// True when <paramref name="player"/> was the one hit. The same call serves both directions,
    /// which is what keeps the attacker's and the victim's rules from drifting apart.
    /// </param>
    /// <remarks>
    /// Port of <c>Player::UpdateCombatSkills</c>. The shape is <c>3 × lvldif × skillDiff / level</c>
    /// — how far behind the cap you are, times how much the opponent has to teach.
    /// <list type="bullet">
    /// <item>
    /// <b>The mob's level is capped at yours plus five, and the difference floored at three.</b>
    /// Without the floor, grinding something grey gives a chance of zero and the skill can never be
    /// finished off; without the cap, one hit from something enormous would be worth a level's grind.
    /// </item>
    /// <item>
    /// <b>Intellect helps you learn to swing, but not to take a hit.</b> Two percent per point, and
    /// only on the attacking side — it is an old rule and it looks like a bug until you find it in
    /// the source.
    /// </item>
    /// <item>
    /// The chance is floored at one percent, so no fight is entirely wasted.
    /// </item>
    /// </list>
    /// </remarks>
    /// <returns>The chance as a percentage, or zero when there is nothing to gain.</returns>
    public static float CombatChance(Player player, Unit opponent, bool defending)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(opponent);

        byte level = player.Level;

        int current = defending ? player.DefenseSkillValue : player.WeaponSkillValue;
        int cap = level * 5;
        int skillDiff = cap - current;

        // Already at the cap for this level. Can go negative after a level is taken away, which is
        // why this is <= rather than ==.
        if (skillDiff <= 0 || level == 0)
        {
            return 0f;
        }

        byte grey = ExperienceFormula.GrayLevel(level);
        int mobLevel = Math.Min(opponent.Level, level + 5);

        int levelDiff = Math.Max(mobLevel - grey, 3);

        float chance = 3f * levelDiff * skillDiff / level;

        if (!defending)
        {
            chance += chance * 0.02f * player.GetStat(StatIntellect);
        }

        return Math.Max(chance, 1f);
    }

    /// <summary>
    /// Rolls for a skill-up after one melee exchange, and applies it.
    /// </summary>
    /// <returns>The skill that went up, or 0.</returns>
    /// <remarks>
    /// Port of <c>Player::UpdateCombatSkills</c> and <c>UpdateWeaponSkill</c> together. Which skill
    /// goes up on the attacking side depends on what is in hand, and the two exceptions are worth
    /// keeping:
    /// <list type="bullet">
    /// <item>
    /// <b>Empty-handed raises Unarmed and Fist Weapons together.</b> They are meant to track each
    /// other, and letting them drift means picking up a fist weapon suddenly swings at a worse skill
    /// than your bare hands.
    /// </item>
    /// <item>
    /// <b>A fist weapon raises Unarmed as well as itself</b> — the same rule from the other side.
    /// </item>
    /// <item>
    /// A fishing pole raises nothing. Beating something to death with it is not fishing, and it is
    /// not swordsmanship either.
    /// </item>
    /// </list>
    /// </remarks>
    public static uint RollCombat(
        Player player, Unit opponent, bool defending, Func<uint, uint, uint> roll)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(opponent);
        ArgumentNullException.ThrowIfNull(roll);

        float chance = CombatChance(player, opponent, defending);

        if (chance <= 0f)
        {
            return 0;
        }

        // Percent, in tenths, so a chance of 1.5% is not rounded away.
        if (roll(1, 1000) > (uint)(chance * 10f))
        {
            return 0;
        }

        if (defending)
        {
            return Raise(player, SkillType.Defense) ? SkillType.Defense : 0;
        }

        Item? weapon = player.Inventory.Equipped(InventorySlots.MainHand);

        if (weapon is null)
        {
            bool unarmed = Raise(player, SkillType.Unarmed);

            Raise(player, SkillType.FistWeapons);

            return unarmed ? SkillType.Unarmed : 0;
        }

        if (weapon.Template.SubClass == FishingPoleSubClass
            && weapon.Template.Class == Data.Db.ItemClass.Weapon)
        {
            return 0;
        }

        uint skillId = SkillType.ForItem(weapon.Template.Class, weapon.Template.SubClass);

        if (skillId == SkillType.FistWeapons)
        {
            Raise(player, SkillType.Unarmed);
        }

        return Raise(player, skillId) ? skillId : 0;
    }

    /// <summary>
    /// Whether an exchange counts towards a skill-up at all.
    /// </summary>
    /// <remarks>
    /// Upstream's proc mask: a normal hit, a miss, a dodge or a parry all teach something. A crit
    /// and a glancing blow do not — you already connected, and there is nothing in the outcome to
    /// learn from.
    /// <para>
    /// A block is the odd one: it teaches the <i>defender</i> and nobody else, since blocking is
    /// what defence is for.
    /// </para>
    /// </remarks>
    public static bool Teaches(MeleeHitOutcome outcome, bool defending) => outcome switch
    {
        MeleeHitOutcome.Normal or MeleeHitOutcome.Miss
            or MeleeHitOutcome.Dodge or MeleeHitOutcome.Parry => true,
        MeleeHitOutcome.Block => defending,
        _ => false,
    };

    /// <summary><c>STAT_INTELLECT</c>, which is the index UNIT_FIELD_STAT0 counts from.</summary>
    private const int StatIntellect = 3;

    /// <summary><c>ITEM_SUBCLASS_WEAPON_FISHING_POLE</c>.</summary>
    private const byte FishingPoleSubClass = 20;
}
