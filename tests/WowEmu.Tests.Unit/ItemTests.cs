using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Protocol;
using WowEmu.WorldServer;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>Builds item templates without a database behind them.</summary>
internal static class ItemFixture
{
    /// <summary>
    /// A template with everything at its column default, so a test sets only what it is about.
    /// </summary>
    public static ItemTemplate Build(
        uint entry = 1,
        string name = "Test Item",
        byte itemClass = ItemClass.Misc,
        byte subClass = 0,
        byte quality = ItemQuality.Normal,
        ushort itemLevel = 0,
        byte inventoryType = InventoryType.NonEquip,
        int stackable = 1,
        ushort maxDurability = 0,
        byte containerSlots = 0,
        uint durationSeconds = 0,
        ItemSpell[]? spells = null,
        ItemDamage[]? damage = null,
        ushort delay = 0,
        byte statsCount = 0,
        ItemStat[]? stats = null) =>
        new(
            Entry: entry,
            Class: itemClass,
            SubClass: subClass,
            SoundOverrideSubclass: -1,
            Name: name,
            DisplayId: 0,
            Quality: quality,
            Flags: 0,
            FlagsExtra: 0,
            BuyCount: 1,
            BuyPrice: 0,
            SellPrice: 0,
            InventoryType: inventoryType,
            AllowableClass: -1,
            AllowableRace: -1,
            ItemLevel: itemLevel,
            RequiredLevel: 0,
            RequiredSkill: 0,
            RequiredSkillRank: 0,
            RequiredSpell: 0,
            RequiredHonorRank: 0,
            RequiredCityRank: 0,
            RequiredReputationFaction: 0,
            RequiredReputationRank: 0,
            MaxCount: 0,
            Stackable: stackable,
            ContainerSlots: containerSlots,
            StatsCount: statsCount,
            Stats: stats ?? new ItemStat[ItemConstants.MaxStats],
            ScalingStatDistribution: 0,
            ScalingStatValue: 0,
            Damage: damage ?? new ItemDamage[ItemConstants.MaxDamages],
            Armor: 0,
            HolyResistance: 0,
            FireResistance: 0,
            NatureResistance: 0,
            FrostResistance: 0,
            ShadowResistance: 0,
            ArcaneResistance: 0,
            Delay: delay,
            AmmoType: 0,
            RangedModRange: 0f,
            Spells: spells ?? Empty(),
            Bonding: 0,
            Description: string.Empty,
            PageText: 0,
            LanguageId: 0,
            PageMaterial: 0,
            StartQuest: 0,
            LockId: 0,
            Material: 0,
            Sheath: 0,
            RandomProperty: 0,
            RandomSuffix: 0,
            Block: 0,
            ItemSet: 0,
            MaxDurability: maxDurability,
            Area: 0,
            Map: 0,
            BagFamily: 0,
            TotemCategory: 0,
            Sockets: new ItemSocket[ItemConstants.MaxSockets],
            SocketBonus: 0,
            GemProperties: 0,
            RequiredDisenchantSkill: -1,
            ArmorDamageModifier: 0f,
            DurationSeconds: durationSeconds,
            ItemLimitCategory: 0,
            HolidayId: 0);

    /// <summary>Five empty spell slots at the table's own defaults — cooldowns of <c>-1</c>.</summary>
    public static ItemSpell[] Empty()
    {
        ItemSpell[] spells = new ItemSpell[ItemConstants.MaxSpells];

        for (int i = 0; i < spells.Length; i++)
        {
            spells[i] = new ItemSpell(0, 0, 0, -1, 0, -1);
        }

        return spells;
    }
}

/// <summary>
/// <c>ItemTemplate</c>'s derived values.
/// </summary>
public sealed class ItemTemplateTests
{
    /// <summary>
    /// Zero, a negative and <c>int.MaxValue</c> all mean "no practical limit".
    /// </summary>
    /// <remarks>
    /// Port of <c>GetMaxStackSize</c>. Reading the column literally would make a stackable of zero
    /// an item that cannot be held at all, and <c>-1</c> — which the table uses for coins — a stack
    /// of four billion.
    /// </remarks>
    [Theory]
    [InlineData(1, 1u)]
    [InlineData(20, 20u)]
    [InlineData(0, 0x7FFFFFFEu)]
    [InlineData(-1, 0x7FFFFFFEu)]
    [InlineData(int.MaxValue, 0x7FFFFFFEu)]
    public void MaxStackSize_TreatsZeroNegativeAndMaxAsUnlimited(int stackable, uint expected) =>
        Assert.Equal(expected, ItemFixture.Build(stackable: stackable).MaxStackSize);

