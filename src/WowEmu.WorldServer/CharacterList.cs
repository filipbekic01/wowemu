using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>
/// Builds <c>SMSG_CHAR_ENUM</c>.
/// </summary>
/// <remarks>
/// Port of <c>Player::BuildEnumData</c>. The layout is rigid and unforgiving: the client reads a
/// fixed number of fields per character with no length prefix, so a single missing or extra field
/// shifts everything after it and the character screen renders garbage or the client disconnects.
/// </remarks>
public static class CharacterList
{
    /// <summary>
    /// Equipment slots the client expects per character — <c>INVENTORY_SLOT_BAG_END</c>.
    /// </summary>
    /// <remarks>
    /// All 23 are always written, even with no items: the count is part of the format, not a
    /// function of what the character owns.
    /// </remarks>
    public const int EquipmentSlots = 23;

    /// <summary>Upper bound on one character's bytes, for sizing the buffer.</summary>
    public const int MaxBytesPerCharacter = 8 + 13 + 64 + (EquipmentSlots * 9) + 32;

    /// <summary>At-login flag meaning the character has never entered the world.</summary>
    public const ushort AtLoginFirst = 0x20;

    /// <summary>What one character is wearing, as the selection screen draws it.</summary>
    /// <param name="DisplayId">The item's <c>displayid</c>, which is what picks the model.</param>
    /// <param name="InventoryType">Which slot it is in, so the client knows where to hang it.</param>
    public readonly record struct VisibleItem(uint DisplayId, byte InventoryType);

    /// <summary>
    /// Writes the character list.
    /// </summary>
    /// <param name="equipment">
    /// What each character is wearing, by guid counter, in equipment-slot order. A character with
    /// no entry is drawn naked, which is what happens when the lookup is skipped entirely.
    /// </param>
    public static void Write(
        PacketWriter writer,
        IReadOnlyList<CharacterSummary> characters,
        IReadOnlyDictionary<uint, VisibleItem[]>? equipment = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(characters);

        writer.WriteUInt8((byte)characters.Count);

        foreach (CharacterSummary character in characters)
        {
            VisibleItem[]? worn = null;
            equipment?.TryGetValue(character.Id, out worn);

            Write(writer, character, worn);
        }
    }

    private static void Write(PacketWriter writer, CharacterSummary character, VisibleItem[]? equipment)
    {
        // A player guid: no entry, so the counter occupies the full low 32 bits.
        writer.WriteUInt64(ObjectGuid.Create(HighGuid.Player, character.Id).Value);

        writer.WriteCString(character.Name);
        writer.WriteUInt8(character.Race);
        writer.WriteUInt8(character.Class);
        writer.WriteUInt8(character.Gender);

        writer.WriteUInt8(character.Skin);
        writer.WriteUInt8(character.Face);
        writer.WriteUInt8(character.HairStyle);
        writer.WriteUInt8(character.HairColor);
        writer.WriteUInt8(character.FacialStyle);

        writer.WriteUInt8(character.Level);

        // A character that has never logged in reports zone 0, so the client shows no location
        // rather than a stale one.
        bool firstLogin = (character.AtLoginFlags & AtLoginFirst) != 0;
        writer.WriteUInt32(firstLogin ? 0 : character.Zone);

        writer.WriteUInt32(character.Map);

        writer.WriteSingle(character.PositionX);
        writer.WriteSingle(character.PositionY);
        writer.WriteSingle(character.PositionZ);

        writer.WriteUInt32(character.GuildId);

        // Character flags: ghost, hidden helm/cloak, forced rename. Phase 5 derives these from
        // player flags; nothing sets them yet.
        writer.WriteUInt32(0);

        // Customize flags: pending appearance/faction/race change. None pending.
        writer.WriteUInt32(0);

        writer.WriteUInt8((byte)(firstLogin ? 1 : 0));

        // Pet display, level and family — shown for hunters, warlocks and death knights.
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);

        // All 23 are written whatever the character owns — the count is part of the format, and
        // the last four are the bag slots, which the selection screen ignores but still reads.
        for (int slot = 0; slot < EquipmentSlots; slot++)
        {
            VisibleItem worn = equipment is not null && slot < equipment.Length
                ? equipment[slot]
                : default;

            writer.WriteUInt32(worn.DisplayId);
            writer.WriteUInt8(worn.InventoryType);
            writer.WriteUInt32(0);   // enchantment aura id
        }
    }
}
