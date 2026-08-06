using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;
using WowEmu.Protocol;
using WowEmu.WorldServer;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>Builds quest templates and stores without a database behind them.</summary>
internal static class QuestFixture
{
    /// <summary>A quest with everything at its column default, so a test sets only what it is about.</summary>
    public static QuestTemplate Build(
        uint id = 1,
        byte method = 2,
        string title = "A Task",
        short level = 5,
        byte minLevel = 0,
        byte maxLevel = 0,
        ushort requiredClasses = 0,
        ushort requiredRaces = 0,
        byte rewardXpDifficulty = 1,
        int rewardOrRequiredMoney = 0,
        uint flags = 0,
        uint specialFlags = 0,
        int prevQuestId = 0,
        byte requiredPlayerKills = 0,
        QuestObjective[]? objectives = null,
        QuestItem[]? requiredItems = null,
        QuestItem[]? rewards = null,
        QuestItem[]? rewardChoices = null) =>
        new(
            Id: id,
            Method: method,
            Level: level,
            MinLevel: minLevel,
            MaxLevel: maxLevel,
            SortId: 0,
            Type: 0,
            SuggestedPlayers: 0,
            RequiredClasses: requiredClasses,
            RequiredRaces: requiredRaces,
            PrevQuestId: prevQuestId,
            NextQuestId: 0,
            NextQuestIdChain: 0,
            RewardXpDifficulty: rewardXpDifficulty,
            RewardOrRequiredMoney: rewardOrRequiredMoney,
            RewardMoneyMaxLevel: 0,
            RewardSpell: 0,
            RewardSpellCast: 0,
            SourceItemId: 0,
            SourceItemCount: 0,
            Flags: flags,
            SpecialFlags: specialFlags,
            Rewards: Pad(rewards, QuestConstants.MaxRewards),
            RewardChoices: Pad(rewardChoices, QuestConstants.MaxRewardChoices),
            Objectives: PadObjectives(objectives),
            RequiredItems: Pad(requiredItems, QuestConstants.MaxItemObjectives),
            SourceItems: new uint[QuestConstants.MaxObjectives],
            LogTitle: title,
            LogDescription: "Do the thing.",
            QuestDescription: "Somebody should do the thing.",
            AreaDescription: "Where the thing is.",
            CompletedText: "The thing is done.",
            OfferRewardText: "You did the thing.",
            RequestItemsText: "Have you done the thing?",
            ObjectiveText: ["", "", "", ""],
            RequiredPlayerKills: requiredPlayerKills);

    private static QuestItem[] Pad(QuestItem[]? items, int width)
    {
        QuestItem[] padded = new QuestItem[width];

        for (int i = 0; items is not null && i < items.Length && i < width; i++)
        {
            padded[i] = items[i];
        }

        return padded;
    }

    private static QuestObjective[] PadObjectives(QuestObjective[]? objectives)
    {
        QuestObjective[] padded = new QuestObjective[QuestConstants.MaxObjectives];

        for (int i = 0; objectives is not null && i < objectives.Length && i < padded.Length; i++)
        {
            padded[i] = objectives[i];
        }

        return padded;
    }

    /// <summary>A quest store holding exactly the templates a test names.</summary>
    public static QuestStore Store(params QuestTemplate[] quests)
    {
        QuestStore store = new();

        System.Reflection.FieldInfo field = typeof(QuestStore)
            .GetField("_quests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        Dictionary<uint, QuestTemplate> map = (Dictionary<uint, QuestTemplate>)field.GetValue(store)!;

        foreach (QuestTemplate quest in quests)
        {
            map[quest.Id] = quest;
        }

        return store;
    }
}

/// <summary>The two overloaded columns, and what a quest is worth.</summary>
public sealed class QuestTemplateTests
{
    /// <summary>
    /// A negative objective entry is a gameobject, and goes on the wire with the high bit set.
    /// </summary>
    /// <remarks>
    /// There is no column saying which kind it is; the sign is the whole distinction. And the
    /// client wants <c>id | 0x80000000</c>, not the negative — sending <c>-id</c> leaves it looking
    /// for a creature that does not exist, and the objective line never updates.
    /// </remarks>
    [Fact]
    public void ANegativeObjectiveEntry_IsAGameObject()
    {
        QuestObjective creature = new(299, 5);
        QuestObjective gameObject = new(-1732, 1);

        Assert.False(creature.IsGameObject);
        Assert.Equal(299u, creature.WireEntry);

        Assert.True(gameObject.IsGameObject);
        Assert.Equal(1732u, gameObject.AbsoluteEntry);
        Assert.Equal(1732u | 0x80000000u, gameObject.WireEntry);
    }

    /// <summary>
    /// One money column means two opposite things.
    /// </summary>
    /// <remarks>
    /// Read unsigned, a quest that costs gold pays out four billion copper instead.
    /// </remarks>
    [Fact]
    public void TheMoneyColumn_IsARewardOrACost()
    {
        QuestTemplate pays = QuestFixture.Build(rewardOrRequiredMoney: 500);
        QuestTemplate costs = QuestFixture.Build(rewardOrRequiredMoney: -500);

        Assert.Equal(500u, pays.RewardMoney);
        Assert.Equal(0u, pays.RequiredMoney);

        Assert.Equal(0u, costs.RewardMoney);
        Assert.Equal(500u, costs.RequiredMoney);
    }

    /// <summary>An objective with a count of zero is unused, whatever the entry says.</summary>
    [Fact]
    public void AnObjectiveWithNoCount_IsUnused()
    {
        QuestTemplate quest = QuestFixture.Build(objectives: [new QuestObjective(299, 0)]);

        Assert.Equal(0, quest.ObjectiveCount);
        Assert.False(quest.HasObjectives);
    }
}

/// <summary>What a quest pays.</summary>
public sealed class QuestRewardTests
{
    /// <summary>A table with one level's row, at a known payout.</summary>
    private static DbcStore<QuestXpEntry> Table(uint level, uint amount)
    {
        uint[] byDifficulty = new uint[QuestXpEntry.DifficultyCount];
        byDifficulty[1] = amount;

        return Store(new QuestXpEntry(level, byDifficulty));
    }

    private static DbcStore<QuestXpEntry> Store(params QuestXpEntry[] rows)
    {
        DbcStore<QuestXpEntry> store =
            (DbcStore<QuestXpEntry>)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(DbcStore<QuestXpEntry>));

        System.Reflection.FieldInfo entries = typeof(DbcStore<QuestXpEntry>)
            .GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        Dictionary<uint, QuestXpEntry> map = [];

        foreach (QuestXpEntry row in rows)
        {
            map[row.Level] = row;
        }

        entries.SetValue(store, map);

        return store;
    }

    /// <summary>
    /// The table is indexed by the quest's level, and RewardXPId is a column within that row.
    /// </summary>
    /// <remarks>
    /// Looking the quest up by its own id finds nothing, and every quest in the game pays zero.
    /// </remarks>
    [Fact]
    public void TheTable_IsIndexedByQuestLevel()
    {
        QuestTemplate quest = QuestFixture.Build(id: 8901, level: 5, rewardXpDifficulty: 1);

        // A row for the quest's level pays; a row for its id does not exist.
        Assert.True(QuestReward.Experience(quest, 5, Table(level: 5, amount: 500)) > 0);
        Assert.Equal(0u, QuestReward.Experience(quest, 5, Table(level: 8901, amount: 500)));
    }

    /// <summary>
    /// A quest level of -1 means the player's level.
    /// </summary>
    /// <remarks>
    /// It is what makes a scaling quest pay sensibly at any level. Read unsigned the level becomes
    /// 65,535 and the reward is nothing.
    /// </remarks>
    [Fact]
    public void ALevelOfMinusOne_MeansThePlayers()
    {
        QuestTemplate scaling = QuestFixture.Build(level: -1, rewardXpDifficulty: 1);

        Assert.True(QuestReward.Experience(scaling, 20, Table(level: 20, amount: 1000)) > 0);
    }

