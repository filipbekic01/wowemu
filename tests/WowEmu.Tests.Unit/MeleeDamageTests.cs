using WowEmu.Game.Combat;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Armour mitigation: the curve, its kink at 59, and its two clamps.
/// </summary>
public sealed class ArmorMitigationTests(ITestOutputHelper output)
{
    [Fact]
    public void NoArmour_TakesTheWholeHit()
    {
        Assert.Equal(0f, ArmorMitigation.ReductionFor(0f, 60));
        Assert.Equal(100u, ArmorMitigation.Reduce(100, 0f, 60));
    }

    /// <summary>Negative armour is treated as none rather than as a damage bonus.</summary>
    [Fact]
    public void NegativeArmour_IsNotADamageBonus()
    {
        Assert.Equal(0f, ArmorMitigation.ReductionFor(-5000f, 60));
        Assert.Equal(100u, ArmorMitigation.Reduce(100, -5000f, 60));
    }

    /// <summary>
    /// Armour saturates at three quarters, however much of it there is.
    /// </summary>
    /// <remarks>
    /// The cap is what keeps armour from being an alternative to health. Without it the curve still
    /// approaches 1 asymptotically, so the failure would be gradual rather than obvious.
    /// </remarks>
    [Fact]
    public void Armour_SaturatesAtThreeQuarters()
    {
        Assert.Equal(ArmorMitigation.MaxReduction, ArmorMitigation.ReductionFor(1_000_000f, 60));
        Assert.Equal(ArmorMitigation.MaxReduction, ArmorMitigation.ReductionFor(float.MaxValue, 80));

        Assert.Equal(250u, ArmorMitigation.Reduce(1000, 1_000_000f, 60));
    }

    /// <summary>
    /// A hit is never mitigated away entirely — the result is rounded up.
    /// </summary>
    /// <remarks>
    /// At the 75 % cap a 1-damage hit mathematically becomes 0.25. Rounding it down would make a
    /// heavily-armoured target immune to weak attacks, which is a very different game than one where
    /// they take 1 per swing.
    /// </remarks>
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void ASmallHit_IsNeverMitigatedToZero(uint damage)
    {
        Assert.True(ArmorMitigation.Reduce(damage, 1_000_000f, 80) > 0, "armour made the target immune");
    }

    /// <summary>More armour always reduces at least as much, never less.</summary>
    [Fact]
    public void TheCurve_IsMonotonic()
    {
        float previous = -1f;

        for (float armor = 0f; armor < 30_000f; armor += 250f)
        {
            float reduction = ArmorMitigation.ReductionFor(armor, 60);

            Assert.True(reduction >= previous, $"reduction fell at {armor} armour");
            previous = reduction;
        }
    }

    /// <summary>
    /// Past level 59 the same armour is worth less, because the level term grows faster.
    /// </summary>
    /// <remarks>
    /// This is the kink that makes armour scale sensibly through two expansions. It is easy to leave
    /// out — everything below 60 is identical either way — and the resulting numbers only look wrong
    /// at raid level.
    /// </remarks>
    [Fact]
    public void PastFiftyNine_ArmourIsWorthLess()
    {
        const float Armor = 6000f;

        float atFiftyNine = ArmorMitigation.ReductionFor(Armor, 59);
        float atSixty = ArmorMitigation.ReductionFor(Armor, 60);
        float atEighty = ArmorMitigation.ReductionFor(Armor, 80);

        Assert.True(atSixty < atFiftyNine, "the level-60 step is missing");
        Assert.True(atEighty < atSixty);

        // The break really is at 59, not 60: the step from 58 to 59 is the ordinary one.
        float atFiftyEight = ArmorMitigation.ReductionFor(Armor, 58);

        Assert.True(atFiftyNine - atSixty > atFiftyEight - atFiftyNine, "the kink is at the wrong level");

        output.WriteLine($"{Armor:F0} armour: 58 {atFiftyEight:P1}, 59 {atFiftyNine:P1}, 60 {atSixty:P1}, 80 {atEighty:P1}");
    }
}

/// <summary>
/// What each outcome does to the damage number.
/// </summary>
public sealed class MeleeDamageTests
{
    private const uint Level = 60;

    private static MeleeDamageInfo Apply(
        MeleeHitOutcome outcome, uint damage = 100, uint blockValue = 0, uint victimLevel = Level) =>
        MeleeDamage.Apply(outcome, damage, Level, victimLevel, blockValue);

