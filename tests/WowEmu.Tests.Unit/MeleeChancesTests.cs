using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;
using Xunit.Abstractions;
using GameUnit = WowEmu.Game.Unit;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The chances that feed the attack table.
/// </summary>
public sealed class MeleeChancesTests(ITestOutputHelper output)
{
    /// <summary>An even fight has the 5 % base miss and nothing else.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEvenFight_MissesFivePercent(bool victimIsPlayer) =>
        Assert.Equal(MeleeChances.BaseMissChance, MeleeChances.MissChance(0, victimIsPlayer), 0.001f);

    /// <summary>
    /// Against a player, falling behind on skill costs twice what getting ahead saves.
    /// </summary>
    /// <remarks>
    /// 0.04 per point down against 0.02 per point up. A symmetric version would look fine in
    /// isolation and quietly make every duel between mismatched levels closer than it should be.
    /// </remarks>
    [Fact]
    public void AgainstAPlayer_TheSkillCurveIsAsymmetric()
    {
        // Ten points behind: 10 × 0.04 = 0.4 more miss.
        Assert.Equal(5.4f, MeleeChances.MissChance(-10, victimIsPlayer: true), 0.001f);

        // Ten points ahead: only 10 × 0.02 = 0.2 less.
        Assert.Equal(4.8f, MeleeChances.MissChance(10, victimIsPlayer: true), 0.001f);
    }

    /// <summary>
    /// Against a creature there is a cliff at ten points behind, not a straight line.
    /// </summary>
    /// <remarks>
    /// Under ten points each is worth 0.1; past ten each is worth 0.4. Two levels down is a fifteen
    /// point deficit, which is why attacking something three levels above you stops feeling merely
    /// harder and starts feeling futile.
    /// </remarks>
    [Theory]
    [InlineData(0, 5.0f)]
    [InlineData(-5, 5.5f)]      // 5 × 0.1
    [InlineData(-10, 6.0f)]     // 10 × 0.1, still on the shallow slope
    [InlineData(-11, 6.4f)]     // over the cliff: 5 + 1 + 1 × 0.4
    [InlineData(-15, 8.0f)]     // 5 + 1 + 5 × 0.4
    [InlineData(-25, 12.0f)]    // 5 + 1 + 15 × 0.4
    public void AgainstACreature_TheSkillCurveHasACliff(int skillDifference, float expected) =>
        Assert.Equal(expected, MeleeChances.MissChance(skillDifference, victimIsPlayer: false), 0.001f);

    /// <summary>The cliff is at ten points behind, and only in that direction.</summary>
    [Fact]
    public void TheCliff_IsOnlyOnTheLosingSide()
    {
        float atTen = MeleeChances.MissChance(-10, victimIsPlayer: false);
        float atEleven = MeleeChances.MissChance(-11, victimIsPlayer: false);
        float atNine = MeleeChances.MissChance(-9, victimIsPlayer: false);

        Assert.True(atEleven - atTen > (atTen - atNine) * 3, "the cliff is missing or in the wrong place");

        // Ahead by ten is the shallow slope in reverse, not the steep one.
        Assert.Equal(4.0f, MeleeChances.MissChance(10, victimIsPlayer: false), 0.001f);
    }

    /// <summary>Miss is clamped at both ends: never negative, never past 60 %.</summary>
    [Fact]
    public void Miss_IsClampedAtBothEnds()
    {
        Assert.Equal(0f, MeleeChances.MissChance(10_000, victimIsPlayer: false));
        Assert.Equal(0f, MeleeChances.MissChance(10_000, victimIsPlayer: true));

        Assert.Equal(MeleeChances.MaxMissChance, MeleeChances.MissChance(-10_000, victimIsPlayer: false));
        Assert.Equal(MeleeChances.MaxMissChance, MeleeChances.MissChance(-10_000, victimIsPlayer: true));
    }

    /// <summary>Only a humanoid parries; a beast has nothing to parry with.</summary>
    [Theory]
    [InlineData(MeleeChances.HumanoidCreatureType, MeleeChances.HumanoidParryChance)]
    [InlineData((byte)1, 0f)]   // beast
    [InlineData((byte)6, 0f)]   // undead
    public void OnlyAHumanoid_Parries(byte creatureType, float expected) =>
        Assert.Equal(expected, MeleeChances.CreatureParry(isWorldBoss: false, creatureType));

    /// <summary>A world boss parries whatever it is, and far more than a humanoid.</summary>
    [Fact]
    public void AWorldBoss_ParriesRegardlessOfType()
    {
        Assert.Equal(MeleeChances.BossParryChance, MeleeChances.CreatureParry(isWorldBoss: true, creatureType: 1));
        Assert.True(MeleeChances.BossParryChance > MeleeChances.HumanoidParryChance);
    }

    [Fact]
    public void AWorldBoss_DodgesMoreThanAnythingElse() =>
        Assert.True(MeleeChances.CreatureDodge(true) > MeleeChances.CreatureDodge(false));

    /// <summary>Crit moves with the skill difference and never goes negative.</summary>
    [Fact]
    public void Crit_TracksTheSkillDifference()
    {
        // Even: just the base.
        Assert.Equal(5f, MeleeChances.CritChance(5f, 300, 300), 0.001f);

        // Twenty-five points ahead: 25 × 0.04 = 1 more.
        Assert.Equal(6f, MeleeChances.CritChance(5f, 325, 300), 0.001f);

        // Far enough behind to go negative, which clamps rather than subtracting from hits.
        Assert.Equal(0f, MeleeChances.CritChance(5f, 100, 1000), 0.001f);
    }

    // ------------------------------------------------------------------ assembling a swing

