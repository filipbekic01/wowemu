using WowEmu.Core;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>What kind of body this is. <c>CorpseType</c>.</summary>
public static class CorpseType
{
    /// <summary>A pile of bones. Nobody can resurrect at it and it decays on its own.</summary>
    /// <remarks>
    /// What a corpse becomes once its owner is alive again. Removing the object outright instead
    /// leaves nothing where the body was, and a player who dies twice in a place would see their
    /// first body vanish the moment they stood up from the second.
    /// </remarks>
    public const byte Bones = 0;

    public const byte ResurrectablePve = 1;
    public const byte ResurrectablePvp = 2;
}

/// <summary><c>CorpseFlags</c>.</summary>
public static class CorpseFlags
{
    public const uint None = 0x00;
    public const uint Bones = 0x01;

    /// <summary>Set on every corpse upstream creates, with no explanation anywhere.</summary>
    /// <remarks>
    /// <c>CORPSE_FLAG_UNK2</c>. Kept because it is unconditional in <c>CreateCorpse</c> and the
    /// client presumably reads it; dropping it is the kind of change that looks harmless and shows
    /// up as a rendering oddity nobody can trace.
    /// </remarks>
    public const uint Unknown2 = 0x04;

    public const uint HideHelm = 0x08;
    public const uint HideCloak = 0x10;
    public const uint Lootable = 0x20;
}

/// <summary>
/// A player's body, left where they died.
/// </summary>
/// <remarks>
/// Port of <c>Corpse</c> and <c>Player::CreateCorpse</c>. Until now a corpse was a remembered
/// position on the player and nothing else — which was enough to walk back to and reclaim, and
/// invisible to everyone including its owner. The client draws the body from a real object, and the
/// resurrect dialog appears because one is nearby.
/// <para>
/// <b>The appearance is repacked, not copied.</b> A corpse carries its own bytes fields laid out
/// differently from the player's, and the equipment slots hold a display id and an inventory type
/// packed into one word rather than item guids. A corpse built by copying the player's fields
/// across renders as something else entirely.
/// </para>
/// </remarks>
public sealed class Corpse : WorldObject
{
    private Corpse(ObjectGuid guid)
        : base(guid, TypeId.Corpse, UpdateFields.CORPSE_END, TypeMask.Corpse | TypeMask.Object)
    {
    }

    /// <summary>Whose body it is.</summary>
    public ObjectGuid Owner => Fields.GetGuid(UpdateFields.CORPSE_FIELD_OWNER);

    /// <summary>Resurrectable, or a pile of bones.</summary>
    public byte Type { get; private set; } = CorpseType.ResurrectablePve;

    /// <summary>Whether this is still something its owner could resurrect at.</summary>
    public bool IsResurrectable => Type != CorpseType.Bones;

    /// <summary>
    /// Builds the body a player leaves behind.
    /// </summary>
    /// <remarks>
    /// Everything visible is taken at the moment of death and never updated afterwards — a corpse
    /// wears what its owner died in, whatever they are wearing when they come back for it.
    /// </remarks>
    public static Corpse Create(Player owner, uint lowGuid)
    {
        ArgumentNullException.ThrowIfNull(owner);

        Corpse corpse = new(ObjectGuid.Create(HighGuid.Corpse, lowGuid))
        {
            MapId = owner.MapId,
            Position = owner.Position,
            Type = CorpseType.ResurrectablePve,
        };

        UpdateFieldStorage fields = corpse.Fields;

        fields.SetFloat(UpdateFields.OBJECT_FIELD_SCALE_X, 1f);
        fields.SetGuid(UpdateFields.CORPSE_FIELD_OWNER, owner.Guid);

        fields.SetUInt32(
            UpdateFields.CORPSE_FIELD_DISPLAY_ID,
            owner.Fields.GetUInt32(UpdateFields.UNIT_FIELD_NATIVEDISPLAYID));

        WriteAppearance(corpse, owner);
        WriteEquipment(corpse, owner);

        uint flags = CorpseFlags.Unknown2;

        if ((owner.PlayerFlags & PlayerFlagHideHelm) != 0)
        {
            flags |= CorpseFlags.HideHelm;
        }

        if ((owner.PlayerFlags & PlayerFlagHideCloak) != 0)
        {
            flags |= CorpseFlags.HideCloak;
        }

        fields.SetUInt32(UpdateFields.CORPSE_FIELD_FLAGS, flags);

        corpse.Name = owner.Name;

        return corpse;
    }

