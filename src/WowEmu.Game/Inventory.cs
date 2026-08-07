using WowEmu.Core;
using WowEmu.Data.Client;
using WowEmu.Data.Db;
using WowEmu.Protocol;

namespace WowEmu.Game;

/// <summary>
/// Where a player's things live. <c>EquipmentSlots</c> and the enums that follow it.
/// </summary>
/// <remarks>
/// One flat run of 150 slots, split by convention into equipment, bags, backpack, bank and the
/// rest. <b>Every one of them maps to <c>PLAYER_FIELD_INV_SLOT_HEAD + slot × 2</c></b> — the five
/// separate field names the client's headers give these ranges describe one contiguous guid array,
/// and treating them as separate arrays puts the backpack 46 words too far along.
/// </remarks>
public static class InventorySlots
{
    /// <summary>
    /// The player itself, as a container. <b>255, not 0.</b>
    /// </summary>
    /// <remarks>
    /// <c>INVENTORY_SLOT_BAG_0</c>. Bag 0 is a real bag in the first bag slot; the backpack and the
    /// equipment are addressed through 255. Reading a bag byte of 0 as "the backpack" sends every
    /// query to whatever is in the first bag slot instead.
    /// </remarks>
    public const byte Backpack = 255;

    /// <summary>Nothing. <c>NULL_SLOT</c>.</summary>
    public const byte None = 255;

    public const byte EquipmentStart = 0;
    public const byte Head = 0;
    public const byte Neck = 1;
    public const byte Shoulders = 2;
    public const byte Body = 3;
    public const byte Chest = 4;
    public const byte Waist = 5;
    public const byte Legs = 6;
    public const byte Feet = 7;
    public const byte Wrists = 8;
    public const byte Hands = 9;
    public const byte Finger1 = 10;
    public const byte Finger2 = 11;
    public const byte Trinket1 = 12;
    public const byte Trinket2 = 13;
    public const byte Back = 14;
    public const byte MainHand = 15;
    public const byte OffHand = 16;
    public const byte Ranged = 17;
    public const byte Tabard = 18;
    public const byte EquipmentEnd = 19;

    /// <summary>The four slots a bag can be worn in.</summary>
    public const byte BagStart = 19;
    public const byte BagEnd = 23;

    /// <summary>The sixteen slots of the backpack itself, which is not a bag and cannot be removed.</summary>
    public const byte ItemStart = 23;
    public const byte ItemEnd = 39;

    public const byte BankItemStart = 39;
    public const byte BankItemEnd = 67;
    public const byte BankBagStart = 67;
    public const byte BankBagEnd = 74;
    public const byte BuybackStart = 74;
    public const byte BuybackEnd = 86;
    public const byte KeyringStart = 86;
    public const byte KeyringEnd = 118;
    public const byte CurrencyTokenStart = 118;
    public const byte CurrencyTokenEnd = 150;

    /// <summary>How wide the player's slot array is. <c>CURRENCYTOKEN_SLOT_END</c>.</summary>
    public const int SlotCount = CurrencyTokenEnd;

    /// <summary>Whether a slot is worn rather than carried, which is what makes it visible.</summary>
    public static bool IsEquipment(byte slot) => slot < EquipmentEnd;

    /// <summary>Whether a slot is one of the four a bag can be worn in.</summary>
    public static bool IsBagSlot(byte slot) => slot is >= BagStart and < BagEnd;

    /// <summary>Whether a slot is one of the sixteen in the backpack.</summary>
    public static bool IsBackpackSlot(byte slot) => slot is >= ItemStart and < ItemEnd;

    /// <summary>Whether a slot is somewhere a player can carry something day to day.</summary>
    /// <remarks>
    /// Equipment, bags and the backpack. The bank, buyback and keyring exist in the array and are
    /// not part of what this phase moves things between.
    /// </remarks>
    public static bool IsInventory(byte slot) => slot < ItemEnd;
}

/// <summary>
/// Why an inventory operation was refused. <c>InventoryResult</c>.
/// </summary>
/// <remarks>
/// The client turns each of these into its own sentence, so the specific code matters — a generic
/// failure prints nothing useful and the player is left guessing. Only the ones this phase can
/// actually produce are here; the enum runs to 89 upstream.
/// </remarks>
public enum InventoryResult : byte
{
    Ok = 0,
    CantEquipLevel = 1,
    CantEquipSkill = 2,
    ItemDoesNotGoToSlot = 3,
    BagFull = 4,
    NonEmptyBagOverOtherBag = 5,
    NoRequiredProficiency = 8,
    NoEquipmentSlotAvailable = 9,
    YouCanNeverUseThatItem = 10,
    CantEquipWithTwoHanded = 13,
    CantDualWield = 14,
    ItemDoesNotGoIntoBag = 15,
    CantCarryMoreOfThis = 17,
    ItemCantStack = 19,
    ItemCantBeEquipped = 20,
    ItemsCantBeSwapped = 21,
    SlotIsEmpty = 22,
    ItemNotFound = 23,
    TriedToSplitMoreThanCount = 26,
    CouldNotSplitItems = 27,
    NotABag = 30,
    CanOnlyDoWithEmptyBags = 31,
    DontOwnThatItem = 32,
    YouAreStunned = 37,
    YouAreDead = 38,
    InventoryFull = 50,
    ItemNotFound2 = 54,
    NotWhileDisarmed = 61,

    /// <summary>Only one of this item may be worn. <c>ITEM_FLAG_UNIQUE_EQUIPPABLE</c>.</summary>
    ItemUniqueEquippable = 67,

    /// <summary>Too many of this family already held.</summary>
    ItemMaxLimitCategoryCountExceeded = 84,

    /// <summary>Too many of this family already worn.</summary>
    ItemMaxLimitCategoryEquippedExceeded = 89,
}

/// <summary>One place an item can be: a container and a slot inside it.</summary>
/// <param name="Bag">
/// <see cref="InventorySlots.Backpack"/> for the player's own array, or the slot a worn bag is in.
/// </param>
public readonly record struct ItemPosition(byte Bag, byte Slot)
{
    /// <summary>The player's own array rather than inside a bag.</summary>
    public bool IsOnThePlayer => Bag == InventorySlots.Backpack;

    /// <summary>
    /// The two bytes packed into the <c>uint16</c> the client sends and receives.
    /// </summary>
    /// <remarks>
    /// <b>Bag in the high byte.</b> Packing them the other way round addresses slot 255 of bag 23,
    /// which exists in neither direction and simply does nothing.
    /// </remarks>
    public ushort Packed => (ushort)((Bag << 8) | Slot);

    public static ItemPosition Unpack(ushort packed) => new((byte)(packed >> 8), (byte)(packed & 0xFF));

    public override string ToString() => $"{Bag}/{Slot}";
}