    /// <summary>
    /// The difficulty factor is clamped at both ends.
    /// </summary>
    /// <remarks>
    /// An over-levelled player still gets a tenth rather than nothing, and an under-levelled one
    /// gets no more than the full amount.
    /// </remarks>
    [Fact]
    public void TheDifficultyFactor_IsClamped()
    {
        QuestTemplate quest = QuestFixture.Build(level: 10, rewardXpDifficulty: 1);
        DbcStore<QuestXpEntry> table = Table(level: 10, amount: 1000);

        // Level 1 against a level 10 quest: the factor would be 38, clamped to 10, so the full
        // 1000. Level 60: the factor would be -80, clamped to 1, so a tenth.
        Assert.Equal(1000u, QuestReward.Experience(quest, 1, table));
        Assert.Equal(100u, QuestReward.Experience(quest, 60, table));
    }

    /// <summary>
    /// The payout is rounded to a band, and the band widens with the amount.
    /// </summary>
    /// <remarks>
    /// Skipping the rounding pays amounts the real client never shows — close enough to look right
    /// and wrong in a way nothing would flag.
    /// </remarks>
    [Theory]
    [InlineData(70u, 70u)]      // 70 stays 70; the band is 5 below 100
    [InlineData(330u, 330u)]    // the band is 10 up to 500
    [InlineData(770u, 775u)]    // 77 × 10 = 770, rounded to the nearest 25
    [InlineData(1230u, 1250u)]  // above 1000 the band is 50
    public void ThePayout_IsRoundedToABand(uint raw, uint expected)
    {
        QuestTemplate quest = QuestFixture.Build(level: 10, rewardXpDifficulty: 1);

        // A difficulty factor of exactly 10 makes the raw figure pass through undivided.
        Assert.Equal(expected, QuestReward.Experience(quest, 1, Table(level: 10, amount: raw)));
    }

    /// <summary>A quest whose level has no row pays nothing rather than throwing.</summary>
    [Fact]
    public void AQuestWithNoRow_PaysNothing() =>
        Assert.Equal(0u, QuestReward.Experience(QuestFixture.Build(level: 99), 10, Table(5, 500)));
}

/// <summary>Taking quests, tracking them, and the log slots.</summary>
public sealed class QuestLogTests
{
    private static readonly QuestObjective[] KillFiveWolves = [new QuestObjective(299, 5)];

    /// <summary>A quest with objectives starts incomplete.</summary>
    [Fact]
    public void AQuestWithObjectives_StartsIncomplete()
    {
        Player player = InventoryFixture.Player(level: 5);
        QuestTemplate quest = QuestFixture.Build(objectives: KillFiveWolves);

        QuestProgress? progress = player.Quests.Accept(quest);

        Assert.NotNull(progress);
        Assert.Equal(QuestStatus.Incomplete, progress.Status);
        Assert.Equal(0, progress.Slot);
    }

    /// <summary>
    /// A quest with no objectives is complete the moment it is taken.
    /// </summary>
    /// <remarks>
    /// A "go and speak to someone" quest. Marking it incomplete leaves it uncompletable — there is
    /// nothing that could ever finish it.
    /// </remarks>
    [Fact]
    public void AQuestWithNoObjectives_IsCompleteOnAcceptance()
    {
        Player player = InventoryFixture.Player(level: 5);

        Assert.Equal(QuestStatus.Complete, player.Quests.Accept(QuestFixture.Build())!.Status);
    }

    /// <summary>The quest id lands in the log slot the client reads.</summary>
    [Fact]
    public void TheQuestId_LandsInTheLogSlot()
    {
        Player player = InventoryFixture.Player(level: 5);

        player.Quests.Accept(QuestFixture.Build(id: 26, objectives: KillFiveWolves));

        Assert.Equal(26u, player.Fields.GetUInt32(UpdateFields.PLAYER_QUEST_LOG_1_1));
    }

    /// <summary>Slots are handed out in order, five fields apart.</summary>
    [Fact]
    public void Slots_AreFiveFieldsApart()
    {
        Player player = InventoryFixture.Player(level: 5);

        player.Quests.Accept(QuestFixture.Build(id: 1, objectives: KillFiveWolves));
        player.Quests.Accept(QuestFixture.Build(id: 2, objectives: KillFiveWolves));

        Assert.Equal(1u, player.Fields.GetUInt32(UpdateFields.PLAYER_QUEST_LOG_1_1));
        Assert.Equal(2u, player.Fields.GetUInt32(UpdateFields.PLAYER_QUEST_LOG_2_1));
        Assert.Equal(UpdateFields.PLAYER_QUEST_LOG_1_1 + 5, UpdateFields.PLAYER_QUEST_LOG_2_1);
    }

    /// <summary>
    /// Four counters share two update fields, sixteen bits each.
    /// </summary>
    /// <remarks>
    /// The pair is one 64-bit value to the client. Writing a counter per field would put the first
    /// objective's count where the client reads all four at once.
    /// </remarks>
    [Fact]
    public void FourCounters_ShareTwoFields()
    {
        Player player = InventoryFixture.Player(level: 5);

        QuestTemplate quest = QuestFixture.Build(objectives:
        [
            new QuestObjective(1, 10),
            new QuestObjective(2, 10),
            new QuestObjective(3, 10),
            new QuestObjective(4, 10),
        ]);

        QuestStore store = QuestFixture.Store(quest);
        player.Quests.Accept(quest);

        // One of each, so every counter holds a different value only if they are packed correctly.
        player.Quests.CreditKill(1, store);
        player.Quests.CreditKill(2, store);
        player.Quests.CreditKill(2, store);
        player.Quests.CreditKill(4, store);

        int counters = UpdateFields.PLAYER_QUEST_LOG_1_1 + 2;

        ulong packed = player.Fields.GetUInt32(counters)
            | ((ulong)player.Fields.GetUInt32(counters + 1) << 32);

        Assert.Equal(1u, (packed >> 0) & 0xFFFF);
        Assert.Equal(2u, (packed >> 16) & 0xFFFF);
        Assert.Equal(0u, (packed >> 32) & 0xFFFF);
        Assert.Equal(1u, (packed >> 48) & 0xFFFF);
    }

    /// <summary>A kill counts against every quest that wants it, not the first.</summary>
    /// <remarks>
    /// Two quests asking for the same creature is common, and crediting one is a bug a player
    /// notices and cannot explain.
    /// </remarks>
    [Fact]
    public void AKill_CountsAgainstEveryQuestThatWantsIt()
    {
        Player player = InventoryFixture.Player(level: 5);

        QuestTemplate first = QuestFixture.Build(id: 1, objectives: KillFiveWolves);
        QuestTemplate second = QuestFixture.Build(id: 2, objectives: KillFiveWolves);
        QuestStore store = QuestFixture.Store(first, second);

        player.Quests.Accept(first);
        player.Quests.Accept(second);

        IReadOnlyList<QuestKillCredit> credited = player.Quests.CreditKill(299, store);

        Assert.Equal(2, credited.Count);
        Assert.Equal(1, player.Quests.Find(1)!.Killed[0]);
        Assert.Equal(1, player.Quests.Find(2)!.Killed[0]);
    }

