namespace WowEmu.Data.Db;

/// <summary>
/// One item that exists: its own guid, what it is, and how worn it is.
/// </summary>
/// <remarks>
/// Split from <see cref="CharacterInventoryEntity"/> the way upstream splits
/// <c>item_instance</c> from <c>character_inventory</c>, and for the same reason: an item outlives
/// any particular place. A traded item changes owner and slot but stays the same item, and one in
/// the mail or the auction house is in neither an inventory nor a bag.
/// </remarks>
public sealed class ItemInstanceEntity
{
    /// <summary>
    /// The item's guid counter, and the low 32 bits of its <c>ObjectGuid</c>.
    /// </summary>
    /// <remarks>
    /// <b>Not auto-generated.</b> The client is told an item's guid the moment it appears, which is
    /// before anything is written to the database — so the number has to be allocated in memory and
    /// the column has to accept it. See <c>ItemGuidGenerator</c>.
    /// </remarks>
    public uint Id { get; set; }

    /// <summary>The <c>item_template</c> row it is an instance of.</summary>
    public uint Entry { get; set; }

    /// <summary>Which character owns it. Zero for an item in flight.</summary>
    public uint OwnerId { get; set; }

    public uint Count { get; set; } = 1;

    public uint Durability { get; set; }

    /// <summary>Seconds left before it disappears. Zero means it does not.</summary>
    public uint DurationSeconds { get; set; }

    /// <summary>
    /// The five spell charge counters, comma-separated.
    /// </summary>
    /// <remarks>
    /// One column rather than five, because nothing queries on a charge count and five sparse
    /// integer columns on every row of the largest table in the schema is a poor trade. Upstream
    /// stores it as a space-separated string for the same reason.
    /// </remarks>
    public string SpellCharges { get; set; } = string.Empty;

    public uint Flags { get; set; }

    /// <summary>
    /// The rolled random-properties id, signed. Zero for an item with none.
    /// </summary>
    /// <remarks>
    /// <b>Signed, and stored signed.</b> Negative names a scaled suffix and positive a fixed one;
    /// an unsigned column turns every "of the Bear" into a four-billion-ish property id that
    /// resolves to nothing.
    /// </remarks>
    public int RandomPropertyId { get; set; }

    /// <summary>The suffix factor the scaled amounts are computed from.</summary>
    /// <remarks>
    /// Saved rather than recomputed. It is derived from the template today, but it is <i>the
    /// item's</i> and recomputing it would silently restat everyone's gear if the table changed.
    /// </remarks>
    public uint SuffixFactor { get; set; }
}

/// <summary>
/// Where one item is: whose it is, which container, which slot.
/// </summary>
/// <remarks>
/// Keyed by the <i>item</i>, not by the slot. An item is in exactly one place, and making that the
/// key is what stops two rows claiming the same item — which would duplicate it on the next login.
/// </remarks>
public sealed class CharacterInventoryEntity
{
    /// <summary>The item this row places. Primary key.</summary>
    public uint ItemId { get; set; }

    /// <summary>Which character's inventory.</summary>
    public uint CharacterId { get; set; }

    /// <summary>
    /// The guid of the bag it is inside, or zero for the player's own slot array.
    /// </summary>
    /// <remarks>
    /// An item guid, not a slot number — a bag moved from one bag slot to another keeps its guid,
    /// so its contents need no rewriting.
    /// </remarks>
    public uint BagId { get; set; }

    /// <summary>The slot within the bag, or within the player's 150-slot array.</summary>
    public byte Slot { get; set; }
}

/// <summary>
/// One quest a character has taken, and how far through it is.
/// </summary>
/// <remarks>
/// The four kill counters are separate columns rather than one packed value. The client packs them
/// into 16 bits apiece; the database does not have to, and a column that can be read in a query is
/// worth more than the width saved.
/// <para>
/// <b>Item counts are not stored.</b> They are recounted from the bags on load, which cannot drift
/// — an item can arrive by looting, trading, buying or mail, and a stored count is one missed
/// increment away from a quest that can never be finished.
/// </para>
/// <para>
/// A row survives being handed in — that is what stops a quest being offered twice — so
/// <see cref="Status"/> distinguishes rewarded from complete.
/// </para>
/// </remarks>
public sealed class CharacterQuestEntity
{
    public uint CharacterId { get; set; }

    public uint QuestId { get; set; }