/// <summary>
/// Everything a player is carrying, and the rules for moving it about.
/// </summary>
/// <remarks>
/// Port of the parts of <c>PlayerStorage.cpp</c> that M6 needs. It owns the <see cref="Item"/>
/// objects and keeps the player's update fields in step with them; nothing else writes
/// <c>PLAYER_FIELD_INV_SLOT_HEAD</c> or the visible-item block.
/// <para>
/// <b>Not implemented:</b> the bank, the keyring, bag families, unique constraints, item limit
/// categories and soulbinding. Each is a separate refusal reason the client already knows how to
/// print, so they slot in without changing the shape here.
/// </para>
/// </remarks>
public sealed class Inventory(Player owner)
{
    private readonly Item?[] _slots = new Item?[InventorySlots.SlotCount];

    /// <summary>
    /// How many of a family of items may be held or worn.
    /// </summary>
    /// <remarks>
    /// Settable rather than required, because most of what an inventory does needs no DBC at all
    /// and a test that had to load a client to move an item between bags would not get written.
    /// With none set the category limits pass, which is what they did before this existed.
    /// </remarks>
    public DbcStore<ItemLimitCategoryEntry>? LimitCategories { get; set; }

    /// <summary>Every item this player holds anywhere, including inside bags.</summary>
    public IEnumerable<Item> All
    {
        get
        {
            foreach (Item? item in _slots)
            {
                if (item is null)
                {
                    continue;
                }

                yield return item;

                if (item is not Bag bag)
                {
                    continue;
                }

                for (byte slot = 0; slot < bag.SlotCount; slot++)
                {
                    if (Contents(bag, slot) is { } inside)
                    {
                        yield return inside;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Everything the player is carrying but not wearing — the backpack and the worn bags.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="All"/> in both directions, which is the point. It excludes the
    /// equipment slots, so a caller that walks the worn items and then this one does not visit them
    /// twice; and it excludes the bank, the keyring and the currency tokens, which are not on the
    /// player. <c>DurabilityLossAll</c>'s <c>inventory</c> pass is exactly this set.
    /// </remarks>
    public IEnumerable<Item> Carried
    {
        get
        {
            for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
            {
                if (_slots[slot] is { } item)
                {
                    yield return item;
                }
            }

            for (byte bagSlot = InventorySlots.BagStart; bagSlot < InventorySlots.BagEnd; bagSlot++)
            {
                if (_slots[bagSlot] is not Bag bag)
                {
                    continue;
                }

                for (byte inner = 0; inner < bag.SlotCount; inner++)
                {
                    if (Contents(bag, inner) is { } inside)
                    {
                        yield return inside;
                    }
                }
            }
        }
    }

    /// <summary>Every item and where it is, which is what persistence writes out.</summary>
    public IEnumerable<(ItemPosition Position, Item Item)> AllWithPositions
    {
        get
        {
            for (byte slot = 0; slot < InventorySlots.SlotCount; slot++)
            {
                if (_slots[slot] is not { } item)
                {
                    continue;
                }

                yield return (new ItemPosition(InventorySlots.Backpack, slot), item);

                if (item is not Bag bag)
                {
                    continue;
                }

                for (byte inner = 0; inner < bag.SlotCount; inner++)
                {
                    if (Contents(bag, inner) is { } inside)
                    {
                        yield return (new ItemPosition(slot, inner), inside);
                    }
                }
            }
        }
    }

    /// <summary>What is at a position, or null.</summary>
    public Item? Get(ItemPosition position)
    {
        if (position.IsOnThePlayer)
        {
            return position.Slot < InventorySlots.SlotCount ? _slots[position.Slot] : null;
        }

        if (!InventorySlots.IsBagSlot(position.Bag) || _slots[position.Bag] is not Bag bag)
        {
            return null;
        }

        return position.Slot < bag.SlotCount ? Contents(bag, position.Slot) : null;
    }

    /// <inheritdoc cref="Get(ItemPosition)"/>
    public Item? Get(byte bag, byte slot) => Get(new ItemPosition(bag, slot));

    /// <summary>What is worn in an equipment slot.</summary>
    public Item? Equipped(byte slot) =>
        InventorySlots.IsEquipment(slot) ? _slots[slot] : null;

    /// <summary>Where an item is, if this inventory holds it.</summary>
    public ItemPosition? PositionOf(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);

        foreach ((ItemPosition position, Item held) in AllWithPositions)
        {
            if (ReferenceEquals(held, item))
            {
                return position;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the player may hold this many more of something.
    /// </summary>
    /// <returns>The refusal, or null when there is no objection.</returns>
    /// <remarks>
    /// Port of <c>Player::CanTakeMoreSimilarItems</c>. Two independent caps:
    /// <list type="bullet">
    /// <item>
    /// The template's own <c>MaxCount</c> — how many of <i>this</i> item. Zero and negative both
    /// mean no limit, as does <c>int.MaxValue</c>; reading the column literally would make a
    /// <c>MaxCount</c> of 0 an item nobody may carry at all, which is most of them.
    /// </item>
    /// <item>
    /// A limit <i>category</i>, which is shared across different items — the mana gem family caps
    /// you at one however many kinds exist. Capping each item separately lets you carry one of each,
    /// which is exactly what the category is there to prevent.
    /// </item>
    /// </list>
    /// <para>
    /// <b>Only a "have" category refuses here.</b> An "equip" category is a limit on what you wear,
    /// not on what you carry, and refusing the pick-up would stop a player looting a second trinket
    /// they are entitled to own.
    /// </para>
    /// </remarks>
    public InventoryResult? CanTakeMoreSimilarItems(
        ItemTemplate template, uint count, Item? excluding = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (template.MaxCount > 0 && template.MaxCount != int.MaxValue
            && CountOf(template.Entry, excluding) + count > (uint)template.MaxCount)
        {
            return InventoryResult.CantCarryMoreOfThis;
        }

        if (template.ItemLimitCategory == 0 || LimitCategories is null)
        {
            return null;
        }

        if (!LimitCategories.TryGet((uint)template.ItemLimitCategory, out ItemLimitCategoryEntry? limit)
            || limit is null)
        {
            // A category the table does not describe. Upstream refuses rather than passing, and it
            // is the right direction: passing turns a data gap into an unlimited item.
            return InventoryResult.ItemCantBeEquipped;
        }

        if (limit.Mode != ItemLimitCategoryEntry.ModeHave)
        {
            return null;
        }

        return CountOfLimitCategory(template.ItemLimitCategory, excluding) + count > limit.MaxCount
            ? InventoryResult.CantCarryMoreOfThis
            : null;
    }

    /// <summary>
    /// Whether the player may <i>wear</i> another of this, which is a separate limit from holding it.
    /// </summary>
    /// <param name="exceptSlot">
    /// A slot to ignore, so that replacing a unique item with another of its kind is allowed —
    /// without it, swapping one unique ring for the same ring refuses because the one being taken
    /// off is still counted.
    /// </param>
    /// <remarks>
    /// Port of <c>Player::CanEquipUniqueItem</c>. The unique-equipped flag and the category limit
    /// are different mechanisms: the flag caps one specific item at one worn, the category caps a
    /// family at whatever the DBC says.
    /// </remarks>
    public InventoryResult? CanEquipUnique(ItemTemplate template, byte exceptSlot = InventorySlots.None)
    {
        ArgumentNullException.ThrowIfNull(template);

        if ((template.Flags & UniqueEquippableFlag) != 0
            && EquippedCountOfLimitCategoryFree(template, exceptSlot))
        {
            return InventoryResult.ItemUniqueEquippable;
        }

        if (template.ItemLimitCategory == 0 || LimitCategories is null)
        {
            return null;
        }

        if (!LimitCategories.TryGet((uint)template.ItemLimitCategory, out ItemLimitCategoryEntry? limit)
            || limit is null)
        {
            return InventoryResult.ItemCantBeEquipped;
        }

        // Both modes apply here — upstream's own note is that a "have" limit necessarily bounds what
        // can be worn too, since you cannot wear what you may not hold.
        return EquippedCountOfLimitCategory(template.ItemLimitCategory, exceptSlot) >= limit.MaxCount
            ? InventoryResult.ItemMaxLimitCategoryEquippedExceeded
            : null;
    }

    /// <summary>Whether another of this entry is already worn, ignoring one slot.</summary>
    private bool EquippedCountOfLimitCategoryFree(ItemTemplate template, byte exceptSlot)
    {
        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (slot != exceptSlot && _slots[slot] is { } worn && worn.Entry == template.Entry)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>ITEM_FLAG_UNIQUE_EQUIPPABLE</c> — only one may be worn.</summary>
    private const uint UniqueEquippableFlag = 0x00080000;

    /// <summary>How many items of a limit category the player holds, worn or carried.</summary>
    public uint CountOfLimitCategory(short category, Item? excluding = null)
    {
        if (category == 0)
        {
            return 0;
        }

        uint total = 0;

        foreach (Item item in All)
        {
            if (item.Template.ItemLimitCategory == category && !ReferenceEquals(item, excluding))
            {
                total += item.Count;
            }
        }

        return total;
    }

    /// <summary>How many of an entry are being <i>worn</i>, which is a different limit.</summary>
    public uint EquippedCountOf(uint entry)
    {
        uint total = 0;

        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (_slots[slot] is { } worn && worn.Entry == entry)
            {
                total++;
            }
        }

        return total;
    }

    /// <summary>How many of a limit category are being worn.</summary>
    public uint EquippedCountOfLimitCategory(short category, byte exceptSlot = InventorySlots.None)
    {
        if (category == 0)
        {
            return 0;
        }

        uint total = 0;

        for (byte slot = InventorySlots.EquipmentStart; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (slot != exceptSlot && _slots[slot] is { } worn
                && worn.Template.ItemLimitCategory == category)
            {
                total++;
            }
        }

        return total;
    }

    /// <summary>How many of an entry the player holds, across every stack.</summary>
    public uint CountOf(uint entry, Item? excluding = null)
    {
        uint total = 0;

        foreach (Item item in All)
        {
            if (item.Entry == entry && !ReferenceEquals(item, excluding))
            {
                total += item.Count;
            }
        }

        return total;
    }

    /// <summary>How many free slots there are to put something new in.</summary>
    public int FreeSlots
    {
        get
        {
            int free = 0;

            foreach (ItemPosition position in CarryPositions())
            {
                if (Get(position) is null)
                {
                    free++;
                }
            }

            return free;
        }
    }

    // ------------------------------------------------------------------ placing and removing

    /// <summary>
    /// Puts an item in a slot, replacing whatever was there without asking.
    /// </summary>
    /// <remarks>
    /// The unchecked primitive the rest of the class is built on. It keeps the item's own owner and
    /// container fields, the player's slot guid and — for an equipment slot — the visible-item block
    /// in step, which is the part that is easy to forget and shows up as an item that is held but
    /// not drawn.
    /// </remarks>
    public void Place(ItemPosition position, Item? item)
    {
        if (position.IsOnThePlayer)
        {
            if (position.Slot >= InventorySlots.SlotCount)
            {
                return;
            }

            _slots[position.Slot] = item;
            owner.Fields.SetGuid(SlotField(position.Slot), item?.Guid ?? ObjectGuid.Empty);

            if (item is not null)
            {
                item.Owner = owner.Guid;
                item.Container = owner.Guid;

                // Here rather than in the equip path, because everything that puts an item anywhere
                // comes through Place — looting, buying, a GM command. Binding only on equip would
                // leave a bind-on-pickup item tradeable for as long as it stayed in a bag.
                Bind(item, equipping: InventorySlots.IsEquipment(position.Slot));
            }

            if (InventorySlots.IsEquipment(position.Slot))
            {
                SetVisible(position.Slot, item);

                // After the slot is filled, not before: the recompute reads what is worn, and
                // running it first would price the swing on the item that just left.
                Combat.PlayerCombatStats.Apply(owner);
            }

            return;
        }

        if (_slots[position.Bag] is not Bag bag || position.Slot >= bag.SlotCount)
        {
            return;
        }

        bag.SetSlot(position.Slot, item?.Guid ?? ObjectGuid.Empty);
        SetContents(bag, position.Slot, item);

        if (item is not null)
        {
            item.Owner = owner.Guid;
            item.Container = bag.Guid;
        }
    }

    /// <summary>Takes whatever is at a position off the player and hands it back.</summary>
    public Item? Take(ItemPosition position)
    {
        Item? item = Get(position);

        if (item is null)
        {
            return null;
        }

        Place(position, null);

        item.Owner = ObjectGuid.Empty;
        item.Container = ObjectGuid.Empty;

        return item;
    }

    // ------------------------------------------------------------------ storing

    /// <summary>
    /// Works out where a new stack would go, without moving anything.
    /// </summary>
    /// <remarks>
    /// Port of the shape of <c>CanStoreItem</c>. Existing partial stacks are filled before a free
    /// slot is taken, because a player with sixteen full slots and a half stack of cloth can still
    /// pick up cloth — refusing there is the difference between a full bag and an apparently broken
    /// one.
    /// <para>
    /// <b>Upstream tries specialised bags first</b> — a quiver before the backpack for arrows. Bag
    /// families are not modelled here, so everything is treated as a general bag and the backpack
    /// comes first. The result differs only in which slot something lands in.
    /// </para>
    /// </remarks>
    /// <param name="destinations">Where the count would go, split across stacks. Empty on failure.</param>
    public InventoryResult CanStore(
        ItemTemplate template,
        uint count,
        out IReadOnlyList<(ItemPosition Position, uint Count)> destinations,
        Item? moving = null,
        ItemPosition? vacating = null) =>
        Plan(template, count, CarryPositions(), out destinations, moving, vacating);

    /// <summary>
    /// Where a stack would go in the <i>bank</i>, and whether it fits.
    /// </summary>
    /// <remarks>
    /// The same planner over a different set of slots. Written as a second copy of the stacking
    /// logic it would drift the first time one of them learned something the other did not — and
    /// the bank is exactly where a player would notice a stack that failed to merge.
    /// </remarks>
    public InventoryResult CanBank(
        ItemTemplate template,
        uint count,
        out IReadOnlyList<(ItemPosition Position, uint Count)> destinations,
        Item? moving = null,
        ItemPosition? vacating = null) =>
        Plan(template, count, BankPositions(), out destinations, moving, vacating);

    /// <summary>
    /// Works out which slots a stack would land in, given somewhere to look.
    /// </summary>
    /// <remarks>
    /// <b>Partial stacks before empty slots.</b> Filling an empty slot first leaves two half stacks
    /// of the same thing and burns a slot for nothing, which is how a bag ends up full of
    /// twelve-of-twenty stacks.
    /// </remarks>
    /// <param name="moving">
    /// An item already held that is being moved rather than acquired, so it is not counted against
    /// its own limit — otherwise putting your one unique trinket into the bank is refused on the
    /// grounds that you already have one.
    /// </param>
    /// <param name="vacating">
    /// A slot that is about to be emptied, and so counts as free. Without it a move within the same
    /// region cannot pick the slot the item is already in, and something banked twice walks along
    /// the bank one slot at a time instead of staying where it is.
    /// </param>
    private InventoryResult Plan(
        ItemTemplate template,
        uint count,
        IEnumerable<ItemPosition> positions,
        out IReadOnlyList<(ItemPosition Position, uint Count)> destinations,
        Item? moving = null,
        ItemPosition? vacating = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        List<(ItemPosition, uint)> plan = [];
        destinations = plan;

        if (count == 0)
        {
            return InventoryResult.Ok;
        }

        if (CanTakeMoreSimilarItems(template, count, moving) is { } tooMany)
        {
            return tooMany;
        }

        uint remaining = count;

        // Materialised because it is walked twice, and the bank sequence reads the slot array as it
        // goes — enumerating it a second time after the first pass is fine, but only because
        // nothing has moved in between.
        List<ItemPosition> candidates = [.. positions];

        if (template.MaxStackSize > 1)
        {
            foreach (ItemPosition position in candidates)
            {
                if (position == vacating || Get(position) is not { } held
                    || held.Entry != template.Entry)
                {
                    continue;
                }

                uint room = held.FreeStackSpace;

                if (room == 0)
                {
                    continue;
                }

                uint taken = Math.Min(room, remaining);

                plan.Add((position, taken));
                remaining -= taken;

                if (remaining == 0)
                {
                    return InventoryResult.Ok;
                }
            }
        }

        foreach (ItemPosition position in candidates)
        {
            if (position != vacating && Get(position) is not null)
            {
                continue;
            }

            uint taken = Math.Min(template.MaxStackSize, remaining);

            plan.Add((position, taken));
            remaining -= taken;

            if (remaining == 0)
            {
                return InventoryResult.Ok;
            }
        }

        plan.Clear();

        return InventoryResult.InventoryFull;
    }

    /// <summary>
    /// Every slot the bank can hold something in.
    /// </summary>
    /// <remarks>
    /// The twenty-eight built-in slots, then whatever the bought bank bags add. <b>Only bags in
    /// slots the character has actually paid for</b> — the field is the client's own count, and
    /// walking all seven regardless would let a player use bank bags they never bought.
    /// </remarks>
    private IEnumerable<ItemPosition> BankPositions()
    {
        for (byte slot = InventorySlots.BankItemStart; slot < InventorySlots.BankItemEnd; slot++)
        {
            yield return new ItemPosition(InventorySlots.Backpack, slot);
        }

        byte bought = owner.BankBagSlots;

        for (byte index = 0; index < bought; index++)
        {
            byte bagSlot = (byte)(InventorySlots.BankBagStart + index);

            if (bagSlot >= InventorySlots.BankBagEnd || _slots[bagSlot] is not Bag bag)
            {
                continue;
            }

            for (byte inner = 0; inner < bag.SlotCount; inner++)
            {
                yield return new ItemPosition(bagSlot, inner);
            }
        }
    }

    /// <summary>
    /// Puts a new stack in, merging into partial stacks where it can.
    /// </summary>
    /// <returns>Every item the count ended up in — an existing stack that grew, or a new one.</returns>
    /// <summary>
    /// Moves something between the bags and the bank.
    /// </summary>
    /// <param name="toBank">Which way. The same routine serves both, so they cannot disagree.</param>
    /// <remarks>
    /// The item is taken out of its slot only after somewhere has been found for it. Removing first
    /// and then discovering the destination is full loses the item entirely — and it is the obvious
    /// order to write, because it reads as "pick it up, then put it down".
    /// <para>
    /// It merges into partial stacks on the way, which is what makes moving twenty of something into
    /// a bank that already holds five behave like the client's own drag-and-drop.
    /// </para>
    /// </remarks>
    public InventoryResult Move(ItemPosition from, bool toBank)
    {
        if (Get(from) is not { } item)
        {
            return InventoryResult.SlotIsEmpty;
        }

        InventoryResult planned = toBank
            ? CanBank(
                item.Template, item.Count,
                out IReadOnlyList<(ItemPosition Position, uint Count)> plan, item, from)
            : CanStore(item.Template, item.Count, out plan, item, from);

        if (planned != InventoryResult.Ok)
        {
            return planned;
        }

        // Already where it is going, and the plan says to put it back in its own slot. Upstream
        // answers this with a no-op rather than an error, since the client sends it on a double
        // click of something already banked.
        if (plan.Count == 1 && plan[0].Position == from)
        {
            return InventoryResult.Ok;
        }

        Place(from, null);

        foreach ((ItemPosition position, uint amount) in plan)
        {
            if (Get(position) is { } existing)
            {
                existing.Count += amount;
                continue;
            }

            item.Count = amount;
            Place(position, item);
        }

        return InventoryResult.Ok;
    }

    public InventoryResult Store(
        ItemTemplate template, uint count, Func<uint> nextGuidCounter, out IReadOnlyList<Item> affected)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(nextGuidCounter);

        List<Item> touched = [];
        affected = touched;

        InventoryResult result = CanStore(
            template, count, out IReadOnlyList<(ItemPosition Position, uint Count)> plan);

        if (result != InventoryResult.Ok)
        {
            return result;
        }

        foreach ((ItemPosition position, uint amount) in plan)
        {
            if (Get(position) is { } existing)
            {
                existing.Count += amount;
                touched.Add(existing);

                continue;
            }

            Item created = Item.Create(nextGuidCounter(), template, owner.Guid);
            created.Count = amount;

            Place(position, created);
            touched.Add(created);
        }

        return InventoryResult.Ok;
    }

    /// <summary>
    /// Puts a new item wherever it best belongs: worn if it can be, carried otherwise.
    /// </summary>
    /// <remarks>
    /// Port of <c>StoreNewItemInBestSlots</c>, which is how a new character is dressed. Equipping is
    /// tried <b>one at a time</b> — a stack of five identical shirts equips one and carries four,
    /// rather than the whole stack failing because the second has nowhere to go.
    /// </remarks>
    /// <returns>False when neither equipping nor carrying worked, so the item is simply lost.</returns>
    public bool StoreInBestSlots(
        ItemTemplate template, uint count, Func<uint> nextGuidCounter, out IReadOnlyList<Item> affected)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(nextGuidCounter);

        List<Item> touched = [];
        affected = touched;

        uint remaining = count;

        while (remaining > 0)
        {
            byte slot = FindEquipSlot(template);

            if (slot == InventorySlots.None)
            {
                break;
            }

            Item worn = Item.Create(nextGuidCounter(), template, owner.Guid);

            if (CanEquip(worn, slot) != InventoryResult.Ok)
            {
                break;
            }

            Place(new ItemPosition(InventorySlots.Backpack, slot), worn);
            touched.Add(worn);
            remaining--;
        }

        if (remaining == 0)
        {
            return true;
        }

        InventoryResult stored = Store(template, remaining, nextGuidCounter, out IReadOnlyList<Item> carried);

        foreach (Item item in carried)
        {
            touched.Add(item);
        }

        return stored == InventoryResult.Ok;
    }

    /// <summary>
    /// Puts an item that already exists back where it was, without any of the checks.
    /// </summary>
    /// <remarks>
    /// For loading a saved inventory, and nothing else. The rules were applied when the item was
    /// first placed; re-running them on load would silently rearrange a character's bags because
    /// something has since changed — a level requirement, a dual-wield spell — and the player would
    /// log in to find their weapon in the backpack.
    /// </remarks>
    public void Restore(ItemPosition position, Item item)
    {
        ArgumentNullException.ThrowIfNull(item);

        Place(position, item);
    }

    // ------------------------------------------------------------------ equipping

    /// <summary>
    /// Which equipment slot an item wants, or <see cref="InventorySlots.None"/>.
    /// </summary>
    /// <remarks>
    /// Port of <c>FindEquipSlot</c>. Several inventory types have two candidates — a ring, a
    /// trinket, a one-handed weapon for someone who can dual wield — and an empty one is preferred
    /// over swapping into a full one.
    /// <para>
    /// <b>An empty off-hand is not necessarily a free one:</b> a two-handed weapon leaves it empty
    /// and occupied at the same time, which is why <see cref="IsTwoHandUsed"/> is consulted rather
    /// than just the slot's contents.
    /// </para>
    /// </remarks>
    public byte FindEquipSlot(ItemTemplate template, bool swap = false)
    {
        ArgumentNullException.ThrowIfNull(template);

        Span<byte> candidates = stackalloc byte[4];
        candidates.Fill(InventorySlots.None);

        switch (template.InventoryType)
        {
            case InventoryType.Head: candidates[0] = InventorySlots.Head; break;
            case InventoryType.Neck: candidates[0] = InventorySlots.Neck; break;
            case InventoryType.Shoulders: candidates[0] = InventorySlots.Shoulders; break;
            case InventoryType.Body: candidates[0] = InventorySlots.Body; break;

            // A robe and a chestpiece go in the same slot; the two types exist only so the client
            // knows whether to draw the model long.
            case InventoryType.Chest:
            case InventoryType.Robe:
                candidates[0] = InventorySlots.Chest;
                break;

            case InventoryType.Waist: candidates[0] = InventorySlots.Waist; break;
            case InventoryType.Legs: candidates[0] = InventorySlots.Legs; break;
            case InventoryType.Feet: candidates[0] = InventorySlots.Feet; break;
            case InventoryType.Wrists: candidates[0] = InventorySlots.Wrists; break;
            case InventoryType.Hands: candidates[0] = InventorySlots.Hands; break;

            case InventoryType.Finger:
                candidates[0] = InventorySlots.Finger1;
                candidates[1] = InventorySlots.Finger2;
                break;

            case InventoryType.Trinket:
                candidates[0] = InventorySlots.Trinket1;
                candidates[1] = InventorySlots.Trinket2;
                break;

            case InventoryType.Cloak: candidates[0] = InventorySlots.Back; break;

            case InventoryType.Weapon:
                candidates[0] = InventorySlots.MainHand;

                if (owner.CanDualWield)
                {
                    candidates[1] = InventorySlots.OffHand;
                }

                break;

            case InventoryType.Shield:
            case InventoryType.WeaponOffHand:
            case InventoryType.Holdable:
                candidates[0] = InventorySlots.OffHand;
                break;

            case InventoryType.Ranged:
            case InventoryType.RangedRight:
            case InventoryType.Thrown:
                candidates[0] = InventorySlots.Ranged;
                break;

            // Titan's Grip would add an off-hand candidate here. It is a talent, and there are no
            // talents, so a two-hander only ever goes in the main hand.
            case InventoryType.TwoHandWeapon:
                candidates[0] = InventorySlots.MainHand;
                break;

            case InventoryType.Tabard: candidates[0] = InventorySlots.Tabard; break;
            case InventoryType.WeaponMainHand: candidates[0] = InventorySlots.MainHand; break;

            case InventoryType.Bag:
                for (byte i = 0; i < 4; i++)
                {
                    candidates[i] = (byte)(InventorySlots.BagStart + i);
                }

                break;

            default:
                return InventorySlots.None;
        }

        foreach (byte candidate in candidates)
        {
            if (candidate == InventorySlots.None || _slots[candidate] is not null)
            {
                continue;
            }

            if (candidate == InventorySlots.OffHand && IsTwoHandUsed)
            {
                continue;
            }

            return candidate;
        }

        if (!swap)
        {
            return InventorySlots.None;
        }

        foreach (byte candidate in candidates)
        {
            if (candidate != InventorySlots.None)
            {
                return candidate;
            }
        }

        return InventorySlots.None;
    }

    /// <summary>Whether the main hand holds something that also occupies the off hand.</summary>
    public bool IsTwoHandUsed =>
        _slots[InventorySlots.MainHand]?.Template.InventoryType == InventoryType.TwoHandWeapon;

    /// <summary>
    /// Whether the character is trained for an item at all, independent of which slot it wants.
    /// </summary>
    /// <returns>The refusal, or null when nothing objects.</returns>
    /// <remarks>
    /// Port of <c>Player::CanUseItem</c>'s skill and spell checks. Three separate rules that all
    /// look like "do you know how to use this":
    /// <list type="bullet">
    /// <item>
    /// <b>The proficiency</b> — the skill implied by the item's own class and subclass. It is a
    /// mono skill sitting at 1/1, so the test is only whether it is there at all; a value of zero
    /// means the character was never taught to hold this kind of thing.
    /// </item>
    /// <item>
    /// <b>The template's own required skill</b>, which is a different question and a different
    /// answer. Lacking the skill entirely is <see cref="InventoryResult.NoRequiredProficiency"/>;
    /// having it but not far enough along is <see cref="InventoryResult.CantEquipSkill"/> — the
    /// client shows different text for each, and collapsing them tells a jewelcrafter they cannot
    /// use a ring they simply need more practice for.
    /// </item>
    /// <item><b>A required spell</b>, which is how the riding skills and a few recipes gate.</item>
    /// </list>
    /// </remarks>
    /// <summary>
    /// Binds an item to its holder, if its bonding rule says being here does that.
    /// </summary>
    /// <param name="equipping">
    /// Whether it is going onto the body rather than into a bag. Bind-on-equip turns on exactly
    /// this — the same item in a bag stays tradeable, which is the whole point of the category.
    /// </param>
    /// <remarks>
    /// Port of the binding in <c>Player::StoreItem</c> and <c>VisualizeItem</c>. Bind-on-pickup and
    /// quest items bind wherever they land; bind-on-equip binds only when worn.
    /// </remarks>
    private static void Bind(Item item, bool equipping)
    {
        bool binds = item.Template.Bonding switch
        {
            ItemBonding.OnPickup or ItemBonding.QuestItem or ItemBonding.QuestItemUnused => true,
            ItemBonding.OnEquip => equipping,
            _ => false,
        };

        if (binds)
        {
            item.IsSoulBound = true;
        }
    }

    private InventoryResult? CanUse(ItemTemplate template)
    {
        if (!AllowsClass(template, owner.Class) || !AllowsRace(template, owner.Race))
        {
            return InventoryResult.YouCanNeverUseThatItem;
        }

        if (template.RequiredSkill != 0)
        {
            if (owner.Skills.Value(template.RequiredSkill) == 0)
            {
                return InventoryResult.NoRequiredProficiency;
            }

            if (owner.Skills.Value(template.RequiredSkill) < template.RequiredSkillRank)
            {
                return InventoryResult.CantEquipSkill;
            }
        }

        if (template.RequiredSpell != 0 && !owner.Spells.Knows(template.RequiredSpell))
        {
            return InventoryResult.NoRequiredProficiency;
        }

        // After the skill and spell checks, not before: upstream's CanUseItem reaches the level last
        // of the three, so an item that fails both reports the skill rather than the level.
        if (template.RequiredLevel > owner.Level)
        {
            return InventoryResult.CantEquipLevel;
        }

        uint proficiency = SkillType.ForItem(template.Class, template.SubClass);

        if (proficiency != 0 && owner.Skills.Value(proficiency) == 0 && !MorphsForThisClass(template, proficiency))
        {
            return InventoryResult.NoRequiredProficiency;
        }

        return null;
    }

    /// <summary>
    /// Whether a heirloom's armour type bends to fit a character who has not learned it yet.
    /// </summary>
    /// <remarks>
    /// Heirloom armour changes type as its wearer levels — a warrior's shoulders are mail until 40
    /// and plate after. The item's own subclass says plate the whole time, so the plain proficiency
    /// check would refuse it to the level-1 warrior it was bought for. Upstream allows the two
    /// classes whose armour genuinely upgrades, and only for the type they upgrade <i>into</i>.
    /// </remarks>
    private bool MorphsForThisClass(ItemTemplate template, uint proficiency)
    {
        if (template.Quality != ItemQuality.Heirloom || template.Class != ItemClass.Armor)
        {
            return false;
        }

        return owner.Class switch
        {
            ClassWarrior or ClassPaladin => proficiency == SkillType.PlateMail,
            ClassHunter or ClassShaman => proficiency == SkillType.Mail,
            _ => false,
        };
    }

    private const byte ClassWarrior = 1;
    private const byte ClassPaladin = 2;
    private const byte ClassHunter = 3;
    private const byte ClassShaman = 7;

    /// <summary>
    /// Whether an item may be worn in a slot, and why not.
    /// </summary>
    /// <remarks>
    /// Port of the checks in <c>CanEquipItem</c> and <c>CanUseItem</c> that this phase can answer.
    /// Unique constraints and item limit categories still need a limit-category store and pass
    /// silently; proficiency no longer does.
    /// </remarks>
    public InventoryResult CanEquip(Item item, byte slot)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!owner.IsAlive)
        {
            return InventoryResult.YouAreDead;
        }

        if (!InventorySlots.IsEquipment(slot) && !InventorySlots.IsBagSlot(slot))
        {
            return InventoryResult.ItemDoesNotGoToSlot;
        }

        ItemTemplate template = item.Template;

        // The slot the item actually wants, allowing a swap: the caller has named a slot, and this
        // is what says whether that slot is one of the item's candidates at all.
        //
        // Before the usability checks, matching upstream: CanEquipItem resolves the slot with
        // FindEquipSlot and only then calls CanUseItem. Dragging a sword onto the head slot is a
        // wrong-slot mistake whether or not the character could ever swing it, and answering
        // "you lack the proficiency" there sends the player off to a trainer for nothing.
        if (!WantsSlot(template, slot))
        {
            return InventoryResult.ItemDoesNotGoToSlot;
        }

        if (CanUse(template) is { } refusal)
        {
            return refusal;
        }

        // Bound to someone else. Cannot happen through the normal flow — an item reaches a player's
        // bags already owned by them — but it is the check that makes trading and mail safe to build
        // on top of this rather than around it.
        if (item.IsBoundToSomeoneElse(owner))
        {
            return InventoryResult.DontOwnThatItem;
        }

        // The slot being filled is excluded, so replacing a unique item with another of its kind is
        // allowed — without that, swapping one unique ring for the same ring refuses because the one
        // coming off is still counted.
        if (CanEquipUnique(template, exceptSlot: slot) is { } unique)
        {
            return unique;
        }

        if (slot == InventorySlots.OffHand)
        {
            if (IsTwoHandUsed)
            {
                return InventoryResult.CantEquipWithTwoHanded;
            }

            bool isWeapon = template.InventoryType
                is InventoryType.Weapon or InventoryType.WeaponOffHand;

            if (isWeapon && !owner.CanDualWield)
            {
                return InventoryResult.CantDualWield;
            }
        }

        // Taking a bag off has to leave somewhere for what was inside it.
        if (InventorySlots.IsBagSlot(slot) && _slots[slot] is Bag worn && !worn.IsEmpty)
        {
            return InventoryResult.NonEmptyBagOverOtherBag;
        }

        return InventoryResult.Ok;
    }

    /// <summary>
    /// Wears an item, putting whatever it displaces where the item came from.
    /// </summary>
    /// <remarks>
    /// A two-hander going into the main hand also clears the off hand, and what was in the off hand
    /// has to go somewhere — if there is nowhere, the whole thing is refused rather than half done.
    /// </remarks>
    public InventoryResult Equip(ItemPosition from, byte slot)
    {
        if (Get(from) is not { } item)
        {
            return InventoryResult.ItemNotFound;
        }

        InventoryResult allowed = CanEquip(item, slot);

        if (allowed != InventoryResult.Ok)
        {
            return allowed;
        }

        Item? displacedOffHand = null;

        if (item.Template.InventoryType == InventoryType.TwoHandWeapon
            && slot == InventorySlots.MainHand
            && _slots[InventorySlots.OffHand] is { } inOffHand)
        {
            // The off-hand item needs a slot of its own, and the one being vacated may be the only
            // free one — so it is only taken out once somewhere is known to exist.
            if (FirstFreeCarryPosition(except: from) is not { } spare)
            {
                return InventoryResult.InventoryFull;
            }

            displacedOffHand = inOffHand;
            Place(new ItemPosition(InventorySlots.Backpack, InventorySlots.OffHand), null);
            Place(spare, displacedOffHand);
        }

        Item? displaced = _slots[slot];

        Place(from, displaced);
        Place(new ItemPosition(InventorySlots.Backpack, slot), item);

        return InventoryResult.Ok;
    }

    /// <summary>
    /// Swaps two positions, or moves one into an empty one.
    /// </summary>
    /// <remarks>
    /// Equipment slots go through <see cref="CanEquip"/> on the way in, so dragging a staff onto the
    /// ring slot is refused with the client's own message rather than quietly working.
    /// </remarks>
    public InventoryResult Swap(ItemPosition first, ItemPosition second)
    {
        if (first == second)
        {
            return InventoryResult.ItemsCantBeSwapped;
        }

        Item? a = Get(first);
        Item? b = Get(second);

        if (a is null && b is null)
        {
            return InventoryResult.SlotIsEmpty;
        }

        if (a is not null && DestinationRefusal(a, second) is { } refusedA)
        {
            return refusedA;
        }

        if (b is not null && DestinationRefusal(b, first) is { } refusedB)
        {
            return refusedB;
        }

        Place(first, b);
        Place(second, a);

        return InventoryResult.Ok;
    }

    /// <summary>
    /// Splits a stack, moving part of it somewhere else.
    /// </summary>
    /// <remarks>
    /// The destination must be empty or the same entry with room. Splitting onto a different item
    /// is refused rather than swapping, which is what the client expects — it draws the drag
    /// differently for the two.
    /// </remarks>
    public InventoryResult Split(ItemPosition from, ItemPosition to, uint count, Func<uint> nextGuidCounter)
    {
        ArgumentNullException.ThrowIfNull(nextGuidCounter);

        if (Get(from) is not { } source)
        {
            return InventoryResult.ItemNotFound;
        }

        if (count == 0 || count >= source.Count)
        {
            return InventoryResult.TriedToSplitMoreThanCount;
        }

        Item? destination = Get(to);

        if (destination is null)
        {
            if (DestinationRefusal(source, to) is { } refused)
            {
                return refused;
            }

            Item created = Item.Create(nextGuidCounter(), source.Template, owner.Guid);
            created.Count = count;

            Place(to, created);
            source.Count -= count;

            return InventoryResult.Ok;
        }

        if (destination.Entry != source.Entry || destination.FreeStackSpace < count)
        {
            return InventoryResult.CouldNotSplitItems;
        }

        destination.Count += count;
        source.Count -= count;

        return InventoryResult.Ok;
    }

    /// <summary>
    /// Destroys some or all of a stack.
    /// </summary>
    /// <remarks>
    /// A bag with anything in it is refused: destroying it would strand its contents, which have
    /// their own guids and would otherwise be leaked.
    /// </remarks>
    public InventoryResult Destroy(ItemPosition position, uint count, out Item? removed)
    {
        removed = null;

        if (Get(position) is not { } item)
        {
            return InventoryResult.ItemNotFound;
        }

        if (item is Bag bag && !bag.IsEmpty)
        {
            return InventoryResult.CanOnlyDoWithEmptyBags;
        }

        // Zero means the whole stack, which is what the client sends when it has not asked how many.
        if (count == 0 || count >= item.Count)
        {
            removed = Take(position);

            return InventoryResult.Ok;
        }

        item.Count -= count;

        return InventoryResult.Ok;
    }

    // ------------------------------------------------------------------ internals

    /// <summary>What is inside a bag, resolved from the guid the bag's own field holds.</summary>
    /// <remarks>
    /// A bag's contents are stored beside it rather than in the bag object, because the object's
    /// fields carry guids and the game layer needs the items themselves. Keeping the two in step is
    /// <see cref="Place"/>'s job.
    /// </remarks>
    private readonly Dictionary<(ObjectGuid Bag, byte Slot), Item> _inBags = [];

    private Item? Contents(Bag bag, byte slot) =>
        _inBags.GetValueOrDefault((bag.Guid, slot));

    private void SetContents(Bag bag, byte slot, Item? item)
    {
        if (item is null)
        {
            _inBags.Remove((bag.Guid, slot));

            return;
        }

        _inBags[(bag.Guid, slot)] = item;
    }

    /// <summary>Every position a player can carry something in, in the order they are filled.</summary>
    /// <remarks>
    /// The backpack before the bags, which is upstream's order for a general bag. Equipment slots
    /// are not here: something is put in one deliberately, never because it was the next free space.
    /// </remarks>
    private IEnumerable<ItemPosition> CarryPositions()
    {
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            yield return new ItemPosition(InventorySlots.Backpack, slot);
        }

        for (byte bagSlot = InventorySlots.BagStart; bagSlot < InventorySlots.BagEnd; bagSlot++)
        {
            if (_slots[bagSlot] is not Bag bag)
            {
                continue;
            }

            for (byte inner = 0; inner < bag.SlotCount; inner++)
            {
                yield return new ItemPosition(bagSlot, inner);
            }
        }
    }

    private ItemPosition? FirstFreeCarryPosition(ItemPosition? except = null)
    {
        foreach (ItemPosition position in CarryPositions())
        {
            if (position != except && Get(position) is null)
            {
                return position;
            }
        }

        return null;
    }

    /// <summary>Why an item may not go to a position, or null if it may.</summary>
    private InventoryResult? DestinationRefusal(Item item, ItemPosition destination)
    {
        if (destination.IsOnThePlayer
            && (InventorySlots.IsEquipment(destination.Slot) || InventorySlots.IsBagSlot(destination.Slot)))
        {
            InventoryResult allowed = CanEquip(item, destination.Slot);

            return allowed == InventoryResult.Ok ? null : allowed;
        }

        if (!destination.IsOnThePlayer)
        {
            // Bags do not nest. The client offers the drag and the server is what refuses it.
            if (item is Bag)
            {
                return InventoryResult.ItemDoesNotGoIntoBag;
            }

            return _slots[destination.Bag] is Bag ? null : InventoryResult.NotABag;
        }

        return InventorySlots.IsBackpackSlot(destination.Slot) ? null : InventoryResult.ItemDoesNotGoToSlot;
    }

    /// <summary>Whether a slot is one of the ones an item's inventory type can go in.</summary>
    private bool WantsSlot(ItemTemplate template, byte slot)
    {
        // Asked with swap, because the slot may well be occupied — that is what a swap is.
        byte free = FindEquipSlot(template, swap: true);

        if (free == slot)
        {
            return true;
        }

        // The single-candidate answer above is the *preferred* slot. A second candidate — the other
        // ring finger, the other trinket, another bag slot — is equally valid and is what the player
        // has actually dragged onto.
        return template.InventoryType switch
        {
            InventoryType.Finger => slot is InventorySlots.Finger1 or InventorySlots.Finger2,
            InventoryType.Trinket => slot is InventorySlots.Trinket1 or InventorySlots.Trinket2,
            InventoryType.Weapon => slot == InventorySlots.MainHand
                || (slot == InventorySlots.OffHand && owner.CanDualWield),
            InventoryType.Bag => InventorySlots.IsBagSlot(slot),
            _ => false,
        };
    }

    private static bool AllowsClass(ItemTemplate template, byte playerClass) =>
        template.AllowableClass == -1 || (template.AllowableClass & (1 << (playerClass - 1))) != 0;

    private static bool AllowsRace(ItemTemplate template, byte playerRace) =>
        template.AllowableRace == -1 || (template.AllowableRace & (1 << (playerRace - 1))) != 0;

    /// <summary>
    /// The one flat guid array, whatever the field names suggest.
    /// </summary>
    /// <remarks>
    /// <c>PLAYER_FIELD_INV_SLOT_HEAD</c>, <c>PACK_SLOT_1</c>, <c>BANK_SLOT_1</c> and the rest are
    /// consecutive names for consecutive stretches of the same 150-guid run. Adding the range's own
    /// base to a range-relative slot double-counts the offset.
    /// </remarks>
    private static int SlotField(byte slot) => UpdateFields.PLAYER_FIELD_INV_SLOT_HEAD + (slot * 2);

    /// <summary>
    /// What other players see on this one.
    /// </summary>
    /// <remarks>
    /// Port of <c>SetVisibleItemSlot</c>. Only equipment has a visible block — sixteen carried items
    /// are the player's business. The enchantment word is written as zero rather than skipped, so
    /// unequipping actually clears the previous item's enchant.
    /// </remarks>
    private void SetVisible(byte slot, Item? item)
    {
        int entryField = UpdateFields.PLAYER_VISIBLE_ITEM_1_ENTRYID + (slot * 2);

        owner.Fields.SetUInt32(entryField, item?.Entry ?? 0);
        owner.Fields.SetUInt32(entryField + 1, 0);
    }
}