    /// <summary>A counter stops at the objective's requirement.</summary>
    [Fact]
    public void ACounter_StopsAtTheRequirement()
    {
        Player player = InventoryFixture.Player(level: 5);
        QuestTemplate quest = QuestFixture.Build(objectives: [new QuestObjective(299, 2)]);
        QuestStore store = QuestFixture.Store(quest);

        player.Quests.Accept(quest);

        Assert.Single(player.Quests.CreditKill(299, store));
        Assert.Single(player.Quests.CreditKill(299, store));
        Assert.Empty(player.Quests.CreditKill(299, store));

        Assert.Equal(2, player.Quests.Find(1)!.Killed[0]);
        Assert.Equal(QuestStatus.Complete, player.Quests.StatusOf(1));
    }

    /// <summary>A gameobject objective is not credited by killing a creature of the same id.</summary>
    [Fact]
    public void AGameObjectObjective_IsNotCreditedByACreature()
    {
        Player player = InventoryFixture.Player(level: 5);
        QuestTemplate quest = QuestFixture.Build(objectives: [new QuestObjective(-299, 1)]);
        QuestStore store = QuestFixture.Store(quest);

        player.Quests.Accept(quest);

        Assert.Empty(player.Quests.CreditKill(299, store));
        Assert.Equal(QuestStatus.Incomplete, player.Quests.StatusOf(1));
    }

    /// <summary>Item objectives are counted from the bags, including at the moment of acceptance.</summary>
    /// <remarks>
    /// A player already carrying the items is done immediately, which is what upstream does — and
    /// counting only on pickup would leave them stuck.
    /// </remarks>
    [Fact]
    public void ItemObjectives_AreCountedFromTheBags()
    {
        Player player = InventoryFixture.Player(level: 5);
        ItemTemplate pelt = ItemFixture.Build(entry: 50432, stackable: 20);

        InventoryFixture.Place(player, pelt, InventoryFixture.Backpack(), count: 8);

        QuestTemplate quest = QuestFixture.Build(requiredItems: [new QuestItem(50432, 8)]);

        Assert.Equal(QuestStatus.Complete, player.Quests.Accept(quest)!.Status);
    }

    /// <summary>Picking the items up afterwards finishes the quest.</summary>
    [Fact]
    public void PickingTheItemsUpLater_FinishesTheQuest()
    {
        Player player = InventoryFixture.Player(level: 5);
        ItemTemplate pelt = ItemFixture.Build(entry: 50432, stackable: 20);

        QuestTemplate quest = QuestFixture.Build(requiredItems: [new QuestItem(50432, 3)]);
        QuestStore store = QuestFixture.Store(quest);

        player.Quests.Accept(quest);

        Assert.Equal(QuestStatus.Incomplete, player.Quests.StatusOf(1));

        InventoryFixture.Place(player, pelt, InventoryFixture.Backpack(), count: 3);

        Assert.Equal([1u], player.Quests.RecountAllItems(store));
        Assert.Equal(QuestStatus.Complete, player.Quests.StatusOf(1));
    }

    /// <summary>A quest requiring money is not complete until the player has it.</summary>
    [Fact]
    public void AQuestRequiringMoney_WaitsForIt()
    {
        Player player = InventoryFixture.Player(level: 5);
        QuestTemplate quest = QuestFixture.Build(rewardOrRequiredMoney: -100, objectives: KillFiveWolves);
        QuestStore store = QuestFixture.Store(quest);

        player.Quests.Accept(quest);

        for (int i = 0; i < 5; i++)
        {
            player.Quests.CreditKill(299, store);
        }

        Assert.Equal(QuestStatus.Incomplete, player.Quests.StatusOf(1));

        player.Money = 100;
        player.Quests.RecountItems(quest, player.Quests.Find(1)!);

        Assert.Equal(QuestStatus.Complete, player.Quests.StatusOf(1));
    }

    /// <summary>
    /// Handing in leaves the record behind, so the quest is not offered again.
    /// </summary>
    /// <remarks>
    /// Dropping it lets a player repeat every quest in the game by talking to the same NPC twice.
    /// </remarks>
    [Fact]
    public void HandingIn_LeavesTheRecordBehind()
    {
        Player player = InventoryFixture.Player(level: 5);
        QuestTemplate quest = QuestFixture.Build();

        QuestProgress progress = player.Quests.Accept(quest)!;
        player.Quests.Reward(progress);

        Assert.Equal(QuestStatus.Rewarded, player.Quests.StatusOf(1));
        Assert.True(player.Quests.IsRewarded(1));
        Assert.Equal(0, player.Quests.InLogCount);
        Assert.Equal(0u, player.Fields.GetUInt32(UpdateFields.PLAYER_QUEST_LOG_1_1));
        Assert.Equal(QuestTakeResult.AlreadyDone, player.Quests.CanTake(quest));
    }

    /// <summary>A repeatable quest can be taken again after being handed in.</summary>
    [Fact]
    public void ARepeatableQuest_CanBeRetaken()
    {
        Player player = InventoryFixture.Player(level: 5);
        QuestTemplate quest = QuestFixture.Build(specialFlags: QuestSpecialFlags.Repeatable);

        player.Quests.Reward(player.Quests.Accept(quest)!);

        Assert.Equal(QuestTakeResult.Ok, player.Quests.CanTake(quest));
        Assert.NotNull(player.Quests.Accept(quest));
    }

    /// <summary>A freed slot is reused.</summary>
    [Fact]
    public void AbandoningAQuest_FreesItsSlot()
    {
        Player player = InventoryFixture.Player(level: 5);

        player.Quests.Accept(QuestFixture.Build(id: 1, objectives: KillFiveWolves));

        Assert.True(player.Quests.Abandon(1));
        Assert.Equal(QuestStatus.None, player.Quests.StatusOf(1));

        player.Quests.Accept(QuestFixture.Build(id: 2, objectives: KillFiveWolves));

        Assert.Equal(2u, player.Fields.GetUInt32(UpdateFields.PLAYER_QUEST_LOG_1_1));
    }

    /// <summary>A quest below the player's minimum level is refused.</summary>
    [Fact]
    public void ATooHighQuest_IsRefused()
    {
        Player player = InventoryFixture.Player(level: 5);

        Assert.Equal(
            QuestTakeResult.TooLowLevel,
            player.Quests.CanTake(QuestFixture.Build(minLevel: 20)));
    }

    /// <summary>
    /// A maximum level of zero means no maximum.
    /// </summary>
    /// <remarks>
    /// Nearly every quest in the game has zero here. Comparing against it literally makes every one
    /// of them unavailable to everybody.
    /// </remarks>
    [Fact]
    public void AMaxLevelOfZero_MeansNoMaximum()
    {
        Player player = InventoryFixture.Player(level: 60);

        Assert.Equal(QuestTakeResult.Ok, player.Quests.CanTake(QuestFixture.Build(maxLevel: 0)));
        Assert.Equal(QuestTakeResult.TooHighLevel, player.Quests.CanTake(QuestFixture.Build(maxLevel: 10)));
    }

    /// <summary>
    /// The class and race masks are bit masks over one-based ids.
    /// </summary>
    /// <remarks>
    /// A warrior is class 1 and bit 0. Shifting by the class rather than by class minus one locks
    /// every warrior out of every warrior quest.
    /// </remarks>
    [Fact]
    public void TheClassMask_IsOneBased()
    {
        Player warrior = InventoryFixture.Player(level: 5, characterClass: 1);

        Assert.Equal(QuestTakeResult.Ok, warrior.Quests.CanTake(QuestFixture.Build(requiredClasses: 1 << 0)));
        Assert.Equal(QuestTakeResult.WrongClass, warrior.Quests.CanTake(QuestFixture.Build(requiredClasses: 1 << 3)));

        // Zero means anyone.
        Assert.Equal(QuestTakeResult.Ok, warrior.Quests.CanTake(QuestFixture.Build(requiredClasses: 0)));
    }

