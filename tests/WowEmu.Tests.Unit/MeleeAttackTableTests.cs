using WowEmu.Game;
using WowEmu.Game.Combat;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The melee attack table: one roll against a running sum.
/// </summary>
/// <remarks>
/// Every test here drives the roll rather than sampling it, because the table is a set of adjacent
/// half-open ranges and the only interesting values are their edges. A distribution test would need
/// millions of draws to notice a one-in-ten-thousand boundary error; an exact roll notices it once.
/// </remarks>
public sealed class MeleeAttackTableTests(ITestOutputHelper output)
{
    /// <summary>An even fight: same level, same skill, no chances set unless a test sets them.</summary>
    private static MeleeAttack Even(
        int miss = 0, int dodge = 0, int parry = 0, int block = 0, int crit = 0,
        int attackerLevel = 60, int victimLevel = 60,
        bool victimIsPlayer = false, bool attackerIsPlayerControlled = true,
        bool fromBehind = false,
        WeaponAttackType attackType = WeaponAttackType.BaseAttack) => new(
            AttackerLevel: attackerLevel,
            VictimLevel: victimLevel,
            AttackerWeaponSkill: attackerLevel * 5,
            AttackerMaxSkill: attackerLevel * 5,
            VictimDefenseSkill: victimLevel * 5,
            VictimMaxSkill: victimLevel * 5,
            MissChance: miss,
            DodgeChance: dodge,
            ParryChance: parry,
            BlockChance: block,
            CritChance: crit,
            VictimIsPlayer: victimIsPlayer,
            AttackerIsPlayerControlled: attackerIsPlayerControlled,
            AttackerIsBehindVictim: fromBehind,
            AttackType: attackType);

    /// <summary>Always returns the same roll, so a boundary can be walked exactly.</summary>
    private static Func<uint, uint, uint> Rolls(int value) => (_, _) => (uint)value;

    /// <summary>
    /// The roll is inclusive at both ends, so there are 10001 outcomes.
    /// </summary>
    /// <remarks>
    /// PLAN.md §6 calls this out specifically. With an exclusive upper bound the last ten-thousandth
    /// of the range would be unreachable — invisible in play, and exactly the kind of thing a
    /// differential test against the C++ would eventually surface.
    /// </remarks>
    [Fact]
    public void TheRoll_SpansElevenThousandAndOneValues()
    {
        uint low = uint.MaxValue, high = 0;

        MeleeAttackTable.Roll(Even(), (min, max) => { low = min; high = max; return min; });

        Assert.Equal(0u, low);
        Assert.Equal((uint)MeleeAttackTable.RollMax, high);
    }

