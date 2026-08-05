using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>One clickable line, as the client draws it.</summary>
/// <param name="Index">
/// What the client sends back when the line is clicked. It is the table's own option id, and must
/// be echoed rather than renumbered — a menu with a gap would otherwise select the wrong line.
/// </param>
public readonly record struct GossipLine(
    uint Index,
    byte Icon,
    bool Coded,
    uint BoxMoney,
    string Text,
    string BoxText);

/// <summary>One line of a vendor's stock, as the client draws it.</summary>
/// <param name="Slot">
/// <b>One-based.</b> The client counts vendor slots from 1 and subtracts one before sending a
/// purchase back, so a zero-based slot here buys the wrong item — or nothing, for slot 0.
/// </param>
/// <param name="InStock">How many are left, or <c>-1</c> for an unlimited supply.</param>
public readonly record struct VendorLine(
    uint Slot,
    uint ItemId,
    uint DisplayId,
    int InStock,
    uint Price,
    uint MaxDurability,
    uint BuyCount,
    uint ExtendedCost);

/// <summary>Why a purchase was refused. <c>BuyResult</c>.</summary>
public enum BuyResult : byte
{
    CantFindItem = 0,
    ItemAlreadySold = 1,
    NotEnoughMoney = 2,
    SellerDoesNotLikeYou = 4,
    DistanceTooFar = 5,
    ItemSoldOut = 7,
    CantCarryMore = 8,
    RankRequire = 11,
    ReputationRequire = 12,
}

/// <summary>Why a sale was refused. <c>SellResult</c>.</summary>
public enum SellResult : byte
{
    CantFindItem = 1,
    CantSellItem = 2,
    CantFindVendor = 3,
    YouDontOwnThatItem = 4,
    Unknown = 5,
    OnlyEmptyBag = 6,
}

/// <summary>
/// Writes the gossip and vendor packets.
/// </summary>
/// <remarks>
/// Port of <c>PlayerMenu::SendGossipMenu</c> and <c>WorldSession::SendListInventory</c>.
/// </remarks>
public static class GossipPackets
{
    /// <summary>
    /// Writes <c>SMSG_GOSSIP_MESSAGE</c> — what an NPC says, and what can be clicked.
    /// </summary>
    /// <remarks>
    /// The quest list rides in the same packet as the gossip lines, which is why a questgiver that
    /// also sells things shows both in one window. Sending them separately produces two windows,
    /// one of which the client immediately closes.
    /// </remarks>
    public static void WriteGossipMenu(
        PacketWriter writer,
        ObjectGuid npc,
        uint menuId,
        uint textId,
        IReadOnlyList<GossipLine> lines,
        IReadOnlyList<QuestMenuEntry> quests)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(quests);

        writer.WriteUInt64(npc.Value);
        writer.WriteUInt32(menuId);
        writer.WriteUInt32(textId);

        writer.WriteUInt32((uint)lines.Count);

        foreach (GossipLine line in lines)
        {
            writer.WriteUInt32(line.Index);
            writer.WriteUInt8(line.Icon);
            writer.WriteUInt8(line.Coded ? (byte)1 : (byte)0);
            writer.WriteUInt32(line.BoxMoney);
            writer.WriteCString(line.Text);
            writer.WriteCString(line.BoxText);
        }

        writer.WriteUInt32((uint)quests.Count);

