using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>How many of each repeating block a quest carries.</summary>
public static class QuestConstants
{
    /// <summary>Creature or gameobject objectives. <c>QUEST_OBJECTIVES_COUNT</c>.</summary>
    public const int MaxObjectives = 4;

    /// <summary>Item objectives. <c>QUEST_ITEM_OBJECTIVES_COUNT</c> — six, not four.</summary>
    public const int MaxItemObjectives = 6;

    /// <summary>Items handed over unconditionally. <c>QUEST_REWARDS_COUNT</c>.</summary>
    public const int MaxRewards = 4;

    /// <summary>Items the player picks one of. <c>QUEST_REWARD_CHOICES_COUNT</c>.</summary>
    public const int MaxRewardChoices = 6;

    /// <summary>Reputation rewards. <c>QUEST_REPUTATIONS_COUNT</c>.</summary>
    public const int MaxReputations = 5;

    /// <summary>How many quests a player may hold. <c>MAX_QUEST_LOG_SIZE</c>.</summary>
    public const int MaxLogSize = 25;

    /// <summary>How many update-field slots one log entry occupies. <c>MAX_QUEST_OFFSET</c>.</summary>
    public const int LogSlotWidth = 5;
}

/// <summary>How far through a quest a player is. <c>QuestStatus</c>.</summary>
/// <remarks>
/// <b>The numbers are not contiguous.</b> 2 and 4 were removed and the rest kept their values, so
/// renumbering them to close the gaps changes what the client is told.
/// </remarks>
public enum QuestStatus : byte
{
    None = 0,
    Complete = 1,
    Incomplete = 3,
    Failed = 5,

    /// <summary>Handed in. Server-side only; never stored as a status in the database.</summary>
    Rewarded = 6,
}

/// <summary>
/// What the exclamation mark over a questgiver's head looks like. <c>QuestGiverStatus</c>.
/// </summary>
/// <remarks>
/// Not the same as <see cref="QuestStatus"/>, and easy to confuse: this describes the <i>NPC</i>
/// from one player's point of view, and the other describes the quest.
/// </remarks>
public static class QuestGiverStatus
{
    public const uint None = 0;
    public const uint Unavailable = 1;
    public const uint LowLevelAvailable = 2;
    public const uint Incomplete = 5;
    public const uint Available = 8;

    /// <summary>Complete, without a dot on the minimap.</summary>
    public const uint RewardNoDot = 9;

    /// <summary>Complete, with the dot.</summary>
    public const uint Reward = 10;
}

/// <summary>Flags on a quest. <c>QuestFlags</c>. Only the ones this phase reads.</summary>
public static class QuestFlags
{
    /// <summary>Staying in the log after completion — a daily, or a grind.</summary>
    public const uint Repeatable = 0x0001;

    /// <summary>Hands itself in the moment the objectives are met.</summary>
    public const uint AutoComplete = 0x0004;

    /// <summary>The rewards are not shown in the details window.</summary>
    public const uint HiddenRewards = 0x0100;

    public const uint Daily = 0x1000;
    public const uint Weekly = 0x8000;
    public const uint Monthly = 0x400000;
}

/// <summary>One creature or gameobject a quest asks the player to deal with.</summary>
/// <param name="Entry">
/// A creature entry when positive and a <b>gameobject entry when negative</b>. There is no separate
/// column saying which; the sign is the whole distinction.
/// </param>
public readonly record struct QuestObjective(int Entry, ushort Count)
{
    public bool IsUsed => Entry != 0 && Count > 0;

    public bool IsGameObject => Entry < 0;

    /// <summary>The entry without its sign.</summary>
    public uint AbsoluteEntry => (uint)Math.Abs(Entry);

    /// <summary>
    /// How the client wants a gameobject entry written: the id with the high bit set.
    /// </summary>
    /// <remarks>
    /// Not the negative. Sending <c>-id</c> instead leaves the client looking for a creature it
    /// cannot find, and the objective line never updates.
    /// </remarks>
    public uint WireEntry => IsGameObject ? AbsoluteEntry | 0x80000000 : (uint)Entry;
}

/// <summary>One item a quest asks for, or hands over.</summary>
public readonly record struct QuestItem(uint ItemId, uint Count)
{
    public bool IsUsed => ItemId != 0 && Count > 0;
}