    /// <summary>
    /// Damage per second averages the range and converts the swing to seconds in one step.
    /// </summary>
    /// <remarks>
    /// Port of <c>getDPS</c>. The <c>× 500</c> is <c>× 1000 ÷ 2</c> folded together, which is easy to
    /// read as a magic number and get wrong by a factor of two.
    /// </remarks>
    [Fact]
    public void DamagePerSecond_AveragesTheRangeOverTheSwing()
    {
        ItemDamage[] damage = new ItemDamage[ItemConstants.MaxDamages];
        damage[0] = new ItemDamage(10f, 20f, 0);

        // (10 + 20) × 500 / 2000 = 7.5
        Assert.Equal(7.5f, ItemFixture.Build(damage: damage, delay: 2000).DamagePerSecond);
    }

    /// <summary>A weapon with no swing time is not a divide by zero.</summary>
    [Fact]
    public void DamagePerSecond_IsZeroWithoutASwingTime()
    {
        ItemDamage[] damage = new ItemDamage[ItemConstants.MaxDamages];
        damage[0] = new ItemDamage(10f, 20f, 0);

        Assert.Equal(0f, ItemFixture.Build(damage: damage, delay: 0).DamagePerSecond);
    }

    /// <summary>Both damage ranges count, because a weapon can carry two.</summary>
    [Fact]
    public void DamagePerSecond_CountsBothRanges()
    {
        ItemDamage[] damage =
        [
            new ItemDamage(10f, 20f, 0),
            new ItemDamage(5f, 5f, 4),
        ];

        // (10 + 20 + 5 + 5) × 500 / 2000 = 10
        Assert.Equal(10f, ItemFixture.Build(damage: damage, delay: 2000).DamagePerSecond);
    }

    /// <summary>A container is a bag whichever of the two columns says so.</summary>
    [Theory]
    [InlineData(ItemClass.Container, InventoryType.NonEquip, true)]
    [InlineData(ItemClass.Misc, InventoryType.Bag, true)]
    [InlineData(ItemClass.Misc, InventoryType.NonEquip, false)]
    public void IsBag_ReadsEitherColumn(byte itemClass, byte inventoryType, bool expected) =>
        Assert.Equal(expected, ItemFixture.Build(itemClass: itemClass, inventoryType: inventoryType).IsBag);
}

/// <summary>
/// <c>Item::Create</c> and the field block it produces.
/// </summary>
public sealed class ItemObjectTests
{
    private static readonly ObjectGuid Owner = ObjectGuid.Create(HighGuid.Player, 7);

    /// <summary>
    /// A fresh item is one of the thing, not a full stack.
    /// </summary>
    /// <remarks>
    /// Starting at the template's stack size would hand out twenty of everything the first time
    /// anything created an item.
    /// </remarks>
    [Fact]
    public void AFreshItem_HoldsOne()
    {
        Item item = Item.Create(1, ItemFixture.Build(stackable: 20));

        Assert.Equal(1u, item.Count);
        Assert.False(item.IsFullStack);
        Assert.Equal(19u, item.FreeStackSpace);
    }

    /// <summary>Owner and container both start as the owner, and are separate afterwards.</summary>
    /// <remarks>
    /// They diverge the moment the item goes into a bag: the container becomes the bag and the owner
    /// stays the player. Writing one through the other loses whichever was overwritten.
    /// </remarks>
    [Fact]
    public void OwnerAndContainer_StartTheSameAndMoveIndependently()
    {
        Item item = Item.Create(1, ItemFixture.Build(), Owner);

        Assert.Equal(Owner, item.Owner);
        Assert.Equal(Owner, item.Container);

        ObjectGuid bag = ObjectGuid.Create(HighGuid.Container, 9);
        item.Container = bag;

        Assert.Equal(Owner, item.Owner);
        Assert.Equal(bag, item.Container);
    }

    /// <summary>An item starts fully repaired, at its template's durability.</summary>
    [Fact]
    public void AFreshItem_IsFullyRepaired()
    {
        Item item = Item.Create(1, ItemFixture.Build(maxDurability: 65));

        Assert.Equal(65u, item.Durability);
        Assert.Equal(65u, item.MaxDurability);
        Assert.False(item.IsDamaged);
        Assert.False(item.IsBroken);

        item.Durability = 0;

        Assert.True(item.IsDamaged);
        Assert.True(item.IsBroken);
    }