    /// <summary>Nothing configured means every swing lands normally.</summary>
    [Fact]
    public void WithNoChances_EverySwingIsNormal()
    {
        for (int roll = 0; roll <= MeleeAttackTable.RollMax; roll += 137)
        {
            Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(Even(), Rolls(roll)));
        }
    }

    /// <summary>
    /// A range is claimed from zero up to but excluding its total.
    /// </summary>
    /// <remarks>
    /// The comparison is <c>roll &lt; sum</c>, so a 5 % miss owns rolls 0 to 499 and roll 500 is
    /// already past it. Off by one here shifts every outcome after it too.
    /// </remarks>
    [Fact]
    public void AChance_OwnsTheRollsBelowItsTotal()
    {
        MeleeAttack attack = Even(miss: 500);

        Assert.Equal(MeleeHitOutcome.Miss, MeleeAttackTable.Roll(attack, Rolls(0)));
        Assert.Equal(MeleeHitOutcome.Miss, MeleeAttackTable.Roll(attack, Rolls(499)));
        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(500)));
    }

    /// <summary>
    /// The outcomes are tested in a fixed order and each claims the range after the last.
    /// </summary>
    /// <remarks>
    /// This is the property that makes it a table rather than five independent rolls: the ranges are
    /// adjacent and mutually exclusive. Rolling separately per outcome would let a swing be both
    /// dodged and parried, and the chances would no longer sum to certainty.
    /// </remarks>
    [Fact]
    public void TheOutcomes_AreAdjacentRangesInOrder()
    {
        MeleeAttack attack = Even(miss: 1000, dodge: 1000, parry: 1000, block: 1000, crit: 1000);

        // miss 0-999, dodge 1000-1999, parry 2000-2999, block 3000-3999, crit 4000-4999.
        Assert.Equal(MeleeHitOutcome.Miss, MeleeAttackTable.Roll(attack, Rolls(999)));
        Assert.Equal(MeleeHitOutcome.Dodge, MeleeAttackTable.Roll(attack, Rolls(1000)));
        Assert.Equal(MeleeHitOutcome.Dodge, MeleeAttackTable.Roll(attack, Rolls(1999)));
        Assert.Equal(MeleeHitOutcome.Parry, MeleeAttackTable.Roll(attack, Rolls(2000)));
        Assert.Equal(MeleeHitOutcome.Parry, MeleeAttackTable.Roll(attack, Rolls(2999)));
        Assert.Equal(MeleeHitOutcome.Block, MeleeAttackTable.Roll(attack, Rolls(3000)));
        Assert.Equal(MeleeHitOutcome.Block, MeleeAttackTable.Roll(attack, Rolls(3999)));
        Assert.Equal(MeleeHitOutcome.Crit, MeleeAttackTable.Roll(attack, Rolls(4000)));
        Assert.Equal(MeleeHitOutcome.Crit, MeleeAttackTable.Roll(attack, Rolls(4999)));
        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(5000)));
    }

    /// <summary>
    /// Skill advantage is subtracted from dodge, parry and block — but never from miss.
    /// </summary>
    /// <remarks>
    /// Four hundredths of a percent per skill point. Miss is computed by the caller and arrives
    /// already adjusted, which is why the table leaves it alone; applying the bonus twice would make
    /// a skilled attacker miss far less than upstream.
    /// </remarks>
    [Fact]
    public void SkillAdvantage_ReducesTheDefensiveChancesOnly()
    {
        // Ten points of weapon skill over the victim's cap: 40 hundredths off each defence.
        MeleeAttack attack = Even(miss: 500, dodge: 500) with
        {
            AttackerWeaponSkill = (60 * 5) + 10,
        };

        // Miss is untouched: still 0-499.
        Assert.Equal(MeleeHitOutcome.Miss, MeleeAttackTable.Roll(attack, Rolls(499)));

        // Dodge is 500 - 40 = 460 wide, so it owns 500-959 and stops there.
        Assert.Equal(MeleeHitOutcome.Dodge, MeleeAttackTable.Roll(attack, Rolls(959)));
        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(960)));
    }

    /// <summary>A defence the skill bonus wipes out entirely is skipped, not clamped to zero.</summary>
    [Fact]
    public void ADefenceSmallerThanTheSkillBonus_IsSkipped()
    {
        MeleeAttack attack = Even(dodge: 100) with
        {
            AttackerWeaponSkill = (60 * 5) + 50,   // 200 hundredths of bonus against 100 of dodge
        };

        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    // ------------------------------------------------------------------ facing

    /// <summary>
    /// Only a player loses its dodge when attacked from behind.
    /// </summary>
    /// <remarks>
    /// A creature dodges from any direction. That asymmetry is deliberate upstream and is what makes
    /// a boss survivable to tank — "nobody dodges from behind" is the intuitive reading and it is
    /// wrong.
    /// </remarks>
    [Fact]
    public void FromBehind_OnlyAPlayerLosesItsDodge()
    {
        MeleeAttack againstPlayer = Even(dodge: 5000, victimIsPlayer: true, fromBehind: true);
        MeleeAttack againstCreature = Even(dodge: 5000, victimIsPlayer: false, fromBehind: true);

        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(againstPlayer, Rolls(0)));
        Assert.Equal(MeleeHitOutcome.Dodge, MeleeAttackTable.Roll(againstCreature, Rolls(0)));
    }

    /// <summary>Parry and block are lost by anyone attacked from behind.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromBehind_NobodyParriesOrBlocks(bool victimIsPlayer)
    {
        MeleeAttack attack = Even(parry: 5000, block: 5000, victimIsPlayer: victimIsPlayer, fromBehind: true);

        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    [Fact]
    public void FromTheFront_EverythingApplies()
    {
        MeleeAttack attack = Even(dodge: 1000, parry: 1000, block: 1000, victimIsPlayer: true);

        Assert.Equal(MeleeHitOutcome.Dodge, MeleeAttackTable.Roll(attack, Rolls(0)));
        Assert.Equal(MeleeHitOutcome.Parry, MeleeAttackTable.Roll(attack, Rolls(1000)));
        Assert.Equal(MeleeHitOutcome.Block, MeleeAttackTable.Roll(attack, Rolls(2000)));
    }

    // ------------------------------------------------------------------ glancing

    /// <summary>
    /// A player attacking something above its level takes glancing blows.
    /// </summary>
    /// <remarks>
    /// The mechanic that makes fighting up-level enemies feel bad, and it exists only in that
    /// direction — attacking downwards never glances.
    /// </remarks>
    [Fact]
    public void APlayerAttackingUpwards_Glances()
    {
        MeleeAttack attack = Even(attackerLevel: 60, victimLevel: 63);

        Assert.Equal(MeleeHitOutcome.Glancing, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    [Fact]
    public void APlayerAttackingDownwards_NeverGlances()
    {
        MeleeAttack attack = Even(attackerLevel: 63, victimLevel: 60);

        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    [Fact]
    public void ACreature_NeverGlances()
    {
        MeleeAttack attack = Even(attackerLevel: 60, victimLevel: 63, attackerIsPlayerControlled: false);

        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    [Fact]
    public void ARangedAttack_NeverGlances()
    {
        MeleeAttack attack = Even(
            attackerLevel: 60, victimLevel: 63, attackType: WeaponAttackType.RangedAttack);

        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    /// <summary>Glancing is capped at 40 %, however far the skills diverge.</summary>
    [Fact]
    public void Glancing_IsCappedAtFortyPercent()
    {
        MeleeAttack attack = Even(attackerLevel: 1, victimLevel: 60);

        Assert.Equal(MeleeHitOutcome.Glancing, MeleeAttackTable.Roll(attack, Rolls(MeleeAttackTable.MaxGlancingChance - 1)));
        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(MeleeAttackTable.MaxGlancingChance)));
    }

    // ------------------------------------------------------------------ crushing

    /// <summary>
    /// A creature four or more levels above its victim lands crushing blows.
    /// </summary>
    /// <remarks>
    /// Four levels is a hard threshold, not a curve: at three levels up there is no crushing at all.
    /// </remarks>
    [Fact]
    public void ACreatureFourLevelsUp_Crushes()
    {
        MeleeAttack attack = Even(
            attackerLevel: 64, victimLevel: 60,
            victimIsPlayer: true, attackerIsPlayerControlled: false);

        Assert.Equal(MeleeHitOutcome.Crushing, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    [Fact]
    public void ACreatureThreeLevelsUp_DoesNotCrush()
    {
        MeleeAttack attack = Even(
            attackerLevel: 63, victimLevel: 60,
            victimIsPlayer: true, attackerIsPlayerControlled: false);

        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    /// <summary>A player-controlled attacker never crushes, however far above its victim.</summary>
    [Fact]
    public void APlayer_NeverCrushes()
    {
        MeleeAttack attack = Even(
            attackerLevel: 80, victimLevel: 60,
            victimIsPlayer: true, attackerIsPlayerControlled: true);

        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    /// <summary>Crushing starts at 15 %, which is where the two-percent-per-point curve begins.</summary>
    [Fact]
    public void Crushing_StartsAtFifteenPercent()
    {
        // Exactly the 15-point gap: 15 * 200 - 1500 = 1500, so rolls 0-1499.
        MeleeAttack attack = Even(
            attackerLevel: 64, victimLevel: 60,
            victimIsPlayer: true, attackerIsPlayerControlled: false) with
        {
            AttackerMaxSkill = 315,
            VictimDefenseSkill = 300,
            VictimMaxSkill = 300,
        };

        Assert.Equal(MeleeHitOutcome.Crushing, MeleeAttackTable.Roll(attack, Rolls(1499)));
        Assert.Equal(MeleeHitOutcome.Normal, MeleeAttackTable.Roll(attack, Rolls(1500)));
    }

    /// <summary>Defence above the victim's own cap does not help against crushing.</summary>
    [Fact]
    public void DefenceAboveTheCap_DoesNotPreventCrushing()
    {
        MeleeAttack attack = Even(
            attackerLevel: 64, victimLevel: 60,
            victimIsPlayer: true, attackerIsPlayerControlled: false) with
        {
            AttackerMaxSkill = 320,
            VictimDefenseSkill = 9999,   // far above the cap
            VictimMaxSkill = 300,
        };

        // The excess is discarded, so the gap is still 320 - 300 = 20 points.
        Assert.Equal(MeleeHitOutcome.Crushing, MeleeAttackTable.Roll(attack, Rolls(0)));
    }

    // ------------------------------------------------------------------ distribution

    /// <summary>
    /// Over a million swings the outcomes land within a tenth of a percent of their chances.
    /// </summary>
    /// <remarks>
    /// The exact tests above pin the boundaries; this pins that the boundaries are the <i>only</i>
    /// thing deciding the outcome. A table that used a fresh roll per outcome would pass every
    /// boundary test and fail this one badly.
    /// <para>
    /// PLAN.md §6 asks for this comparison against the C++ server itself. That needs one running,
    /// which is a separate exercise — this checks against the chances we asked for, which catches
    /// everything except both implementations being wrong the same way.
    /// </para>
    /// </remarks>
    [Fact]
    public void OverAMillionSwings_TheDistributionMatchesTheChances()
    {
        const int Swings = 1_000_000;

        MeleeAttack attack = Even(miss: 500, dodge: 500, parry: 500, block: 500, crit: 1500);

        WowEmu.Core.GameRandom.SeedCurrentThread(20260805);

        Dictionary<MeleeHitOutcome, int> counts = [];

        for (int i = 0; i < Swings; i++)
        {
            MeleeHitOutcome outcome = MeleeAttackTable.Roll(attack, WowEmu.Core.GameRandom.Urand);
            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
        }

        // Chances are in hundredths of a percent over a range of 10001.
        AssertShare(counts, MeleeHitOutcome.Miss, 500);
        AssertShare(counts, MeleeHitOutcome.Dodge, 500);
        AssertShare(counts, MeleeHitOutcome.Parry, 500);
        AssertShare(counts, MeleeHitOutcome.Block, 500);
        AssertShare(counts, MeleeHitOutcome.Crit, 1500);
        AssertShare(counts, MeleeHitOutcome.Normal, MeleeAttackTable.RollMax - 3500);

        foreach ((MeleeHitOutcome outcome, int count) in counts.OrderByDescending(entry => entry.Value))
        {
            output.WriteLine($"  {outcome,-9} {count,8:N0}  {100.0 * count / Swings,6:F2}%");
        }

        void AssertShare(Dictionary<MeleeHitOutcome, int> observed, MeleeHitOutcome outcome, int expectedInTenThousandths)
        {
            double expected = expectedInTenThousandths / (double)(MeleeAttackTable.RollMax + 1);
            double actual = observed.GetValueOrDefault(outcome) / (double)Swings;

            Assert.True(
                Math.Abs(actual - expected) < 0.001,
                $"{outcome}: expected {expected:P2}, got {actual:P2}");
        }
    }
}