    [Fact]
    public void ANormalHit_LandsForWhatItRolled()
    {
        MeleeDamageInfo info = Apply(MeleeHitOutcome.Normal);

        Assert.Equal(100u, info.Damage);
        Assert.Equal(0u, info.CleanDamage);
        Assert.Equal(VictimState.Hit, info.VictimState);
        Assert.Equal(HitInfo.AffectsVictim, info.HitInfo);
    }

    [Fact]
    public void ACrit_LandsForDouble()
    {
        MeleeDamageInfo info = Apply(MeleeHitOutcome.Crit);

        Assert.Equal(200u, info.Damage);
        Assert.Equal(VictimState.Hit, info.VictimState);
        Assert.True(info.HitInfo.HasFlag(HitInfo.CriticalHit));
    }

    /// <summary>A crushing blow is 150 %, with the odd half truncated.</summary>
    [Theory]
    [InlineData(100u, 150u)]
    [InlineData(101u, 151u)]
    [InlineData(1u, 1u)]
    public void ACrushingBlow_LandsForOneAndAHalf(uint damage, uint expected)
    {
        MeleeDamageInfo info = Apply(MeleeHitOutcome.Crushing, damage);

        Assert.Equal(expected, info.Damage);
        Assert.True(info.HitInfo.HasFlag(HitInfo.Crushing));
    }

    /// <summary>A miss deals nothing and generates nothing.</summary>
    [Fact]
    public void AMiss_DealsNothingAndGeneratesNothing()
    {
        MeleeDamageInfo info = Apply(MeleeHitOutcome.Miss);

        Assert.Equal(0u, info.Damage);
        Assert.Equal(0u, info.CleanDamage);
        Assert.Equal(VictimState.Intact, info.VictimState);
        Assert.True(info.HitInfo.HasFlag(HitInfo.Miss));
        Assert.False(info.HitInfo.HasFlag(HitInfo.AffectsVictim));
    }

    /// <summary>
    /// A dodged or parried swing deals nothing but still counts as clean damage.
    /// </summary>
    /// <remarks>
    /// This is what pays the attacker's rage and the victim's threat. Zeroing both — the intuitive
    /// reading of "nothing happened" — makes a tank lose rage every time a boss parries, which is
    /// how the mechanic is felt rather than seen.
    /// </remarks>
    [Theory]
    [InlineData(MeleeHitOutcome.Dodge, VictimState.Dodge)]
    [InlineData(MeleeHitOutcome.Parry, VictimState.Parry)]
    public void ADodgeOrParry_StillGeneratesCleanDamage(MeleeHitOutcome outcome, VictimState expected)
    {
        MeleeDamageInfo info = Apply(outcome);

        Assert.Equal(0u, info.Damage);
        Assert.Equal(100u, info.CleanDamage);
        Assert.Equal(expected, info.VictimState);

        // Not a miss: the swing connected, the victim did something about it.
        Assert.False(info.HitInfo.HasFlag(HitInfo.Miss));
        Assert.True(info.HitInfo.HasFlag(HitInfo.AffectsVictim));
    }

    /// <summary>
    /// An evade generates nothing at all — not even clean damage.
    /// </summary>
    /// <remarks>
    /// The one outcome that differs from dodge and parry, and it has to: an evading creature is
    /// resetting, and giving it threat would put it straight back into the fight it just left.
    /// </remarks>
    [Fact]
    public void AnEvade_GeneratesNothingAtAll()
    {
        MeleeDamageInfo info = Apply(MeleeHitOutcome.Evade);

        Assert.Equal(0u, info.Damage);
        Assert.Equal(0u, info.CleanDamage);
        Assert.Equal(VictimState.Evades, info.VictimState);
    }

    // ------------------------------------------------------------------ block

    /// <summary>Block takes a flat amount off, not a fraction.</summary>
    [Fact]
    public void APartialBlock_SubtractsTheShieldValue()
    {
        MeleeDamageInfo info = Apply(MeleeHitOutcome.Block, damage: 100, blockValue: 30);

        Assert.Equal(70u, info.Damage);
        Assert.Equal(30u, info.BlockedAmount);
        Assert.Equal(30u, info.CleanDamage);

        // Still a hit — the client draws the damage, with the block flag beside it.
        Assert.Equal(VictimState.Hit, info.VictimState);
        Assert.True(info.HitInfo.HasFlag(HitInfo.Block));
    }