    /// <summary>An item with no durability at all is never damaged and never broken.</summary>
    /// <remarks>
    /// Most items have none. Treating zero-of-zero as broken would make every potion in the game
    /// unusable.
    /// </remarks>
    [Fact]
    public void AnItemWithNoDurability_IsNeverBroken()
    {
        Item item = Item.Create(1, ItemFixture.Build(maxDurability: 0));

        Assert.False(item.IsDamaged);
        Assert.False(item.IsBroken);
    }

    /// <summary>Spell charges are copied from the template, negatives and all.</summary>
    /// <remarks>
    /// A negative count is what destroys the item when it runs out — a potion is <c>-1</c>. Storing
    /// the absolute value instead leaves an empty potion in the bag forever.
    /// </remarks>
    [Fact]
    public void SpellCharges_ComeFromTheTemplateSigned()
    {
        ItemSpell[] spells = ItemFixture.Empty();
        spells[0] = new ItemSpell(SpellId: 439, Trigger: 0, Charges: -1, CooldownMs: -1, Category: 0, CategoryCooldownMs: -1);
        spells[1] = new ItemSpell(SpellId: 1, Trigger: 1, Charges: 5, CooldownMs: -1, Category: 0, CategoryCooldownMs: -1);

        Item item = Item.Create(1, ItemFixture.Build(spells: spells));

        Assert.Equal(-1, item.GetSpellCharges(0));
        Assert.Equal(5, item.GetSpellCharges(1));
        Assert.Equal(0, item.GetSpellCharges(2));
    }

    /// <summary>
    /// An item's guid carries no entry, so the whole low half is the counter.
    /// </summary>
    /// <remarks>
    /// Building it with the entry-carrying overload would put the item's entry in bits 24-47 and
    /// collide two items whose counters differ only above the 24th bit.
    /// </remarks>
    [Fact]
    public void TheGuid_IsAllCounter()
    {
        Item item = Item.Create(0x01234567, ItemFixture.Build(entry: 6948));

        Assert.Equal(HighGuid.Item, item.Guid.High);
        Assert.Equal(0x01234567u, item.Guid.Counter);
        Assert.Equal(6948u, item.Entry);
    }

    /// <summary>
    /// A container is built as a <see cref="Bag"/>, with the longer field block and the extra bit.
    /// </summary>
    /// <remarks>
    /// The client reads the container fields whenever the type mask says container, so a bag sent as
    /// a plain item leaves it reading 74 words past the end of the block.
    /// </remarks>
    [Fact]
    public void AContainer_IsBuiltAsABag()
    {
        Item item = Item.Create(1, ItemFixture.Build(itemClass: ItemClass.Container, containerSlots: 16));

        Bag bag = Assert.IsType<Bag>(item);

        Assert.Equal(TypeId.Container, bag.TypeId);
        Assert.Equal(16u, bag.SlotCount);
        Assert.True(bag.IsEmpty);
        Assert.Equal(HighGuid.Container, bag.Guid.High);
    }

    /// <summary>A bag with something in it is not empty.</summary>
    [Fact]
    public void ABagWithSomethingInIt_IsNotEmpty()
    {
        Bag bag = (Bag)Item.Create(1, ItemFixture.Build(itemClass: ItemClass.Container, containerSlots: 4));

        bag.SetSlot(2, ObjectGuid.Create(HighGuid.Item, 42));

        Assert.False(bag.IsEmpty);
        Assert.Equal(ObjectGuid.Create(HighGuid.Item, 42), bag.GetSlot(2));

        // Past the bag's own slot count, so it does not count towards emptiness.
        bag.SetSlot(2, ObjectGuid.Empty);
        bag.SetSlot(10, ObjectGuid.Create(HighGuid.Item, 43));

        Assert.True(bag.IsEmpty);
    }

    /// <summary>A plain item is an object and an item, and nothing else.</summary>
    [Fact]
    public void APlainItem_SetsOnlyTheObjectAndItemBits()
    {
        Item item = Item.Create(1, ItemFixture.Build());

        Assert.Equal(
            TypeMask.Object | TypeMask.Item,
            item.Fields.GetUInt32(UpdateFields.OBJECT_FIELD_TYPE));
    }
}

