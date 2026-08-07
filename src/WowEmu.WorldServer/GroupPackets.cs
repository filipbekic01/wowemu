using WowEmu.Core;
using WowEmu.Game;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>
/// The party frame's packets.
/// </summary>
/// <remarks>
/// Port of <c>Group::SendUpdateToPlayer</c> and the party-result replies in <c>GroupHandler</c>.
/// </remarks>
public static class GroupPackets
{
    /// <summary>
    /// Writes <c>SMSG_GROUP_LIST</c> — the whole party frame, as one member sees it.
    /// </summary>
    /// <remarks>
    /// <b>Built per recipient, not once per group.</b> The header carries the recipient's own
    /// sub-group and flags, and the member list <i>excludes them</i> — a shared packet puts one
    /// member's flags on everybody and shows each player a duplicate of themselves.
    /// <para>
    /// The trailing loot block is written only when there is more than one member. The client reads
    /// to the end of the packet, so writing it for a one-member group is not merely wasteful — it
    /// desynchronises the parse.
    /// </para>
    /// </remarks>
    public static void WriteGroupList(PacketWriter writer, Group group, ObjectGuid recipient)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(group);

        GroupMember? self = group.Find(recipient);

        writer.WriteUInt8(group.Type);
        writer.WriteUInt8(self?.SubGroup ?? 0);
        writer.WriteUInt8(self?.Flags ?? GroupMemberFlags.None);
        writer.WriteUInt8(self?.Roles ?? 0);

        writer.WriteUInt64(group.Guid.Value);

        // Incremented every time, and the client discards a list that arrives with a counter it has
        // already seen. A constant makes every update after the first vanish.
        writer.WriteUInt32(group.NextCounter());

        writer.WriteUInt32((uint)(group.Members.Count - 1));

        foreach (GroupMember member in group.Members)
        {
            if (member.Guid == recipient)
            {
                continue;
            }

            writer.WriteCString(member.Name);
            writer.WriteUInt64(member.Guid.Value);

            // Online for anyone the registry still holds. Offline members are a thing this server
            // does not have yet — a character that logs out leaves the group.
            writer.WriteUInt8(GroupMemberStatus.Online);
            writer.WriteUInt8(member.SubGroup);
            writer.WriteUInt8(member.Flags);
            writer.WriteUInt8(member.Roles);
        }

        writer.WriteUInt64(group.Leader.Value);

        if (group.Members.Count <= 1)
        {
            return;
        }

        writer.WriteUInt8(group.LootMethod);

        // The looter guid slot carries the master looter under master loot and nothing otherwise —
        // it is not the round-robin turn, which the client never sees.
        writer.WriteUInt64(
            group.LootMethod == LootMethod.MasterLoot ? group.MasterLooter.Value : 0);

        writer.WriteUInt8(group.LootThreshold);

