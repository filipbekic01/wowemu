using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>One line of an NPC's quest menu, as the client draws it.</summary>
/// <param name="Icon">
/// <see cref="QuestMenuIcon"/>, <b>not <see cref="QuestGiverStatus"/></b>. It sorts the line into
/// the available or the active half of the window.
/// </param>
/// <param name="Repeatable">
/// Whether the line gets a blue question mark instead of a yellow exclamation. A daily is
/// repeatable and still gets the exclamation, so it is not simply the flag.
/// </param>
public readonly record struct QuestMenuEntry(
    uint QuestId, uint Icon, int Level, uint Flags, bool Repeatable, string Title);

/// <summary>
/// Writes the quest packets.
/// </summary>
/// <remarks>
/// Port of <c>PlayerMenu</c>'s quest half. Here rather than in <c>WowEmu.Protocol</c> for the same
/// reason as <see cref="ItemQueryResponse"/>: these packets are mostly a <c>quest_template</c> row
/// written straight out, and a wire-shaped copy of a 70-column record would be noise.
/// <para>
/// Several of them end in runs of constants whose meaning is not known — <c>0x04 0x08 0x10</c> at
/// the end of the request-items packet, for instance. They are reproduced because the client reads
/// them, not because anyone has worked out what they are for.
/// </para>
/// </remarks>
public static class QuestPackets
{
    /// <summary>How many emote pairs the details packet always carries. <c>QUEST_EMOTE_COUNT</c>.</summary>
    public const int EmoteCount = 4;

    /// <summary>Writes <c>SMSG_QUESTGIVER_STATUS</c> — the mark over an NPC's head.</summary>
    public static void WriteStatus(PacketWriter writer, ObjectGuid npc, uint status)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(npc.Value);