/// <summary>
/// One row of <c>quest_template</c>.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Quest</c> that M6 needs. Wide, like <see cref="ItemTemplate"/> and for
/// the same reason: most of it is written back out in the details, request-items and offer-reward
/// packets, which are how the client draws the quest at all.
/// <para>
/// Absent: reputation requirements and rewards, spell rewards, titles, talents, arena points,
/// timers, exclusive groups and the point-of-interest columns. Each is read as zero and would need
/// a system that does not exist.
/// </para>
/// </remarks>
public sealed record QuestTemplate(
    uint Id,
    byte Method,

    /// <summary>
    /// The quest's own level, or <c>-1</c> meaning "the player's".
    /// </summary>
    /// <remarks>
    /// Signed, and the <c>-1</c> is load-bearing: it is what makes a scaling quest pay the right
    /// experience at any level. Read unsigned it becomes 65,535 and the reward is zero.
    /// </remarks>
    short Level,
    byte MinLevel,
    byte MaxLevel,
    short SortId,
    ushort Type,
    byte SuggestedPlayers,
    ushort RequiredClasses,
    ushort RequiredRaces,
    int PrevQuestId,
    int NextQuestId,
    uint NextQuestIdChain,
    byte RewardXpDifficulty,

    /// <summary>
    /// Money rewarded, or <b>required when negative</b>.
    /// </summary>
    /// <remarks>
    /// One column for two opposite things. Read unsigned, a quest that costs gold pays out four
    /// billion copper instead.
    /// </remarks>
    int RewardOrRequiredMoney,
    uint RewardMoneyMaxLevel,
    uint RewardSpell,
    int RewardSpellCast,
    uint SourceItemId,
    byte SourceItemCount,
    uint Flags,
    byte SpecialFlags,
    QuestItem[] Rewards,
    QuestItem[] RewardChoices,
    QuestObjective[] Objectives,
    QuestItem[] RequiredItems,

    /// <summary>
    /// Items that drop only while the quest is held, one per creature objective.
    /// </summary>
    /// <remarks>
    /// <c>ItemDrop</c> in the C++. The client draws no line for them; they exist so the query
    /// response can tell it which drops to expect.
    /// </remarks>
    uint[] SourceItems,
    string LogTitle,
    string LogDescription,
    string QuestDescription,

    /// <summary>
    /// The line shown at the top of the quest's objectives panel.
    /// </summary>
    /// <remarks>
    /// <b>The column is <c>EndText</c> in the vendored dump and <c>AreaDescription</c> in the
    /// current C++.</b> The same divergence as <c>creature_template_model</c> — read the C++ for
    /// behaviour and the dump for column names.
    /// </remarks>
    string AreaDescription,

    /// <summary>Shown once every objective is met. <c>QuestCompletionLog</c>.</summary>
    string CompletedText,
    string OfferRewardText,
    string RequestItemsText,
    string[] ObjectiveText)
{
    /// <summary>Whether it stays available after being handed in.</summary>
    public bool IsRepeatable => (Flags & QuestFlags.Repeatable) != 0;

    /// <summary>Whether it resets on a schedule rather than being freely repeatable.</summary>
    public bool IsDailyOrWeeklyOrMonthly =>
        (Flags & (QuestFlags.Daily | QuestFlags.Weekly | QuestFlags.Monthly)) != 0;

    /// <summary>Money the player is <i>paid</i>. Zero when the quest costs money instead.</summary>
    public uint RewardMoney => RewardOrRequiredMoney > 0 ? (uint)RewardOrRequiredMoney : 0;

    /// <summary>Money the player must <i>hand over</i>. Zero when the quest pays instead.</summary>
    public uint RequiredMoney => RewardOrRequiredMoney < 0 ? (uint)(-RewardOrRequiredMoney) : 0;

    /// <summary>How many creature or gameobject objectives are used.</summary>
    public int ObjectiveCount => Count(Objectives);

    /// <summary>How many item objectives are used.</summary>
    public int RequiredItemCount => Count(RequiredItems);

    /// <summary>How many unconditional rewards there are.</summary>
    public int RewardCount => Count(Rewards);

    /// <summary>How many rewards the player picks between.</summary>
    public int RewardChoiceCount => Count(RewardChoices);

    /// <summary>Whether the quest asks for anything at all beyond talking to someone.</summary>
    public bool HasObjectives => ObjectiveCount > 0 || RequiredItemCount > 0;

    private static int Count(QuestObjective[] objectives)
    {
        int used = 0;

        foreach (QuestObjective objective in objectives)
        {
            if (objective.IsUsed)
            {
                used++;
            }
        }

        return used;
    }

    private static int Count(QuestItem[] items)
    {
        int used = 0;

        foreach (QuestItem item in items)
        {
            if (item.IsUsed)
            {
                used++;
            }
        }

        return used;
    }
}

