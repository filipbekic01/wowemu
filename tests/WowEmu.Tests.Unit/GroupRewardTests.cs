using WowEmu.Core;
using WowEmu.Game;
using WowEmu.Game.Combat;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Dividing a kill among a group.
/// </summary>
public sealed class GroupRewardTests
{
    /// <summary>
    /// Two players earn the same as one; only the third pays a bonus.
    /// </summary>
    /// <remarks>
    /// <b>Flat at 1.0 up to two members.</b> Assuming the rate climbs from the first extra member
    /// over-pays every duo in the game, and nothing in the client says so.
    /// </remarks>
    [Theory]
    [InlineData(1, 1.0f)]
    [InlineData(2, 1.0f)]
    [InlineData(3, 1.166f)]
    [InlineData(4, 1.3f)]
    [InlineData(5, 1.4f)]
    [InlineData(40, 1.4f)]
    public void TheGroupRate_IsFlatUntilThree(int count, float expected) =>
        Assert.Equal(expected, GroupReward.RateFor(count, isRaid: false));

    /// <summary>A raid gets no bonus at all.</summary>
    /// <remarks>Upstream marks scaling by raid size as unimplemented, and returns 1.0 outright.</remarks>
    [Fact]
    public void ARaid_GetsNoBonus() =>
        Assert.Equal(1.0f, GroupReward.RateFor(25, isRaid: true));

    /// <summary>
    /// A member across the map earns nothing.
    /// </summary>
    /// <remarks>
    /// Same map checked before distance: a member on another continent is not "far away", they are
    /// not there at all, and coordinates on different maps are not comparable.
    /// </remarks>
    [Fact]
    public void AMemberOnAnotherMap_IsOutOfRange()
    {
        Player member = InventoryFixture.Player(level: 30, proficiencies: false);
        Creature victim = CreatureFixture.Build(position: new Position(0f, 0f, 0f, 0f));

        member.Position = new Position(0f, 0f, 0f, 0f);
        member.MapId = victim.MapId;

        Assert.True(GroupReward.IsInRewardRange(member, victim));

        member.MapId = victim.MapId + 1;

        Assert.False(GroupReward.IsInRewardRange(member, victim));
    }

    /// <summary>And so does one standing too far away.</summary>
    [Fact]
    public void AMemberTooFarAway_IsOutOfRange()
    {
        Player member = InventoryFixture.Player(level: 30, proficiencies: false);
        Creature victim = CreatureFixture.Build(position: new Position(0f, 0f, 0f, 0f));

        member.MapId = victim.MapId;
        member.Position = new Position(GroupReward.RewardDistance + 1f, 0f, 0f, 0f);

        Assert.False(GroupReward.IsInRewardRange(member, victim));

        member.Position = new Position(GroupReward.RewardDistance - 1f, 0f, 0f, 0f);

        Assert.True(GroupReward.IsInRewardRange(member, victim));
    }

    /// <summary>
    /// The share is weighted by level, not divided evenly.
    /// </summary>
    /// <remarks>
    /// <b>A level 30 beside a level 10 takes three times as much.</b> An even split is the obvious
    /// reading, gives the same total, and pays both players the wrong amount.
    /// </remarks>
    [Fact]
    public void TheShare_IsWeightedByLevel()
    {
        // Level 30, so the victim is grey to neither member — otherwise the grey rules decide the
        // test rather than the weighting it is about.
        Creature victim = CreatureFixture.Build(
            position: new Position(0f, 0f, 0f, 0f), level: 30);

        Player high = MemberAt(30, victim);
        Player low = MemberAt(10, victim);

        IReadOnlyList<GroupShare> shares =
            GroupReward.Split([high, low], victim, isRaid: false, contentLevel: 0);

        Assert.Equal(2, shares.Count);

        uint highShare = shares.First(share => share.Member == high).Experience;
        uint lowShare = shares.First(share => share.Member == low).Experience;

        Assert.True(highShare > 0);
        Assert.True(lowShare > 0);

        // 30/40 against 10/40 — three to one, within the integer truncation.
        Assert.InRange(highShare / (double)lowShare, 2.8, 3.2);
    }

    /// <summary>
    /// A member the victim is grey to earns nothing.
    /// </summary>
    /// <remarks>
    /// This is the power-levelling hole. A level 70 standing beside a level 10 must not be paid for
    /// a level 5 creature, and paying them "a little" is the same bug in a smaller size.
    /// </remarks>
    [Fact]
    public void AMemberTheVictimIsGreyTo_EarnsNothing()
    {
        Creature victim = CreatureFixture.Build(
            position: new Position(0f, 0f, 0f, 0f), level: 10);

        Player low = MemberAt(10, victim);
        Player high = MemberAt(70, victim);

        IReadOnlyList<GroupShare> shares =
            GroupReward.Split([low, high], victim, isRaid: false, contentLevel: 0);

        Assert.Equal(0u, shares.First(share => share.Member == high).Experience);
    }

    /// <summary>
    /// A grey member halves what everybody earns, not just their own share.
    /// </summary>
    /// <remarks>
    /// The penalty is collective, which is what stops a high-level friend inflating a group's
    /// income. Applying it only to the grey member leaves the exploit wide open.
    /// </remarks>
    [Fact]
    public void AGreyMember_HalvesEverybodysShare()
    {
        Creature victim = CreatureFixture.Build(
            position: new Position(0f, 0f, 0f, 0f), level: 10);

        Player alone = MemberAt(10, victim);
        uint solo = GroupReward.Split([alone], victim, isRaid: false, contentLevel: 0)[0].Experience;

        Player low = MemberAt(10, victim);
        Player high = MemberAt(70, victim);

        uint withHelper = GroupReward
            .Split([low, high], victim, isRaid: false, contentLevel: 0)
            .First(share => share.Member == low).Experience;

        Assert.True(solo > 0);
        Assert.True(withHelper < solo, "A grey helper must reduce, not raise, the low member's share.");
    }

    /// <summary>An empty group earns nothing rather than throwing.</summary>
    [Fact]
    public void AnEmptyGroup_EarnsNothing() =>
        Assert.Empty(GroupReward.Split(
            [], CreatureFixture.Build(position: new Position(0f, 0f, 0f, 0f)),
            isRaid: false, contentLevel: 0));

    private static Player MemberAt(byte level, Creature victim)
    {
        Player member = InventoryFixture.Player(level: level, proficiencies: false);

        member.MapId = victim.MapId;
        member.Position = new Position(1f, 0f, 0f, 0f);

        return member;
    }
}