    /// <summary>
    /// Percentages become hundredths exactly once, by truncation.
    /// </summary>
    /// <remarks>
    /// A boss's 5.85 % dodge has to arrive as 585, not as 5. The conversion is a truncation of a
    /// <i>float</i> product, which is not the same as truncating the decimal you wrote down: 13.4 is
    /// not representable, but 13.4f × 100 rounds to exactly 1340.0f, so parry comes out 1340 rather
    /// than the 1339 the decimal arithmetic would suggest. Doing the multiplication in double instead
    /// gives 1339 and quietly disagrees with the C++ on every chance with an awkward fraction.
    /// </remarks>
    [Fact]
    public void TheChances_ArriveAsHundredthsOfAPercent()
    {
        Creature boss = CreatureFixture.Build(rank: MeleeChances.WorldBossRank);
        Creature attacker = CreatureFixture.Build();

        MeleeAttack attack = MeleeChances.For(attacker, boss, WeaponAttackType.BaseAttack);

        Assert.Equal(585, attack.DodgeChance);
        Assert.Equal(1340, attack.ParryChance);
        Assert.Equal(500, attack.BlockChance);

        // The float product really is what decides it, and it is not the decimal answer.
        Assert.Equal(1340, (int)(MeleeChances.BossParryChance * 100));
        Assert.Equal(1339, (int)((double)MeleeChances.BossParryChance * 100));
    }

    /// <summary>A creature's skills are its level cap, both offensive and defensive.</summary>
    [Fact]
    public void ACreaturesSkills_AreItsLevelCap()
    {
        Creature creature = CreatureFixture.Build();

        Assert.Equal(creature.Level * 5, creature.WeaponSkillValue);
        Assert.Equal(creature.Level * 5, creature.DefenseSkillValue);
        Assert.False(creature.IsPlayerControlled);
    }

    /// <summary>The flags-extra bits reach the assembled swing.</summary>
    [Fact]
    public void TheCreatureFlags_ReachTheAssembledSwing()
    {
        Creature attacker = CreatureFixture.Build(
            flagsExtra: (uint)(CreatureFlagsExtra.NoCrit | CreatureFlagsExtra.NoCrushingBlows));

        Creature victim = CreatureFixture.Build(
            flagsExtra: (uint)(CreatureFlagsExtra.NoDodge | CreatureFlagsExtra.NoParry));

        MeleeAttack attack = MeleeChances.For(attacker, victim, WeaponAttackType.BaseAttack);

        Assert.False(attack.AttackerCanCrit);
        Assert.False(attack.AttackerCanCrush);
        Assert.False(attack.VictimCanDodge);
        Assert.False(attack.VictimCanParry);
        Assert.True(attack.VictimCanBlock);
    }

    // ------------------------------------------------------------------ the whole swing

    /// <summary>
    /// A full swing produces something sane over a thousand rolls.
    /// </summary>
    /// <remarks>
    /// The three pieces are each pinned exactly elsewhere; this checks that connecting them does not
    /// produce anything impossible — damage above the crit ceiling, or a hit that lands on a miss.
    /// </remarks>
    [Fact]
    public void AFullSwing_StaysWithinItsBounds()
    {
        Creature attacker = CreatureFixture.Build();
        Creature victim = CreatureFixture.Build();

        WowEmu.Core.GameRandom.SeedCurrentThread(20260805);

        uint ceiling = (uint)MathF.Ceiling(attacker.MaxDamage) * MeleeDamage.CritMultiplier;
        Dictionary<MeleeHitOutcome, int> counts = [];

        for (int i = 0; i < 1000; i++)
        {
            MeleeDamageInfo info = attacker.CalculateMeleeDamage(
                victim, WeaponAttackType.BaseAttack, WowEmu.Core.GameRandom.Urand);

            counts[info.Outcome] = counts.GetValueOrDefault(info.Outcome) + 1;

            Assert.True(info.Damage <= ceiling, $"{info.Outcome} dealt {info.Damage}, over the {ceiling} ceiling");

            if (info.Outcome is MeleeHitOutcome.Miss or MeleeHitOutcome.Dodge or MeleeHitOutcome.Parry)
            {
                Assert.Equal(0u, info.Damage);
            }
        }

        // Both fixtures are level 5 with 5 % dodge, block and crit and no parry, so the common
        // outcomes must all show up in a thousand swings.
        Assert.True(counts.ContainsKey(MeleeHitOutcome.Normal), "no normal hits in 1000 swings");
        Assert.True(counts.ContainsKey(MeleeHitOutcome.Miss), "no misses in 1000 swings");
        Assert.True(counts.ContainsKey(MeleeHitOutcome.Crit), "no crits in 1000 swings");

        foreach ((MeleeHitOutcome outcome, int count) in counts.OrderByDescending(entry => entry.Value))
        {
            output.WriteLine($"  {outcome,-9} {count,5}");
        }
    }

    /// <summary>
    /// Armour makes a real difference to a real creature, without ever reaching immunity.
    /// </summary>
    [RequiresWorldDatabaseFact]
    public async Task RealArmour_MitigatesWithoutGrantingImmunity()
    {
        CreatureStatsStore stats = new();
        await stats.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);

        foreach (byte level in (byte[])[1, 20, 40, 60, 70, 80])
        {
            Assert.True(stats.TryGet(level, 1, out CreatureBaseStats baseStats));

            float reduction = ArmorMitigation.ReductionFor(baseStats.BaseArmor, level);

            Assert.InRange(reduction, 0f, ArmorMitigation.MaxReduction);
            Assert.True(ArmorMitigation.Reduce(1, baseStats.BaseArmor, level) > 0, "armour granted immunity");

            output.WriteLine($"  level {level,2}: {baseStats.BaseArmor,5} armour -> {reduction:P1} mitigated");
        }
    }
}
