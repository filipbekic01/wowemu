using WowEmu.Data.Client;

namespace WowEmu.Game.Combat;

/// <summary>
/// The effect ids this server handles. <c>SpellEffects</c>.
/// </summary>
/// <remarks>
/// Four of upstream's 164. PLAN.md §7 risk 4 caps M5 at roughly 25 effect handlers precisely so the
/// spell system does not become the whole project; these are the ones a damage spell and a heal
/// need, and the rest arrive on demand.
/// </remarks>
public static class SpellEffectId
{
    /// <summary>Direct damage of the spell's own school.</summary>
    public const uint SchoolDamage = 2;

    /// <summary>Restores health.</summary>
    public const uint Heal = 10;

    /// <summary>
    /// Puts an aura on the target.
    /// </summary>
    /// <remarks>
    /// The most common effect in the whole file. What it <i>does</i> is decided by the effect's
    /// <c>ApplyAuraName</c>, not by this id — see <see cref="AuraType"/>.
    /// </remarks>
    public const uint ApplyAura = 6;

    /// <summary>Weapon damage plus a flat bonus, dealt in no particular school.</summary>
    public const uint WeaponDamageNoSchool = 17;

    /// <summary>Weapon damage plus a flat bonus.</summary>
    public const uint WeaponDamage = 58;

    /// <summary>Weapon damage computed from a normalised weapon speed rather than the real one.</summary>
    public const uint NormalizedWeaponDamage = 121;

    /// <summary>Whether an effect is one of the three weapon-damage forms.</summary>
    /// <remarks>
    /// The three differ in how the weapon roll is produced, not in what is done with it — all of
    /// them add the effect's calculated value on top as a flat bonus.
    /// </remarks>
    public static bool IsWeaponDamage(uint effect) =>
        effect is WeaponDamage or WeaponDamageNoSchool or NormalizedWeaponDamage;
}

/// <summary>What one spell did to one target.</summary>
/// <param name="Damage">Health taken, after mitigation.</param>
/// <param name="Healing">Health given.</param>
/// <param name="SchoolMask">Which school the damage was, for the combat log.</param>
/// <param name="Resisted">How much resistance absorbed. Always zero until resistances exist.</param>
/// <param name="Blocked">How much a shield blocked. Always zero for a spell.</param>
/// <param name="IsPhysical">
/// Whether the client should describe this as a physical hit. Decides which combat-log line it
/// prints, so it is a display flag rather than a mitigation one.
/// </param>
public readonly record struct SpellHit(
    uint Damage,
    uint Healing,
    uint SchoolMask,
    uint Resisted = 0,
    uint Blocked = 0,
    bool IsPhysical = false)
{
    /// <summary>Whether anything happened worth telling the client about.</summary>
    public bool IsAnything => Damage > 0 || Healing > 0;
}

/// <summary>
/// Turns a spell's effects into damage or healing.
/// </summary>
/// <remarks>
/// Port of <c>SpellEffectInfo::CalcValue</c> and the handful of <c>Spell::Effect*</c> handlers M5
/// needs.
/// </remarks>
public static class SpellEffects
{
    /// <summary>Physical, which is the school armour applies to. <c>SPELL_SCHOOL_MASK_NORMAL</c>.</summary>
    public const uint PhysicalSchoolMask = 1;

    /// <summary>
    /// The magnitude of one effect for a given caster.
    /// </summary>
    /// <remarks>
    /// Port of <c>SpellEffectInfo::CalcValue</c>.
    /// <list type="bullet">
    /// <item><b>The die roll is <c>irand(1, sides)</c>, not <c>irand(0, sides)</c></b>, which is why
    /// <c>BasePoints</c> is stored one below the minimum. One side adds exactly 1 and consumes no
    /// random draw at all — matching upstream's explicit <c>case 1</c>.</item>
    /// <item>The level used for scaling is <i>clamped</i> into the spell's own range and then
    /// reduced by <c>max(BaseLevel, SpellLevel)</c>. Using the caster's raw level makes a low-rank
    /// spell scale forever.</item>
    /// </list>
    /// <para>
    /// Upstream also rescales a <i>creature's</i> spell damage by the ratio of its level's base
    /// damage to the spell's, for spells flagged to scale with creature level. That needs the
    /// creature stats table threaded through and is not here — a creature casting such a spell hits
    /// for its spell's own listed value.
    /// </para>
    /// </remarks>
    public static int CalculateValue(SpellEntry spell, in SpellEffectEntry effect, uint casterLevel, Func<int, int, int> roll)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(roll);

