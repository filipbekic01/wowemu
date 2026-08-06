using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// The client-facing state of a quest log slot. <c>QuestSlotStateMask</c>.
/// </summary>
/// <remarks>
/// A different thing from <see cref="QuestStatus"/>, which is the server's own record. This is the
/// second of a slot's five words, and it is what the client's log reads to decide whether to draw
/// a quest as finished.
/// </remarks>
public static class QuestSlotState
{
    public const uint None = 0x0000;
    public const uint Complete = 0x0001;
    public const uint Failed = 0x0002;
}

/// <summary>
/// One quest a player is carrying, or has carried.
/// </summary>
/// <remarks>
/// Port of <c>QuestStatusData</c>. The counters are kept here as well as in the player's update
/// fields, because the fields hold only sixteen bits per objective and nothing else can be read
/// back out of them — the log slot is a view for the client, not the server's own record.
/// </remarks>
public sealed class QuestProgress(uint questId)
{
    public uint QuestId { get; } = questId;

    public QuestStatus Status { get; set; } = QuestStatus.Incomplete;

    /// <summary>
    /// Which of the 25 log slots this occupies, or <see cref="NoSlot"/> once handed in.
    /// </summary>
    /// <remarks>
    /// The slot is the client's handle on the quest — every objective update names it. A rewarded
    /// quest has none, which is why this is not simply an index into a list.
    /// </remarks>
    public byte Slot { get; set; } = NoSlot;

    /// <summary>How many of each creature or gameobject objective have been dealt with.</summary>
    public ushort[] Killed { get; } = new ushort[QuestConstants.MaxObjectives];

    /// <summary>How many of each item objective the player is holding.</summary>
    public ushort[] Collected { get; } = new ushort[QuestConstants.MaxItemObjectives];

    /// <summary>Not in the log. 25 slots means the highest real one is 24.</summary>
    public const byte NoSlot = 255;

    public bool IsInLog => Slot != NoSlot;
}

/// <summary>
/// Every quest a player has taken, and what has happened to each.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Player</c>'s quest handling that M6 needs: taking a quest, counting
/// kills, noticing completion, handing in. It owns the log slots in
/// <c>PLAYER_QUEST_LOG_1_1</c> and nothing else writes them.
/// <para>
/// <b>Not implemented:</b> timed quests, exclusive groups, reputation and skill requirements,
/// spell and title rewards, daily resets, and the area-triggered and event objectives. Each is a
/// separate check on top of the same shape.
/// </para>
/// </remarks>
public sealed class QuestLog(Player owner)
{
    private readonly Dictionary<uint, QuestProgress> _quests = [];

    /// <summary>Every quest this player has taken, whatever became of it.</summary>
    public IEnumerable<QuestProgress> All => _quests.Values;

