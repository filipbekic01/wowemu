using WowEmu.Core;
using WowEmu.Game;

namespace WowEmu.Tests.Unit;

/// <summary>
/// How a group divides its loot: the roll, the threshold, and the round-robin turn.
/// </summary>
public sealed class GroupLootTests
{
    private static readonly ObjectGuid Alice = ObjectGuid.Create(HighGuid.Player, 1);
    private static readonly ObjectGuid Bob = ObjectGuid.Create(HighGuid.Player, 2);
    private static readonly ObjectGuid Carol = ObjectGuid.Create(HighGuid.Player, 3);
    private static readonly ObjectGuid Corpse = ObjectGuid.Create(HighGuid.Unit, 9);

    /// <summary>
    /// Need beats greed outright, whatever the numbers.
    /// </summary>
    /// <remarks>
    /// <b>A need roll of 1 takes the item from a greed roll of 100.</b> The two pools are never
    /// compared; a single highest-roll pass over both is the obvious implementation and is wrong in
    /// exactly the case players care about most.
    /// </remarks>
    [Fact]
    public void Need_BeatsGreedOutright()
    {
        GroupLootRoll roll = Roll(Alice, Bob);

        roll.Vote(Alice, LootVote.Need);
        roll.Vote(Bob, LootVote.Greed);

        // Alice rolls 1, Bob rolls 100 — and Alice still wins.
        Queue<byte> draws = new([1, 100]);

        LootRollOutcome outcome = roll.Decide(draws.Dequeue, out _);

        Assert.False(outcome.EveryonePassed);
        Assert.Equal(Alice, outcome.Winner);
        Assert.Equal(LootVote.Need, outcome.WinningVote);
    }

    /// <summary>Among equal votes, the highest roll wins.</summary>
    [Fact]
    public void AmongEqualVotes_TheHighestRollWins()
    {
        GroupLootRoll roll = Roll(Alice, Bob);

        roll.Vote(Alice, LootVote.Greed);
        roll.Vote(Bob, LootVote.Greed);

        Queue<byte> draws = new([30, 70]);

        LootRollOutcome outcome = roll.Decide(draws.Dequeue, out IReadOnlyList<LootRollVote> log);

        Assert.Equal(Bob, outcome.Winner);
        Assert.Equal(70, outcome.WinningRoll);
        Assert.Equal(2, log.Count);
    }

    /// <summary>
    /// Greed and disenchant roll in the same pool.
    /// </summary>
    /// <remarks>
    /// Both mean "I do not need this". Ranking one above the other would make disenchanting a way
    /// to beat everyone who greeded.
    /// </remarks>
    [Fact]
    public void GreedAndDisenchant_RollTogether()
    {
        GroupLootRoll roll = Roll(Alice, Bob);

        roll.Vote(Alice, LootVote.Disenchant);
        roll.Vote(Bob, LootVote.Greed);

        Queue<byte> draws = new([10, 90]);

        Assert.Equal(Bob, roll.Decide(draws.Dequeue, out _).Winner);
    }

    /// <summary>Everyone passing leaves the item unclaimed.</summary>
    [Fact]
    public void EveryonePassing_LeavesItUnclaimed()
    {
        GroupLootRoll roll = Roll(Alice, Bob);

        roll.Vote(Alice, LootVote.Pass);
        roll.Vote(Bob, LootVote.Pass);

        LootRollOutcome outcome = roll.Decide(() => 50, out _);

        Assert.True(outcome.EveryonePassed);
        Assert.True(outcome.Winner.IsEmpty);
    }

    /// <summary>
    /// A player answers once.
    /// </summary>
    /// <remarks>
    /// Without the check a client sends Need repeatedly and rolls as many times as it likes, taking
    /// the best of them — and the roll log shows only the last, so it looks fair.
    /// </remarks>
    [Fact]
    public void APlayerAnswersOnce()
    {
        GroupLootRoll roll = Roll(Alice, Bob);

        Assert.True(roll.Vote(Alice, LootVote.Greed));
        Assert.False(roll.Vote(Alice, LootVote.Need));

        Assert.Equal(LootVote.Greed, roll.Votes[Alice]);
    }

    /// <summary>Someone who was not asked cannot vote.</summary>
    [Fact]
    public void SomeoneNotAsked_CannotVote() =>
        Assert.False(Roll(Alice, Bob).Vote(Carol, LootVote.Need));

    /// <summary>
    /// The roll ends when everyone has answered, or when the timer runs out.
    /// </summary>
    /// <remarks>
    /// Without the timer, one player walking away from their keyboard freezes the item on the
    /// corpse forever — for the whole party, not just for them.
    /// </remarks>
    [Fact]
    public void TheRoll_EndsOnTimeoutToo()
    {
        GroupLootRoll roll = Roll(Alice, Bob);

        roll.Vote(Alice, LootVote.Need);

        Assert.False(roll.IsSettled);
        Assert.False(roll.Tick(GroupLootRoll.TimeoutMs - 1));
        Assert.True(roll.Tick(1));
    }