    /// <summary>The same quest cannot be taken twice at once.</summary>
    [Fact]
    public void AQuestAlreadyInTheLog_CannotBeRetaken()
    {
        Player player = InventoryFixture.Player(level: 5);
        QuestTemplate quest = QuestFixture.Build(objectives: KillFiveWolves);

        player.Quests.Accept(quest);

        Assert.Equal(QuestTakeResult.AlreadyOn, player.Quests.CanTake(quest));
        Assert.Null(player.Quests.Accept(quest));
    }

    /// <summary>The log holds 25 and refuses the twenty-sixth.</summary>
    [Fact]
    public void TheLog_HoldsTwentyFive()
    {
        Player player = InventoryFixture.Player(level: 5);

        for (uint i = 1; i <= QuestConstants.MaxLogSize; i++)
        {
            Assert.NotNull(player.Quests.Accept(QuestFixture.Build(id: i, objectives: KillFiveWolves)));
        }

        Assert.Equal(QuestConstants.MaxLogSize, player.Quests.InLogCount);
        Assert.Equal(
            QuestTakeResult.LogFull,
            player.Quests.CanTake(QuestFixture.Build(id: 999, objectives: KillFiveWolves)));
    }
}

/// <summary>The quest packets.</summary>
public sealed class QuestPacketTests
{
    private static readonly ObjectGuid Npc = ObjectGuid.Create(HighGuid.Unit, 197, 5);

    private static uint NoDisplay(uint itemId) => 0;

    /// <summary>The status is a single byte, though the enum behind it is a word.</summary>
    [Fact]
    public void TheStatus_IsOneByte()
    {
        PacketWriter writer = new();
        QuestPackets.WriteStatus(writer, Npc, QuestGiverStatus.Available);

        Assert.Equal(9, writer.WrittenSpan.Length);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt64(out ulong npc));
        Assert.Equal(Npc.Value, npc);

        Assert.True(reader.TryReadUInt8(out byte status));
        Assert.Equal(QuestGiverStatus.Available, status);
    }

    /// <summary>The details window reads back field by field.</summary>
    [Fact]
    public void TheDetails_ReadBackFieldByField()
    {
        PacketWriter writer = new();

        QuestPackets.WriteDetails(
            writer, Npc, QuestFixture.Build(id: 26, title: "Kobold Camp Cleanup"),
            rewardMoney: 250, rewardXp: 400, NoDisplay);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt64(out ulong npc));
        Assert.Equal(Npc.Value, npc);

        // The divider — who shared the quest. Empty unless another player did.
        Assert.True(reader.TryReadUInt64(out ulong divider));
        Assert.Equal(0u, divider);

        Assert.True(reader.TryReadUInt32(out uint questId));
        Assert.Equal(26u, questId);

        Assert.True(reader.TryReadCString(out string? title));
        Assert.Equal("Kobold Camp Cleanup", title);
    }

    /// <summary>
    /// The reward count is of the used slots, and the loop skips the unused ones.
    /// </summary>
    /// <remarks>
    /// A quest with a gap in its reward columns writes fewer entries than the array is long.
    /// Writing all four regardless puts zeroes where the client reads the money.
    /// </remarks>
    [Fact]
    public void TheRewardCount_MatchesWhatIsWritten()
    {
        QuestTemplate two = QuestFixture.Build(rewards: [new QuestItem(25, 1), new QuestItem(35, 1)]);
        QuestTemplate none = QuestFixture.Build();

        PacketWriter withRewards = new();
        QuestPackets.WriteDetails(withRewards, Npc, two, 0, 0, NoDisplay);

        PacketWriter without = new();
        QuestPackets.WriteDetails(without, Npc, none, 0, 0, NoDisplay);

        // Three words per reward.
        Assert.Equal(2 * 12, withRewards.WrittenSpan.Length - without.WrittenSpan.Length);
    }

    /// <summary>An incomplete quest greys out the Continue button rather than erroring.</summary>
    [Fact]
    public void AnIncompleteQuest_GreysOutContinue()
    {
        QuestTemplate quest = QuestFixture.Build(requiredItems: [new QuestItem(50432, 8)]);

        PacketWriter complete = new();
        QuestPackets.WriteRequestItems(complete, Npc, quest, canComplete: true, NoDisplay);

        PacketWriter incomplete = new();
        QuestPackets.WriteRequestItems(incomplete, Npc, quest, canComplete: false, NoDisplay);

        // Same length; only the enable word differs.
        Assert.Equal(complete.WrittenSpan.Length, incomplete.WrittenSpan.Length);
        Assert.NotEqual(
            complete.WrittenSpan.ToArray()[^16..],
            incomplete.WrittenSpan.ToArray()[^16..]);
    }

    /// <summary>The kill-credit packet reads back field by field.</summary>
    [Fact]
    public void AKillCredit_ReadsBackFieldByField()
    {
        ObjectGuid victim = ObjectGuid.Create(HighGuid.Unit, 299, 42);

        PacketWriter writer = new();
        QuestPackets.WriteAddKill(writer, questId: 26, wireEntry: 299, current: 3, required: 5, victim);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt32(out uint questId));
        Assert.Equal(26u, questId);

        Assert.True(reader.TryReadUInt32(out uint entry));
        Assert.Equal(299u, entry);

        Assert.True(reader.TryReadUInt32(out uint current));
        Assert.Equal(3u, current);

        Assert.True(reader.TryReadUInt32(out uint required));
        Assert.Equal(5u, required);

        Assert.True(reader.TryReadUInt64(out ulong guid));
        Assert.Equal(victim.Value, guid);

        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// The query response's objectives read back, with a gameobject's high bit intact.
    /// </summary>
    /// <remarks>
    /// This packet is what makes the quest log draw a row at all: the details window is enough to
    /// accept a quest, and the client will not list one it has no structured data for.
    /// </remarks>
    [Fact]
    public void TheQueryResponse_CarriesTheStructuredObjectives()
    {
        QuestTemplate quest = QuestFixture.Build(
            id: 33,
            title: "Wolves Across the Border",
            objectives: [new QuestObjective(299, 5), new QuestObjective(-1732, 1)],
            requiredItems: [new QuestItem(50432, 8)]);

        PacketWriter writer = new();
        QuestPackets.WriteQueryResponse(writer, quest);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt32(out uint questId));
        Assert.Equal(33u, questId);

        // Everything between the id and the five strings, counted out rather than guessed:
        // 26 scalar words (the id included), the reward and choice arrays as pairs, three
        // reputation blocks of five, and four point-of-interest words.
        const int ScalarWords = 26;
        const int RewardWords = (QuestConstants.MaxRewards + QuestConstants.MaxRewardChoices) * 2;
        const int ReputationWords = QuestConstants.MaxReputations * 3;
        const int PoiWords = 4;

        reader.Skip((ScalarWords - 1 + RewardWords + ReputationWords + PoiWords) * 4);

        Assert.True(reader.TryReadCString(out string? title));
        Assert.Equal("Wolves Across the Border", title);

        // Objectives then details — not the column order, and not the details packet's order.
        Assert.True(reader.TryReadCString(out string? objectives));
        Assert.Equal("Do the thing.", objectives);

        Assert.True(reader.TryReadCString(out string? details));
        Assert.Equal("Somebody should do the thing.", details);

        Assert.True(reader.TryReadCString(out string? area));
        Assert.Equal("Where the thing is.", area);

        Assert.True(reader.TryReadCString(out string? completed));
        Assert.Equal("The thing is done.", completed);

        Assert.True(reader.TryReadUInt32(out uint firstEntry));
        Assert.Equal(299u, firstEntry);

        Assert.True(reader.TryReadUInt32(out uint firstCount));
        Assert.Equal(5u, firstCount);

        reader.Skip(4 + 4);          // source item and its count

        // The gameobject objective keeps its high bit rather than arriving as a negative.
        Assert.True(reader.TryReadUInt32(out uint secondEntry));
        Assert.Equal(1732u | 0x80000000u, secondEntry);
    }

    /// <summary>
    /// All four rewards and all six choices are written, gaps included.
    /// </summary>
    /// <remarks>
    /// Unlike the details and offer packets, there is no count in front of them — the client reads
    /// a fixed number, so skipping the empty slots shifts everything after.
    /// </remarks>
    [Fact]
    public void TheQueryResponse_WritesEveryRewardSlot()
    {
        PacketWriter empty = new();
        QuestPackets.WriteQueryResponse(empty, QuestFixture.Build());

        PacketWriter full = new();
        QuestPackets.WriteQueryResponse(full, QuestFixture.Build(
            rewards: [new QuestItem(25, 1)],
            rewardChoices: [new QuestItem(80, 1), new QuestItem(81, 1)]));

        Assert.Equal(empty.WrittenSpan.Length, full.WrittenSpan.Length);
    }

    /// <summary>The completion packet carries the experience and money actually paid.</summary>
    [Fact]
    public void TheCompletion_CarriesWhatWasPaid()
    {
        PacketWriter writer = new();
        QuestPackets.WriteComplete(writer, questId: 26, experience: 400, money: 250);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        reader.TryReadUInt32(out uint questId);
        reader.TryReadUInt32(out uint experience);
        reader.TryReadUInt32(out uint money);

        Assert.Equal(26u, questId);
        Assert.Equal(400u, experience);
        Assert.Equal(250u, money);
        Assert.Equal(12, reader.Remaining);
    }
}

