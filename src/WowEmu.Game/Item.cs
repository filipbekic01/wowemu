using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// One item: a stack of something, owned by someone, sitting in a slot.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Item</c> that Phase 4.2 needs. It is a <see cref="GameObjectBase"/> but
/// <b>not</b> a <see cref="WorldObject"/>: an item has no position, no map and no movement block.
/// Its field block ends at <c>ITEM_END</c>, which is 64 slots against a unit's 148.
/// <para>
/// The client is told about items the same way it is told about creatures — a create block with a
/// field mask — but it never sees one in the world. An item reaches a client only because that
/// client owns it, which is why <see cref="Owner"/> is set at creation and not discovered later.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Matches the base class's vocabulary.")]
public class Item : GameObjectBase
{
    protected Item(ObjectGuid guid, TypeId typeId, int fieldCount, uint typeMask)
        : base(guid, typeId, fieldCount, typeMask)
    {
    }

    /// <summary>The <c>item_template</c> row this came from.</summary>
    public ItemTemplate Template { get; private init; } = null!;

    /// <summary>The <c>item_template</c> entry, as the client sees it.</summary>
    public uint Entry => Fields.GetUInt32(UpdateFields.OBJECT_FIELD_ENTRY);

    /// <summary>Who owns it. Empty while it is in flight — freshly looted, or in a mail.</summary>
    public ObjectGuid Owner
    {
        get => Fields.GetGuid(UpdateFields.ITEM_FIELD_OWNER);
        set => Fields.SetGuid(UpdateFields.ITEM_FIELD_OWNER, value);
    }

    /// <summary>
    /// What it is inside: a bag, or the owner for anything in the backpack.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Owner"/> even though both start out the same. Moving an item into a
    /// bag changes this and leaves the owner alone; trading it changes the owner.
    /// </remarks>
    public ObjectGuid Container
    {
        get => Fields.GetGuid(UpdateFields.ITEM_FIELD_CONTAINED);
        set => Fields.SetGuid(UpdateFields.ITEM_FIELD_CONTAINED, value);
    }

    /// <summary>Who crafted it, for the "Crafted by" line.</summary>
    public ObjectGuid Creator
    {
        get => Fields.GetGuid(UpdateFields.ITEM_FIELD_CREATOR);
        set => Fields.SetGuid(UpdateFields.ITEM_FIELD_CREATOR, value);
    }

    /// <summary>
    /// How many are in the stack.
    /// </summary>
    /// <remarks>
    /// Never zero on a live item: a stack that reaches zero is destroyed rather than kept as an
    /// empty one, and the client draws a zero-count slot as occupied but blank.
    /// </remarks>
    public uint Count
    {
        get => Fields.GetUInt32(UpdateFields.ITEM_FIELD_STACK_COUNT);
        set => Fields.SetUInt32(UpdateFields.ITEM_FIELD_STACK_COUNT, value);
    }

    /// <summary>Seconds left before it disappears. Zero means it does not.</summary>
    public uint DurationSeconds
    {
        get => Fields.GetUInt32(UpdateFields.ITEM_FIELD_DURATION);
        set => Fields.SetUInt32(UpdateFields.ITEM_FIELD_DURATION, value);
    }

    public uint Durability
    {
        get => Fields.GetUInt32(UpdateFields.ITEM_FIELD_DURABILITY);
        set => Fields.SetUInt32(UpdateFields.ITEM_FIELD_DURABILITY, value);
    }

    public uint MaxDurability
    {
        get => Fields.GetUInt32(UpdateFields.ITEM_FIELD_MAXDURABILITY);
        set => Fields.SetUInt32(UpdateFields.ITEM_FIELD_MAXDURABILITY, value);
    }

    public uint ItemFlags
    {
        get => Fields.GetUInt32(UpdateFields.ITEM_FIELD_FLAGS);
        set => Fields.SetUInt32(UpdateFields.ITEM_FIELD_FLAGS, value);
    }