    /// <summary>How many quests are in the log — not counting ones already handed in.</summary>
    public int InLogCount
    {
        get
        {
            int count = 0;

            foreach (QuestProgress progress in _quests.Values)
            {
                if (progress.IsInLog)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public QuestProgress? Find(uint questId) => _quests.GetValueOrDefault(questId);

    /// <summary>How far through a quest the player is. <see cref="QuestStatus.None"/> if untouched.</summary>
    public QuestStatus StatusOf(uint questId) => Find(questId)?.Status ?? QuestStatus.None;

    /// <summary>Whether the quest has been handed in.</summary>
    public bool IsRewarded(uint questId) => StatusOf(questId) == QuestStatus.Rewarded;

    /// <summary>
    /// Whether a player may take a quest, and why not.
    /// </summary>
    /// <remarks>
    /// Port of the checks in <c>Player::CanTakeQuest</c> this phase can answer. Reputation, skill,
    /// timers and exclusive groups are absent and <b>pass</b>, so a quest gated on any of them is
    /// offered when it should not be.
    /// </remarks>
    /// <seealso cref="SatisfiesPreviousQuest"/>
    public QuestTakeResult CanTake(QuestTemplate quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        QuestStatus status = StatusOf(quest.Id);

        if (status is QuestStatus.Incomplete or QuestStatus.Complete)
        {
            return QuestTakeResult.AlreadyOn;
        }

        if (status == QuestStatus.Rewarded && !quest.IsRepeatable)
        {
            return QuestTakeResult.AlreadyDone;
        }

        if (!SatisfiesPreviousQuest(quest))
        {
            return QuestTakeResult.MissingPrerequisite;
        }

        if (owner.Level < quest.MinLevel)
        {
            return QuestTakeResult.TooLowLevel;
        }


        // A maximum of zero means no maximum, which is nearly every quest. Comparing against it
        // literally would make every one of them unavailable.
        if (quest.MaxLevel > 0 && owner.Level > quest.MaxLevel)
        {
            return QuestTakeResult.TooHighLevel;
        }

        if (!AllowsClass(quest.RequiredClasses, owner.Class))
        {
            return QuestTakeResult.WrongClass;
        }

        if (!AllowsRace(quest.RequiredRaces, owner.Race))
        {
            return QuestTakeResult.WrongRace;
        }

        if (InLogCount >= QuestConstants.MaxLogSize)
        {
            return QuestTakeResult.LogFull;
        }

        return QuestTakeResult.Ok;
    }

    /// <summary>
    /// Whether a quest would be on offer if the player were the right level.
    /// </summary>
    /// <remarks>
    /// Port of <c>Player::CanSeeStartQuest</c>, which is <c>CanTakeQuest</c> with the level check
    /// lifted out. The questgiver's marker needs the distinction: a quest the player is simply too
    /// low for shows a grey exclamation, and one they can never take shows nothing at all.
    /// </remarks>
    public bool CanSeeStartQuest(QuestTemplate quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        QuestTakeResult result = CanTake(quest);

        return result is QuestTakeResult.Ok or QuestTakeResult.TooLowLevel;
    }

    /// <summary>
    /// Puts a quest in the log.
    /// </summary>
    /// <remarks>
    /// Goes in incomplete and is then asked the ordinary completion question, exactly as
    /// <c>Player::AddQuestAndCheckCompletion</c> does. That is how a "go and speak to someone"
    /// errand — no objectives, nothing to do but hand it back — comes out complete, without
    /// treating every objective-less row the same way: a scripted or PvP quest has no objective
    /// columns either, and is nowhere near done.
    /// </remarks>
    public QuestProgress? Accept(QuestTemplate quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        if (CanTake(quest) != QuestTakeResult.Ok)
        {
            return null;
        }

        if (NextFreeSlot() is not { } slot)
        {
            return null;
        }

        // Re-taking a repeatable quest reuses its record and starts from zero rather than keeping
        // the counters from last time.
        QuestProgress progress = new(quest.Id) { Slot = slot };

        _quests[quest.Id] = progress;
        WriteSlot(progress);

        progress.Status = QuestStatus.Incomplete;

        // Counting what is already in the bags: a player who happens to be carrying the items is
        // done the instant they take the quest, and upstream checks the same way.
        RecountItems(quest, progress);

        // And a quest with nothing to do at all is finished on the spot. Going through the same
        // predicate as every later check, rather than testing for "no objectives" here, is what
        // keeps the two answers from drifting — a scripted or PvP quest has no objective columns
        // either, and is nowhere near done.
        RefreshCompletion(quest, progress);

        return progress;
    }

    /// <summary>
    /// Credits a kill against every quest that wants it.
    /// </summary>
    /// <remarks>
    /// Every quest, not the first: two quests can ask for the same creature, and crediting only one
    /// of them is the sort of bug a player notices and cannot explain.
    /// </remarks>
    /// <returns>What changed, so the caller can tell the client.</returns>
    public IReadOnlyList<QuestKillCredit> CreditKill(uint creatureEntry, QuestStore quests)
    {
        ArgumentNullException.ThrowIfNull(quests);

        List<QuestKillCredit> credited = [];

        foreach (QuestProgress progress in _quests.Values)
        {
            if (progress.Status != QuestStatus.Incomplete
                || !quests.TryGet(progress.QuestId, out QuestTemplate? quest) || quest is null)
            {
                continue;
            }

            for (int i = 0; i < quest.Objectives.Length; i++)
            {
                QuestObjective objective = quest.Objectives[i];

                if (!objective.IsUsed || objective.IsGameObject || objective.AbsoluteEntry != creatureEntry)
                {
                    continue;
                }

                if (progress.Killed[i] >= objective.Count)
                {
                    continue;
                }

                progress.Killed[i]++;
                WriteCounter(progress, i, progress.Killed[i]);

                credited.Add(new QuestKillCredit(
                    quest, i, objective.WireEntry, progress.Killed[i], objective.Count));
            }

            RefreshCompletion(quest, progress);
        }

        return credited;
    }

    /// <summary>
    /// Recounts what the player is carrying against a quest's item objectives.
    /// </summary>
    /// <remarks>
    /// Counted from the bags rather than incremented on pickup. An item can arrive by looting,
    /// trading, buying or being mailed, and an increment at each of those places is four chances to
    /// miss one — whereas a recount cannot drift.
    /// </remarks>
    public void RecountItems(QuestTemplate quest, QuestProgress progress)
    {
        ArgumentNullException.ThrowIfNull(quest);
        ArgumentNullException.ThrowIfNull(progress);

        for (int i = 0; i < quest.RequiredItems.Length; i++)
        {
            QuestItem required = quest.RequiredItems[i];

            if (!required.IsUsed)
            {
                continue;
            }

            uint held = Math.Min(owner.Inventory.CountOf(required.ItemId), required.Count);
            progress.Collected[i] = (ushort)held;
        }

        RefreshCompletion(quest, progress);
    }

    /// <summary>Recounts every incomplete quest's items. What picking something up triggers.</summary>
    public IReadOnlyList<uint> RecountAllItems(QuestStore quests)
    {
        ArgumentNullException.ThrowIfNull(quests);

        List<uint> nowComplete = [];

        foreach (QuestProgress progress in _quests.Values)
        {
            if (progress.Status != QuestStatus.Incomplete
                || !quests.TryGet(progress.QuestId, out QuestTemplate? quest) || quest is null)
            {
                continue;
            }

            RecountItems(quest, progress);

            if (progress.Status == QuestStatus.Complete)
            {
                nowComplete.Add(progress.QuestId);
            }
        }

        return nowComplete;
    }

    /// <summary>
    /// Whether every objective is met.
    /// </summary>
    /// <remarks>
    /// Port of the second half of <c>Player::CanCompleteQuest</c>. Two of its gates are here as
    /// flat refusals rather than as counters, because what would move them does not exist: an
    /// exploration-or-event quest is finished by a script, and a player-kill quest by PvP credit.
    /// Answering yes for either hands the player a quest they have not done; answering no leaves
    /// two quests in the game uncompletable and every scripted one waiting on a script. That is the
    /// side to be wrong on.
    /// </remarks>
    public bool IsSatisfied(QuestTemplate quest, QuestProgress progress)
    {
        ArgumentNullException.ThrowIfNull(quest);
        ArgumentNullException.ThrowIfNull(progress);

        if (quest.IsExplorationOrEvent || quest.RequiredPlayerKills > 0)
        {
            return false;
        }

        for (int i = 0; i < quest.Objectives.Length; i++)
        {
            if (quest.Objectives[i].IsUsed && progress.Killed[i] < quest.Objectives[i].Count)
            {
                return false;
            }
        }

        for (int i = 0; i < quest.RequiredItems.Length; i++)
        {
            if (quest.RequiredItems[i].IsUsed && progress.Collected[i] < quest.RequiredItems[i].Count)
            {
                return false;
            }
        }

        return owner.Money >= quest.RequiredMoney;
    }

    /// <summary>
    /// Hands a quest in and takes it out of the log.
    /// </summary>
    /// <remarks>
    /// The record stays, with a status of <see cref="QuestStatus.Rewarded"/>. It is what stops the
    /// quest being offered again, and dropping it would let a player repeat every quest in the game
    /// by talking to the same NPC twice.
    /// </remarks>
    public void Reward(QuestProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        ClearSlot(progress.Slot);

        progress.Slot = QuestProgress.NoSlot;
        progress.Status = QuestStatus.Rewarded;
    }

    /// <summary>Abandons a quest, forgetting it entirely.</summary>
    public bool Abandon(uint questId)
    {
        if (Find(questId) is not { } progress || !progress.IsInLog)
        {
            return false;
        }

        ClearSlot(progress.Slot);
        _quests.Remove(questId);

        return true;
    }

    /// <summary>Puts a saved quest back, without re-running any of the rules.</summary>
    /// <remarks>
    /// For loading, and nothing else — the same reasoning as the inventory's restore. What was
    /// legal when the quest was taken is what it stays.
    /// </remarks>
    public void Restore(QuestProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        _quests[progress.QuestId] = progress;

        if (!progress.IsInLog)
        {
            return;
        }

        WriteSlot(progress);

        // The slot state is derived rather than stored: it is a view of the status, and two
        // records of the same fact are two records that can disagree.
        if (progress.Status == QuestStatus.Complete)
        {
            WriteSlotState(progress, QuestSlotState.Complete);
        }
        else if (progress.Status == QuestStatus.Failed)
        {
            WriteSlotState(progress, QuestSlotState.Failed);
        }
    }

    // ------------------------------------------------------------------ the client's view

    /// <summary>
    /// The lowest log slot nothing is using.
    /// </summary>
    /// <remarks>
    /// Read back out of the update fields rather than tracked separately, so the two cannot
    /// disagree — the fields are what the client draws from.
    /// </remarks>
    private byte? NextFreeSlot()
    {
        for (byte slot = 0; slot < QuestConstants.MaxLogSize; slot++)
        {
            if (owner.Fields.GetUInt32(SlotField(slot)) == 0)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes a whole log slot — all five words, whatever they hold.
    /// </summary>
    /// <remarks>
    /// <b>Every word is marked for sending even when it is already zero.</b> The five are one unit
    /// as far as the client is concerned, and <c>SetUInt32</c> ignores a write that changes
    /// nothing — so a fresh slot would otherwise put only the quest id on the wire and leave the
    /// client with a quest whose state, counters and timer it has never been told.
    /// </remarks>
    private void WriteSlot(QuestProgress progress)
    {
        int field = SlotField(progress.Slot);

        owner.Fields.SetUInt32(field, progress.QuestId);
        owner.Fields.SetUInt32(field + 1, QuestSlotState.None);
        owner.Fields.SetUInt32(field + 2, 0);       // counters, low
        owner.Fields.SetUInt32(field + 3, 0);       // counters, high
        owner.Fields.SetUInt32(field + 4, 0);       // timer

        for (int i = 0; i < QuestConstants.LogSlotWidth; i++)
        {
            owner.Fields.MarkDirty(field + i);
        }

        for (int i = 0; i < progress.Killed.Length; i++)
        {
            if (progress.Killed[i] > 0)
            {
                WriteCounter(progress, i, progress.Killed[i]);
            }
        }
    }

    /// <summary>
    /// Sets the client-facing state of a slot.
    /// </summary>
    /// <remarks>
    /// Port of <c>SetQuestSlotState</c>, which upstream calls from <c>CompleteQuest</c>. A quest
    /// the server treats as complete but whose slot state is still zero is drawn by the client as
    /// still in progress.
    /// </remarks>
    private void WriteSlotState(QuestProgress progress, uint state)
    {
        if (!progress.IsInLog)
        {
            return;
        }

        owner.Fields.SetUInt32(SlotField(progress.Slot) + 1, state);
    }

    private void ClearSlot(byte slot)
    {
        if (slot >= QuestConstants.MaxLogSize)
        {
            return;
        }

        int field = SlotField(slot);

        for (int i = 0; i < QuestConstants.LogSlotWidth; i++)
        {
            owner.Fields.SetUInt32(field + i, 0);
        }
    }

    /// <summary>
    /// Writes one objective's counter into the packed pair of words.
    /// </summary>
    /// <remarks>
    /// <b>Four counters share two update fields, sixteen bits each.</b> The pair is one 64-bit
    /// value as far as the client is concerned, so objective 2 lives in the top half of the first
    /// word and objective 3 in the bottom half of the second. Writing a counter per field would put
    /// the first objective's count where the client reads all four.
    /// </remarks>
    private void WriteCounter(QuestProgress progress, int objective, ushort count)
    {
        if (!progress.IsInLog || objective >= QuestConstants.MaxObjectives)
        {
            return;
        }

        int field = SlotField(progress.Slot) + 2;

        ulong packed = owner.Fields.GetUInt32(field)
            | ((ulong)owner.Fields.GetUInt32(field + 1) << 32);

        int shift = objective * 16;

        packed &= ~(0xFFFFUL << shift);
        packed |= (ulong)count << shift;

        owner.Fields.SetUInt32(field, (uint)packed);
        owner.Fields.SetUInt32(field + 1, (uint)(packed >> 32));
    }

    private static int SlotField(byte slot) =>
        UpdateFields.PLAYER_QUEST_LOG_1_1 + (slot * QuestConstants.LogSlotWidth);

    private void RefreshCompletion(QuestTemplate quest, QuestProgress progress)
    {
        if (progress.Status == QuestStatus.Incomplete && IsSatisfied(quest, progress))
        {
            MarkComplete(progress);
        }
    }

    /// <summary>Marks a quest finished, in the server's record and in the client's slot.</summary>
    /// <remarks>
    /// One place, because the two have to move together: a status of complete with a slot state of
    /// zero is a quest the client keeps drawing as unfinished.
    /// </remarks>
    private void MarkComplete(QuestProgress progress)
    {
        progress.Status = QuestStatus.Complete;

        WriteSlotState(progress, QuestSlotState.Complete);
    }

    /// <summary>
    /// Whether the quest ahead of this one in its chain has been done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of <c>Player::SatisfyQuestPreviousQuest</c>, reduced to the single-prerequisite case.
    /// <b>The sign of <c>PrevQuestId</c> chooses the test</b>: a positive id must have been handed
    /// in, a negative one need only have been started. The C++ reads a whole list of them, built
    /// from <c>PrevQuestId</c> plus every quest whose <c>ExclusiveGroup</c> ties it in; only the one
    /// column is loaded here, so a quest gated on a group is admitted on its first prerequisite.
    /// </para>
    /// <para>
    /// Without this check a questgiver offers its entire chain at once. That is how a fresh level-1
    /// character can take the second quest in a line before the first, which then reads as the
    /// wrong quest appearing in the log.
    /// </para>
    /// </remarks>
    private bool SatisfiesPreviousQuest(QuestTemplate quest)
    {
        if (quest.PrevQuestId == 0)
        {
            return true;
        }

        uint previous = (uint)Math.Abs(quest.PrevQuestId);

        return quest.PrevQuestId > 0
            ? IsRewarded(previous)
            : StatusOf(previous) != QuestStatus.None;
    }

    /// <summary>
    /// Whether a class mask admits a class. A mask of zero means everyone.
    /// </summary>
    /// <remarks>
    /// Classes are one-based, so a warrior is class 1 and bit 0. Shifting by the class rather than
    /// by class minus one locks every warrior out of every warrior quest.
    /// </remarks>
    private static bool AllowsClass(ushort mask, byte playerClass) =>
        mask == 0 || (mask & (1 << (playerClass - 1))) != 0;

    /// <inheritdoc cref="AllowsClass"/>
    private static bool AllowsRace(ushort mask, byte playerRace) =>
        mask == 0 || (mask & (1 << (playerRace - 1))) != 0;
}

/// <summary>Why a quest may not be taken.</summary>
public enum QuestTakeResult
{
    Ok,
    AlreadyOn,
    AlreadyDone,

    /// <summary>The quest before this one in its chain has not been done.</summary>
    MissingPrerequisite,
    TooLowLevel,
    TooHighLevel,
    WrongClass,
    WrongRace,
    LogFull,
}

/// <summary>One objective that moved because of a kill.</summary>
/// <param name="WireEntry">
/// The entry as the client wants it — a gameobject's id carries the high bit. See
/// <see cref="QuestObjective.WireEntry"/>.
/// </param>
public readonly record struct QuestKillCredit(
    QuestTemplate Quest,
    int ObjectiveIndex,
    uint WireEntry,
    ushort Current,
    ushort Required);

/// <summary>
/// What a quest is worth.
/// </summary>
/// <remarks>
/// Port of <c>Quest::XPValue</c> and <c>Quest::GetRewOrReqMoney</c>.
/// </remarks>
public static class QuestReward
{
    /// <summary>
    /// The experience a quest pays a player of a given level.
    /// </summary>
    /// <remarks>
    /// Three things here are easy to get wrong and invisible if you do:
    /// <list type="bullet">
    /// <item><b>The DBC is indexed by the quest's level</b>, and <c>RewardXPId</c> is a column
    /// within that row, not a row id.</item>
    /// <item>A quest level of <c>-1</c> means "the player's level", which is what makes a scaling
    /// quest pay sensibly at any level.</item>
    /// <item>The result is <b>rounded to a band</b> — to 5 below 100, then 10, then 25, then 50.
    /// Skipping it pays amounts the real client never shows.</item>
    /// </list>
    /// </remarks>
    public static uint Experience(QuestTemplate quest, byte playerLevel, DbcStore<QuestXpEntry> table)
    {
        ArgumentNullException.ThrowIfNull(quest);
        ArgumentNullException.ThrowIfNull(table);

        int questLevel = quest.Level == -1 ? playerLevel : quest.Level;

        if (questLevel < 0 || !table.TryGet((uint)questLevel, out QuestXpEntry? row) || row is null)
        {
            return 0;
        }

        // How much easier the quest is than the player. Clamped, so an over-levelled player still
        // gets a tenth rather than nothing, and an under-levelled one gets no bonus.
        int difficulty = (2 * (questLevel - playerLevel)) + 20;
        difficulty = Math.Clamp(difficulty, 1, 10);

        uint xp = (uint)(difficulty * row.For(quest.RewardXpDifficulty) / 10);

        return Round(xp);
    }

    /// <summary>Rounds to the band the client shows. <c>Quest::XPValue</c>'s tail.</summary>
    private static uint Round(uint xp) => xp switch
    {
        <= 100 => 5 * ((xp + 2) / 5),
        <= 500 => 10 * ((xp + 5) / 10),
        <= 1000 => 25 * ((xp + 12) / 25),
        _ => 50 * ((xp + 25) / 50),
    };
}