/// <summary>Quest credit through a real map.</summary>
public sealed class MapQuestTests
{
    /// <summary>A kill credits the quests of everyone on the threat list.</summary>
    /// <remarks>
    /// The threat list is the closest thing to a group here, and it is why the credit runs before
    /// <c>Kill()</c> — which clears it.
    /// </remarks>
    [Fact]
    public void AKill_CreditsTheQuestsOfWhoeverWasFighting()
    {
        QuestTemplate quest = QuestFixture.Build(id: 26, objectives: [new QuestObjective(299, 2)]);
        QuestStore quests = QuestFixture.Store(quest);

        (Map map, Player player, Creature victim, MapCombatFixture.Link link) =
            MapCombatFixture.Engaged(quests: quests);

        player.AttackStop();
        player.Quests.Accept(quest);

        victim.Threat.AddThreat(player, 100f);
        victim.Health = 0;

        map.Kill(victim);

        Assert.Equal(1, player.Quests.Find(26)!.Killed[0]);
        Assert.Single(link.QuestCredits);
        Assert.Equal((26u, 299u, 1u, 2u), link.QuestCredits[0]);
        Assert.Empty(link.QuestsCompleted);
    }

    /// <summary>The last kill also tells the client the quest is ready to hand in.</summary>
    [Fact]
    public void TheLastKill_AlsoReportsCompletion()
    {
        QuestTemplate quest = QuestFixture.Build(id: 26, objectives: [new QuestObjective(299, 1)]);
        QuestStore quests = QuestFixture.Store(quest);

        (Map map, Player player, Creature victim, MapCombatFixture.Link link) =
            MapCombatFixture.Engaged(quests: quests);

        player.AttackStop();
        player.Quests.Accept(quest);

        victim.Threat.AddThreat(player, 100f);
        victim.Health = 0;

        map.Kill(victim);

        Assert.Equal([26u], link.QuestsCompleted);
        Assert.Equal(QuestStatus.Complete, player.Quests.StatusOf(26));
    }

    /// <summary>Someone who was not fighting gets no credit.</summary>
    [Fact]
    public void SomeoneWhoWasNotFighting_GetsNoCredit()
    {
        QuestTemplate quest = QuestFixture.Build(id: 26, objectives: [new QuestObjective(299, 2)]);
        QuestStore quests = QuestFixture.Store(quest);

        (Map map, Player player, Creature victim, _) = MapCombatFixture.Engaged(quests: quests);

        player.AttackStop();
        player.Quests.Accept(quest);

        victim.Threat.Remove(player);
        victim.Health = 0;

        map.Kill(victim);

        Assert.Equal(0, player.Quests.Find(26)!.Killed[0]);
    }
}

/// <summary>The quest tables, over the real vendored rows.</summary>
public sealed class QuestStoreTests(ITestOutputHelper output)
{
    private static CancellationToken TestToken => CancellationToken.None;

    [RequiresWorldDatabaseFact]
    public async Task TheStores_LoadEveryRow()
    {
        QuestStore quests = new();
        QuestRelationStore starters = new("creature_queststarter");
        QuestRelationStore enders = new("creature_questender");

        await quests.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await starters.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await enders.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(quests.Count > 9_000, $"only {quests.Count} quests");
        Assert.True(starters.RowCount > 7_000, $"only {starters.RowCount} starter links");
        Assert.True(enders.RowCount > 7_000, $"only {enders.RowCount} ender links");

        output.WriteLine($"{quests}; {starters}; {enders}");
    }

    /// <summary>
    /// A known quest reads back with the values the client shows.
    /// </summary>
    /// <remarks>
    /// A 70-column read lands every column in the right field or none of them. Counting rows would
    /// pass with every column one to the left.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task AKnownQuest_ReadsBackCorrectly()
    {
        QuestStore quests = new();
        await quests.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        // "Wolves Across the Border" — the human starting zone's wolf quest.
        Assert.True(quests.TryGet(176, out QuestTemplate? quest));
        Assert.NotNull(quest);

        Assert.False(string.IsNullOrEmpty(quest.LogTitle), "the title did not load");
        Assert.False(string.IsNullOrEmpty(quest.QuestDescription), "the description did not load");
        Assert.True(quest.HasObjectives, "the objectives did not load");

        output.WriteLine(
            $"{quest.Id} '{quest.LogTitle}' level {quest.Level}, "
            + $"{quest.ObjectiveCount} kill and {quest.RequiredItemCount} item objective(s)");
    }

    /// <summary>
    /// Every real quest level has a row in QuestXP.dbc, and level 255 deliberately does not.
    /// </summary>
    /// <remarks>
    /// A missing row pays zero, silently, so this is the only place a gap would ever be noticed.
    /// <b>Level 255 is a sentinel</b> — four quests carry it, the DBC has no row for it, and
    /// upstream pays nothing for them too. That is the data being explicit rather than a hole.
    /// </remarks>
    /// <summary>
    /// The auto-accept flag is merged in from <c>SpecialFlags</c> at load, and the switch strips it.
    /// </summary>
    /// <remarks>
    /// Two columns feed one behaviour: <c>Quest::Quest</c> reads <c>Flags</c> and
    /// <c>LoadQuestTemplateAddon</c> ORs the bit in for any quest whose <c>SpecialFlags</c> asks.
    /// Against real data because that is the only way to know the merge matches the content — a
    /// mistake either way is a starting zone nobody can play.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task AutoAccept_IsMergedFromSpecialFlagsAndCanBeSwitchedOff()
    {
        // 783 "A Threat Within" already carries the bit in its own Flags column, so it proves
        // nothing about the merge. 170 "A New Threat" has Flags 8 and SpecialFlags 4 — it is
        // auto-accept ONLY if the two columns are combined, and 110 quests are in that position.
        const uint AThreatWithin = 783;
        const uint ANewThreat = 170;

        QuestStore honoured = new();
        await honoured.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(honoured.TryGet(ANewThreat, out QuestTemplate? merged) && merged is not null);
        Assert.NotEqual(0u, merged.SpecialFlags & QuestSpecialFlags.AutoAccept);
        Assert.True(
            merged.IsAutoAccept,
            "quest 170 is auto-accept only via SpecialFlags — if the two columns are not merged at "
            + "load, 110 quests silently stop being taken by anyone, because the client never "
            + "sends an accept for them either");

        Assert.True(honoured.TryGet(AThreatWithin, out QuestTemplate? raw) && raw is not null);
        Assert.True(raw!.IsAutoAccept);

        QuestStore ignored = new() { IgnoreAutoAccept = true };
        await ignored.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        foreach (uint id in (uint[])[AThreatWithin, ANewThreat])
        {
            Assert.True(ignored.TryGet(id, out QuestTemplate? stripped) && stripped is not null);
            Assert.False(stripped!.IsAutoAccept, $"quest {id} kept the flag with the switch on");
        }

        int autoAccept = 0;

        for (uint id = 1; id < 30_000; id++)
        {
            if (honoured.TryGet(id, out QuestTemplate? each) && each is not null && each.IsAutoAccept)
            {
                autoAccept++;
            }
        }

        output.WriteLine($"{autoAccept} auto-accept quests of {honoured.Count}");

        // A targeted set, not a blanket. If this ever becomes most of the game, the merge is wrong.
        Assert.InRange(autoAccept, 1, honoured.Count / 10);
    }

