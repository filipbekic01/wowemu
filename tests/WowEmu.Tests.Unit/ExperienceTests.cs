using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Combat;
using WowEmu.Game.Maps;
using WowEmu.Data.Client;
using WowEmu.Protocol;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>
/// How much experience a kill is worth.
/// </summary>
/// <remarks>
/// The curve either side of the player's own level is two different formulas, and both are hand-
/// tuned rather than derivable. What is pinned here is the shape — where it caps, where it falls to
/// nothing — and the exact arithmetic at a few points.
/// </remarks>
public sealed class ExperienceFormulaTests(ITestOutputHelper output)
{
    /// <summary>
    /// The grey level has three segments and a flat floor, none derivable from the others.
    /// </summary>
    /// <remarks>
    /// Below level 6 nothing is grey at all — a level 1 character gets experience from anything it
    /// can kill, because there is nothing below it.
    /// </remarks>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(10, 4)]    // 10 - 5 - 1
    [InlineData(39, 31)]   // 39 - 5 - 3
    [InlineData(40, 31)]   // switches formula: 40 - 1 - 8
    [InlineData(59, 47)]   // 59 - 1 - 11
    [InlineData(60, 51)]   // switches again: 60 - 9
    [InlineData(80, 71)]
    public void TheGreyLevel_HasThreeSegments(byte playerLevel, byte expected) =>
        Assert.Equal(expected, ExperienceFormula.GrayLevel(playerLevel));

    /// <summary>The zero-difference step function widens as the player levels.</summary>
    [Theory]
    [InlineData(1, 5)]
    [InlineData(7, 5)]
    [InlineData(8, 6)]
    [InlineData(20, 11)]
    [InlineData(59, 16)]
    [InlineData(60, 17)]
    [InlineData(80, 17)]
    public void TheZeroDifference_WidensWithLevel(byte playerLevel, byte expected) =>
        Assert.Equal(expected, ExperienceFormula.ZeroDifference(playerLevel));

    /// <summary>
    /// The base figure comes from the content, not from the creature's level.
    /// </summary>
    /// <remarks>
    /// Outland is worth five times Azeroth and Northrend nearly thirteen times. That jump is what
    /// makes stepping through the Dark Portal at 58 worth more than finishing the old world.
    /// </remarks>
    [Fact]
    public void TheBaseFigure_ComesFromTheContent()
    {
        Assert.Equal(ExperienceFormula.BaseExpClassic, ExperienceFormula.BaseExpFor(0));
        Assert.Equal(ExperienceFormula.BaseExpBurningCrusade, ExperienceFormula.BaseExpFor(1));
        Assert.Equal(ExperienceFormula.BaseExpWrath, ExperienceFormula.BaseExpFor(2));

        // An unknown expansion falls back to classic rather than paying nothing.
        Assert.Equal(ExperienceFormula.BaseExpClassic, ExperienceFormula.BaseExpFor(99));
    }

    /// <summary>An equal-level kill pays the same whichever way the comparison is read.</summary>
    [Fact]
    public void AnEqualLevelKill_PaysTheAtOrAboveFormula()
    {
        // (10 * 5 + 45) * (20 + 0) / 10 + 1) / 2 = (95 * 2 + 1) / 2 = 95.
        Assert.Equal(95u, ExperienceFormula.BaseGain(10, 10, 0));
    }

    /// <summary>
    /// A creature more than four levels above pays no more than one exactly four above.
    /// </summary>
    /// <remarks>
    /// The cap is what stops a low-level character farming something far above it for enormous
    /// experience — which would otherwise be the fastest way to level in the game.
    /// </remarks>
    [Fact]
    public void AboveFourLevels_TheGainStopsGrowing()
    {
        uint atFour = ExperienceFormula.BaseGain(20, 24, 0);
        uint atTen = ExperienceFormula.BaseGain(20, 30, 0);
        uint atFifty = ExperienceFormula.BaseGain(20, 70, 0);

        Assert.Equal(atFour, atTen);
        Assert.Equal(atFour, atFifty);

        // And it really does grow up to the cap.
        Assert.True(atFour > ExperienceFormula.BaseGain(20, 20, 0));
    }

    /// <summary>Below the player's level the gain falls off, and reaches zero at the grey level.</summary>
    [Fact]
    public void BelowTheGreyLevel_AKillIsWorthNothing()
    {
        const byte PlayerLevel = 30;

        byte grey = ExperienceFormula.GrayLevel(PlayerLevel);

        Assert.Equal(0u, ExperienceFormula.BaseGain(PlayerLevel, grey, 0));
        Assert.Equal(0u, ExperienceFormula.BaseGain(PlayerLevel, (byte)(grey - 1), 0));
        Assert.Equal(0u, ExperienceFormula.BaseGain(PlayerLevel, 1, 0));

        // Just above grey is worth something, so the boundary is where it is claimed to be.
        Assert.True(ExperienceFormula.BaseGain(PlayerLevel, (byte)(grey + 1), 0) > 0);
    }

