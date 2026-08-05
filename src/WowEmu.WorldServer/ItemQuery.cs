using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>
/// How the server answers a spell lookup for an item's on-use cooldown.
/// </summary>
/// <remarks>
/// The response has to carry a cooldown for every one of an item's five spell slots, and the figure
/// comes from <c>Spell.dbc</c> whenever <c>item_template</c> has nothing to say. The protocol layer
/// does not read DBCs, so the caller supplies the lookup.
/// </remarks>
/// <returns><c>false</c> when the spell does not exist, which zeroes the whole slot.</returns>
public delegate bool SpellCooldownLookup(int spellId, out uint recoveryMs, out uint category, out uint categoryRecoveryMs);

/// <summary>
/// Writes <c>SMSG_ITEM_QUERY_SINGLE_RESPONSE</c>.
/// </summary>
/// <remarks>
/// Port of <c>WorldSession::HandleItemQuerySingleOpcode</c>. This packet is how the client learns
/// what an item <i>is</i>: it holds a guid and an entry from a create block, and everything the
/// tooltip prints comes from here.
/// <para>
/// <b>Nearly every field is four bytes wide regardless of its column.</b> The table stores
/// <c>class</c> as a <c>tinyint</c> and the packet writes a <c>uint32</c>, because the C++ struct
/// widens it on load. Writing the column's own width instead shifts every field after it and the
/// client draws a tooltip of noise.
/// </para>
/// </remarks>
public static class ItemQueryResponse
{
    /// <summary>
    /// Set on the entry to say "no such item".
    /// </summary>
    /// <remarks>
    /// The whole failure response is this one word. Sending a zeroed body instead leaves the client
    /// believing in an item with no name, which it then caches.
    /// </remarks>
    public const uint NotFoundFlag = 0x80000000;

