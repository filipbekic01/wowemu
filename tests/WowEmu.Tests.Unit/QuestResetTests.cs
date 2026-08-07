using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Protocol;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Daily, weekly and monthly quests, and the shared instants that clear them.
/// </summary>
public sealed class QuestResetTests
{
    /// <summary>
    /// A daily done today cannot be taken again until the reset.
    /// </summary>
    /// <remarks>
    /// Every daily in the data also carries the Repeatable flag, so the ordinary "already rewarded"
    /// check lets it straight through — this is the only thing standing between a player and doing
    /// the same daily all afternoon.
    /// </remarks>
    [Fact]
    public void ADailyDoneToday_CannotBeTakenAgain()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);
        QuestTemplate quest = Daily(1);

        Assert.Equal(QuestTakeResult.Ok, player.Quests.CanTake(quest));

        player.QuestResets.Record(quest);

        Assert.Equal(QuestTakeResult.AlreadyDoneThisPeriod, player.Quests.CanTake(quest));
    }

    /// <summary>And can be taken again once the day's record is cleared.</summary>
    [Fact]
    public void ADaily_ComesBackAfterTheReset()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);
        QuestTemplate quest = Daily(1);

        player.QuestResets.Record(quest);
        player.QuestResets.ResetDaily();

        Assert.Equal(QuestTakeResult.Ok, player.Quests.CanTake(quest));
    }

    /// <summary>
    /// The daily reset does not clear weeklies or monthlies.
    /// </summary>
    /// <remarks>
    /// They run on their own schedules. A daily reset that cleared all three would make a weekly
    /// quest repeatable every day, which is the whole of the difference between them.
    /// </remarks>
    [Fact]
    public void TheDailyReset_LeavesTheOtherPeriodsAlone()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);
        QuestTemplate weekly = Weekly(2);
        QuestTemplate monthly = Monthly(3);

        player.QuestResets.Record(weekly);
        player.QuestResets.Record(monthly);

        player.QuestResets.ResetDaily();

        Assert.True(player.QuestResets.IsDone(weekly));
        Assert.True(player.QuestResets.IsDone(monthly));
    }

    /// <summary>
    /// Dailies go in the update-field block the client reads.
    /// </summary>
    /// <remarks>
    /// The client draws its own "N dailies remaining" from these 25 slots. Tracking them
    /// server-side only leaves that counter stuck at 25 while the server refuses quest after quest.
    /// </remarks>
    [Fact]
    public void ADaily_LandsInTheFieldTheClientReads()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);

        player.QuestResets.Record(Daily(1234));

        Assert.Equal(
            1234u, player.Fields.GetUInt32(UpdateFields.PLAYER_FIELD_DAILY_QUESTS_1));
    }

    /// <summary>
    /// A character at twenty-five dailies is told they are out, not that they already did it.
    /// </summary>
    /// <remarks>
    /// The two refusals are different messages to the player, and the order of the checks is what
    /// picks between them.
    /// </remarks>
    [Fact]
    public void AtTheDailyLimit_TheRefusalSaysSo()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);

        for (uint i = 0; i < PlayerQuestResets.MaxDaily; i++)
        {
            player.QuestResets.Record(Daily(100 + i));
        }

        Assert.False(player.QuestResets.HasDailySlot);
        Assert.Equal(QuestTakeResult.DailyLimitReached, player.Quests.CanTake(Daily(999)));
    }

    /// <summary>
    /// A quest already done is reported as done even at the daily limit.
    /// </summary>
    /// <remarks>
    /// Ordering, and it is easy to get backwards: a full log reached first would tell a player they
    /// are out of dailies for the very quest they already handed in.
    /// </remarks>
    [Fact]
    public void AtTheLimit_AQuestAlreadyDone_StillSaysDone()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);

        for (uint i = 0; i < PlayerQuestResets.MaxDaily; i++)
        {
            player.QuestResets.Record(Daily(100 + i));
        }

        Assert.Equal(
            QuestTakeResult.AlreadyDoneThisPeriod, player.Quests.CanTake(Daily(100)));
    }

    /// <summary>An ordinary quest is not recorded in any bucket.</summary>
    [Fact]
    public void AnOrdinaryQuest_IsNotRecorded()
    {
        Player player = InventoryFixture.Player(level: 20, proficiencies: false);
        QuestTemplate quest = QuestFixture.Build(id: 7, minLevel: 1);

        Assert.False(player.QuestResets.Record(quest));
        Assert.False(player.QuestResets.IsDone(quest));
    }

    // ------------------------------------------------------------------ the shared instants

    /// <summary>
    /// The daily reset is the next 03:00, not twenty-four hours from now.
    /// </summary>
    /// <remarks>
    /// A per-character countdown lets a player walk their own reset later every day, and a restart
    /// would restart the countdown for everyone.
    /// </remarks>
    [Theory]
    [InlineData("2026-08-07T02:59:00", "2026-08-07T03:00:00")]
    [InlineData("2026-08-07T03:00:00", "2026-08-08T03:00:00")]
    [InlineData("2026-08-07T23:30:00", "2026-08-08T03:00:00")]
    public void TheDailyReset_IsTheNextThreeAm(string now, string expected)
    {
        Assert.Equal(
            DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            QuestResetTime.NextDaily(
                DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>The weekly reset falls on a Wednesday.</summary>
    /// <remarks>The raid lockout day, which is what weeklies follow.</remarks>
    [Fact]
    public void TheWeeklyReset_IsAWednesday()
    {
        DateTime next = QuestResetTime.NextWeekly(
            new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DayOfWeek.Wednesday, next.DayOfWeek);
        Assert.Equal(3, next.Hour);
    }

    /// <summary>
    /// The monthly reset is the first of the next month, not thirty days out.
    /// </summary>
    /// <remarks>
    /// Months are not the same length, and adding a fixed thirty days walks the reset backwards
    /// through the calendar until it lands in the previous month.
    /// </remarks>
    [Fact]
    public void TheMonthlyReset_IsTheFirstOfTheMonth()
    {
        DateTime next = QuestResetTime.NextMonthly(
            new DateTime(2026, 1, 31, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc), next);
    }

    // ------------------------------------------------------------------ helpers

    // Every daily in the data also carries Repeatable, so the fixtures do too — without it the
    // ordinary "already rewarded" check would mask the period check these tests are about.
    private static QuestTemplate Daily(uint id) => QuestFixture.Build(
        id: id, minLevel: 1, flags: QuestFlags.Daily, specialFlags: QuestSpecialFlags.Repeatable);

    private static QuestTemplate Weekly(uint id) => QuestFixture.Build(
        id: id, minLevel: 1, flags: QuestFlags.Weekly, specialFlags: QuestSpecialFlags.Repeatable);

    private static QuestTemplate Monthly(uint id) => QuestFixture.Build(
        id: id, minLevel: 1,
        specialFlags: QuestSpecialFlags.Monthly | QuestSpecialFlags.Repeatable);
}