    /// <summary>The gain falls monotonically as the creature's level drops.</summary>
    [Fact]
    public void TheGain_FallsMonotonicallyBelowTheThePlayer()
    {
        const byte PlayerLevel = 40;

        uint previous = uint.MaxValue;

        for (byte creatureLevel = PlayerLevel; creatureLevel >= 1; creatureLevel--)
        {
            uint gain = ExperienceFormula.BaseGain(PlayerLevel, creatureLevel, 0);

            Assert.True(gain <= previous, $"gain rose at creature level {creatureLevel}");
            previous = gain;
        }

        Assert.Equal(0u, previous);
    }

    // ------------------------------------------------------------------ multipliers

    /// <summary>An elite is worth twice an ordinary creature of the same level.</summary>
    [Fact]
    public void AnElite_IsWorthDouble()
    {
        Player player = ExperienceFixture.NewPlayer(level: 10);

        Creature normal = CreatureFixture.Build();
        Creature elite = CreatureFixture.Build(rank: Creature.RankElite);

        normal.Level = 10;
        elite.Level = 10;

        Assert.Equal(
            ExperienceFormula.Gain(player, normal, 0) * 2,
            ExperienceFormula.Gain(player, elite, 0));
    }

    /// <summary>
    /// Rare is not elite, despite sitting above world boss numerically.
    /// </summary>
    /// <remarks>
    /// Rank 4 is an ordinary creature that happens to be uncommon. A numeric <c>&gt;= 1</c> test
    /// pays double for it, which is wrong and easy to write.
    /// </remarks>
    [Fact]
    public void Rare_IsNotElite()
    {
        Assert.True(ExperienceFormula.IsElite(Creature.RankElite));
        Assert.True(ExperienceFormula.IsElite(Creature.RankRareElite));
        Assert.True(ExperienceFormula.IsElite(Creature.RankWorldBoss));

        Assert.False(ExperienceFormula.IsElite(Creature.RankNormal));
        Assert.False(ExperienceFormula.IsElite(Creature.RankRare));
    }

    /// <summary>
    /// A critter is worth nothing, however many are killed.
    /// </summary>
    /// <remarks>
    /// Without the check a field of rabbits is a levelling strategy.
    /// </remarks>
    [Fact]
    public void ACritter_IsWorthNothing()
    {
        Player player = ExperienceFixture.NewPlayer(level: 5);

        Creature critter = CreatureFixture.Build(creatureType: ExperienceFormula.CritterCreatureType);
        critter.Level = 5;

        Assert.Equal(0u, ExperienceFormula.Gain(player, critter, 0));
    }

    /// <summary>Anything flagged as giving no experience gives none.</summary>
    [Fact]
    public void AFlaggedCreature_IsWorthNothing()
    {
        Player player = ExperienceFixture.NewPlayer(level: 5);

        Creature dummy = CreatureFixture.Build(flagsExtra: (uint)CreatureFlagsExtra.NoExperience);
        dummy.Level = 5;

        Assert.Equal(0u, ExperienceFormula.Gain(player, dummy, 0));
    }

    /// <summary>The server rate multiplies the whole gain.</summary>
    [Fact]
    public void TheServerRate_MultipliesTheGain()
    {
        Player player = ExperienceFixture.NewPlayer(level: 10);

        Creature creature = CreatureFixture.Build();
        creature.Level = 10;

        uint single = ExperienceFormula.Gain(player, creature, 0, rate: 1f);
        uint quintuple = ExperienceFormula.Gain(player, creature, 0, rate: 5f);

        Assert.Equal(single * 5, quintuple);
    }

    /// <summary>A table of the curve, for eyeballing against the game.</summary>
    [Fact]
    public void TheCurve_IsWorthLookingAt()
    {
        const byte PlayerLevel = 20;

        output.WriteLine($"player level {PlayerLevel}, grey at {ExperienceFormula.GrayLevel(PlayerLevel)}:");

        for (byte level = 24; level >= 10; level--)
        {
            output.WriteLine($"  vs level {level,2}: {ExperienceFormula.BaseGain(PlayerLevel, level, 0),4} xp");
        }
    }
}

