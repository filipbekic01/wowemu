namespace WowEmu.Game.Combat;

/// <summary>
/// How much of a physical hit the victim's armour absorbs.
/// </summary>
/// <remarks>
/// Port of <c>Unit::CalcArmorReducedDamage</c>, minus the aura and armour-penetration terms — those
/// need auras and item ratings, and the caller subtracts them from <c>armor</c> before calling.
/// </remarks>
public static class ArmorMitigation
{
    /// <summary>Armour can never take more than three quarters of a hit.</summary>
    public const float MaxReduction = 0.75f;

    /// <summary>Above this level the effective level used in the denominator grows faster.</summary>
    public const int SuperlinearLevel = 59;

    /// <summary>
    /// The fraction of a physical hit that <paramref name="armor"/> absorbs, in <c>[0, 0.75]</c>.
    /// </summary>
    /// <remarks>
    /// <c>0.1 × armor / (8.5 × level + 40)</c>, fed through <c>x / (1 + x)</c> so the curve saturates
    /// instead of ever reaching total immunity — doubling armour never doubles effective health.
    /// <para>
    /// Past level 59 the level term becomes <c>level + 4.5 × (level - 59)</c>. Without that kink the
    /// same armour value is far stronger at level 80 than Blizzard intended, and every raid-level
    /// number comes out wrong while every levelling number looks fine.
    /// </para>
    /// </remarks>
    public static float ReductionFor(float armor, uint attackerLevel)
    {
        if (armor < 0f)
        {
            armor = 0f;
        }

        float levelModifier = attackerLevel;

        if (levelModifier > SuperlinearLevel)
        {
            levelModifier += 4.5f * (levelModifier - SuperlinearLevel);
        }

        float value = 0.1f * armor / ((8.5f * levelModifier) + 40f);
        value /= 1.0f + value;

        return Math.Clamp(value, 0f, MaxReduction);
    }

    /// <summary>
    /// Applies <see cref="ReductionFor"/> to a hit.
    /// </summary>
    /// <remarks>
    /// Rounded <b>up</b>. That is not a rounding preference: it is what stops heavy armour reducing a
    /// small hit to nothing, so an over-armoured target still takes 1 damage per swing rather than
    /// becoming quietly invulnerable.
    /// </remarks>
    public static uint Reduce(uint damage, float armor, uint attackerLevel)
    {
        float reduced = damage * (1f - ReductionFor(armor, attackerLevel));

        return (uint)MathF.Ceiling(MathF.Max(reduced, 0f));
    }
}
