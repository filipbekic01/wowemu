using WowEmu.Core;
using WowEmu.Game;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>
/// The talent pane's packets.
/// </summary>
/// <remarks>
/// Port of <c>Player::BuildPlayerTalentsInfoData</c>. There is exactly one packet the client draws
/// the whole pane from, and it carries <b>both</b> specs — not just the one being played, because
/// the client renders the inactive tab from the same message.
/// </remarks>
public static class TalentPackets
{
    /// <summary>
    /// Writes <c>SMSG_TALENTS_INFO</c> for a player.
    /// </summary>
    /// <remarks>
    /// The per-spec talent count is written <i>after</i> the talents it counts, by going back and
    /// patching the byte — the count is not known until the walk is done, and reserving it wrongly
    /// leaves the client reading talents out of the glyph block.
    /// </remarks>
    public static void WritePlayerTalents(PacketWriter writer, Player player)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(player);

        // 0 for a player, 1 for a pet. Pets are not built, so this is always a player.
        writer.WriteUInt8(0);

        writer.WriteUInt32(player.Talents.FreePoints);
        writer.WriteUInt8(player.Talents.SpecCount);
        writer.WriteUInt8(player.Talents.ActiveSpec);

        for (int spec = 0; spec < player.Talents.SpecCount; spec++)
        {
            IReadOnlyDictionary<uint, byte> talents = player.Talents.InSpec(spec);

            writer.WriteUInt8((byte)talents.Count);

            foreach ((uint talentId, byte rank) in talents)
            {
                writer.WriteUInt32(talentId);
                writer.WriteUInt8(rank);
            }

            writer.WriteUInt8(PlayerGlyphs.SlotCount);

            IReadOnlyList<uint> glyphs = player.Glyphs.InSpec(spec);

            for (int slot = 0; slot < PlayerGlyphs.SlotCount; slot++)
            {
                // Sixteen bits, not thirty-two. A glyph id fits, and writing a full word here
                // shifts every following spec's block by six bytes.
                writer.WriteUInt16((ushort)glyphs[slot]);
            }
        }
    }

    /// <summary>
    /// Writes the "you have no talents to wipe" form of <c>MSG_TALENT_WIPE_CONFIRM</c>.
    /// </summary>
    /// <remarks>
    /// A guid of zero and a cost of zero. The client leaves its confirmation box up until something
    /// comes back, so going quiet on a character with nothing to reset hangs the dialog.
    /// </remarks>
    public static void WriteNothingToWipe(PacketWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(0);
        writer.WriteUInt32(0);
    }

    /// <summary>Writes <c>MSG_TALENT_WIPE_CONFIRM</c> — the trainer and what they charge.</summary>
    public static void WriteWipeConfirm(PacketWriter writer, ObjectGuid trainer, uint cost)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt64(trainer.Value);
        writer.WriteUInt32(cost);
    }
}