/// <summary>Gaining experience and levelling.</summary>
public sealed class GiveExperienceTests
{
    [RequiresWorldDatabaseFact]
    public async Task ExperienceAccumulatesWithoutLevelling()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();

        Player player = ExperienceFixture.NewPlayer(level: 1);
        player.NextLevelXp = xp.XpToLeave(1);

        IReadOnlyList<LevelUp> levels = Experience.Give(player, 100, xp, stats);

        Assert.Empty(levels);
        Assert.Equal(100u, player.Xp);
        Assert.Equal(1, player.Level);
    }

    [RequiresWorldDatabaseFact]
    public async Task EnoughExperience_LevelsUp()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();

        Player player = ExperienceFixture.NewPlayer(level: 1);

        uint cost = xp.XpToLeave(1);

        IReadOnlyList<LevelUp> levels = Experience.Give(player, cost, xp, stats);

        Assert.Single(levels);
        Assert.Equal(2, player.Level);
        Assert.Equal(0u, player.Xp);
        Assert.Equal(xp.XpToLeave(2), player.NextLevelXp);
    }

    /// <summary>
    /// The surplus carries forward rather than being thrown away.
    /// </summary>
    /// <remarks>
    /// Resetting to zero on level-up loses up to a level's worth of experience on every kill that
    /// crosses a threshold — small each time and substantial over a levelling run.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task TheSurplus_CarriesForward()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();

        Player player = ExperienceFixture.NewPlayer(level: 1);

        uint cost = xp.XpToLeave(1);

        Experience.Give(player, cost + 137, xp, stats);

        Assert.Equal(2, player.Level);
        Assert.Equal(137u, player.Xp);
    }

    /// <summary>
    /// One award can cross several levels.
    /// </summary>
    /// <remarks>
    /// A single <c>if</c> instead of a loop would leave the surplus sitting above the threshold
    /// until the next kill, so the bar would show full and not level.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task OneAward_CanCrossSeveralLevels()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();

        Player player = ExperienceFixture.NewPlayer(level: 1);

        uint enoughForThree = xp.XpToLeave(1) + xp.XpToLeave(2) + xp.XpToLeave(3);

        IReadOnlyList<LevelUp> levels = Experience.Give(player, enoughForThree, xp, stats);

        Assert.Equal(3, levels.Count);
        Assert.Equal(4, player.Level);
        Assert.Equal([2u, 3u, 4u], levels.Select(level => level.NewLevel));
    }

    /// <summary>Levelling recomputes health, mana and the five attributes.</summary>
    [RequiresWorldDatabaseFact]
    public async Task Levelling_RecomputesStats()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();

        Player player = ExperienceFixture.NewPlayer(level: 1);

        uint healthBefore = player.MaxHealth;
        uint strengthBefore = player.GetStat(0);

        LevelUp? levelUp = Experience.LevelUpTo(player, 2, stats);

        Assert.NotNull(levelUp);
        Assert.True(player.MaxHealth > healthBefore, "health did not grow");
        Assert.True(player.GetStat(0) >= strengthBefore, "strength went down");

        // The packet carries deltas, and they have to agree with what actually changed.
        Assert.Equal((int)(player.MaxHealth - healthBefore), levelUp!.Value.HealthDelta);
        Assert.Equal((int)(player.GetStat(0) - strengthBefore), levelUp.Value.StatDeltas[0]);
    }

    /// <summary>
    /// Levelling refills health and mana.
    /// </summary>
    /// <remarks>
    /// Not a side effect of the maximum changing — upstream sets both to the new base. It is what
    /// makes levelling mid-fight a real swing.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task Levelling_RefillsHealthAndMana()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();

        Player player = ExperienceFixture.NewPlayer(level: 1);

        player.Health = 1;

        Experience.LevelUpTo(player, 2, stats);

        Assert.Equal(player.MaxHealth, player.Health);
    }

    /// <summary>At the level cap experience is not awarded at all.</summary>
    [RequiresWorldDatabaseFact]
    public async Task AtTheCap_NothingIsGained()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();

        Player player = ExperienceFixture.NewPlayer(level: (byte)(xp.MaxLevel + 1));

        IReadOnlyList<LevelUp> levels = Experience.Give(player, 1_000_000, xp, stats);

        Assert.Empty(levels);
        Assert.Equal(0u, player.Xp);
    }

    /// <summary>A dead player gains nothing.</summary>
    [RequiresWorldDatabaseFact]
    public async Task TheDead_GainNothing()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();

        Player player = ExperienceFixture.NewPlayer(level: 1);
        player.DeathState = DeathState.Corpse;

        Assert.Empty(Experience.Give(player, 10_000, xp, stats));
        Assert.Equal(0u, player.Xp);
    }

    /// <summary>
    /// The table's rows are per-level costs, not running totals.
    /// </summary>
    /// <remarks>
    /// Reading them as totals makes every level after the first cost far too much — the numbers
    /// climb either way, so it is not obvious from the data alone.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task TheTable_HoldsPerLevelCostsNotTotals()
    {
        (PlayerXpStore xp, _) = await ExperienceFixture.LoadAsync();

        // Level 1 to 2 costs 400 in the real table; a running total would have level 2 at 1300.
        Assert.Equal(400u, xp.XpToLeave(1));
        Assert.Equal(900u, xp.XpToLeave(2));

        Assert.True(xp.CanLevelPast(1));
        Assert.False(xp.CanLevelPast((byte)(xp.MaxLevel + 1)));

        // A level with no row costs zero, which the caller has to read as "cannot level".
        Assert.Equal(0u, xp.XpToLeave(250));
    }
}