/// <summary>
/// <c>SMSG_ITEM_QUERY_SINGLE_RESPONSE</c>.
/// </summary>
public sealed class ItemQueryResponseTests
{
    private static byte[] Write(ItemTemplate item, SpellCooldownLookup? cooldowns = null)
    {
        PacketWriter writer = new();
        ItemQueryResponse.Write(writer, item, cooldowns);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// A missing item is one word with the high bit set, and nothing else.
    /// </summary>
    /// <remarks>
    /// Answering with a zeroed body instead leaves the client believing in a nameless item, which it
    /// then caches to disk and stops asking about.
    /// </remarks>
    [Fact]
    public void AMissingItem_IsTheEntryWithTheHighBitSet()
    {
        PacketWriter writer = new();
        ItemQueryResponse.WriteNotFound(writer, 12345);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt32(out uint entry));
        Assert.Equal(12345u | 0x80000000u, entry);
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>The head of the packet reads back field by field.</summary>
    [Fact]
    public void TheHead_ReadsBackFieldByField()
    {
        PacketReader reader = new(Write(ItemFixture.Build(entry: 25, name: "Worn Shortsword")));

        Assert.True(reader.TryReadUInt32(out uint entry));
        Assert.Equal(25u, entry);

        Assert.True(reader.TryReadUInt32(out uint itemClass));
        Assert.Equal((uint)ItemClass.Misc, itemClass);

        Assert.True(reader.TryReadUInt32(out uint subClass));
        Assert.Equal(0u, subClass);

        // Signed in the table and sent as a full word, so -1 is 0xFFFFFFFF and not a single byte.
        Assert.True(reader.TryReadUInt32(out uint soundOverride));
        Assert.Equal(0xFFFFFFFFu, soundOverride);

        Assert.True(reader.TryReadCString(out string? name));
        Assert.Equal("Worn Shortsword", name);
    }

    /// <summary>
    /// The three unused names are one zero byte each, not three words.
    /// </summary>
    /// <remarks>
    /// The client still reads them, and Blizzard never filled them. Writing words instead shifts
    /// everything after by nine bytes.
    /// </remarks>
    [Fact]
    public void TheThreeUnusedNames_AreOneByteEach()
    {
        PacketReader reader = new(Write(ItemFixture.Build(name: "x")));

        reader.Skip(4 + 4 + 4 + 4);

        Assert.True(reader.TryReadCString(out string? name));
        Assert.Equal("x", name);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(reader.TryReadUInt8(out byte empty));
            Assert.Equal(0, empty);
        }

        Assert.True(reader.TryReadUInt32(out uint displayId));
        Assert.Equal(0u, displayId);
    }

    /// <summary>
    /// The stat count is a length prefix, and only that many pairs follow.
    /// </summary>
    /// <remarks>
    /// Writing all ten regardless would put words of zeroes where the client expects the scaling
    /// block, and every field after would be read one stat pair late.
    /// </remarks>
    [Fact]
    public void TheStatCount_IsALengthPrefix()
    {
        ItemStat[] stats = new ItemStat[ItemConstants.MaxStats];
        stats[0] = new ItemStat(4, 3);
        stats[1] = new ItemStat(7, 5);

        byte[] two = Write(ItemFixture.Build(statsCount: 2, stats: stats));
        byte[] none = Write(ItemFixture.Build(statsCount: 0, stats: stats));

        Assert.Equal(2 * 8, two.Length - none.Length);
    }

    /// <summary>A stat count larger than the ten columns is clamped rather than read off the end.</summary>
    [Fact]
    public void AnOversizedStatCount_IsClamped()
    {
        byte[] bytes = Write(ItemFixture.Build(statsCount: 40));
        byte[] full = Write(ItemFixture.Build(statsCount: 10));

        Assert.Equal(full.Length, bytes.Length);
    }

    /// <summary>
    /// A spell slot is six words whether or not the spell exists.
    /// </summary>
    /// <remarks>
    /// Five slots, always. A slot skipped because its spell is zero would shift the whole tail of
    /// the packet — and every item in the game has at least one empty slot.
    /// </remarks>
    [Fact]
    public void EverySpellSlot_IsSixWords()
    {
        ItemSpell[] spells = ItemFixture.Empty();
        spells[0] = new ItemSpell(439, 0, -1, -1, 0, -1);

        byte[] withSpell = Write(ItemFixture.Build(spells: spells), Exists);
        byte[] without = Write(ItemFixture.Build(), Exists);

        Assert.Equal(without.Length, withSpell.Length);
    }

