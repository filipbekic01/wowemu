using WowEmu.Core;
using WowEmu.Data.Db;

namespace WowEmu.Game;

/// <summary>
/// Bits of <c>UNIT_DYNAMIC_FLAGS</c>. <c>UnitDynFlags</c>.
/// </summary>
/// <remarks>
/// <see cref="Lootable"/> is what makes a corpse sparkle and accept a right click. Without it the
/// client refuses to send <c>CMSG_LOOT</c> at all, so the loot exists and is unreachable.
/// </remarks>
public static class UnitDynamicFlags
{
    public const uint Lootable = 0x0001;
    public const uint TrackUnit = 0x0002;
    public const uint Tapped = 0x0004;
    public const uint Rooted = 0x0008;
    public const uint SpecialInfo = 0x0010;
    public const uint Dead = 0x0020;
    public const uint ReferAFriend = 0x0040;
    public const uint TappedByAllThreatList = 0x0080;
}

/// <summary>What kind of loot window this is. <c>LootType</c>.</summary>
/// <remarks>
/// The client draws each differently — a corpse gets a skull, a chest a lid — and the type is also
/// what tells it whether releasing the window should close the corpse.
/// </remarks>
public static class LootType
{
    public const byte None = 0;
    public const byte Corpse = 1;
    public const byte Pickpocketing = 2;
    public const byte Fishing = 3;
    public const byte Disenchanting = 4;
    public const byte Skinning = 6;
    public const byte Prospecting = 7;
    public const byte Milling = 8;
}

/// <summary>Why a loot window could not be opened. <c>LootError</c>.</summary>
public enum LootError : byte
{
    DidNotKill = 0,
    TooFar = 4,
    BadFacing = 5,
    Locked = 6,
    NotStanding = 8,
    Stunned = 9,
    PlayerNotFound = 10,
    PlayerTimeout = 11,
    NoLoot = 12,

    /// <summary>"Your target has already had its pockets picked."</summary>
    AlreadyPickpocketed = 15,
}

/// <summary>
/// How a slot appears in the loot window. <c>LootSlotType</c>.
/// </summary>
/// <remarks>
/// Everything is <see cref="AllowLoot"/> until groups exist. The others describe rolls and master
/// looting, both of which need a party.
/// </remarks>
public static class LootSlotType
{
    public const byte AllowLoot = 0;
    public const byte RollOngoing = 1;
    public const byte Master = 2;
    public const byte Locked = 3;
    public const byte Owner = 4;
}

/// <summary>One thing in a loot window.</summary>
/// <param name="Index">
/// Its slot in the window. <b>Stable for the window's lifetime</b> — the client sends this number
/// back, so taking an item must not renumber the ones after it.
/// </param>
public sealed record LootItem(byte Index, uint ItemId, uint Count, uint DisplayId, bool NeedsQuest)
{
    /// <summary>Whether it has already been taken.</summary>
    public bool IsLooted { get; set; }
}

/// <summary>
/// What one corpse is holding.
/// </summary>
/// <remarks>
/// Port of the parts of <c>Loot</c> that M6 needs: money, items, and who is allowed at it. No
/// group rules, no round-robin, no rolls, no master looter, no free-for-all — all of which need
/// parties, which do not exist.
/// </remarks>
public sealed class Loot
{
    /// <summary>The client's own ceiling on loot slots. <c>MAX_NR_LOOT_ITEMS</c>.</summary>
    public const int MaxItems = 16;

    private readonly List<LootItem> _items = [];

    public IReadOnlyList<LootItem> Items => _items;

    /// <summary>Copper. Zero once taken.</summary>
    public uint Gold { get; set; }

    /// <summary>Who is allowed to open it. Empty means nobody has claimed the kill.</summary>
    public ObjectGuid Owner { get; set; }