    /// <summary>
    /// A shield bigger than the hit stops all of it, and reports only what it stopped.
    /// </summary>
    /// <remarks>
    /// Reporting the shield's full value instead would have the client draw a block larger than the
    /// swing that caused it.
    /// </remarks>
    [Fact]
    public void AFullBlock_ReportsTheHitAndNotTheShield()
    {
        MeleeDamageInfo info = Apply(MeleeHitOutcome.Block, damage: 40, blockValue: 500);

        Assert.Equal(0u, info.Damage);
        Assert.Equal(40u, info.BlockedAmount);
        Assert.Equal(40u, info.CleanDamage);
        Assert.Equal(VictimState.Blocks, info.VictimState);
    }

    /// <summary>A block with no shield behind it takes nothing off.</summary>
    [Fact]
    public void ABlockWithNoShield_TakesNothingOff()
    {
        MeleeDamageInfo info = Apply(MeleeHitOutcome.Block, damage: 100, blockValue: 0);

        Assert.Equal(100u, info.Damage);
        Assert.Equal(0u, info.BlockedAmount);
    }

    // ------------------------------------------------------------------ glancing

    /// <summary>Glancing loses 10 % per level of difference, down to a floor of 70 %.</summary>
    [Theory]
    [InlineData(60u, 100u)]   // same level, no reduction
    [InlineData(61u, 90u)]
    [InlineData(62u, 80u)]
    [InlineData(63u, 70u)]
    [InlineData(73u, 70u)]    // capped at three levels, not ten
    public void AGlancingBlow_LosesTenPercentPerLevel(uint victimLevel, uint expected)
    {
        MeleeDamageInfo info = Apply(MeleeHitOutcome.Glancing, damage: 100, victimLevel: victimLevel);

        Assert.Equal(expected, info.Damage);
        Assert.Equal(100u - expected, info.CleanDamage);
        Assert.True(info.HitInfo.HasFlag(HitInfo.Glancing));
    }

    /// <summary>What glancing removes is preserved as clean damage rather than lost.</summary>
    [Fact]
    public void Glancing_AccountsForEveryPointItRemoved()
    {
        for (uint damage = 1; damage <= 500; damage++)
        {
            MeleeDamageInfo info = MeleeDamage.Apply(MeleeHitOutcome.Glancing, damage, Level, Level + 2);

            Assert.Equal(damage, info.Damage + info.CleanDamage);
        }
    }

    // ------------------------------------------------------------------ ordering

    /// <summary>
    /// Armour is applied before the outcome multiplier, not after.
    /// </summary>
    /// <remarks>
    /// The two orders differ because of the <c>ceil</c> between them: mitigating then critting gives
    /// <c>ceil(x) × 2</c>, which is always even, while critting then mitigating gives
    /// <c>ceil(2x)</c>, which can be odd. They agree whenever the mitigated value happens to land
    /// past the half — which is most of the time, and is why this is easy to get wrong and hard to
    /// notice.
    /// </remarks>
    [Fact]
    public void Armour_IsAppliedBeforeTheOutcomeMultiplier()
    {
        const float Armor = 3000f;

        int divergences = 0;

        for (uint raw = 1; raw <= 400; raw++)
        {
            uint mitigated = ArmorMitigation.Reduce(raw, Armor, Level);
            uint ours = MeleeDamage.Apply(MeleeHitOutcome.Crit, mitigated, Level, Level).Damage;

            // Ours doubles what armour already took its cut of.
            Assert.Equal(mitigated * 2, ours);

            if (ArmorMitigation.Reduce(raw * 2, Armor, Level) != ours)
            {
                divergences++;
            }
        }

        // Half the inputs, give or take: enough that the wrong order is a real difference and not a
        // theoretical one.
        Assert.True(divergences > 100, $"only {divergences} of 400 inputs distinguish the two orders");
    }

    [Fact]
    public void AnOffHandSwing_IsFlaggedAsOne()
    {
        MeleeDamageInfo info = MeleeDamage.Apply(
            MeleeHitOutcome.Normal, 100, Level, Level, blockValue: 0, isOffHand: true);

        Assert.True(info.HitInfo.HasFlag(HitInfo.OffHand));
    }
}