    /// <summary>
    /// A slot whose table row declines a cooldown falls back on the spell's own.
    /// </summary>
    /// <remarks>
    /// <c>-1</c> in both cooldown columns is how the table says "ask the spell". Sending the
    /// <c>-1</c> through instead shows a clickable item with a four-billion-millisecond cooldown.
    /// </remarks>
    [Fact]
    public void ASlotWithNoCooldownData_TakesTheSpells()
    {
        ItemSpell[] fromSpell = ItemFixture.Empty();
        fromSpell[0] = new ItemSpell(439, 0, -1, CooldownMs: -1, Category: 0, CategoryCooldownMs: -1);

        ItemSpell[] fromTable = ItemFixture.Empty();
        fromTable[0] = new ItemSpell(439, 0, -1, CooldownMs: 5000, Category: 7, CategoryCooldownMs: 9000);

        Assert.Equal((3000u, 11u, 4000u), FirstSpellCooldown(fromSpell));
        Assert.Equal((5000u, 7u, 9000u), FirstSpellCooldown(fromTable));
    }

    /// <summary>A spell the server has never heard of zeroes its slot, with -1 cooldowns.</summary>
    [Fact]
    public void AnUnknownSpell_ZeroesItsSlot()
    {
        ItemSpell[] spells = ItemFixture.Empty();
        spells[0] = new ItemSpell(999999, 3, 7, 5000, 7, 9000);

        PacketReader reader = new(Write(ItemFixture.Build(spells: spells), Missing));

        SkipToSpells(ref reader);

        Assert.True(reader.TryReadUInt32(out uint spellId));
        Assert.Equal(0u, spellId);

        Assert.True(reader.TryReadUInt32(out uint trigger));
        Assert.Equal(0u, trigger);

        Assert.True(reader.TryReadUInt32(out uint charges));
        Assert.Equal(0u, charges);

        Assert.True(reader.TryReadUInt32(out uint cooldown));
        Assert.Equal(0xFFFFFFFFu, cooldown);
    }

    /// <summary>Every write ends exactly at the holiday id, with nothing left over.</summary>
    [Fact]
    public void TheTail_EndsAtTheHolidayId()
    {
        ItemTemplate item = ItemFixture.Build() with { HolidayId = 181, DurationSeconds = 900 };

        PacketReader reader = new(Write(item));

        // Rewinding to the tail from the front means replaying the whole variable-length head, so
        // read from the end instead: the last three words are duration, limit category, holiday.
        byte[] bytes = Write(item);

        Assert.Equal(900u, BitConverter.ToUInt32(bytes, bytes.Length - 12));
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, bytes.Length - 8));
        Assert.Equal(181u, BitConverter.ToUInt32(bytes, bytes.Length - 4));

        Assert.True(reader.Remaining > 0);
    }

    /// <summary>Reads back the first spell slot's three cooldown words.</summary>
    private static (uint Recovery, uint Category, uint CategoryRecovery) FirstSpellCooldown(ItemSpell[] spells)
    {
        PacketReader reader = new(Write(ItemFixture.Build(spells: spells), Exists));

        SkipToSpells(ref reader);
        reader.Skip(4 + 4 + 4);

        reader.TryReadUInt32(out uint recovery);
        reader.TryReadUInt32(out uint category);
        reader.TryReadUInt32(out uint categoryRecovery);

        return (recovery, category, categoryRecovery);
    }

    /// <summary>
    /// Walks the fixed-shape head so the spell block can be read positionally.
    /// </summary>
    /// <remarks>
    /// By reference, and it has to be: <c>PacketReader</c> is a <c>ref struct</c>, so passing it by
    /// value hands over a copy whose advanced position never comes back — the caller carries on
    /// reading from byte zero and the assertions compare against the head of the packet.
    /// </remarks>
    private static void SkipToSpells(ref PacketReader reader)
    {
        reader.Skip(4 + 4 + 4 + 4);         // entry, class, subclass, sound override
        reader.TryReadCString(out _);        // name
        reader.Skip(3);                      // the three unused names

        // displayid through containerslots: 21 words, then the stat count of zero.
        reader.Skip(21 * 4);
        reader.Skip(4);

        reader.Skip(4 + 4);                  // scaling distribution and value
        reader.Skip(ItemConstants.MaxDamages * 12);
        reader.Skip(7 * 4);                  // armour and six resistances
        reader.Skip(4 + 4 + 4);              // delay, ammo type, ranged mod range
    }

    private static bool Exists(int spellId, out uint recoveryMs, out uint category, out uint categoryRecoveryMs)
    {
        recoveryMs = 3000;
        category = 11;
        categoryRecoveryMs = 4000;

        return true;
    }

    private static bool Missing(int spellId, out uint recoveryMs, out uint category, out uint categoryRecoveryMs)
    {
        recoveryMs = 0;
        category = 0;
        categoryRecoveryMs = 0;

        return false;
    }
}