    /// <summary>A still-pending vote is decided as a pass.</summary>
    [Fact]
    public void APendingVote_CountsAsAPass()
    {
        GroupLootRoll roll = Roll(Alice, Bob);

        roll.Vote(Alice, LootVote.Greed);

        // Bob never answered. Alice takes it unopposed rather than the roll hanging.
        LootRollOutcome outcome = roll.Decide(() => 50, out IReadOnlyList<LootRollVote> log);

        Assert.Equal(Alice, outcome.Winner);
        Assert.Single(log);
    }

    // ------------------------------------------------------------------ the rules

    /// <summary>
    /// Only items at or above the threshold are rolled for.
    /// </summary>
    /// <remarks>
    /// Rolling for every grey makes a dungeon unplayable, which is exactly what the threshold is
    /// for.
    /// </remarks>
    [Theory]
    [InlineData(LootMethod.GroupLoot, 1, false)]
    [InlineData(LootMethod.GroupLoot, 2, true)]
    [InlineData(LootMethod.NeedBeforeGreed, 3, true)]
    [InlineData(LootMethod.RoundRobin, 4, false)]
    [InlineData(LootMethod.FreeForAll, 4, false)]
    [InlineData(LootMethod.MasterLoot, 4, false)]
    public void OnlyAboveTheThreshold_IsRolledFor(byte method, byte quality, bool expected) =>
        Assert.Equal(expected, GroupLoot.NeedsRoll(method, quality, threshold: 2));

    /// <summary>
    /// Round-robin is the only method that restricts ordinary drops.
    /// </summary>
    /// <remarks>
    /// Applying the turn under the other methods leaves four members watching one person loot
    /// everything, and nothing in the client says why.
    /// </remarks>
    [Fact]
    public void OnlyRoundRobin_RestrictsOrdinaryDrops()
    {
        Group robin = new() { LootMethod = LootMethod.RoundRobin };
        Group free = new() { LootMethod = LootMethod.FreeForAll };

        robin.Add(Alice, "Alice");
        robin.Add(Bob, "Bob");
        free.Add(Alice, "Alice");
        free.Add(Bob, "Bob");

        Assert.False(GroupLoot.CanTakeUncontested(robin, Bob, robin.Looter));
        Assert.True(GroupLoot.CanTakeUncontested(robin, Alice, robin.Looter));
        Assert.True(GroupLoot.CanTakeUncontested(free, Bob, free.Looter));
    }

    /// <summary>An ungrouped player is restricted by nothing.</summary>
    [Fact]
    public void AnUngroupedPlayer_IsRestrictedByNothing() =>
        Assert.True(GroupLoot.CanTakeUncontested(null, Alice, ObjectGuid.Empty));

    /// <summary>
    /// Need is hidden from a player who cannot use the item, under need-before-greed only.
    /// </summary>
    /// <remarks>
    /// Under plain group loot everyone gets the button. Applying the need-before-greed rule to both
    /// takes the button away from half the party in a mode that never intended it.
    /// </remarks>
    [Fact]
    public void NeedIsHidden_OnlyUnderNeedBeforeGreed()
    {
        Assert.Equal(
            LootRollMask.All, GroupLoot.VoteMaskFor(LootMethod.GroupLoot, canUse: false));

        byte restricted = GroupLoot.VoteMaskFor(LootMethod.NeedBeforeGreed, canUse: false);

        Assert.Equal(0, restricted & LootRollMask.Need);
        Assert.NotEqual(0, restricted & LootRollMask.Greed);

        Assert.Equal(
            LootRollMask.All, GroupLoot.VoteMaskFor(LootMethod.NeedBeforeGreed, canUse: true));
    }

    /// <summary>
    /// A class mask of zero means anyone, and so does -1.
    /// </summary>
    /// <remarks>
    /// Almost every item in the game has zero. Reading it as "nobody" hides the need button from
    /// the entire party on almost every drop.
    /// </remarks>
    [Fact]
    public void AnEmptyClassMask_MeansAnyone()
    {
        Assert.True(GroupLoot.CanUse(
            ItemFixture.Build(entry: 1, name: "Anything"), classId: 1, race: 1));

        Assert.True(GroupLoot.CanUse(
            ItemFixture.Build(entry: 1, name: "Anything") with { AllowableClass = -1 },
            classId: 1, race: 1));
    }

    /// <summary>And a mask that excludes a class refuses it.</summary>
    [Fact]
    public void AClassMask_ExcludesOtherClasses()
    {
        // Warrior only.
        var warriorOnly = ItemFixture.Build(entry: 1, name: "Plate") with { AllowableClass = 1 };

        Assert.True(GroupLoot.CanUse(warriorOnly, classId: 1, race: 1));
        Assert.False(GroupLoot.CanUse(warriorOnly, classId: 8, race: 1));
    }

    private static GroupLootRoll Roll(params ObjectGuid[] candidates)
    {
        GroupLootRoll roll = new()
        {
            Holder = Corpse,
            Slot = 0,
            ItemId = 1234,
            Count = 1,
        };

        foreach (ObjectGuid candidate in candidates)
        {
            roll.Ask(candidate);
        }

        return roll;
    }
}