/// <summary>Which quests a creature offers and which it takes back.</summary>
/// <remarks>
/// Two tables, not one. A quest is very often started by one NPC and finished by another, and
/// treating either as both puts a hand-in exclamation mark over the wrong person.
/// </remarks>
public sealed class QuestRelationStore(string tableName)
{
    private readonly Dictionary<uint, List<uint>> _byCreature = [];

    public string TableName { get; } = tableName;

    /// <summary>How many creatures have at least one quest here.</summary>
    public int Count => _byCreature.Count;

    /// <summary>How many rows were read.</summary>
    public int RowCount { get; private set; }

    /// <summary>The quests one creature is involved in, in the order the table lists them.</summary>
    public IReadOnlyList<uint> For(uint creatureEntry) =>
        _byCreature.TryGetValue(creatureEntry, out List<uint>? quests) ? quests : [];

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _byCreature.Clear();
        RowCount = 0;

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT id, quest FROM {TableName}";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint creature = reader.GetUInt32(0);

            if (!_byCreature.TryGetValue(creature, out List<uint>? quests))
            {
                quests = [];
                _byCreature[creature] = quests;
            }

            quests.Add(reader.GetUInt32(1));
            RowCount++;
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{RowCount} {TableName} rows across {Count} creatures");
}

/// <summary><c>quest_template</c>, loaded once at startup.</summary>
public sealed class QuestStore
{
    private readonly Dictionary<uint, QuestTemplate> _quests = [];

    public int Count => _quests.Count;

    public IEnumerable<QuestTemplate> All => _quests.Values;

