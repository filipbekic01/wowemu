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
        string title = "A Task",
        short level = 5,
        byte minLevel = 0,
        byte maxLevel = 0,
        ushort requiredClasses = 0,
        ushort requiredRaces = 0,
        byte rewardXpDifficulty = 1,
        int rewardOrRequiredMoney = 0,
        uint flags = 0,
        QuestObjective[]? objectives = null,
        QuestItem[]? requiredItems = null,
        QuestItem[]? rewards = null,
        QuestItem[]? rewardChoices = null) =>
        new(
            Id: id,
            Method: 2,
            Level: level,
            MinLevel: minLevel,
            MaxLevel: maxLevel,
            SortId: 0,
            Type: 0,
            SuggestedPlayers: 0,
            RequiredClasses: requiredClasses,
            RequiredRaces: requiredRaces,
            PrevQuestId: 0,
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
            SpecialFlags: 0,
            Rewards: Pad(rewards, QuestConstants.MaxRewards),
            RewardChoices: Pad(rewardChoices, QuestConstants.MaxRewardChoices),
            Objectives: PadObjectives(objectives),
            RequiredItems: Pad(requiredItems, QuestConstants.MaxItemObjectives),
            LogTitle: title,
            LogDescription: "Do the thing.",
            QuestDescription: "Somebody should do the thing.",
            OfferRewardText: "You did the thing.",
            RequestItemsText: "Have you done the thing?",
            ObjectiveText: ["", "", "", ""]);

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
        QuestTemplate quest = QuestFixture.Build(flags: QuestFlags.Repeatable);

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