        foreach (QuestMenuEntry quest in quests)
        {
            writer.WriteUInt32(quest.QuestId);
            writer.WriteUInt32(quest.Icon);
            writer.WriteUInt32((uint)quest.Level);
            writer.WriteUInt32(quest.Flags);
            writer.WriteUInt8(0);       // repeatable, which changes the icon
            writer.WriteCString(quest.Title);
        }
    }

    /// <summary>
    /// Writes <c>SMSG_NPC_TEXT_UPDATE</c> — the text behind a gossip window.
    /// </summary>
    /// <remarks>
    /// <b>Eight blocks, always.</b> The client reads a fixed eight probability-and-text groups and
    /// picks between them; writing only the one that has anything in it leaves it reading the rest
    /// of the packet as text. Each block is a probability, two strings, a language and six emote
    /// pairs.
    /// </remarks>
    public static void WriteNpcText(PacketWriter writer, uint textId, string text)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(textId);

        for (int block = 0; block < 8; block++)
        {
            // Only the first block has any probability, so the client always picks it.
            writer.WriteSingle(block == 0 ? 1.0f : 0f);

            // Male and female variants. The same string for both: the tables carry a separate
            // female text and nothing reads it yet.
            writer.WriteCString(block == 0 ? text : string.Empty);
            writer.WriteCString(block == 0 ? text : string.Empty);

            writer.WriteUInt32(0);      // language

            for (int emote = 0; emote < 3; emote++)
            {
                writer.WriteUInt32(0);  // delay
                writer.WriteUInt32(0);  // emote
            }
        }
    }

    /// <summary>
    /// Writes <c>SMSG_LIST_INVENTORY</c> — a vendor's stock.
    /// </summary>
    /// <remarks>
    /// An empty list is <b>a count of zero followed by an error byte</b>, which is a different
    /// shape from a list that happens to have no entries. Writing the ordinary form with a zero
    /// count leaves the client waiting for a byte that never comes.
    /// </remarks>
    public static void WriteVendorList(PacketWriter writer, ObjectGuid vendor, IReadOnlyList<VendorLine> items)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(items);

        writer.WriteUInt64(vendor.Value);

        if (items.Count == 0)
        {
            writer.WriteUInt8(0);
            writer.WriteUInt8(0);       // "this vendor has no inventory"

            return;
        }

        writer.WriteUInt8((byte)items.Count);

        foreach (VendorLine item in items)
        {
            writer.WriteUInt32(item.Slot);
            writer.WriteUInt32(item.ItemId);
            writer.WriteUInt32(item.DisplayId);
            writer.WriteUInt32(unchecked((uint)item.InStock));
            writer.WriteUInt32(item.Price);
            writer.WriteUInt32(item.MaxDurability);
            writer.WriteUInt32(item.BuyCount);
            writer.WriteUInt32(item.ExtendedCost);
        }
    }

    /// <summary>Writes <c>SMSG_BUY_ITEM</c> — the purchase went through.</summary>
    /// <remarks>
    /// The slot is echoed one-based, exactly as it was sent out in the list.
    /// </remarks>
    public static void WriteBought(PacketWriter writer, ObjectGuid vendor, uint slot, int inStock, uint count)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(vendor.Value);
        writer.WriteUInt32(slot);
        writer.WriteUInt32(unchecked((uint)inStock));
        writer.WriteUInt32(count);
    }

    /// <summary>
    /// Writes <c>SMSG_BUY_FAILED</c>.
    /// </summary>
    /// <remarks>
    /// <b>The parameter word appears only when it is non-zero.</b> The packet's length is how the
    /// client tells whether one is there, so writing a zero shifts the reason byte into it.
    /// </remarks>
    public static void WriteBuyFailed(
        PacketWriter writer, ObjectGuid vendor, uint itemId, BuyResult reason, uint parameter = 0)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(vendor.Value);
        writer.WriteUInt32(itemId);

        if (parameter > 0)
        {
            writer.WriteUInt32(parameter);
        }

        writer.WriteUInt8((byte)reason);
    }

    /// <summary>Writes <c>SMSG_SELL_ITEM</c>, which is only ever a refusal.</summary>
    /// <remarks>
    /// A successful sale sends nothing: the item leaving the bag and the money arriving are both
    /// field updates, and the client works the rest out for itself.
    /// </remarks>
    public static void WriteSellFailed(
        PacketWriter writer, ObjectGuid vendor, ObjectGuid item, SellResult reason)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(vendor.Value);
        writer.WriteUInt64(item.Value);
        writer.WriteUInt8((byte)reason);
    }
}