        // A byte, though the enum behind it is a uint32. Writing four would shift whatever the
        // client reads next.
        writer.WriteUInt8((byte)status);
    }

    /// <summary>
    /// Writes <c>SMSG_QUESTGIVER_QUEST_LIST</c> — the menu an NPC with several quests shows.
    /// </summary>
    /// <param name="greeting">
    /// What the NPC says before the list. Empty is normal and blizzlike for many creatures.
    /// </param>
    public static void WriteQuestList(
        PacketWriter writer, ObjectGuid npc, string greeting, IReadOnlyList<QuestMenuEntry> quests)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(quests);

        writer.WriteUInt64(npc.Value);
        writer.WriteCString(greeting);

        writer.WriteUInt32(0);      // emote delay
        writer.WriteUInt32(0);      // emote

        writer.WriteUInt8((byte)quests.Count);

        foreach (QuestMenuEntry entry in quests)
        {
            writer.WriteUInt32(entry.QuestId);
            writer.WriteUInt32(entry.Icon);
            writer.WriteUInt32((uint)entry.Level);
            writer.WriteUInt32(entry.Flags);

            writer.WriteUInt8(entry.Repeatable ? (byte)1 : (byte)0);
            writer.WriteCString(entry.Title);
        }
    }

    /// <summary>
    /// Writes <c>SMSG_QUESTGIVER_QUEST_DETAILS</c> — the "accept this?" window.
    /// </summary>
    /// <param name="displayIdFor">Resolves an item's display id, for the reward icons.</param>
    public static void WriteDetails(
        PacketWriter writer,
        ObjectGuid npc,
        QuestTemplate quest,
        uint rewardMoney,
        uint rewardXp,
        Func<uint, uint> displayIdFor)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(quest);
        ArgumentNullException.ThrowIfNull(displayIdFor);

        writer.WriteUInt64(npc.Value);

        // The "divider" — who shared the quest. Empty unless it came from another player.
        writer.WriteUInt64(0);

        writer.WriteUInt32(quest.Id);
        writer.WriteCString(quest.LogTitle);
        writer.WriteCString(quest.QuestDescription);
        writer.WriteCString(quest.LogDescription);

        writer.WriteUInt8(1);       // activate accept
        writer.WriteUInt32(quest.Flags);
        writer.WriteUInt32(quest.SuggestedPlayers);
        writer.WriteUInt8(0);       // is finished, echoed back in the accept packet

        WriteRewardBlock(writer, quest, rewardMoney, rewardXp, displayIdFor);

        writer.WriteUInt32(0);      // honor, ten times the real figure
        writer.WriteSingle(0f);     // honor multiplier
        writer.WriteUInt32(quest.RewardSpell);
        writer.WriteUInt32((uint)quest.RewardSpellCast);
        writer.WriteUInt32(0);      // title
        writer.WriteUInt32(0);      // bonus talents
        writer.WriteUInt32(0);      // arena points
        writer.WriteUInt32(0);      // unknown

        WriteReputationBlock(writer);

        // The emote count is written and then exactly that many pairs follow — unlike the offer
        // packet, where the count is of the non-zero ones only.
        writer.WriteUInt32(EmoteCount);

        for (int i = 0; i < EmoteCount; i++)
        {
            writer.WriteUInt32(0);  // emote
            writer.WriteUInt32(0);  // delay
        }
    }

    /// <summary>
    /// Writes <c>SMSG_QUESTGIVER_REQUEST_ITEMS</c> — "have you got them yet?".
    /// </summary>
    /// <remarks>
    /// Sent when a quest asks for items. A quest with none skips straight to the offer, because
    /// this window would have nothing to show and the player would be stuck at an empty dialog.
    /// </remarks>
    public static void WriteRequestItems(
        PacketWriter writer,
        ObjectGuid npc,
        QuestTemplate quest,
        bool canComplete,
        Func<uint, uint> displayIdFor)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(quest);
        ArgumentNullException.ThrowIfNull(displayIdFor);

        writer.WriteUInt64(npc.Value);
        writer.WriteUInt32(quest.Id);
        writer.WriteCString(quest.LogTitle);
        writer.WriteCString(quest.RequestItemsText);

        writer.WriteUInt32(0);      // unknown
        writer.WriteUInt32(0);      // the complete or incomplete emote
        writer.WriteUInt32(0);      // close the window on cancel

        writer.WriteUInt32(quest.Flags);
        writer.WriteUInt32(quest.SuggestedPlayers);
        writer.WriteUInt32(quest.RequiredMoney);

        writer.WriteUInt32((uint)quest.RequiredItemCount);

        foreach (QuestItem required in quest.RequiredItems)
        {
            if (!required.IsUsed)
            {
                continue;
            }

            writer.WriteUInt32(required.ItemId);
            writer.WriteUInt32(required.Count);
            writer.WriteUInt32(displayIdFor(required.ItemId));
        }

        // Three is what enables the Continue button. Zero greys it out, which is how an incomplete
        // quest is refused without an error message.
        writer.WriteUInt32(canComplete ? 3u : 0u);

        // Reproduced from upstream. Nobody has established what they mean; the client reads them.
        writer.WriteUInt32(0x04);
        writer.WriteUInt32(0x08);
        writer.WriteUInt32(0x10);
    }

    /// <summary>Writes <c>SMSG_QUESTGIVER_OFFER_REWARD</c> — "here is what you get".</summary>
    public static void WriteOfferReward(
        PacketWriter writer,
        ObjectGuid npc,
        QuestTemplate quest,
        uint rewardMoney,
        uint rewardXp,
        Func<uint, uint> displayIdFor)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(quest);
        ArgumentNullException.ThrowIfNull(displayIdFor);

        writer.WriteUInt64(npc.Value);
        writer.WriteUInt32(quest.Id);
        writer.WriteCString(quest.LogTitle);
        writer.WriteCString(quest.OfferRewardText);

        writer.WriteUInt8(1);       // auto finish
        writer.WriteUInt32(quest.Flags);
        writer.WriteUInt32(quest.SuggestedPlayers);

        // Here the count is of the emotes actually written, unlike the details packet where it is
        // always four. The two are genuinely different shapes.
        writer.WriteUInt32(0);

        WriteRewardBlock(writer, quest, rewardMoney, rewardXp, displayIdFor);

        writer.WriteUInt32(0);      // honor
        writer.WriteSingle(0f);     // honor multiplier
        writer.WriteUInt32(0x08);   // unused by the client
        writer.WriteUInt32(quest.RewardSpell);
        writer.WriteUInt32((uint)quest.RewardSpellCast);
        writer.WriteUInt32(0);      // title
        writer.WriteUInt32(0);      // bonus talents
        writer.WriteUInt32(0);      // arena points
        writer.WriteUInt32(0);

        WriteReputationBlock(writer);
    }

    /// <summary>
    /// Writes <c>SMSG_QUEST_QUERY_RESPONSE</c> — everything the client needs to draw a quest.
    /// </summary>
    /// <remarks>
    /// <b>Without this the quest log is empty.</b> The details window carries enough to decide
    /// whether to accept, but the log entry needs the structured objectives — which creature, how
    /// many, which item — and the client will not draw a row for a quest it has no data for. It
    /// asks for anything missing from its own <c>QuestCache.wdb</c>, which after a cache wipe is
    /// every quest in the game.
    /// <para>
    /// Unlike the details and offer packets, the reward arrays here are written in <b>full</b> —
    /// all four rewards and all six choices, gaps included — because there is no count in front of
    /// them. The client reads a fixed number.
    /// </para>
    /// </remarks>
    public static void WriteQueryResponse(PacketWriter writer, QuestTemplate quest)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(quest);

        // A quest that keeps its rewards secret until hand-in: the money and both item arrays go
        // out as zeroes here and are filled in by the offer packet instead.
        bool hideRewards = (quest.Flags & QuestFlags.HiddenRewards) != 0;

        writer.WriteUInt32(quest.Id);
        writer.WriteUInt32(quest.Method);

        // Signed on the way in and written as a word: -1 means "the player's level", and the
        // client understands it.
        writer.WriteUInt32(unchecked((uint)quest.Level));
        writer.WriteUInt32(quest.MinLevel);

        // Which tab of the log it files under. Negative is a sort id rather than a zone.
        writer.WriteUInt32(unchecked((uint)quest.SortId));

        writer.WriteUInt32(quest.Type);
        writer.WriteUInt32(quest.SuggestedPlayers);

        // Two reputation objectives, neither implemented.
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);

        writer.WriteUInt32(quest.NextQuestIdChain);
        writer.WriteUInt32(quest.RewardXpDifficulty);

        // Signed and raw: a negative value is money the quest *costs*, and the client draws it as a
        // "required" line. Clamping it to zero here loses that line on the 109 quests that have one.
        writer.WriteUInt32(hideRewards ? 0u : unchecked((uint)quest.RewardOrRequiredMoney));
        writer.WriteUInt32(quest.RewardMoneyMaxLevel);
        writer.WriteUInt32(quest.RewardSpell);
        writer.WriteUInt32(unchecked((uint)quest.RewardSpellCast));

        writer.WriteUInt32(0);          // honour
        writer.WriteSingle(0f);         // honour multiplier
        writer.WriteUInt32(quest.SourceItemId);

        // Only the low sixteen bits. The rest are server-side flags the client has no use for.
        writer.WriteUInt32(quest.Flags & 0xFFFF);

        writer.WriteUInt32(0);          // title
        writer.WriteUInt32(quest.RequiredPlayerKills);
        writer.WriteUInt32(0);          // bonus talents
        writer.WriteUInt32(0);          // arena points
        writer.WriteUInt32(0);          // review reputation mask

        // Both blocks are still written when the rewards are hidden — as zeroes. The client reads a
        // fixed count either way, so leaving them out shifts everything after by eighty bytes.
        for (int i = 0; i < QuestConstants.MaxRewards; i++)
        {
            writer.WriteUInt32(hideRewards ? 0 : quest.Rewards[i].ItemId);
            writer.WriteUInt32(hideRewards ? 0 : quest.Rewards[i].Count);
        }

        for (int i = 0; i < QuestConstants.MaxRewardChoices; i++)
        {
            writer.WriteUInt32(hideRewards ? 0 : quest.RewardChoices[i].ItemId);
            writer.WriteUInt32(hideRewards ? 0 : quest.RewardChoices[i].Count);
        }

        WriteReputationBlock(writer);

        // Point of interest — the map marker. Not read from the table yet.
        writer.WriteUInt32(0);
        writer.WriteSingle(0f);
        writer.WriteSingle(0f);
        writer.WriteUInt32(0);

        // The order is title, objectives, details — not the order the columns are in, and not the
        // order the details packet uses.
        writer.WriteCString(quest.LogTitle);
        writer.WriteCString(quest.LogDescription);
        writer.WriteCString(quest.QuestDescription);
        writer.WriteCString(quest.AreaDescription);
        writer.WriteCString(quest.CompletedText);

        for (int i = 0; i < QuestConstants.MaxObjectives; i++)
        {
            writer.WriteUInt32(quest.Objectives[i].WireEntry);
            writer.WriteUInt32(quest.Objectives[i].Count);
            writer.WriteUInt32(quest.SourceItems[i]);
            writer.WriteUInt32(0);      // source count
        }

        for (int i = 0; i < QuestConstants.MaxItemObjectives; i++)
        {
            writer.WriteUInt32(quest.RequiredItems[i].ItemId);
            writer.WriteUInt32(quest.RequiredItems[i].Count);
        }

        for (int i = 0; i < QuestConstants.MaxObjectives; i++)
        {
            writer.WriteCString(quest.ObjectiveText[i]);
        }
    }

    /// <summary>Writes <c>SMSG_QUESTGIVER_QUEST_COMPLETE</c> — the reward actually paid.</summary>
    public static void WriteComplete(PacketWriter writer, uint questId, uint experience, uint money)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(questId);
        writer.WriteUInt32(experience);
        writer.WriteUInt32(money);
        writer.WriteUInt32(0);      // honor, ten times the real figure
        writer.WriteUInt32(0);      // bonus talents
        writer.WriteUInt32(0);      // arena points
    }

    /// <summary>Writes <c>SMSG_QUESTUPDATE_ADD_KILL</c> — one objective moved.</summary>
    /// <param name="wireEntry">
    /// The creature entry, or a gameobject's id with the high bit set. See
    /// <see cref="QuestObjective.WireEntry"/>.
    /// </param>
    public static void WriteAddKill(
        PacketWriter writer, uint questId, uint wireEntry, uint current, uint required, ObjectGuid victim)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(questId);
        writer.WriteUInt32(wireEntry);
        writer.WriteUInt32(current);
        writer.WriteUInt32(required);
        writer.WriteUInt64(victim.Value);
    }

    /// <summary>Writes <c>SMSG_QUESTUPDATE_COMPLETE</c> — every objective is met.</summary>
    public static void WriteQuestComplete(PacketWriter writer, uint questId)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(questId);
    }

    /// <summary>
    /// The reward block shared by the details and offer packets.
    /// </summary>
    /// <remarks>
    /// <b>The count is of the used slots and the loop skips the unused ones</b>, so a quest with a
    /// gap in its reward columns writes fewer entries than the array is long. Writing all four
    /// regardless puts zeroes where the client expects the money.
    /// </remarks>
    private static void WriteRewardBlock(
        PacketWriter writer,
        QuestTemplate quest,
        uint rewardMoney,
        uint rewardXp,
        Func<uint, uint> displayIdFor)
    {
        writer.WriteUInt32((uint)quest.RewardChoiceCount);

        foreach (QuestItem choice in quest.RewardChoices)
        {
            if (!choice.IsUsed)
            {
                continue;
            }

            writer.WriteUInt32(choice.ItemId);
            writer.WriteUInt32(choice.Count);
            writer.WriteUInt32(displayIdFor(choice.ItemId));
        }

        writer.WriteUInt32((uint)quest.RewardCount);

        foreach (QuestItem reward in quest.Rewards)
        {
            if (!reward.IsUsed)
            {
                continue;
            }

            writer.WriteUInt32(reward.ItemId);
            writer.WriteUInt32(reward.Count);
            writer.WriteUInt32(displayIdFor(reward.ItemId));
        }

        writer.WriteUInt32(rewardMoney);
        writer.WriteUInt32(rewardXp);
    }

    /// <summary>Five faction ids, five values and five overrides — all zero until reputation exists.</summary>
    private static void WriteReputationBlock(PacketWriter writer)
    {
        for (int block = 0; block < 3; block++)
        {
            for (int i = 0; i < QuestConstants.MaxReputations; i++)
            {
                writer.WriteUInt32(0);
            }
        }
    }
}