    /// <summary>
    /// Bound to whoever picked it up, and so no longer tradeable or sellable.
    /// </summary>
    /// <remarks>
    /// <c>ITEM_FIELD_FLAG_SOULBOUND</c>. It lives on the item's own flags rather than beside the
    /// owner guid, which matters: the client draws "Soulbound" from this field, so an item bound
    /// only in the server's head still reads as tradeable in the tooltip.
    /// </remarks>
    public bool IsSoulBound
    {
        get => (ItemFlags & SoulBoundFlag) != 0;
        set => ItemFlags = value ? ItemFlags | SoulBoundFlag : ItemFlags & ~SoulBoundFlag;
    }

    /// <summary>
    /// Whether this item is bound to somebody who is not <paramref name="player"/>.
    /// </summary>
    /// <remarks>
    /// Port of <c>Item::IsBindedNotWith</c>. Note the shape: an unbound item is never "someone
    /// else's", and a bound item held by its own owner is not either. Only the third case — bound,
    /// and owned by another — refuses.
    /// </remarks>
    public bool IsBoundToSomeoneElse(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        return IsSoulBound && Owner != player.Guid;
    }

    /// <summary><c>ITEM_FIELD_FLAG_SOULBOUND</c>.</summary>
    private const uint SoulBoundFlag = 0x00000001;

    /// <summary>Whether the stack is at its ceiling and cannot take any more.</summary>
    public bool IsFullStack => Count >= Template.MaxStackSize;

    /// <summary>How many more would fit in this stack.</summary>
    public uint FreeStackSpace => IsFullStack ? 0 : Template.MaxStackSize - Count;

    /// <summary>Whether it has taken damage that a repair would undo.</summary>
    public bool IsDamaged => MaxDurability > 0 && Durability < MaxDurability;

    /// <summary>Whether it has broken, and so gives none of its stats.</summary>
    public bool IsBroken => MaxDurability > 0 && Durability == 0;

    /// <summary>Charges left on one of the item's five spells. Negative destroys the item at zero.</summary>
    public int GetSpellCharges(int index) =>
        (int)Fields.GetUInt32(UpdateFields.ITEM_FIELD_SPELL_CHARGES + index);

    /// <inheritdoc cref="GetSpellCharges"/>
    public void SetSpellCharges(int index, int charges) =>
        Fields.SetUInt32(UpdateFields.ITEM_FIELD_SPELL_CHARGES + index, (uint)charges);

    /// <summary>
    /// Builds one item from a template.
    /// </summary>
    /// <remarks>
    /// Port of <c>Item::Create</c>. A container gets a <see cref="Bag"/> instead, because its field
    /// block is longer and the client reads the extra slots unconditionally once the container type
    /// bit is set.
    /// <para>
    /// <b>The stack starts at one, not at the template's stack size.</b> Whoever creates the item
    /// sets the count afterwards; starting full would hand a player twenty of everything.
    /// </para>
    /// </remarks>
    /// <param name="counter">
    /// The low part of the guid. Items have no entry in their guid, so the whole low 32 bits are
    /// this counter — see <see cref="ObjectGuid.Create(HighGuid, uint)"/>.
    /// </param>
    public static Item Create(uint counter, ItemTemplate template, ObjectGuid owner = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        Item item = template.IsBag
            ? new Bag(ObjectGuid.Create(HighGuid.Container, counter)) { Template = template }
            : new Item(
                ObjectGuid.Create(HighGuid.Item, counter),
                TypeId.Item,
                UpdateFields.ITEM_END,
                TypeMask.Object | TypeMask.Item)
            {
                Template = template,
            };

        item.Fields.SetUInt32(UpdateFields.OBJECT_FIELD_ENTRY, template.Entry);
        item.Owner = owner;
        item.Container = owner;
        item.Count = 1;
        item.MaxDurability = template.MaxDurability;
        item.Durability = template.MaxDurability;
        item.DurationSeconds = template.DurationSeconds;

        for (int i = 0; i < ItemConstants.MaxSpells; i++)
        {
            item.SetSpellCharges(i, template.Spells[i].Charges);
        }

        if (item is Bag bag)
        {
            bag.SlotCount = template.ContainerSlots;
        }

        return item;
    }