    /// <summary>A <c>QuestStatus</c>. Rewarded is 6.</summary>
    public byte Status { get; set; }

    /// <summary>Which log slot, or 255 once it is out of the log.</summary>
    public byte Slot { get; set; }

    public ushort Killed1 { get; set; }

    public ushort Killed2 { get; set; }

    public ushort Killed3 { get; set; }

    public ushort Killed4 { get; set; }
}

/// <summary>One spell a character knows.</summary>
/// <remarks>
/// A row per spell rather than a packed list: a spellbook is read whole and written whole, but it
/// is also the sort of thing a support query wants to filter on — "who knows this?" — and a packed
/// column cannot answer that.
/// </remarks>
public sealed class CharacterSpellEntity
{
    public uint CharacterId { get; set; }

    public uint SpellId { get; set; }
}

/// <summary>
/// One skill a character has, and how far along it is.
/// </summary>
/// <remarks>
/// The value and its maximum are stored, but not the slot. A skill's position in the update-field
/// block is whatever was free when it was learned and means nothing outside a live session — saving
/// it would pin a character to a layout that the next login has no reason to reproduce.
/// <para>
/// The bonuses are not stored either: the permanent one belongs to whatever enchantment or book
/// granted it, and re-deriving it from that source is the only way it stays correct when the source
/// goes away.
/// </para>
/// </remarks>
public sealed class CharacterSkillEntity
{
    public uint CharacterId { get; set; }

    public ushort SkillId { get; set; }

    public ushort Value { get; set; }

    public ushort MaxValue { get; set; }

    /// <summary>Which tier a ranked skill has reached. Zero for everything that has no tiers.</summary>
    public ushort Step { get; set; }
}

/// <summary>
/// What one faction thinks of one character.
/// </summary>
/// <remarks>
/// <b>Faction id, not reputation list id.</b> The list id is a display slot the client uses and
/// only 128 factions have one; storing that instead throws away every faction tracked behind the
/// scenes and collides the ones left.
/// </remarks>
public sealed class CharacterReputationEntity
{
    public uint CharacterId { get; set; }

    public ushort FactionId { get; set; }

    /// <summary>Signed: standing runs down to -42,000 as well as up.</summary>
    public int Standing { get; set; }
}

/// <summary>
/// One repeating quest a character has done since its last reset.
/// </summary>
/// <remarks>
/// Three tables rather than one with a period column, matching upstream. Each is truncated
/// wholesale by its own reset, and a shared table would make each reset a filtered delete over
/// rows the other two still need.
/// </remarks>
public abstract class CharacterQuestPeriodEntity
{
    public uint CharacterId { get; set; }

    public uint QuestId { get; set; }
}

/// <inheritdoc cref="CharacterQuestPeriodEntity"/>
public sealed class CharacterQuestDailyEntity : CharacterQuestPeriodEntity;

/// <inheritdoc cref="CharacterQuestPeriodEntity"/>
public sealed class CharacterQuestWeeklyEntity : CharacterQuestPeriodEntity;

/// <inheritdoc cref="CharacterQuestPeriodEntity"/>
public sealed class CharacterQuestMonthlyEntity : CharacterQuestPeriodEntity;

/// <summary>
/// Where a character comes back to — the innkeeper they last spoke to, or their starting zone.
/// </summary>
/// <remarks>
/// A separate table rather than columns on <c>characters</c>, matching upstream, because a
/// character that has never bound anywhere has <i>no row</i> — which is a different thing from a
/// row of zeroes, and zero is a real map id.
/// </remarks>
public sealed class CharacterHomebindEntity
{
    public uint CharacterId { get; set; }

    public uint MapId { get; set; }

    /// <summary>The area, not the zone, despite upstream's column being called <c>zoneId</c>.</summary>
    public uint AreaId { get; set; }

    public float PositionX { get; set; }

    public float PositionY { get; set; }

    public float PositionZ { get; set; }
}

/// <summary>One button on a character's action bars.</summary>
/// <remarks>
/// The action and its type are stored apart even though the client wants them packed into one
/// word: a column that has to be masked to be read is a column nothing can query on.
/// </remarks>
public sealed class CharacterActionEntity
{
    public uint CharacterId { get; set; }

    public byte Button { get; set; }

    public uint Action { get; set; }

    public byte Type { get; set; }
}

/// <summary>Which of the three shared quest resets is being run.</summary>
public enum QuestResetPeriod
{
    Daily,
    Weekly,
    Monthly,
}