/// <summary>The two packets a gain produces.</summary>
public sealed class ExperiencePacketTests
{
    private static readonly ObjectGuid Victim = ObjectGuid.Create(HighGuid.Unit, 299, 42);

    private static byte[] Write(Action<PacketWriter> write)
    {
        PacketWriter writer = new();
        write(writer);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// A kill carries two more fields than a quest reward, and the type byte says which.
    /// </summary>
    /// <remarks>
    /// Sending the kill shape with an empty guid makes the client read the trailing byte from the
    /// wrong place.
    /// </remarks>
    [Fact]
    public void AKillAndANonKill_HaveDifferentShapes()
    {
        byte[] kill = Write(writer => ExperiencePackets.WriteLogXpGain(writer, Victim, 250));
        byte[] other = Write(writer => ExperiencePackets.WriteLogXpGain(writer, ObjectGuid.Empty, 250));

        Assert.Equal(other.Length + 8, kill.Length);

        PacketReader killReader = new(kill);

        Assert.True(killReader.TryReadUInt64(out ulong guid));
        Assert.Equal(Victim.Value, guid);

        Assert.True(killReader.TryReadUInt32(out uint total));
        Assert.Equal(250u, total);

        Assert.True(killReader.TryReadUInt8(out byte type));
        Assert.Equal(0, type);   // 0 is a kill

        Assert.True(killReader.TryReadUInt32(out uint withoutBonus));
        Assert.Equal(250u, withoutBonus);

        Assert.True(killReader.TryReadSingle(out float groupRate));
        Assert.Equal(1f, groupRate);
    }

    [Fact]
    public void ANonKill_IsTypeOne()
    {
        byte[] bytes = Write(writer => ExperiencePackets.WriteLogXpGain(writer, ObjectGuid.Empty, 250));

        PacketReader reader = new(bytes);

        reader.Skip(8 + 4);

        Assert.True(reader.TryReadUInt8(out byte type));
        Assert.Equal(1, type);
    }

    /// <summary>The rested bonus is folded into the total but reported separately too.</summary>
    [Fact]
    public void TheBonus_IsFoldedIntoTheTotal()
    {
        byte[] bytes = Write(writer => ExperiencePackets.WriteLogXpGain(writer, Victim, 100, bonus: 50));

        PacketReader reader = new(bytes);

        reader.Skip(8);

        Assert.True(reader.TryReadUInt32(out uint total));
        Assert.Equal(150u, total);

        reader.Skip(1);

        Assert.True(reader.TryReadUInt32(out uint withoutBonus));
        Assert.Equal(100u, withoutBonus);
    }

    /// <summary>
    /// The level-up packet is a fixed size whatever it is handed.
    /// </summary>
    /// <remarks>
    /// Six powers and five stats, always. A short span pads rather than truncating, because the
    /// client reads a fixed number of fields and a short packet leaves it reading past the end.
    /// </remarks>
    [Fact]
    public void TheLevelUpPacket_IsAFixedSize()
    {
        byte[] full = Write(writer => ExperiencePackets.WriteLevelUp(
            writer, 2, 10, [1, 2, 3, 4, 5, 6], [7, 8, 9, 10, 11]));

        byte[] sparse = Write(writer => ExperiencePackets.WriteLevelUp(writer, 2, 10, [1], [7]));

        int expected = 4 + 4 + (4 * ExperiencePackets.PowerDeltaCount) + (4 * ExperiencePackets.StatDeltaCount);

        Assert.Equal(expected, full.Length);
        Assert.Equal(expected, sparse.Length);
    }

    [Fact]
    public void TheLevelUpPacket_CarriesDeltasInOrder()
    {
        byte[] bytes = Write(writer => ExperiencePackets.WriteLevelUp(
            writer, newLevel: 12, healthDelta: 47, [31, 0, 0, 0, 0, 0], [1, 2, 3, 4, 5]));

        PacketReader reader = new(bytes);

        Assert.True(reader.TryReadUInt32(out uint level));
        Assert.Equal(12u, level);

        Assert.True(reader.TryReadUInt32(out uint health));
        Assert.Equal(47u, health);

        Assert.True(reader.TryReadUInt32(out uint mana));
        Assert.Equal(31u, mana);

        reader.Skip(4 * 5);   // the other five powers

        Assert.True(reader.TryReadUInt32(out uint strength));
        Assert.Equal(1u, strength);
    }
}

/// <summary>Experience awarded through a real map, on a real kill.</summary>
public sealed class MapExperienceTests(ITestOutputHelper output)
{
    /// <summary>Killing something on your threat list pays experience.</summary>
    [RequiresWorldDatabaseFact]
    public async Task AKill_PaysExperience()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();
        (Map map, Player killer, Creature victim, MapCombatFixture.Link link) =
            MapCombatFixture.Engaged(experience: xp, playerStats: stats);