    /// <summary>
    /// The random-properties id, signed. Zero for an item with none, which is most of them.
    /// </summary>
    /// <remarks>
    /// <b>Signed, and the sign is load-bearing</b> — positive names a row of
    /// <c>ItemRandomProperties.dbc</c> and negative one of <c>ItemRandomSuffix.dbc</c>. Stored and
    /// read as an int for that reason; as a uint a suffix becomes a four-billion-ish property id.
    /// </remarks>
    public int RandomPropertiesId
    {
        get => unchecked((int)Fields.GetUInt32(UpdateFields.ITEM_FIELD_RANDOM_PROPERTIES_ID));
        set => Fields.SetUInt32(UpdateFields.ITEM_FIELD_RANDOM_PROPERTIES_ID, unchecked((uint)value));
    }

    /// <summary>
    /// The suffix factor, which scales a scaled suffix's amounts to this item.
    /// </summary>
    /// <remarks>
    /// The client computes the displayed stats from this and the suffix row, so an item with a
    /// negative properties id and no factor shows "of the Bear" and grants nothing.
    /// </remarks>
    public uint SuffixFactor
    {
        get => Fields.GetUInt32(UpdateFields.ITEM_FIELD_PROPERTY_SEED);
        set => Fields.SetUInt32(UpdateFields.ITEM_FIELD_PROPERTY_SEED, value);
    }

    /// <summary>The enchantment in a slot, or zero.</summary>
    public uint GetEnchantment(int slot) =>
        slot >= 0 && slot < EnchantmentSlot.Count
            ? Fields.GetUInt32(EnchantmentField(slot))
            : 0;

    /// <summary>
    /// Puts an enchantment in a slot.
    /// </summary>
    /// <remarks>
    /// <b>Three words per slot, not one.</b> The id, then a duration and a charge count the client
    /// reads for temporary enchants — writing one word per slot puts every enchant past the first
    /// into the previous one's duration field.
    /// </remarks>
    public void SetEnchantment(int slot, uint enchantmentId, uint durationMs = 0, uint charges = 0)
    {
        if (slot < 0 || slot >= EnchantmentSlot.Count)
        {
            return;
        }

        int field = EnchantmentField(slot);

        Fields.SetUInt32(field, enchantmentId);
        Fields.SetUInt32(field + 1, durationMs);
        Fields.SetUInt32(field + 2, charges);
    }

    /// <summary>Three words per slot, from the first slot's first word.</summary>
    private static int EnchantmentField(int slot) =>
        UpdateFields.ITEM_FIELD_ENCHANTMENT_1_1 + (slot * 3);
}

/// <summary>
/// A bag: an item that holds other items.
/// </summary>
/// <remarks>
/// Port of <c>Bag</c>. Its field block runs to <c>CONTAINER_END</c> rather than <c>ITEM_END</c> —
/// 46 slots longer, for the slot count and 36 guid words. The client decides which to read from the
/// <see cref="TypeMask.Container"/> bit, so a bag sent as a plain item leaves it reading 46 words
/// of whatever came next.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Matches the base class's vocabulary.")]
public sealed class Bag(ObjectGuid guid)
    : Item(guid, TypeId.Container, UpdateFields.CONTAINER_END, TypeMask.Object | TypeMask.Item | TypeMask.Container)
{
    /// <summary>The client's ceiling on bag slots, and the width of the guid array below.</summary>
    public const int MaxSlots = 36;

    /// <summary>How many slots this bag actually has.</summary>
    public uint SlotCount
    {
        get => Fields.GetUInt32(UpdateFields.CONTAINER_FIELD_NUM_SLOTS);
        set => Fields.SetUInt32(UpdateFields.CONTAINER_FIELD_NUM_SLOTS, value);
    }

    /// <summary>What is in one slot, as the client sees it. Empty for a free slot.</summary>
    public ObjectGuid GetSlot(int slot) =>
        Fields.GetGuid(UpdateFields.CONTAINER_FIELD_SLOT_1 + (slot * 2));

    /// <inheritdoc cref="GetSlot"/>
    public void SetSlot(int slot, ObjectGuid item) =>
        Fields.SetGuid(UpdateFields.CONTAINER_FIELD_SLOT_1 + (slot * 2), item);

    /// <summary>Whether every slot the bag has is empty.</summary>
    public bool IsEmpty
    {
        get
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (!GetSlot(slot).IsEmpty)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
