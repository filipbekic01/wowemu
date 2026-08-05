using WowEmu.Core;

namespace WowEmu.Protocol;

/// <summary>One row of a loot window, as the client draws it.</summary>
/// <param name="Slot">
/// The index the client sends back when the player clicks it. It must stay stable while the window
/// is open — renumbering after a take makes the next click land on the wrong item.
/// </param>
/// <param name="SlotType">A <c>LootSlotType</c>: whether this client may take it, or only watch.</param>
public readonly record struct LootSlot(
    byte Slot,
    uint ItemId,
    uint Count,
    uint DisplayId,
    byte SlotType);

/// <summary>
/// Writes <c>SMSG_LOOT_RESPONSE</c> and the small packets that keep the window in step.
/// </summary>
/// <remarks>
/// Port of <c>operator&lt;&lt;(ByteBuffer&amp;, LootView const&amp;)</c> and the loot half of
/// <c>Player</c>.
/// </remarks>
public static class LootResponse
{
    /// <summary>A loot type of zero, which is what a refusal carries. <c>LOOT_NONE</c>.</summary>
    public const byte NoLoot = 0;

    /// <summary>
    /// Writes a loot window.
    /// </summary>
    /// <remarks>
    /// <b>The count is of the slots actually written, not of the pile.</b> A taken item is left out
    /// but keeps its number, so the count and the highest slot number disagree — writing the pile's
    /// size instead leaves the client reading past the end.
    /// <para>
    /// The guid is full rather than packed, unlike almost everything else that names an object.
    /// </para>
    /// </remarks>
    public static void Write(PacketWriter writer, ObjectGuid target, byte lootType, uint gold, IReadOnlyList<LootSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(slots);

        writer.WriteUInt64(target.Value);
        writer.WriteUInt8(lootType);
        writer.WriteUInt32(gold);
        writer.WriteUInt8((byte)slots.Count);

        foreach (LootSlot slot in slots)
        {
            writer.WriteUInt8(slot.Slot);
            writer.WriteUInt32(slot.ItemId);
            writer.WriteUInt32(slot.Count);
            writer.WriteUInt32(slot.DisplayId);

            // Random suffix and property, both zero until randomly-suffixed items exist.
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);

            writer.WriteUInt8(slot.SlotType);
        }
    }

    /// <summary>
    /// Writes a refusal.
    /// </summary>
    /// <remarks>
    /// The same opcode as a real window, with a loot type of zero and the error after it. The
    /// client needs this rather than silence: it has already drawn the window frame and will leave
    /// it up, empty and unclosable, if nothing comes back.
    /// </remarks>
    public static void WriteError(PacketWriter writer, ObjectGuid target, byte error)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(target.Value);
        writer.WriteUInt8(NoLoot);
        writer.WriteUInt8(error);
    }

    /// <summary>Writes <c>SMSG_LOOT_RELEASE_RESPONSE</c> — the window is closed.</summary>
    public static void WriteRelease(PacketWriter writer, ObjectGuid target)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(target.Value);
        writer.WriteUInt8(1);
    }

    /// <summary>Writes <c>SMSG_LOOT_REMOVED</c> — one slot has been taken, by anyone.</summary>
    /// <remarks>
    /// The slot number, and nothing else. The client greys out that square; it does not renumber
    /// the rest, which is why the server must not either.
    /// </remarks>
    public static void WriteRemoved(PacketWriter writer, byte slot)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt8(slot);
    }

    /// <summary>
    /// Writes <c>SMSG_LOOT_MONEY_NOTIFY</c> — the chat line for money picked up.
    /// </summary>
    /// <param name="soleLooter">
    /// Chooses the wording: <c>true</c> prints "You loot…", <c>false</c> "Your share is…". Always
    /// true here, because there are no groups to share with.
    /// </param>
    public static void WriteMoneyNotify(PacketWriter writer, uint copper, bool soleLooter = true)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(copper);
        writer.WriteUInt8(soleLooter ? (byte)1 : (byte)0);
    }
}
