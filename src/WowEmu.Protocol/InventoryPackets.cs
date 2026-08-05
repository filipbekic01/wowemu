using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>
/// Writes <c>SMSG_INVENTORY_CHANGE_FAILURE</c>.
/// </summary>
/// <remarks>
/// Port of <c>Player::SendEquipError</c>. The client turns the code into its own sentence, so
/// sending a plausible-but-wrong one is worse than sending nothing: the player is told the bag is
/// full when the item was too high a level.
/// </remarks>
public static class InventoryChangeFailure
{
    /// <summary>Nothing went wrong. <c>EQUIP_ERR_OK</c>.</summary>
    public const byte Ok = 0;

    /// <summary>The two codes that carry a required level after the body.</summary>
    public const byte CantEquipLevel = 1;
    public const byte PurchaseLevelTooLow = 87;

    /// <summary>The three that carry an item limit category instead.</summary>
    public const byte LimitCategoryCountExceeded = 84;
    public const byte LimitCategorySocketedExceeded = 85;
    public const byte LimitCategoryEquippedExceeded = 89;

    /// <summary>
    /// Writes one refusal.
    /// </summary>
    /// <remarks>
    /// <b>A code of zero is the whole packet.</b> Everything after the first byte is written only
    /// when something actually failed, so a success written with a full body leaves the client
    /// reading a guid it was not expecting.
    /// <para>
    /// The two guids are full, not packed. Almost everything else in the protocol packs them, which
    /// makes this easy to get wrong in the direction that still parses.
    /// </para>
    /// </remarks>
    /// <param name="requiredLevel">
    /// Only reaches the wire for the two level codes. It is the <i>item's</i> required level, not
    /// the player's.
    /// </param>
    public static void Write(
        PacketWriter writer,
        byte result,
        ObjectGuid item = default,
        ObjectGuid otherItem = default,
        uint requiredLevel = 0,
        uint limitCategory = 0)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt8(result);

        if (result == Ok)
        {
            return;
        }

        writer.WriteUInt64(item.Value);
        writer.WriteUInt64(otherItem.Value);

        // Bag type subclass, used only by the two codes this server never sends.
        writer.WriteUInt8(0);

        switch (result)
        {
            case CantEquipLevel:
            case PurchaseLevelTooLow:
                writer.WriteUInt32(requiredLevel);
                break;

            case LimitCategoryCountExceeded:
            case LimitCategorySocketedExceeded:
            case LimitCategoryEquippedExceeded:
                writer.WriteUInt32(limitCategory);
                break;

            default:
                break;
        }
    }
}

/// <summary>What an item push should look like to the client.</summary>
/// <param name="Slot">
/// The slot inside <paramref name="Bag"/>, or <c>-1</c> when the count merged into an existing
/// stack. The client uses it to decide which bag slot to flash.
/// </param>
/// <param name="Count">How many arrived — not how many the stack now holds.</param>
/// <param name="TotalOfEntry">How many of the entry the player now has altogether.</param>
public readonly record struct ItemPushResult(
    ObjectGuid Player,
    bool FromNpc,
    bool Created,
    bool ShowInChat,
    byte Bag,
    int Slot,
    uint Entry,
    uint Count,
    uint TotalOfEntry);

/// <summary>
/// Writes <c>SMSG_ITEM_PUSH_RESULT</c> — the "you received" toast and its chat line.
/// </summary>
/// <remarks>
/// Port of <c>Player::SendNewItem</c>. Without it an item appears in the bag with no animation and
/// no chat line, which reads as a bug even though the item is really there.
/// </remarks>
public static class ItemPushResultPacket
{
    /// <summary>The slot value that means "it went onto a stack that already existed".</summary>
    public const int MergedIntoStack = -1;

    public static void Write(PacketWriter writer, in ItemPushResult push)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(push.Player.Value);

        // Three booleans, each a full word. The client reads them as uint32 and a byte apiece
        // would shift the bag slot into the middle of the first.
        writer.WriteUInt32(push.FromNpc ? 1u : 0u);
        writer.WriteUInt32(push.Created ? 1u : 0u);
        writer.WriteUInt32(push.ShowInChat ? 1u : 0u);

        // The bag is a byte and the slot a word, which is not a mistake — the bag is one of 255
        // containers and the slot is signed so it can be -1.
        writer.WriteUInt8(push.Bag);
        writer.WriteUInt32(unchecked((uint)push.Slot));

        writer.WriteUInt32(push.Entry);
        writer.WriteUInt32(0);          // suffix factor, for randomly-suffixed items
        writer.WriteUInt32(0);          // random property id
        writer.WriteUInt32(push.Count);
        writer.WriteUInt32(push.TotalOfEntry);
    }
}