    [RequiresClientDataFact]
    public async Task EveryRealQuestLevel_HasAnExperienceRow()
    {
        QuestStore quests = new();
        await quests.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        if (quests.Count == 0)
        {
            return;
        }

        DbcStores stores = DbcStores.Load(ClientData.DbcDirectory);

        HashSet<short> missing = [];
        int scaling = 0;

        foreach (QuestTemplate quest in quests.All)
        {
            if (quest.Level == -1)
            {
                scaling++;
                continue;
            }

            if (quest.Level > 0 && !stores.QuestXp.TryGet((uint)quest.Level, out _))
            {
                missing.Add(quest.Level);
            }
        }

        output.WriteLine(
            $"{quests.Count} quests, {scaling} scaling, "
            + $"levels with no row: {string.Join(", ", missing.Order())}");

        // 255 and nothing else. A second gap would be a real one.
        Assert.Equal<short>([255], [.. missing.Order()]);

        QuestTemplate sentinel = quests.All.First(quest => quest.Level == 255);

        Assert.Equal(0u, QuestReward.Experience(sentinel, 80, stores.QuestXp));
    }

    /// <summary>Every quest a creature offers actually exists.</summary>
    /// <remarks>
    /// A dangling link is an NPC with an exclamation mark and nothing behind it — the client opens
    /// a window the server cannot fill.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task EveryQuestLink_Resolves()
    {
        QuestStore quests = new();
        QuestRelationStore starters = new("creature_queststarter");
        QuestRelationStore enders = new("creature_questender");

        await quests.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await starters.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await enders.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        HashSet<uint> dangling = [];
        int checkedLinks = 0;

        foreach (QuestRelationStore store in (QuestRelationStore[])[starters, enders])
        {
            for (uint creature = 1; creature < 100_000; creature++)
            {
                foreach (uint questId in store.For(creature))
                {
                    checkedLinks++;

                    if (!quests.TryGet(questId, out _))
                    {
                        dangling.Add(questId);
                    }
                }
            }
        }

        output.WriteLine($"{checkedLinks} links, {dangling.Count} dangling");

        Assert.True(checkedLinks > 10_000, $"only {checkedLinks} links walked");
        Assert.Empty(dangling);
    }
}

/// <summary>
/// The flag columns, the chain prerequisites, and what the query response does with both.
/// </summary>
public sealed class QuestFlagTests
{
    private static readonly QuestObjective[] KillFiveWolves = [new QuestObjective(299, 5)];

    /// <summary>
    /// The two flag columns carry the values the C++ enums do.
    /// </summary>
    /// <remarks>
    /// Pinned because the two sets overlap numerically and nothing else would notice a swap: reading
    /// <c>Flags</c> with a <c>SpecialFlags</c> constant returns a plausible boolean about the wrong
    /// property of every quest in the game.
    /// </remarks>
    [Fact]
    public void TheFlagConstants_MatchTheEnums()
    {
        Assert.Equal(0x00000200u, QuestFlags.HiddenRewards);
        Assert.Equal(0x00000400u, QuestFlags.Tracking);
        Assert.Equal(0x00001000u, QuestFlags.Daily);
        Assert.Equal(0x00008000u, QuestFlags.Weekly);
        Assert.Equal(0x00010000u, QuestFlags.AutoComplete);

        Assert.Equal(0x0001u, QuestSpecialFlags.Repeatable);
        Assert.Equal(0x0002u, QuestSpecialFlags.ExplorationOrEvent);
        Assert.Equal(0x0004u, QuestSpecialFlags.AutoAccept);
        Assert.Equal(0x0010u, QuestSpecialFlags.Monthly);
    }

    /// <summary>Repeatable is read from SpecialFlags, and the same bit in Flags does not set it.</summary>
    [Fact]
    public void Repeatable_ComesFromSpecialFlagsAlone()
    {
        Assert.True(QuestFixture.Build(specialFlags: QuestSpecialFlags.Repeatable).IsRepeatable);

        // 0x0001 in the other column is QUEST_FLAGS_STAY_ALIVE, which says nothing about repeating.
        Assert.False(QuestFixture.Build(flags: 0x0001).IsRepeatable);
    }

    /// <summary>A quest whose chain predecessor is unfinished is not on offer.</summary>
    /// <remarks>
    /// Without this a questgiver hands a fresh character its whole chain at once, and the second
    /// quest in a line can be taken before the first.
    /// </remarks>
    [Fact]
    public void AQuestWithAPrerequisite_NeedsItRewardedFirst()
    {
        Player player = InventoryFixture.Player(level: 5);

        QuestTemplate first = QuestFixture.Build(id: 783);
        QuestTemplate second = QuestFixture.Build(id: 5261, prevQuestId: 783);

        Assert.Equal(QuestTakeResult.MissingPrerequisite, player.Quests.CanTake(second));

        // Holding it is not enough — the positive form wants it handed in.
        player.Quests.Accept(first);
        Assert.Equal(QuestTakeResult.MissingPrerequisite, player.Quests.CanTake(second));

        player.Quests.Reward(player.Quests.Find(783)!);
        Assert.Equal(QuestTakeResult.Ok, player.Quests.CanTake(second));
    }

    /// <summary>A negative prerequisite only wants the earlier quest started.</summary>
    [Fact]
    public void ANegativePrerequisite_IsSatisfiedByHoldingTheEarlierQuest()
    {
        Player player = InventoryFixture.Player(level: 5);

        QuestTemplate second = QuestFixture.Build(id: 5261, prevQuestId: -783);

        Assert.Equal(QuestTakeResult.MissingPrerequisite, player.Quests.CanTake(second));

        player.Quests.Accept(QuestFixture.Build(id: 783, objectives: KillFiveWolves));

        Assert.Equal(QuestTakeResult.Ok, player.Quests.CanTake(second));
    }

    /// <summary>
    /// An exploration-or-event quest is not finished the moment it is taken.
    /// </summary>
    /// <remarks>
    /// It has no objective columns, so it looks objective-less; what completes it is a script. 443
    /// quests in the vendored dump would otherwise be handed in without being done.
    /// </remarks>
    [Fact]
    public void AnExplorationQuest_IsIncompleteOnAccept()
    {
        Player player = InventoryFixture.Player(level: 5);

        QuestTemplate scripted =
            QuestFixture.Build(specialFlags: QuestSpecialFlags.ExplorationOrEvent);

        Assert.NotNull(player.Quests.Accept(scripted));
        Assert.Equal(QuestStatus.Incomplete, player.Quests.StatusOf(scripted.Id));

        // And with no objectives to satisfy, it stays that way.
        Assert.False(player.Quests.IsSatisfied(scripted, player.Quests.Find(scripted.Id)!));
    }