    /// <summary>
    /// Turns this into a pile of bones, which nobody can resurrect at.
    /// </summary>
    /// <remarks>
    /// <c>Corpse::ConvertCorpseToBones</c>. Upstream keeps the object and changes what it is rather
    /// than deleting and recreating, and so does this: the guid stays valid, so every client that
    /// has been told about the corpse sees it change rather than disappear and reappear.
    /// </remarks>
    public void ConvertToBones()
    {
        Type = CorpseType.Bones;

        Fields.SetGuid(UpdateFields.CORPSE_FIELD_OWNER, ObjectGuid.Empty);
        Fields.SetUInt32(
            UpdateFields.CORPSE_FIELD_FLAGS,
            Fields.GetUInt32(UpdateFields.CORPSE_FIELD_FLAGS) | CorpseFlags.Bones);
    }

    /// <summary>
    /// Repacks the owner's look into the two bytes fields a corpse carries.
    /// </summary>
    /// <remarks>
    /// <b>Not the same layout as the player's.</b> A player keeps skin, face, hair style and hair
    /// colour in <c>PLAYER_BYTES</c> and facial hair in <c>PLAYER_BYTES_2</c>; a corpse wants race
    /// and gender in the first word alongside skin, and the rest in the second. Copying the player's
    /// words across gives a body with somebody else's face.
    /// </remarks>
    private static void WriteAppearance(Corpse corpse, Player owner)
    {
        uint playerBytes = owner.Fields.GetUInt32(UpdateFields.PLAYER_BYTES);
        uint playerBytes2 = owner.Fields.GetUInt32(UpdateFields.PLAYER_BYTES_2);

        byte skin = (byte)playerBytes;
        byte face = (byte)(playerBytes >> 8);
        byte hairStyle = (byte)(playerBytes >> 16);
        byte hairColour = (byte)(playerBytes >> 24);
        byte facialHair = (byte)playerBytes2;

        // The low byte is deliberately zero — upstream writes 0x00 there and never says what it is.
        uint bytes1 = (uint)(owner.Race << 8)
            | (uint)(owner.Fields.GetByte(UpdateFields.PLAYER_BYTES_3, 0) << 16)
            | ((uint)skin << 24);

        uint bytes2 = face | ((uint)hairStyle << 8) | ((uint)hairColour << 16) | ((uint)facialHair << 24);

        corpse.Fields.SetUInt32(UpdateFields.CORPSE_FIELD_BYTES_1, bytes1);
        corpse.Fields.SetUInt32(UpdateFields.CORPSE_FIELD_BYTES_2, bytes2);
    }

    /// <summary>
    /// Writes what the owner was wearing, as the client wants it.
    /// </summary>
    /// <remarks>
    /// A display id in the low three bytes and the inventory type in the top one — <b>not</b> an
    /// item guid or entry. Nineteen slots, one per equipment slot, and an empty slot stays zero.
    /// </remarks>
    private static void WriteEquipment(Corpse corpse, Player owner)
    {
        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (owner.Inventory.Equipped(slot) is not { } worn)
            {
                continue;
            }

            uint packed = worn.Template.DisplayId | ((uint)worn.Template.InventoryType << 24);

            corpse.Fields.SetUInt32(UpdateFields.CORPSE_FIELD_ITEM + slot, packed);
        }
    }

    /// <summary><c>PLAYER_FLAGS_HIDE_HELM</c> and <c>_HIDE_CLOAK</c>.</summary>
    private const uint PlayerFlagHideHelm = 0x00000400;
    private const uint PlayerFlagHideCloak = 0x00000800;
}