        int basePoints = effect.BasePoints;

        if (effect.RealPointsPerLevel != 0f)
        {
            int level = (int)casterLevel;

            if (spell.MaxLevel > 0 && level > (int)spell.MaxLevel)
            {
                level = (int)spell.MaxLevel;
            }
            else if (level < (int)spell.BaseLevel)
            {
                level = (int)spell.BaseLevel;
            }

            level -= (int)Math.Max(spell.BaseLevel, spell.SpellLevel);

            basePoints += (int)(level * effect.RealPointsPerLevel);
        }

        // The roll is over [1, sides] as of 3.3.3. A single side is the flat case and is handled
        // without drawing, which keeps the random stream in step with upstream's.
        basePoints += effect.DieSides switch
        {
            0 => 0,
            1 => 1,
            > 1 => roll(1, effect.DieSides),
            _ => roll(effect.DieSides, 1),
        };

        return basePoints;
    }

    /// <summary>
    /// Applies a spell's effects to one target.
    /// </summary>
    /// <remarks>
    /// Every used effect slot is walked and its contribution added, because a spell can carry more
    /// than one damage effect and upstream sums them into a single <c>m_damage</c>.
    /// <para>
    /// <b>Armour is applied once, at the end</b>, rather than per effect. Mitigating each effect
    /// separately rounds up once per effect — see <see cref="ArmorMitigation.Reduce"/>, which
    /// ceilings — and a three-effect spell would be two points stronger than it should be.
    /// </para>
    /// </remarks>
    /// <param name="pick">Draws the effect rolls and the weapon swing.</param>
    public static SpellHit Apply(
        Unit caster,
        Unit target,
        SpellEntry spell,
        Func<uint, uint, uint> pick)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(pick);

        int Roll(int min, int max) => (int)pick((uint)min, (uint)max);

        int damage = 0;
        int healing = 0;
        int weaponBonus = 0;
        bool hasWeaponEffect = false;

        foreach (SpellEffectEntry effect in spell.Effects)
        {
            if (!effect.IsUsed)
            {
                continue;
            }

            int value = CalculateValue(spell, effect, caster.Level, Roll);

            if (effect.Effect == SpellEffectId.SchoolDamage)
            {
                damage += value;
            }
            else if (effect.Effect == SpellEffectId.Heal)
            {
                healing += value;
            }
            else if (SpellEffectId.IsWeaponDamage(effect.Effect))
            {
                // The value is a flat bonus on top of a weapon swing, not damage in its own right.
                // Adding it directly makes an ability that reads "swing plus 11" hit for 11.
                hasWeaponEffect = true;
                weaponBonus += value;
            }
        }

        if (hasWeaponEffect)
        {
            damage += (int)caster.RollSwingDamage(pick) + weaponBonus;
        }

        uint schoolMask = spell.SchoolMask;
        bool physical = (schoolMask & PhysicalSchoolMask) != 0;

        uint finalDamage = damage > 0 ? (uint)damage : 0;

        // Only physical damage goes through armour. A frostbolt reduced by armour would be a plate
        // wearer taking half damage from every school, which is a very different game.
        if (finalDamage > 0 && physical)
        {
            finalDamage = ArmorMitigation.Reduce(finalDamage, target.Armor, caster.Level);
        }

        return new SpellHit(
            Damage: finalDamage,
            Healing: healing > 0 ? (uint)healing : 0,
            SchoolMask: schoolMask,
            IsPhysical: physical);
    }
}