    /// <summary>Writes the answer for an item that does not exist.</summary>
    public static void WriteNotFound(PacketWriter writer, uint entry)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteUInt32(entry | NotFoundFlag);
    }

    /// <summary>
    /// Writes the full description of one item.
    /// </summary>
    /// <param name="cooldowns">
    /// Resolves a spell's own cooldown figures, used for any slot where the table declines to
    /// specify. Passing null treats every spell as missing, which zeroes the slots — fine for a
    /// tooltip test and wrong for a live client with a clickable item.
    /// </param>
    public static void Write(PacketWriter writer, ItemTemplate item, SpellCooldownLookup? cooldowns = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(item);

        writer.WriteUInt32(item.Entry);
        writer.WriteUInt32(item.Class);
        writer.WriteUInt32(item.SubClass);
        writer.WriteUInt32((uint)item.SoundOverrideSubclass);
        writer.WriteCString(item.Name);

        // Three more name slots the client still reads and Blizzard never filled. Each is an empty
        // string, which is one zero byte — not three zero words.
        writer.WriteUInt8(0);
        writer.WriteUInt8(0);
        writer.WriteUInt8(0);

        writer.WriteUInt32(item.DisplayId);
        writer.WriteUInt32(item.Quality);
        writer.WriteUInt32(item.Flags);
        writer.WriteUInt32(item.FlagsExtra);

        // The column is a bigint and the struct is an int32. Following the struct: the client reads
        // four bytes here, and the widest real buy price in the table is well inside it.
        writer.WriteUInt32((uint)(int)item.BuyPrice);
        writer.WriteUInt32(item.SellPrice);
        writer.WriteUInt32(item.InventoryType);
        writer.WriteUInt32((uint)item.AllowableClass);
        writer.WriteUInt32((uint)item.AllowableRace);
        writer.WriteUInt32(item.ItemLevel);
        writer.WriteUInt32(item.RequiredLevel);
        writer.WriteUInt32(item.RequiredSkill);
        writer.WriteUInt32(item.RequiredSkillRank);
        writer.WriteUInt32(item.RequiredSpell);
        writer.WriteUInt32(item.RequiredHonorRank);
        writer.WriteUInt32(item.RequiredCityRank);
        writer.WriteUInt32(item.RequiredReputationFaction);
        writer.WriteUInt32(item.RequiredReputationRank);
        writer.WriteUInt32((uint)item.MaxCount);
        writer.WriteUInt32((uint)item.Stackable);
        writer.WriteUInt32(item.ContainerSlots);

        // The count is a length prefix, and only that many pairs follow. Writing all ten regardless
        // would put six words of zeroes where the client expects the scaling block.
        byte statsCount = Math.Min(item.StatsCount, (byte)ItemConstants.MaxStats);

        writer.WriteUInt32(statsCount);

        for (int i = 0; i < statsCount; i++)
        {
            writer.WriteUInt32(item.Stats[i].Type);
            writer.WriteUInt32((uint)item.Stats[i].Value);
        }

        writer.WriteUInt32((uint)item.ScalingStatDistribution);
        writer.WriteUInt32(item.ScalingStatValue);

        // Two, fixed — 3.1.0 cut this from five and the client's reader went with it.
        for (int i = 0; i < ItemConstants.MaxDamages; i++)
        {
            writer.WriteSingle(item.Damage[i].Min);
            writer.WriteSingle(item.Damage[i].Max);
            writer.WriteUInt32(item.Damage[i].School);
        }

        writer.WriteUInt32(item.Armor);
        writer.WriteUInt32(item.HolyResistance);
        writer.WriteUInt32(item.FireResistance);
        writer.WriteUInt32(item.NatureResistance);
        writer.WriteUInt32(item.FrostResistance);
        writer.WriteUInt32(item.ShadowResistance);
        writer.WriteUInt32(item.ArcaneResistance);

        writer.WriteUInt32(item.Delay);
        writer.WriteUInt32(item.AmmoType);
        writer.WriteSingle(item.RangedModRange);

        for (int i = 0; i < ItemConstants.MaxSpells; i++)
        {
            WriteSpell(writer, item.Spells[i], cooldowns);
        }

        writer.WriteUInt32(item.Bonding);
        writer.WriteCString(item.Description);
        writer.WriteUInt32(item.PageText);
        writer.WriteUInt32(item.LanguageId);
        writer.WriteUInt32(item.PageMaterial);
        writer.WriteUInt32(item.StartQuest);
        writer.WriteUInt32(item.LockId);
        writer.WriteUInt32((uint)item.Material);
        writer.WriteUInt32(item.Sheath);
        writer.WriteUInt32((uint)item.RandomProperty);
        writer.WriteUInt32(item.RandomSuffix);
        writer.WriteUInt32(item.Block);
        writer.WriteUInt32(item.ItemSet);
        writer.WriteUInt32(item.MaxDurability);
        writer.WriteUInt32(item.Area);
        writer.WriteUInt32((uint)item.Map);
        writer.WriteUInt32((uint)item.BagFamily);
        writer.WriteUInt32((uint)item.TotemCategory);

        for (int i = 0; i < ItemConstants.MaxSockets; i++)
        {
            writer.WriteUInt32((uint)item.Sockets[i].Color);
            writer.WriteUInt32((uint)item.Sockets[i].Content);
        }

        writer.WriteUInt32((uint)item.SocketBonus);
        writer.WriteUInt32((uint)item.GemProperties);
        writer.WriteUInt32((uint)item.RequiredDisenchantSkill);
        writer.WriteSingle(item.ArmorDamageModifier);
        writer.WriteUInt32(item.DurationSeconds);
        writer.WriteUInt32((uint)item.ItemLimitCategory);
        writer.WriteUInt32(item.HolidayId);
    }

    /// <summary>
    /// Writes one of the five spell slots.
    /// </summary>
    /// <remarks>
    /// Six words either way, so the layout does not change — but what is <i>in</i> them does. A slot
    /// whose spell does not exist is written as zeroes with <c>-1</c> cooldowns; a slot whose table
    /// row declines to specify a cooldown takes the spell's own instead. Note that
    /// <c>spellppmRate</c>, which sits between the charges and the cooldown in the table, is
    /// <b>not</b> on the wire at all.
    /// </remarks>
    private static void WriteSpell(PacketWriter writer, in ItemSpell spell, SpellCooldownLookup? cooldowns)
    {
        uint recoveryMs = 0;
        uint category = 0;
        uint categoryRecoveryMs = 0;

        bool exists = cooldowns is not null
            && cooldowns(spell.SpellId, out recoveryMs, out category, out categoryRecoveryMs);

        if (!exists)
        {
            // Six words either way, so a missing spell does not shift the slots after it.
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(unchecked((uint)-1));
            writer.WriteUInt32(0);
            writer.WriteUInt32(unchecked((uint)-1));

            return;
        }

        writer.WriteUInt32((uint)spell.SpellId);
        writer.WriteUInt32(spell.Trigger);
        writer.WriteUInt32((uint)spell.Charges);

        if (spell.HasCooldownData)
        {
            writer.WriteUInt32((uint)spell.CooldownMs);
            writer.WriteUInt32(spell.Category);
            writer.WriteUInt32((uint)spell.CategoryCooldownMs);

            return;
        }

        writer.WriteUInt32(recoveryMs);
        writer.WriteUInt32(category);
        writer.WriteUInt32(categoryRecoveryMs);
    }
}