/// <summary>The item store, over the real vendored rows.</summary>
public sealed class ItemStoreTests(ITestOutputHelper output)
{
    private static CancellationToken TestToken => CancellationToken.None;

    [RequiresWorldDatabaseFact]
    public async Task TheStore_LoadsEveryRow()
    {
        ItemTemplateStore items = new();
        await items.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(items.Count > 38_000, $"only {items.Count} items");

        output.WriteLine($"{items.Count} item templates");
    }

    /// <summary>
    /// A handful of known items read back with the values the client shows for them.
    /// </summary>
    /// <remarks>
    /// The whole point of the wide read is that the columns land in the right fields. Counting rows
    /// would pass with every column one to the left.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task KnownItems_ReadBackWithTheirRealValues()
    {
        ItemTemplateStore items = new();
        await items.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        // Worn Shortsword — every human warrior starts with one.
        Assert.True(items.TryGet(25, out ItemTemplate? sword));
        Assert.NotNull(sword);
        Assert.Equal("Worn Shortsword", sword.Name);
        Assert.Equal(ItemClass.Weapon, sword.Class);
        Assert.Equal(InventoryType.WeaponMainHand, sword.InventoryType);
        Assert.True(sword.Delay > 0, "the swing time did not load");
        Assert.True(sword.Damage[0].Max > sword.Damage[0].Min, "the damage range did not load");
        Assert.True(sword.MaxDurability > 0, "the durability did not load");

        // Linen Cloth — the canonical stackable.
        Assert.True(items.TryGet(2589, out ItemTemplate? cloth));
        Assert.NotNull(cloth);
        Assert.Equal("Linen Cloth", cloth.Name);
        Assert.Equal(20u, cloth.MaxStackSize);
        Assert.Equal(0, cloth.MaxDurability);

        output.WriteLine($"{sword.Name}: {sword.Damage[0].Min}-{sword.Damage[0].Max} over {sword.Delay} ms");
    }

    /// <summary>
    /// Every bag reports a slot count, and none exceeds the client's 36.
    /// </summary>
    /// <remarks>
    /// A bag whose slot count runs past 36 would write guids past the end of the container field
    /// block and into whatever follows.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task EveryBag_FitsTheClientsSlotArray()
    {
        ItemTemplateStore items = new();
        await items.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        List<uint> oversized = [];
        int bags = 0;

        foreach (ItemTemplate item in items.All)
        {
            if (!item.IsBag)
            {
                continue;
            }

            bags++;

            if (item.ContainerSlots > Bag.MaxSlots)
            {
                oversized.Add(item.Entry);
            }
        }

        Assert.True(bags > 0, "no bags at all — the class or inventory type column is misread");
        Assert.Empty(oversized);

        output.WriteLine($"{bags} bags, none over {Bag.MaxSlots} slots");
    }

    /// <summary>
    /// Every item survives being written as a query response.
    /// </summary>
    /// <remarks>
    /// The writer casts and clamps in several places, and a row with an unexpected value there —
    /// a stat count past ten, a negative where a cast expects otherwise — would throw on a live
    /// client's first tooltip. This is the cheapest way to find that out here instead.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task EveryItem_CanBeWrittenAsAQueryResponse()
    {
        ItemTemplateStore items = new();
        await items.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        int written = 0;
        int longest = 0;

        foreach (ItemTemplate item in items.All)
        {
            PacketWriter writer = new();
            ItemQueryResponse.Write(writer, item);

            longest = Math.Max(longest, writer.WrittenSpan.Length);
            written++;
        }

        Assert.Equal(items.Count, written);

        output.WriteLine($"wrote {written} responses, longest {longest} bytes");
    }
}
