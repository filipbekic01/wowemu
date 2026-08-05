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