        // Dungeon and raid difficulty, then the 3.3 dynamic-difficulty flag. Instances are not
        // built, so these are the normal settings rather than anything read back.
        writer.WriteUInt8(0);
        writer.WriteUInt8(0);
        writer.WriteUInt8(0);
    }

    /// <summary>
    /// Writes <c>SMSG_GROUP_INVITE</c> — the box asking whether to join.
    /// </summary>
    /// <param name="accepted">
    /// False for "you were invited but you are already in a group", which the client shows
    /// differently. Upstream sends the same opcode for both.
    /// </param>
    public static void WriteInvite(PacketWriter writer, string inviterName, bool accepted = true)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt8((byte)(accepted ? 1 : 0));
        writer.WriteCString(inviterName);
        writer.WriteUInt32(0);
        writer.WriteUInt8(0);
        writer.WriteUInt32(0);
    }

    /// <summary>Writes <c>SMSG_LOOT_START_ROLL</c> — the roll window opening.</summary>
    /// <param name="voteMask">
    /// Which buttons this recipient gets. Per player, not per roll: under need-before-greed the
    /// need button is hidden from anyone who cannot use the item.
    /// </param>
    public static void WriteStartRoll(
        PacketWriter writer, GroupLootRoll roll, uint mapId, byte voteMask)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(roll);

        writer.WriteUInt64(roll.Holder.Value);
        writer.WriteUInt32(mapId);
        writer.WriteUInt32(roll.Slot);
        writer.WriteUInt32(roll.ItemId);
        writer.WriteUInt32(roll.SuffixFactor);
        writer.WriteUInt32(unchecked((uint)roll.RandomPropertyId));
        writer.WriteUInt32(roll.Count);
        writer.WriteUInt32(roll.RemainingMs);
        writer.WriteUInt8(voteMask);
    }

    /// <summary>
    /// Writes <c>SMSG_LOOT_ROLL</c> — one player's choice, or their roll.
    /// </summary>
    /// <param name="rolled">
    /// Zero while the roll is still open, which the client draws as "chose Need" rather than as a
    /// roll of nothing. The real number arrives when the roll is decided.
    /// </param>
    public static void WriteRoll(
        PacketWriter writer,
        GroupLootRoll roll,
        ObjectGuid player,
        byte rolled,
        LootVote vote,
        bool autoPassed = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(roll);

        writer.WriteUInt64(roll.Holder.Value);
        writer.WriteUInt32(roll.Slot);
        writer.WriteUInt64(player.Value);
        writer.WriteUInt32(roll.ItemId);
        writer.WriteUInt32(roll.SuffixFactor);
        writer.WriteUInt32(unchecked((uint)roll.RandomPropertyId));
        writer.WriteUInt8(rolled);
        writer.WriteUInt8((byte)vote);
        writer.WriteUInt8((byte)(autoPassed ? 1 : 0));
    }

    /// <summary>Writes <c>SMSG_LOOT_ROLL_WON</c> — who took it and with what.</summary>
    public static void WriteRollWon(
        PacketWriter writer, GroupLootRoll roll, ObjectGuid winner, byte rolled, LootVote vote)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(roll);

        writer.WriteUInt64(roll.Holder.Value);
        writer.WriteUInt32(roll.Slot);
        writer.WriteUInt32(roll.ItemId);
        writer.WriteUInt32(roll.SuffixFactor);
        writer.WriteUInt32(unchecked((uint)roll.RandomPropertyId));
        writer.WriteUInt64(winner.Value);
        writer.WriteUInt8(rolled);
        writer.WriteUInt8((byte)vote);
    }

    /// <summary>
    /// Writes <c>SMSG_LOOT_ALL_PASSED</c> — nobody wanted it.
    /// </summary>
    /// <remarks>
    /// <b>The random property and the suffix are written in the opposite order here</b> to the two
    /// packets above. Not a mistake in the port — upstream writes propId then suffix in this one
    /// alone, and matching the others swaps two fields the client reads for the item's name.
    /// </remarks>
    public static void WriteAllPassed(PacketWriter writer, GroupLootRoll roll)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(roll);

        writer.WriteUInt64(roll.Holder.Value);
        writer.WriteUInt32(roll.Slot);
        writer.WriteUInt32(roll.ItemId);
        writer.WriteUInt32(unchecked((uint)roll.RandomPropertyId));
        writer.WriteUInt32(roll.SuffixFactor);
    }

    /// <summary>
    /// Writes <c>SMSG_PARTY_COMMAND_RESULT</c> — the answer to any party operation.
    /// </summary>
    /// <remarks>
    /// <b>Every refusal has to be answered.</b> The client leaves its own UI mid-operation until
    /// something comes back, so going quiet on a refusal is worse than refusing loudly.
    /// </remarks>
    public static void WritePartyResult(
        PacketWriter writer, uint operation, string member, PartyResult result)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(operation);
        writer.WriteCString(member);
        writer.WriteUInt32((uint)result);

        // "LFG boot cooldown" only; zero for everything else.
        writer.WriteUInt32(0);
    }
}