        victim.Level = 5;
        victim.Health = 1;

        while (victim.IsAlive)
        {
            map.Update(gameplayDiff: 100, sessionDiff: 100);
        }

        Assert.NotEmpty(link.ExperienceGains);
        Assert.True(link.ExperienceGains[0].Amount > 0, "the kill paid nothing");
        Assert.Equal(link.ExperienceGains[0].Amount, killer.Xp);

        output.WriteLine($"level {killer.Level} killed a level 5: {link.ExperienceGains[0].Amount} xp");
    }

    /// <summary>
    /// Experience is paid before the threat list is cleared.
    /// </summary>
    /// <remarks>
    /// <c>Creature.Kill</c> forgets everyone it hated, so paying afterwards pays nobody — and the
    /// symptom is a kill that works perfectly and awards nothing.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task ExperienceIsPaid_BeforeTheThreatListIsCleared()
    {
        (PlayerXpStore xp, PlayerStatsStore stats) = await ExperienceFixture.LoadAsync();
        (Map map, Player killer, Creature victim, MapCombatFixture.Link link) =
            MapCombatFixture.Engaged(experience: xp, playerStats: stats);

        victim.Level = 5;
        victim.Health = 1;

        // Killed directly, without ticks, so the threat has to be put there explicitly — this is
        // exactly the ordering under test and a tick would hide it.
        victim.Threat.AddThreat(killer, 1f);

        map.Kill(victim);

        Assert.True(victim.Threat.IsEmpty, "the threat list should be cleared by the kill");
        Assert.NotEmpty(link.ExperienceGains);
    }

    /// <summary>A map with no experience table pays nothing rather than throwing.</summary>
    [RequiresWorldDatabaseFact]
    public async Task WithNoExperienceTable_NothingIsPaid()
    {
        _ = await ExperienceFixture.LoadAsync();

        (Map map, Player killer, Creature victim, MapCombatFixture.Link link) = MapCombatFixture.Engaged();

        victim.Level = 5;
        victim.Health = 1;

        // Threat is there, so the only reason nothing is paid is the missing table.
        victim.Threat.AddThreat(killer, 1f);

        map.Kill(victim);

        Assert.Empty(link.ExperienceGains);
    }
}

/// <summary>Loads the experience tables and builds a player at a chosen level.</summary>
internal static class ExperienceFixture
{
    public static async Task<(PlayerXpStore Xp, PlayerStatsStore Stats)> LoadAsync()
    {
        PlayerXpStore xp = new();
        PlayerStatsStore stats = new();

        await xp.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);
        await stats.LoadAsync(WorldDatabase.ConnectionString, CancellationToken.None);

        return (xp, stats);
    }

    public static Player NewPlayer(byte level)
    {
        CharacterSummary summary = new(1, "Leveller", 1, 1, 0, 0, 0, 0, 0, 0, 1, 12, 0, 0f, 0f, 0f, 0, 0, 0);
        ChrRacesEntry race = new(1, 0, 1, 49, 50, 7, 0, 0, "Human", 0);
        ChrClassesEntry characterClass = new(1, 1, "Warrior", 4, 0);
        PlayerBaseStats stats = new(20, 0, 23, 20, 22, 20, 20);

        Player player = Player.Create(summary, race, characterClass, stats);
        player.Level = level;

        return player;
    }
}