    public bool TryGet(uint questId, out QuestTemplate? quest) => _quests.TryGetValue(questId, out quest);

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _quests.Clear();

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = SelectStatement;

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            QuestTemplate quest = Read(reader);
            _quests[quest.Id] = quest;
        }
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Count} quests");

    /// <remarks>
    /// <c>IFNULL</c> on every text column: they are nullable <c>text</c>, and a quest with no
    /// objective text — which is most of them — would otherwise throw on read.
    /// </remarks>
    private const string SelectStatement = """
        SELECT ID, Method, QuestLevel, MinLevel, MaxLevel, QuestSortID, QuestType,
               SuggestedGroupNum, RequiredClasses, RequiredRaces,
               PrevQuestId, NextQuestId, NextQuestIdChain,
               RewardXPId, RewardOrRequiredMoney, RewardMoneyMaxLevel,
               RewardSpell, RewardSpellCast,
               SourceItemId, SourceItemCount, Flags, SpecialFlags,
               RewardItem1, RewardItem2, RewardItem3, RewardItem4,
               RewardAmount1, RewardAmount2, RewardAmount3, RewardAmount4,
               RewardChoiceItemID1, RewardChoiceItemID2, RewardChoiceItemID3,
               RewardChoiceItemID4, RewardChoiceItemID5, RewardChoiceItemID6,
               RewardChoiceItemQuantity1, RewardChoiceItemQuantity2, RewardChoiceItemQuantity3,
               RewardChoiceItemQuantity4, RewardChoiceItemQuantity5, RewardChoiceItemQuantity6,
               RequiredNpcOrGo1, RequiredNpcOrGo2, RequiredNpcOrGo3, RequiredNpcOrGo4,
               RequiredNpcOrGoCount1, RequiredNpcOrGoCount2, RequiredNpcOrGoCount3, RequiredNpcOrGoCount4,
               RequiredItemId1, RequiredItemId2, RequiredItemId3,
               RequiredItemId4, RequiredItemId5, RequiredItemId6,
               RequiredItemCount1, RequiredItemCount2, RequiredItemCount3,
               RequiredItemCount4, RequiredItemCount5, RequiredItemCount6,
               RequiredSourceItemId1, RequiredSourceItemId2,
               RequiredSourceItemId3, RequiredSourceItemId4,
               IFNULL(LogTitle, ''), IFNULL(LogDescription, ''), IFNULL(QuestDescription, ''),
               IFNULL(EndText, ''), IFNULL(QuestCompletionLog, ''),
               IFNULL(OfferRewardText, ''), IFNULL(RequestItemsText, ''),
               IFNULL(ObjectiveText1, ''), IFNULL(ObjectiveText2, ''),
               IFNULL(ObjectiveText3, ''), IFNULL(ObjectiveText4, '')
        FROM quest_template
        """;

    private static QuestTemplate Read(MySqlDataReader reader)
    {
        QuestItem[] rewards = new QuestItem[QuestConstants.MaxRewards];

        for (int i = 0; i < rewards.Length; i++)
        {
            rewards[i] = new QuestItem(reader.GetUInt32(22 + i), reader.GetUInt16(26 + i));
        }

        QuestItem[] choices = new QuestItem[QuestConstants.MaxRewardChoices];

        for (int i = 0; i < choices.Length; i++)
        {
            choices[i] = new QuestItem(reader.GetUInt32(30 + i), reader.GetUInt16(36 + i));
        }

        QuestObjective[] objectives = new QuestObjective[QuestConstants.MaxObjectives];

        for (int i = 0; i < objectives.Length; i++)
        {
            objectives[i] = new QuestObjective(reader.GetInt32(42 + i), reader.GetUInt16(46 + i));
        }

        QuestItem[] required = new QuestItem[QuestConstants.MaxItemObjectives];

        for (int i = 0; i < required.Length; i++)
        {
            required[i] = new QuestItem(reader.GetUInt32(50 + i), reader.GetUInt16(56 + i));
        }

        uint[] sourceItems = new uint[QuestConstants.MaxObjectives];

        for (int i = 0; i < sourceItems.Length; i++)
        {
            sourceItems[i] = reader.GetUInt32(62 + i);
        }

        string[] objectiveText = new string[QuestConstants.MaxObjectives];

        for (int i = 0; i < objectiveText.Length; i++)
        {
            objectiveText[i] = reader.GetString(73 + i);
        }

        return new QuestTemplate(
            Id: reader.GetUInt32(0),
            Method: reader.GetByte(1),
            Level: reader.GetInt16(2),
            MinLevel: reader.GetByte(3),
            MaxLevel: reader.GetByte(4),
            SortId: reader.GetInt16(5),
            Type: reader.GetUInt16(6),
            SuggestedPlayers: reader.GetByte(7),
            RequiredClasses: reader.GetUInt16(8),
            RequiredRaces: reader.GetUInt16(9),
            PrevQuestId: reader.GetInt32(10),
            NextQuestId: reader.GetInt32(11),
            NextQuestIdChain: reader.GetUInt32(12),
            RewardXpDifficulty: reader.GetByte(13),
            RewardOrRequiredMoney: reader.GetInt32(14),
            RewardMoneyMaxLevel: reader.GetUInt32(15),
            RewardSpell: reader.GetUInt32(16),
            RewardSpellCast: reader.GetInt32(17),
            SourceItemId: reader.GetUInt32(18),
            SourceItemCount: reader.GetByte(19),
            Flags: reader.GetUInt32(20),
            SpecialFlags: reader.GetByte(21),
            Rewards: rewards,
            RewardChoices: choices,
            Objectives: objectives,
            RequiredItems: required,
            SourceItems: sourceItems,
            LogTitle: reader.GetString(66),
            LogDescription: reader.GetString(67),
            QuestDescription: reader.GetString(68),
            AreaDescription: reader.GetString(69),
            CompletedText: reader.GetString(70),
            OfferRewardText: reader.GetString(71),
            RequestItemsText: reader.GetString(72),
            ObjectiveText: objectiveText);
    }
}
