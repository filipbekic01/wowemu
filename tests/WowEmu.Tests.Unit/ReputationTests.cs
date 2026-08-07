using WowEmu.Data.Client;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// What every faction thinks of a character.
/// </summary>
/// <remarks>
/// The rank bands are wildly uneven and that is the whole of the arithmetic: Hated spans 36,000 and
/// Exalted only 1,000. Treating them as equal puts every boundary in the wrong place.
/// </remarks>
public sealed class ReputationTests
{
    /// <summary>A character starts Neutral with everyone.</summary>
    /// <remarks>
    /// Zero standing is Neutral, not Hated. It is where everyone begins, and a rank calculation that
    /// starts from the bottom makes every stranger an enemy.
    /// </remarks>
    [Fact]
    public void ACharacter_StartsNeutral()
    {
        Assert.Equal(ReputationRank.Neutral, Reputation.RankOf(0));
        Assert.Equal(ReputationRank.Neutral, Player().Reputation.RankWith(Stormwind));
    }

    /// <summary>The bands are uneven, and the boundaries fall where the table says.</summary>
    [Theory]
    [InlineData(-42000, ReputationRank.Hated)]
    [InlineData(-6001, ReputationRank.Hated)]
    [InlineData(-6000, ReputationRank.Hostile)]
    [InlineData(-3000, ReputationRank.Unfriendly)]
    [InlineData(0, ReputationRank.Neutral)]
    [InlineData(2999, ReputationRank.Neutral)]
    [InlineData(3000, ReputationRank.Friendly)]
    [InlineData(9000, ReputationRank.Honored)]
    [InlineData(21000, ReputationRank.Revered)]
    [InlineData(42000, ReputationRank.Exalted)]
    [InlineData(42999, ReputationRank.Exalted)]
    public void TheBands_FallWhereTheTableSays(int standing, byte rank) =>
        Assert.Equal(rank, Reputation.RankOf(standing));

    /// <summary>Standing is clamped at both ends.</summary>
    /// <remarks>
    /// Signed, with a floor of -42,000. An unclamped subtraction runs off into a rank that does not
    /// exist, and the lookup answers Hated for a number that is nonsense.
    /// </remarks>
    [Fact]
    public void Standing_IsClampedAtBothEnds()
    {
        Player player = Player();

        player.Reputation.Gain(Stormwind, 999_999);
        Assert.Equal(Reputation.Cap, player.Reputation.StandingWith(Stormwind));

        player.Reputation.Gain(Stormwind, -999_999);
        Assert.Equal(Reputation.Bottom, player.Reputation.StandingWith(Stormwind));
    }

    /// <summary>Gaining reputation moves the rank.</summary>
    [Fact]
    public void Gaining_MovesTheRank()
    {
        Player player = Player();

        Assert.Equal(ReputationRank.Neutral, player.Reputation.Gain(Stormwind, 2999));
        Assert.Equal(ReputationRank.Friendly, player.Reputation.Gain(Stormwind, 1));
    }

    /// <summary>Rank-to-standing is the inverse of standing-to-rank.</summary>
    [Theory]
    [InlineData(ReputationRank.Neutral)]
    [InlineData(ReputationRank.Friendly)]
    [InlineData(ReputationRank.Honored)]
    [InlineData(ReputationRank.Exalted)]
    public void RankToStanding_RoundTrips(byte rank) =>
        Assert.Equal(rank, Reputation.RankOf(Reputation.StandingFor(rank)));

    // ------------------------------------------------------------------ the discount

    /// <summary>Five percent off per rank above Neutral.</summary>
    [Fact]
    public void TheDiscount_IsFivePercentPerRank()
    {
        Player player = Player();

        player.Reputation.Set(Stormwind, Reputation.StandingFor(ReputationRank.Friendly));
        Assert.Equal(0.95f, player.Reputation.PriceDiscount(Stormwind), 0.001f);

        player.Reputation.Set(Stormwind, Reputation.StandingFor(ReputationRank.Exalted));
        Assert.Equal(0.80f, player.Reputation.PriceDiscount(Stormwind), 0.001f);
    }

    /// <summary>
    /// Being disliked does not make things more expensive.
    /// </summary>
    /// <remarks>
    /// The multiplier never goes above 1.0. Extending the formula downwards is the obvious symmetry
    /// and it is not what the game does — a hated vendor refuses to trade at all rather than
    /// overcharging.
    /// </remarks>
    [Fact]
    public void BeingDisliked_CostsNoMore()
    {
        Player player = Player();

        player.Reputation.Set(Stormwind, Reputation.StandingFor(ReputationRank.Hated));
        Assert.Equal(1.0f, player.Reputation.PriceDiscount(Stormwind));

        player.Reputation.Set(Stormwind, Reputation.StandingFor(ReputationRank.Neutral));
        Assert.Equal(1.0f, player.Reputation.PriceDiscount(Stormwind));
    }

    /// <summary>A creature with no faction behind it gives no discount.</summary>
    [Fact]
    public void NoFaction_MeansNoDiscount() =>
        Assert.Equal(1.0f, Player().Reputation.PriceDiscount(0));

    /// <summary>The real table loads, and most factions have no reputation slot.</summary>
    /// <remarks>
    /// 401 factions against 128 slots. A faction with no slot is tracked and never shown, and
    /// writing by faction id instead of slot id would run off the end of anything indexed by it.
    /// </remarks>
    [RequiresClientDataFact]
    public void MostFactions_HaveNoSlot()
    {
        DbcStore<FactionEntry> factions = DbcStores.Load(ClientData.DbcDirectory).Factions;

        Assert.Equal(401, factions.Count);

        int shown = factions.Entries.Count(f => f.ReputationListId >= 0);

        Assert.True(shown is > 0 and < Reputation.MaxIndex, $"{shown} shown factions");
        Assert.True(factions.Entries.Count(f => f.ReputationListId < 0) > shown);
    }

    private const uint Stormwind = 72;

    private static Player Player() => InventoryFixture.Player(level: 20, proficiencies: false);
}