    /// <summary>An objective-less quest with no such flag still completes on accept.</summary>
    [Fact]
    public void AnErrand_IsStillCompleteOnAccept()
    {
        Player player = InventoryFixture.Player(level: 5);

        Assert.NotNull(player.Quests.Accept(QuestFixture.Build()));
        Assert.Equal(QuestStatus.Complete, player.Quests.StatusOf(1));
    }

    /// <summary>
    /// The query response writes the money column signed.
    /// </summary>
    /// <remarks>
    /// The column is money paid when positive and money charged when negative. Clamping it to zero
    /// loses the "required" line on the 109 quests that charge.
    /// </remarks>
    [Fact]
    public void TheQueryResponse_WritesACostAsANegative()
    {
        PacketWriter writer = new();
        QuestPackets.WriteQueryResponse(writer, QuestFixture.Build(rewardOrRequiredMoney: -3000));

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        // Thirteen words in: id, method, level, minLevel, sortId, type, suggested, four reputation
        // objective words, the next quest in chain, and the xp id.
        reader.Skip(13 * 4);

        Assert.True(reader.TryReadUInt32(out uint money));
        Assert.Equal(-3000, unchecked((int)money));
    }

    /// <summary>A hidden-rewards quest sends zeroes, but still sends every slot.</summary>
    [Fact]
    public void TheQueryResponse_ZeroesHiddenRewardsWithoutShrinking()
    {
        QuestItem[] rewards = [new QuestItem(2589, 4)];

        PacketWriter shown = new();
        QuestPackets.WriteQueryResponse(
            shown, QuestFixture.Build(rewards: rewards, rewardOrRequiredMoney: 500));

        PacketWriter hidden = new();
        QuestPackets.WriteQueryResponse(
            hidden, QuestFixture.Build(
                rewards: rewards, rewardOrRequiredMoney: 500, flags: QuestFlags.HiddenRewards));

        // Same length: the client reads a fixed count of reward slots either way.
        Assert.Equal(shown.WrittenSpan.Length, hidden.WrittenSpan.Length);

        PacketReader reader = new(hidden.WrittenSpan.ToArray());
        reader.Skip(13 * 4);

        Assert.True(reader.TryReadUInt32(out uint money));
        Assert.Equal(0u, money);

        reader.Skip(12 * 4);        // the rest of the scalar block

        Assert.True(reader.TryReadUInt32(out uint firstRewardItem));
        Assert.Equal(0u, firstRewardItem);
    }

    /// <summary>The players-to-slay word carries the column rather than a hardcoded zero.</summary>
    [Fact]
    public void TheQueryResponse_CarriesTheRequiredPlayerKills()
    {
        PacketWriter writer = new();
        QuestPackets.WriteQueryResponse(writer, QuestFixture.Build(requiredPlayerKills: 20));

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        // Twenty-two words in: the thirteen above, then money, moneyMax, spell, spellCast, honour,
        // honour multiplier, source item, flags and the title id.
        reader.Skip(22 * 4);

        Assert.True(reader.TryReadUInt32(out uint slain));
        Assert.Equal(20u, slain);
    }

    /// <summary>A quest that wants player kills is not completable, since nothing counts them.</summary>
    [Fact]
    public void APlayerKillQuest_DoesNotCompleteOnAccept()
    {
        Player player = InventoryFixture.Player(level: 5);
        QuestTemplate pvp = QuestFixture.Build(requiredPlayerKills: 10);

        Assert.NotNull(player.Quests.Accept(pvp));
        Assert.Equal(QuestStatus.Incomplete, player.Quests.StatusOf(pvp.Id));
    }

    /// <summary>
    /// Swapping two log slots moves the fields and the server's own record together.
    /// </summary>
    /// <remarks>
    /// Drifting apart is invisible until the next kill, which then credits the wrong row.
    /// </remarks>
    [Fact]
    public void SwappingSlots_MovesTheFieldsAndTheRecord()
    {
        Player player = InventoryFixture.Player(level: 5);

        player.Quests.Accept(QuestFixture.Build(id: 11, objectives: KillFiveWolves));
        player.Quests.Accept(QuestFixture.Build(id: 22, objectives: KillFiveWolves));

        Assert.Equal(0, player.Quests.Find(11)!.Slot);
        Assert.Equal(1, player.Quests.Find(22)!.Slot);

        player.Quests.SwapSlots(0, 1);

        Assert.Equal(1, player.Quests.Find(11)!.Slot);
        Assert.Equal(0, player.Quests.Find(22)!.Slot);

        // And the client's view agrees: slot 0 now holds quest 22.
        Assert.Equal(22u, player.Fields.GetUInt32(UpdateFields.PLAYER_QUEST_LOG_1_1));
        Assert.Equal(
            11u,
            player.Fields.GetUInt32(
                UpdateFields.PLAYER_QUEST_LOG_1_1 + QuestConstants.LogSlotWidth));
    }

    /// <summary>An out-of-range or identical swap does nothing rather than corrupting a slot.</summary>
    [Fact]
    public void SwappingSlots_IgnoresNonsense()
    {
        Player player = InventoryFixture.Player(level: 5);

        player.Quests.Accept(QuestFixture.Build(id: 11, objectives: KillFiveWolves));

        player.Quests.SwapSlots(0, 0);
        player.Quests.SwapSlots(0, 200);

        Assert.Equal(0, player.Quests.Find(11)!.Slot);
        Assert.Equal(11u, player.Fields.GetUInt32(UpdateFields.PLAYER_QUEST_LOG_1_1));
    }

    /// <summary>
    /// Taking a quest marks all five of its slot words for sending, not just the ones that changed.
    /// </summary>
    /// <remarks>
    /// The original bug, and the reason quests were invisible in the client's log. Four of the five
    /// words are zero on a fresh slot, <c>SetUInt32</c> skips a write that changes nothing, and the
    /// client reads the slot as one unit — so only the quest id ever went out and the log had a
    /// quest with no state attached. Nothing about this is visible server-side, which is why it
    /// survived: the assertion has to be about the <i>dirty mask</i>, not the values.
    /// </remarks>
    [Fact]
    public void TakingAQuest_SendsAllFiveSlotWords()
    {
        Player player = InventoryFixture.Player(level: 5);

        player.Fields.ClearDirty();
        player.Quests.Accept(QuestFixture.Build(id: 5261));

        for (int word = 0; word < QuestConstants.LogSlotWidth; word++)
        {
            int field = UpdateFields.PLAYER_QUEST_LOG_1_1 + word;

            Assert.True(
                player.Fields.IsFieldDirty(field),
                $"slot word {word} (field {field}) was never marked for sending, so the client "
                + "gets a quest log entry with a hole in it");
        }
    }

    /// <summary>A quest complete on acceptance says so in the slot's state word, not only in its status.</summary>
    /// <remarks>
    /// The state word is what the client's log reads. A status of complete with a state word of
    /// zero is a quest the client keeps drawing as unfinished, at the NPC and in the log both.
    /// </remarks>
    [Fact]
    public void CompletingAQuest_WritesTheSlotStateWord()
    {
        Player player = InventoryFixture.Player(level: 5);

        player.Quests.Accept(QuestFixture.Build(id: 5261));

        Assert.Equal(
            QuestSlotState.Complete,
            player.Fields.GetUInt32(UpdateFields.PLAYER_QUEST_LOG_1_1 + 1));

        Assert.True(player.Fields.IsFieldDirty(UpdateFields.PLAYER_QUEST_LOG_1_1 + 1));
    }