    /// <summary>Whether anything is left.</summary>
    public bool IsEmpty
    {
        get
        {
            if (Gold > 0)
            {
                return false;
            }

            foreach (LootItem item in _items)
            {
                if (!item.IsLooted)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>How many slots the window shows, taken ones included.</summary>
    public int SlotCount => _items.Count;

    /// <summary>What is in one slot, taken or not.</summary>
    public LootItem? At(byte index) => index < _items.Count ? _items[index] : null;

    /// <summary>
    /// Adds a stack, splitting it across slots if it exceeds the item's stack size.
    /// </summary>
    /// <remarks>
    /// Port of <c>Loot::AddItem</c>. A drop of 30 linen becomes two slots of 20 and 10, because a
    /// loot slot holds one stack and the client draws one icon per slot. Handing over 30 in one
    /// slot produces a stack larger than the item allows the moment it is picked up.
    /// </remarks>
    public void Add(ItemTemplate template, uint count, bool needsQuest = false)
    {
        ArgumentNullException.ThrowIfNull(template);

        uint remaining = count;
        uint stackSize = Math.Max(template.MaxStackSize, 1);

        while (remaining > 0 && _items.Count < MaxItems)
        {
            uint inThisSlot = Math.Min(remaining, stackSize);

            _items.Add(new LootItem(
                Index: (byte)_items.Count,
                ItemId: template.Entry,
                Count: inThisSlot,
                DisplayId: template.DisplayId,
                NeedsQuest: needsQuest));

            remaining -= inThisSlot;
        }
    }

    /// <summary>Marks a slot taken. The slot stays, so the indices after it do not move.</summary>
    public bool Take(byte index)
    {
        LootItem? item = At(index);

        if (item is null || item.IsLooted)
        {
            return false;
        }

        item.IsLooted = true;

        return true;
    }
}

/// <summary>
/// Rolls a loot template into an actual pile.
/// </summary>
/// <remarks>
/// Port of <c>LootTemplate::Process</c> and <c>Loot::generateMoneyLoot</c>. The drop rates upstream
/// applies per quality are all 1.0 in a default configuration and are not modelled.
/// </remarks>
public static class LootRoll
{
    /// <summary>
    /// The default loot mode. <c>LOOT_MODE_DEFAULT</c>.
    /// </summary>
    /// <remarks>
    /// A bitmask against the row's own <c>lootmode</c>, which is how heroic and hard-mode drops are
    /// separated from normal ones. Ignoring it puts every difficulty's drops on a normal kill.
    /// </remarks>
    public const ushort DefaultLootMode = 1;

    /// <summary>How deep a reference chain may go before it is treated as a cycle.</summary>
    /// <remarks>
    /// The data is not supposed to contain one. A cycle here would be an infinite loop inside the
    /// map tick, which is a hang rather than a wrong answer, so the depth is bounded regardless.
    /// </remarks>
    public const int MaxReferenceDepth = 10;

    /// <summary>
    /// Fills a pile from a loot template.
    /// </summary>
    /// <param name="items">Resolves an item entry to its template. Rows naming an unknown item are skipped.</param>
    /// <param name="rollPercent">Draws a number in <c>[0, 100)</c>.</param>
    /// <param name="pick">Picks an index below the count it is given.</param>
    /// <param name="urand">Draws an inclusive integer in a range, for stack counts.</param>
    public static void Fill(
        Loot loot,
        LootTemplate template,
        LootStore references,
        ItemTemplateStore items,
        Func<float> rollPercent,
        Func<int, int> pick,
        Func<uint, uint, uint> urand,
        ushort lootMode = DefaultLootMode)
    {
        ArgumentNullException.ThrowIfNull(loot);
        ArgumentNullException.ThrowIfNull(template);

        Process(loot, template, references, items, rollPercent, pick, urand, lootMode, depth: 0, groupId: 0);
    }

    /// <summary>
    /// Rolls the money a kill is worth.
    /// </summary>
    /// <remarks>
    /// Port of <c>Loot::generateMoneyLoot</c>, including its third branch: a range wider than
    /// 32,700 copper is rolled in units of 256 and shifted back up, because upstream's random
    /// helper takes a 32-bit range and the shift is what keeps a very wide range from losing
    /// precision. Every creature in a starting zone takes the middle branch.
    /// </remarks>
    public static uint RollMoney(uint minGold, uint maxGold, Func<uint, uint, uint> urand)
    {
        ArgumentNullException.ThrowIfNull(urand);

        if (maxGold == 0)
        {
            return 0;
        }

        if (maxGold <= minGold)
        {
            return maxGold;
        }

        if (maxGold - minGold < 32700)
        {
            return urand(minGold, maxGold);
        }

        return urand(minGold >> 8, maxGold >> 8) << 8;
    }

    private static void Process(
        Loot loot,
        LootTemplate template,
        LootStore references,
        ItemTemplateStore items,
        Func<float> rollPercent,
        Func<int, int> pick,
        Func<uint, uint, uint> urand,
        ushort lootMode,
        int depth,
        byte groupId)
    {
        if (depth > MaxReferenceDepth)
        {
            return;
        }

        // A reference row can name a group inside the template it points at, in which case only
        // that group is rolled rather than the whole thing.
        if (groupId > 0)
        {
            if (template.Groups.TryGetValue(groupId, out LootGroup? only))
            {
                RollGroup(loot, only, references, items, rollPercent, pick, urand, lootMode, depth);
            }

            return;
        }

        foreach (LootStoreItem row in template.Ungrouped)
        {
            if ((row.LootMode & lootMode) == 0)
            {
                continue;
            }

            if (!Rolls(row, rollPercent))
            {
                continue;
            }

            Yield(loot, row, references, items, rollPercent, pick, urand, lootMode, depth);
        }

        foreach (LootGroup group in template.Groups.Values)
        {
            RollGroup(loot, group, references, items, rollPercent, pick, urand, lootMode, depth);
        }
    }

    private static void RollGroup(
        Loot loot,
        LootGroup group,
        LootStore references,
        ItemTemplateStore items,
        Func<float> rollPercent,
        Func<int, int> pick,
        Func<uint, uint, uint> urand,
        ushort lootMode,
        int depth)
    {
        if (group.Roll(rollPercent, pick) is not { } won)
        {
            return;
        }

        if ((won.LootMode & lootMode) == 0)
        {
            return;
        }

        Yield(loot, won, references, items, rollPercent, pick, urand, lootMode, depth);
    }

    /// <summary>Turns one winning row into items — or into a whole referenced template.</summary>
    private static void Yield(
        Loot loot,
        LootStoreItem row,
        LootStore references,
        ItemTemplateStore items,
        Func<float> rollPercent,
        Func<int, int> pick,
        Func<uint, uint, uint> urand,
        ushort lootMode,
        int depth)
    {
        if (row.IsReference)
        {
            if (!references.TryGet(row.ReferenceId, out LootTemplate? referenced) || referenced is null)
            {
                return;
            }

            // maxcount is how many times the referenced template is rolled, not a stack size.
            // Treating it as a count produces one item where the data asks for several rolls.
            uint rolls = Math.Max(row.MaxCount, (byte)1);

            for (uint i = 0; i < rolls; i++)
            {
                Process(
                    loot, referenced, references, items, rollPercent, pick, urand, lootMode,
                    depth + 1, row.GroupId);
            }

            return;
        }

        if (!items.TryGet(row.ItemId, out ItemTemplate? item) || item is null)
        {
            return;
        }

        uint count = row.MaxCount <= row.MinCount
            ? Math.Max(row.MinCount, 1)
            : urand(row.MinCount, row.MaxCount);

        loot.Add(item, count, row.NeedsQuest);
    }

    /// <summary>Whether one ungrouped row drops. A chance of 100 or more always does.</summary>
    private static bool Rolls(LootStoreItem row, Func<float> rollPercent) =>
        row.DropChance >= 100f || rollPercent() < row.DropChance;
}