    /// <summary>An unfinished quest leaves the state word alone.</summary>
    [Fact]
    public void AnUnfinishedQuest_LeavesTheStateWordClear()
    {
        Player player = InventoryFixture.Player(level: 5);

        player.Quests.Accept(QuestFixture.Build(id: 26, objectives: KillFiveWolves));

        Assert.Equal(
            QuestSlotState.None,
            player.Fields.GetUInt32(UpdateFields.PLAYER_QUEST_LOG_1_1 + 1));
    }

    /// <summary>
    /// The menu icons are the small set, and are not the questgiver-status numbers.
    /// </summary>
    /// <remarks>
    /// Both go out as a <c>uint32</c>, so nothing catches a swap. It matters twice over: the icon
    /// sorts a line into the available or active half of the window, and the client answers an
    /// active line with a different opcode than an available one.
    /// </remarks>
    [Fact]
    public void TheMenuIcons_AreNotTheQuestGiverStatuses()
    {
        Assert.Equal(0u, QuestMenuIcon.Silent);
        Assert.Equal(2u, QuestMenuIcon.Available);
        Assert.Equal(4u, QuestMenuIcon.Active);

        // The pairing that actually bit: "available" is 2 in one enum and 8 in the other.
        Assert.NotEqual(QuestGiverStatus.Available, QuestMenuIcon.Available);
        Assert.NotEqual(QuestGiverStatus.Reward, QuestMenuIcon.Active);
    }

    /// <summary>
    /// Auto-accept is read from <c>Flags</c>, not from the same-named <c>SpecialFlags</c> bit.
    /// </summary>
    /// <remarks>
    /// Both exist and only the <c>Flags</c> one is what <c>Quest::IsAutoAccept</c> reads. Getting
    /// it wrong is not cosmetic: the client reads the same flag and never sends an accept for such
    /// a quest, so if the server disagrees then nobody puts the quest in the log.
    /// </remarks>
    [Fact]
    public void AutoAccept_ComesFromTheFlagsColumn()
    {
        Assert.True(QuestFixture.Build(flags: QuestFlags.AutoAccept).IsAutoAccept);
        Assert.False(QuestFixture.Build(specialFlags: QuestSpecialFlags.AutoAccept).IsAutoAccept);

        Assert.Equal(0x00080000u, QuestFlags.AutoAccept);
        Assert.Equal(0x0004u, QuestSpecialFlags.AutoAccept);
    }

    /// <summary>A quest is auto-complete when its Flags say so, or when its Method is zero.</summary>
    [Fact]
    public void AutoComplete_AlsoCoversMethodZero()
    {
        Assert.True(QuestFixture.Build(flags: QuestFlags.AutoComplete).IsAutoComplete);
        Assert.True(QuestFixture.Build(method: 0).IsAutoComplete);
        Assert.False(QuestFixture.Build().IsAutoComplete);
    }

    /// <summary>
    /// A quest blocked only by level is still visible to the questgiver marker; one blocked by its
    /// chain is not.
    /// </summary>
    /// <remarks>
    /// The distinction the C++ draws between the two, and the reason the marker code cannot just
    /// call <c>CanTake</c>: too low draws a grey mark, a chain not started draws nothing at all.
    /// </remarks>
    [Fact]
    public void CanSeeStartQuest_IgnoresLevelButNotTheChain()
    {
        Player player = InventoryFixture.Player(level: 5);

        Assert.True(player.Quests.CanSeeStartQuest(QuestFixture.Build(minLevel: 40)));
        Assert.False(player.Quests.CanSeeStartQuest(QuestFixture.Build(prevQuestId: 783)));
    }

    /// <summary>Re-asking about completion notices items picked up since the last check.</summary>
    /// <remarks>
    /// The reward window can be asked for at any moment, and the bags may have changed since the
    /// objectives were last counted — which is why the C++ re-checks in the same handler.
    /// </remarks>
    [Fact]
    public void RefreshCompletion_NoticesItemsAcquiredMeanwhile()
    {
        Player player = InventoryFixture.Player(level: 5);
        ItemTemplate pelt = ItemFixture.Build(entry: 50432, stackable: 20);

        QuestTemplate quest = QuestFixture.Build(id: 33, requiredItems: [new QuestItem(50432, 3)]);
        QuestStore store = QuestFixture.Store(quest);

        player.Quests.Accept(quest);
        Assert.Equal(QuestStatus.Incomplete, player.Quests.StatusOf(33));

        InventoryFixture.Place(player, pelt, InventoryFixture.Backpack(), count: 3);

        player.Quests.RefreshCompletion(33, store);

        Assert.Equal(QuestStatus.Complete, player.Quests.StatusOf(33));
    }

    /// <summary>
    /// The multiple-status packet is a count and then fixed nine-byte entries.
    /// </summary>
    /// <remarks>
    /// The packet that repaints marks already on screen. It went unhandled for a while, which is
    /// what left an exclamation mark over a questgiver whose quest was already taken and no
    /// question mark over whoever took it back.
    /// </remarks>
    [Fact]
    public void TheMultipleStatus_WritesNineBytesPerEntry()
    {
        PacketWriter writer = new();

        QuestPackets.WriteStatusMultiple(
            writer,
            [(new ObjectGuid(0x1122334455667788), (byte)QuestGiverStatus.Reward),
             (new ObjectGuid(0x00000000000000AA), (byte)QuestGiverStatus.Available)]);

        Assert.Equal(4 + (2 * 9), writer.WrittenSpan.Length);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt32(out uint count));
        Assert.Equal(2u, count);

        Assert.True(reader.TryReadUInt64(out ulong first));
        Assert.Equal(0x1122334455667788ul, first);
        Assert.True(reader.TryReadUInt8(out byte firstStatus));
        Assert.Equal((byte)QuestGiverStatus.Reward, firstStatus);

        Assert.True(reader.TryReadUInt64(out ulong second));
        Assert.Equal(0xAAul, second);
        Assert.True(reader.TryReadUInt8(out byte secondStatus));
        Assert.Equal((byte)QuestGiverStatus.Available, secondStatus);
    }

    /// <summary>An empty multiple-status packet is a zero count, not an empty body.</summary>
    [Fact]
    public void TheMultipleStatus_WritesACountEvenWhenEmpty()
    {
        PacketWriter writer = new();
        QuestPackets.WriteStatusMultiple(writer, []);

        Assert.Equal(4, writer.WrittenSpan.Length);
    }

    /// <summary>
    /// The quest list carries the repeatable byte the client uses to pick its icon.
    /// </summary>
    /// <remarks>
    /// It was hardcoded to zero in both writers. A blue question mark and a yellow exclamation are
    /// the same packet with this one byte different.
    /// </remarks>
    [Fact]
    public void TheQuestList_CarriesTheRepeatableByte()
    {
        PacketWriter writer = new();

        QuestPackets.WriteQuestList(
            writer,
            new ObjectGuid(7),
            string.Empty,
            [new QuestMenuEntry(33, QuestMenuIcon.Available, 2, 0, Repeatable: true, "Wolves")]);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        reader.Skip(8);                                   // npc guid
        Assert.True(reader.TryReadCString(out _));        // greeting
        reader.Skip(4 + 4);                               // emote delay, emote
        Assert.True(reader.TryReadUInt8(out byte count));
        Assert.Equal(1, count);

        reader.Skip(4 + 4 + 4 + 4);                       // id, icon, level, flags

        Assert.True(reader.TryReadUInt8(out byte repeatable));
        Assert.Equal(1, repeatable);
    }
}
