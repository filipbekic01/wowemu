using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;
using WowEmu.Protocol;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>Builds loot templates and stores without a database behind them.</summary>
internal static class LootFixture
{
    public static LootStoreItem Row(
        uint entry = 1,
        uint itemId = 2589,
        float chance = 100f,
        byte groupId = 0,
        int minCountOrReference = 1,
        byte maxCount = 1,
        ushort lootMode = 1) =>
        new(entry, itemId, chance, lootMode, groupId, minCountOrReference, maxCount);

    public static LootTemplate Template(params LootStoreItem[] rows)
    {
        LootTemplate template = new();

        foreach (LootStoreItem row in rows)
        {
            AddTo(template, row);
        }

        return template;
    }

    /// <summary>
    /// <c>LootTemplate.Add</c> is internal, so the rows go in through a real store's loader shape.
    /// </summary>
    /// <remarks>
    /// Reflection rather than widening the API: a template that anything outside the store can add
    /// to is a template that can disagree with the table it came from.
    /// </remarks>
    private static void AddTo(LootTemplate template, LootStoreItem row) =>
        typeof(LootTemplate)
            .GetMethod("Add", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(template, [row]);

    /// <summary>An item store holding exactly the entries a test names.</summary>
    public static ItemTemplateStore Items(params ItemTemplate[] templates)
    {
        ItemTemplateStore store = new();

        System.Reflection.FieldInfo field = typeof(ItemTemplateStore)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        Dictionary<uint, ItemTemplate> map = (Dictionary<uint, ItemTemplate>)field.GetValue(store)!;

        foreach (ItemTemplate template in templates)
        {
            map[template.Entry] = template;
        }

        return store;
    }

    /// <summary>A store of the given name holding one id.</summary>
    public static LootStore Store(string name, uint id, LootTemplate template)
    {
        LootStore store = new(name);

        System.Reflection.FieldInfo field = typeof(LootStore)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        ((Dictionary<uint, LootTemplate>)field.GetValue(store)!)[id] = template;

        return store;
    }

    /// <summary>A reference store holding one id.</summary>
    public static LootStore References(uint id, LootTemplate template)
    {
        LootStore store = new("reference_loot_template");

        System.Reflection.FieldInfo field = typeof(LootStore)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        ((Dictionary<uint, LootTemplate>)field.GetValue(store)!)[id] = template;

        return store;
    }

    public static LootStore NoReferences() => new("reference_loot_template");

    /// <summary>A roll source that always returns the same percentage.</summary>
    public static Func<float> Always(float percent) => () => percent;

    /// <summary>Picks the first of anything.</summary>
    public static int First(int count) => 0;

    /// <summary>Takes the top of every count range, so stack sizes are not a coin toss.</summary>
    public static uint Highest(uint min, uint max) => max;
}

/// <summary>How the two overloaded columns are read.</summary>
public sealed class LootStoreItemTests
{
    /// <summary>
    /// <c>mincountOrRef</c> is a count when positive and a reference id when negative.
    /// </summary>
    /// <remarks>
    /// Reading it as a count regardless turns every shared drop list into a request for a negative
    /// number of items — which, unsigned, is four billion.
    /// </remarks>
    [Fact]
    public void ANegativeMinCount_IsAReference()
    {
        LootStoreItem reference = LootFixture.Row(minCountOrReference: -34817);

        Assert.True(reference.IsReference);
        Assert.Equal(34817u, reference.ReferenceId);

        LootStoreItem plain = LootFixture.Row(minCountOrReference: 3);

        Assert.False(plain.IsReference);
        Assert.Equal(3u, plain.MinCount);
        Assert.Equal(0u, plain.ReferenceId);
    }

    /// <summary>
    /// A negative chance means a quest drop, and the chance is its absolute value.
    /// </summary>
    /// <remarks>
    /// There is no separate flag. Reading the sign as part of the chance makes every quest item in
    /// the game impossible to get.
    /// </remarks>
    [Fact]
    public void ANegativeChance_IsAQuestDrop()
    {
        LootStoreItem quest = LootFixture.Row(chance: -40f);

        Assert.True(quest.NeedsQuest);
        Assert.Equal(40f, quest.DropChance);

        Assert.False(LootFixture.Row(chance: 40f).NeedsQuest);
    }
}

/// <summary>Groups: at most one member drops, and how the winner is chosen.</summary>
public sealed class LootGroupTests
{
    private static LootGroup Group(params LootStoreItem[] rows)
    {
        LootTemplate template = LootFixture.Template(rows);

        return template.Groups[rows[0].GroupId];
    }

    /// <summary>
    /// A member with a chance of exactly zero is equal-chanced, not impossible.
    /// </summary>
    /// <remarks>
    /// Most groups in the table are entirely zero-chance members. Treating zero as a 0% chance
    /// means those groups never drop anything at all.
    /// </remarks>
    [Fact]
    public void AZeroChanceMember_IsEqualChancedRatherThanImpossible()
    {
        LootGroup group = Group(
            LootFixture.Row(itemId: 1, chance: 0f, groupId: 1),
            LootFixture.Row(itemId: 2, chance: 0f, groupId: 1));

        Assert.Empty(group.ExplicitlyChanced);
        Assert.Equal(2, group.EqualChanced.Count);

        LootStoreItem? won = group.Roll(LootFixture.Always(99f), LootFixture.First);

        Assert.NotNull(won);
        Assert.Equal(1u, won.ItemId);
    }

    /// <summary>
    /// The explicitly-chanced members share one roll, subtracting as it walks.
    /// </summary>
    /// <remarks>
    /// Rolling each member separately would let one group drop two items, which is exactly what a
    /// group exists to prevent.
    /// </remarks>
    [Fact]
    public void ExplicitChances_ShareOneRoll()
    {
        LootStoreItem[] rows =
        [
            LootFixture.Row(itemId: 1, chance: 20f, groupId: 1),
            LootFixture.Row(itemId: 2, chance: 30f, groupId: 1),
        ];

        // 10 is inside the first 20.
        Assert.Equal(1u, Group(rows)!.Roll(LootFixture.Always(10f), LootFixture.First)!.ItemId);

        // 35 falls past the first and inside the second.
        Assert.Equal(2u, Group(rows)!.Roll(LootFixture.Always(35f), LootFixture.First)!.ItemId);

        // 80 is past both, and there is nothing equal-chanced to fall back on.
        Assert.Null(Group(rows)!.Roll(LootFixture.Always(80f), LootFixture.First));
    }

    /// <summary>Equal-chanced members are the fallback when every explicit chance misses.</summary>
    [Fact]
    public void EqualChanced_IsTheFallback()
    {
        LootGroup group = Group(
            LootFixture.Row(itemId: 1, chance: 5f, groupId: 1),
            LootFixture.Row(itemId: 2, chance: 0f, groupId: 1));

        Assert.Equal(2u, group.Roll(LootFixture.Always(90f), LootFixture.First)!.ItemId);
    }

    /// <summary>A chance of 100 or more always wins, whatever the roll.</summary>
    [Fact]
    public void ACertainMember_AlwaysWins()
    {
        LootGroup group = Group(
            LootFixture.Row(itemId: 1, chance: 100f, groupId: 1),
            LootFixture.Row(itemId: 2, chance: 100f, groupId: 1));

        Assert.Equal(1u, group.Roll(LootFixture.Always(99.9f), LootFixture.First)!.ItemId);
    }
}

/// <summary>Rolling a whole template into a pile.</summary>
public sealed class LootRollTests
{
    private static readonly ItemTemplate Cloth =
        ItemFixture.Build(entry: 2589, name: "Linen Cloth", stackable: 20);

    private static readonly ItemTemplate Sword =
        ItemFixture.Build(entry: 25, name: "Worn Shortsword", stackable: 1);

    private static Loot Fill(LootTemplate template, float roll = 0f, LootStore? references = null)
    {
        Loot loot = new();

        LootRoll.Fill(
            loot,
            template,
            references ?? LootFixture.NoReferences(),
            LootFixture.Items(Cloth, Sword),
            LootFixture.Always(roll),
            LootFixture.First,
            LootFixture.Highest);

        return loot;
    }

    /// <summary>Ungrouped rows are independent — a creature can drop all of them.</summary>
    [Fact]
    public void UngroupedRows_AreIndependent()
    {
        Loot loot = Fill(LootFixture.Template(
            LootFixture.Row(itemId: 2589, chance: 100f),
            LootFixture.Row(itemId: 25, chance: 100f)));

        Assert.Equal(2, loot.SlotCount);
    }

    /// <summary>A row that misses its chance drops nothing.</summary>
    [Fact]
    public void ARowThatMisses_DropsNothing()
    {
        Loot loot = Fill(LootFixture.Template(LootFixture.Row(chance: 10f)), roll: 50f);

        Assert.Equal(0, loot.SlotCount);
        Assert.True(loot.IsEmpty);
    }

    /// <summary>A row naming an item the server has never heard of is skipped.</summary>
    [Fact]
    public void ARowNamingAnUnknownItem_IsSkipped()
    {
        Loot loot = Fill(LootFixture.Template(LootFixture.Row(itemId: 999999, chance: 100f)));

        Assert.Equal(0, loot.SlotCount);
    }

    /// <summary>
    /// A stack larger than the item allows is split across slots.
    /// </summary>
    /// <remarks>
    /// A loot slot holds one stack and the client draws one icon per slot. Handing over 30 linen in
    /// one slot produces a stack larger than the item allows the moment it is picked up.
    /// </remarks>
    [Fact]
    public void AnOversizedStack_IsSplitAcrossSlots()
    {
        Loot loot = Fill(LootFixture.Template(
            LootFixture.Row(itemId: 2589, chance: 100f, minCountOrReference: 30, maxCount: 30)));

        Assert.Equal(2, loot.SlotCount);
        Assert.Equal(20u, loot.At(0)!.Count);
        Assert.Equal(10u, loot.At(1)!.Count);
    }

    /// <summary>
    /// A reference row rolls another template, <c>maxcount</c> times.
    /// </summary>
    /// <remarks>
    /// <c>maxcount</c> on a reference is how many <i>rolls</i>, not a stack size. Treating it as a
    /// count produces one item where the data asks for three passes over the referenced list.
    /// </remarks>
    [Fact]
    public void AReference_RollsTheOtherTemplateThatManyTimes()
    {
        LootStore references = LootFixture.References(
            500, LootFixture.Template(LootFixture.Row(entry: 500, itemId: 25, chance: 100f)));

        Loot loot = Fill(
            LootFixture.Template(
                LootFixture.Row(chance: 100f, minCountOrReference: -500, maxCount: 3)),
            references: references);

        Assert.Equal(3, loot.SlotCount);
    }

    /// <summary>A reference to an id nothing defines is skipped rather than throwing.</summary>
    [Fact]
    public void AReferenceToNothing_IsSkipped()
    {
        Loot loot = Fill(LootFixture.Template(
            LootFixture.Row(chance: 100f, minCountOrReference: -999, maxCount: 1)));

        Assert.Equal(0, loot.SlotCount);
    }

    /// <summary>
    /// A reference cycle stops rather than hanging the tick.
    /// </summary>
    /// <remarks>
    /// The data is not supposed to contain one. A cycle here would be an infinite loop inside a map
    /// update, which is a hang rather than a wrong answer — so the depth is bounded regardless.
    /// </remarks>
    [Fact]
    public void AReferenceCycle_Terminates()
    {
        LootTemplate loop = LootFixture.Template(
            LootFixture.Row(entry: 500, chance: 100f, minCountOrReference: -500, maxCount: 1));

        Loot loot = Fill(loop, references: LootFixture.References(500, loop));

        Assert.True(loot.SlotCount <= Loot.MaxItems);
    }

    /// <summary>A row for another loot mode does not drop on a normal kill.</summary>
    /// <remarks>
    /// Ignoring the mask puts every difficulty's drops on every kill — heroic loot from a normal
    /// pull, which looks like generosity rather than a bug.
    /// </remarks>
    [Fact]
    public void ARowForAnotherLootMode_DoesNotDrop()
    {
        Loot loot = Fill(LootFixture.Template(
            LootFixture.Row(chance: 100f, lootMode: 2)));

        Assert.Equal(0, loot.SlotCount);
    }

    /// <summary>A pile never exceeds the client's sixteen slots.</summary>
    [Fact]
    public void APile_NeverExceedsSixteenSlots()
    {
        // The sword does not stack, so 200 of them want 200 slots. maxcount is a tinyint, which is
        // why the count is 200 and not 500.
        Loot loot = Fill(LootFixture.Template(
            LootFixture.Row(itemId: 25, chance: 100f, minCountOrReference: 200, maxCount: 200)));

        Assert.Equal(Loot.MaxItems, loot.SlotCount);
    }

    /// <summary>Taking a slot leaves the ones after it where they were.</summary>
    /// <remarks>
    /// The client sends the slot number back. Renumbering after a take makes the next click land on
    /// the wrong item.
    /// </remarks>
    [Fact]
    public void TakingASlot_DoesNotRenumberTheRest()
    {
        Loot loot = Fill(LootFixture.Template(
            LootFixture.Row(itemId: 2589, chance: 100f),
            LootFixture.Row(itemId: 25, chance: 100f)));

        Assert.True(loot.Take(0));
        Assert.False(loot.Take(0));

        Assert.Equal(1, loot.At(1)!.Index);
        Assert.False(loot.At(1)!.IsLooted);
        Assert.False(loot.IsEmpty);
    }
}

/// <summary>The money roll, including the branch nothing in a starting zone takes.</summary>
public sealed class LootMoneyTests
{
    /// <summary>No maximum means no money.</summary>
    [Fact]
    public void NoMaximum_MeansNoMoney() =>
        Assert.Equal(0u, LootRoll.RollMoney(0, 0, (min, max) => max));

    /// <summary>A maximum at or below the minimum is taken flat, without a draw.</summary>
    [Fact]
    public void AMaximumAtTheMinimum_IsFlat() =>
        Assert.Equal(50u, LootRoll.RollMoney(50, 50, (min, max) => throw new InvalidOperationException()));

    /// <summary>An ordinary range is rolled directly.</summary>
    [Fact]
    public void AnOrdinaryRange_IsRolledDirectly() =>
        Assert.Equal(37u, LootRoll.RollMoney(10, 100, (min, max) => 37));

    /// <summary>
    /// A range wider than 32,700 is rolled in units of 256 and shifted back.
    /// </summary>
    /// <remarks>
    /// Upstream's shape, kept because the result differs: the answer is always a multiple of 256.
    /// Nothing in a starting zone reaches this branch, which is why it is easy to drop and never
    /// notice.
    /// </remarks>
    [Fact]
    public void AVeryWideRange_IsRolledInUnitsOf256()
    {
        uint rolled = LootRoll.RollMoney(0, 100_000, (min, max) => max);

        Assert.Equal(100_000u >> 8 << 8, rolled);
        Assert.Equal(0u, rolled % 256);
    }
}

/// <summary>The loot packets.</summary>
public sealed class LootPacketTests
{
    private static readonly ObjectGuid Corpse = ObjectGuid.Create(HighGuid.Unit, 299, 42);

    /// <summary>The window's body reads back field by field.</summary>
    [Fact]
    public void AWindow_ReadsBackFieldByField()
    {
        PacketWriter writer = new();

        LootResponse.Write(writer, Corpse, LootType.Corpse, gold: 137,
        [
            new LootSlot(0, 2589, 3, 6836, LootSlotType.AllowLoot),
        ]);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt64(out ulong target));
        Assert.Equal(Corpse.Value, target);

        Assert.True(reader.TryReadUInt8(out byte lootType));
        Assert.Equal(LootType.Corpse, lootType);

        Assert.True(reader.TryReadUInt32(out uint gold));
        Assert.Equal(137u, gold);

        Assert.True(reader.TryReadUInt8(out byte count));
        Assert.Equal(1, count);

        Assert.True(reader.TryReadUInt8(out byte slot));
        Assert.Equal(0, slot);

        Assert.True(reader.TryReadUInt32(out uint itemId));
        Assert.Equal(2589u, itemId);

        Assert.True(reader.TryReadUInt32(out uint itemCount));
        Assert.Equal(3u, itemCount);

        Assert.True(reader.TryReadUInt32(out uint displayId));
        Assert.Equal(6836u, displayId);

        // Random suffix and property.
        reader.Skip(4 + 4);

        Assert.True(reader.TryReadUInt8(out byte slotType));
        Assert.Equal(LootSlotType.AllowLoot, slotType);

        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// The count is of the slots written, which is not the pile's size.
    /// </summary>
    /// <remarks>
    /// A taken slot is left out and keeps its number, so a window of three items with the first
    /// taken has a count of two and a highest number of two. Writing the pile's size leaves the
    /// client reading past the end.
    /// </remarks>
    [Fact]
    public void TheCount_IsOfTheSlotsWritten()
    {
        PacketWriter writer = new();

        LootResponse.Write(writer, Corpse, LootType.Corpse, 0,
        [
            new LootSlot(1, 2589, 1, 0, LootSlotType.AllowLoot),
            new LootSlot(2, 25, 1, 0, LootSlotType.AllowLoot),
        ]);

        PacketReader reader = new(writer.WrittenSpan.ToArray());
        reader.Skip(8 + 1 + 4);

        Assert.True(reader.TryReadUInt8(out byte count));
        Assert.Equal(2, count);

        Assert.True(reader.TryReadUInt8(out byte firstSlot));
        Assert.Equal(1, firstSlot);
    }

    /// <summary>A refusal is the same opcode with a loot type of zero.</summary>
    /// <remarks>
    /// The client has already drawn the window frame. Silence leaves it up, empty and unclosable.
    /// </remarks>
    [Fact]
    public void ARefusal_IsALootTypeOfZeroAndAReason()
    {
        PacketWriter writer = new();
        LootResponse.WriteError(writer, Corpse, (byte)LootError.DidNotKill);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt64(out ulong target));
        Assert.Equal(Corpse.Value, target);

        Assert.True(reader.TryReadUInt8(out byte lootType));
        Assert.Equal(LootResponse.NoLoot, lootType);

        Assert.True(reader.TryReadUInt8(out byte reason));
        Assert.Equal((byte)LootError.DidNotKill, reason);

        Assert.Equal(0, reader.Remaining);
    }
}

/// <summary>Looting a corpse through a real map.</summary>
public sealed class MapLootTests(ITestOutputHelper output)
{
    private static readonly ItemTemplate Cloth =
        ItemFixture.Build(entry: 2589, name: "Linen Cloth", stackable: 20);

    /// <summary>A map whose wolf drops one linen cloth, and 50 copper.</summary>
    private static (Map Map, Player Player, Creature Victim, MapCombatFixture.Link Link) Kill(
        uint minGold = 50, uint maxGold = 50, float chance = 100f)
    {
        LootStore creatureLoot = new("creature_loot_template");

        System.Reflection.FieldInfo field = typeof(LootStore)
            .GetField("_templates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        ((Dictionary<uint, LootTemplate>)field.GetValue(creatureLoot)!)[299] =
            LootFixture.Template(LootFixture.Row(entry: 299, itemId: 2589, chance: chance));

        (Map map, Player player, Creature victim, MapCombatFixture.Link link) = MapCombatFixture.Engaged(
            items: LootFixture.Items(Cloth),
            creatureLoot: creatureLoot,
            lootReferences: LootFixture.NoReferences(),
            lootId: 299,
            minGold: minGold,
            maxGold: maxGold);

        player.AttackStop();

        // The threat list is what decides who owns the corpse, so the kill has to come through it.
        victim.Threat.AddThreat(player, 100f);
        victim.Health = 0;

        map.Kill(victim);

        return (map, player, victim, link);
    }

    /// <summary>A kill leaves a lootable corpse, and the flag is what makes it sparkle.</summary>
    /// <remarks>
    /// Without the dynamic flag the client never sends <c>CMSG_LOOT</c> at all, so the loot exists
    /// and cannot be reached.
    /// </remarks>
    [Fact]
    public void AKill_LeavesALootableCorpse()
    {
        (_, Player player, Creature victim, _) = Kill();

        Assert.NotNull(victim.Loot);
        Assert.Equal(player.Guid, victim.Loot.Owner);
        Assert.Equal(UnitDynamicFlags.Lootable, victim.DynamicFlags & UnitDynamicFlags.Lootable);
    }

    /// <summary>A corpse worth nothing is not marked lootable.</summary>
    /// <remarks>
    /// A sparkling corpse that opens an empty window is worse than one that does not sparkle.
    /// </remarks>
    [Fact]
    public void ACorpseWorthNothing_IsNotLootable()
    {
        (_, _, Creature victim, _) = Kill(minGold: 0, maxGold: 0, chance: 0f);

        Assert.Null(victim.Loot);
        Assert.Equal(0u, victim.DynamicFlags & UnitDynamicFlags.Lootable);
    }

    /// <summary>Opening the window shows what is there.</summary>
    [Fact]
    public void Opening_ShowsWhatIsThere()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) = Kill();

        map.OpenLoot(player, victim.Guid);

        Assert.Empty(link.LootErrors);
        Assert.Single(link.LootWindows);

        (ObjectGuid target, uint gold, IReadOnlyList<LootSlot> slots) = link.LootWindows[0];

        Assert.Equal(victim.Guid, target);
        Assert.Equal(50u, gold);
        Assert.Single(slots);
        Assert.Equal(2589u, slots[0].ItemId);

        output.WriteLine($"window: {gold} copper and {slots.Count} item(s)");
    }

    /// <summary>Someone who did not make the kill is refused.</summary>
    [Fact]
    public void SomeoneElse_IsRefused()
    {
        (Map map, _, Creature victim, MapCombatFixture.Link link) = Kill();

        Player stranger = InventoryFixture.Player();
        stranger.Connection = link;

        map.OpenLoot(stranger, victim.Guid);

        Assert.Equal([LootError.DidNotKill], link.LootErrors);
        Assert.Empty(link.LootWindows);
    }

    /// <summary>Standing too far away is refused, with the client's own message.</summary>
    [Fact]
    public void StandingTooFarAway_IsRefused()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) = Kill();

        player.Position = new Position(500f, 0f, 0f, 0f);

        map.OpenLoot(player, victim.Guid);

        Assert.Equal([LootError.TooFar], link.LootErrors);
    }

    /// <summary>Taking a slot puts the item in the bags and tells the client both things.</summary>
    [Fact]
    public void Taking_PutsTheItemInTheBags()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) = Kill();

        map.OpenLoot(player, victim.Guid);
        map.TakeLoot(player, 0);

        Assert.Equal(1u, player.Inventory.CountOf(2589));
        Assert.Equal([(byte)0], link.LootRemoved);
        Assert.Single(link.ItemsPushed);
        Assert.Equal(2589u, link.ItemsPushed[0].Entry);
        Assert.Equal(1u, link.ItemsPushed[0].TotalOfEntry);
    }

    /// <summary>The same slot cannot be taken twice.</summary>
    [Fact]
    public void ASlotCannotBeTakenTwice()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) = Kill();

        map.OpenLoot(player, victim.Guid);
        map.TakeLoot(player, 0);
        map.TakeLoot(player, 0);

        Assert.Equal(1u, player.Inventory.CountOf(2589));
        Assert.Single(link.LootRemoved);
        Assert.Equal([LootError.NoLoot], link.LootErrors);
    }

    /// <summary>
    /// A full inventory leaves the item in the window.
    /// </summary>
    /// <remarks>
    /// Marking the slot taken before the store succeeds destroys the item: it is gone from the
    /// window and nothing is holding it.
    /// </remarks>
    [Fact]
    public void AFullInventory_LeavesTheItemInTheWindow()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) = Kill();

        ItemTemplate filler = ItemFixture.Build(entry: 100, stackable: 1);

        for (byte i = 0; i < 16; i++)
        {
            InventoryFixture.Place(player, filler, InventoryFixture.Backpack(i));
        }

        map.OpenLoot(player, victim.Guid);
        map.TakeLoot(player, 0);

        Assert.Equal(0u, player.Inventory.CountOf(2589));
        Assert.False(victim.Loot!.At(0)!.IsLooted);
        Assert.Empty(link.LootRemoved);
    }

    /// <summary>Taking the money adds it to the character's purse, once.</summary>
    [Fact]
    public void TakingTheMoney_AddsItToThePurse()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) = Kill();

        uint before = player.Money;

        map.OpenLoot(player, victim.Guid);
        map.TakeLootMoney(player);
        map.TakeLootMoney(player);

        Assert.Equal(before + 50, player.Money);
        Assert.Equal([50u], link.LootMoney);
    }

    /// <summary>An emptied corpse stops sparkling.</summary>
    [Fact]
    public void AnEmptiedCorpse_StopsSparkling()
    {
        (Map map, Player player, Creature victim, _) = Kill();

        map.OpenLoot(player, victim.Guid);
        map.TakeLootMoney(player);
        map.TakeLoot(player, 0);

        Assert.Null(victim.Loot);
        Assert.Equal(0u, victim.DynamicFlags & UnitDynamicFlags.Lootable);
    }

    /// <summary>Releasing closes the window and forgets what was open.</summary>
    [Fact]
    public void Releasing_ClosesTheWindow()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) = Kill();

        map.OpenLoot(player, victim.Guid);

        Assert.Equal(victim.Guid, player.LootTarget);

        map.ReleaseLoot(player);

        Assert.Equal(ObjectGuid.Empty, player.LootTarget);
        Assert.Equal([victim.Guid], link.LootReleases);

        // With nothing open, a take does nothing rather than reaching back into the corpse.
        map.TakeLoot(player, 0);

        Assert.Equal(0u, player.Inventory.CountOf(2589));
    }

    /// <summary>A live creature cannot be looted.</summary>
    [Fact]
    public void ALiveCreature_CannotBeLooted()
    {
        (Map map, Player player, Creature victim, MapCombatFixture.Link link) = Kill();

        victim.DeathState = DeathState.Alive;

        map.OpenLoot(player, victim.Guid);

        Assert.Equal([LootError.NoLoot], link.LootErrors);
    }
}

/// <summary>The loot tables, over the real vendored rows.</summary>
public sealed class LootStoreTests(ITestOutputHelper output)
{
    private static CancellationToken TestToken => CancellationToken.None;

    [RequiresWorldDatabaseFact]
    public async Task TheStores_LoadEveryRow()
    {
        LootStore creatures = new("creature_loot_template");
        LootStore references = new("reference_loot_template");

        await creatures.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await references.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(creatures.RowCount > 330_000, $"only {creatures.RowCount} creature loot rows");
        Assert.True(references.RowCount > 14_000, $"only {references.RowCount} reference rows");

        output.WriteLine($"{creatures}; {references}");
    }

    /// <summary>
    /// Every reference in the creature table resolves.
    /// </summary>
    /// <remarks>
    /// A dangling reference is a drop that silently never happens. Finding them by playing would
    /// mean noticing that a particular creature never drops a particular thing.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task EveryReference_Resolves()
    {
        LootStore creatures = new("creature_loot_template");
        LootStore references = new("reference_loot_template");

        await creatures.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await references.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        ItemTemplateStore items = new();
        await items.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        HashSet<uint> dangling = [];
        int referenceRows = 0;

        foreach (ItemTemplate _ in items.All)
        {
            break;
        }

        for (uint entry = 1; entry < 100_000; entry++)
        {
            if (!creatures.TryGet(entry, out LootTemplate? template) || template is null)
            {
                continue;
            }

            foreach (LootStoreItem row in AllRows(template))
            {
                if (!row.IsReference)
                {
                    continue;
                }

                referenceRows++;

                if (!references.TryGet(row.ReferenceId, out _))
                {
                    dangling.Add(row.ReferenceId);
                }
            }
        }

        output.WriteLine($"{referenceRows} reference rows, {dangling.Count} dangling");

        Assert.True(referenceRows > 0, "no reference rows at all — is mincountOrRef being read signed?");
        Assert.Empty(dangling);
    }

    /// <summary>
    /// Rolling every creature's loot produces nothing impossible.
    /// </summary>
    /// <remarks>
    /// The cheapest way to find a row this reader gets wrong: a negative count read unsigned, a
    /// reference cycle, a group that never terminates. Playing would find them one creature at a
    /// time.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task EveryCreaturesLoot_RollsWithoutTrouble()
    {
        LootStore creatures = new("creature_loot_template");
        LootStore references = new("reference_loot_template");
        ItemTemplateStore items = new();

        await creatures.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await references.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await items.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        int rolled = 0;
        int withSomething = 0;
        int biggest = 0;

        // Everything drops, so every branch is walked rather than a lucky few.
        for (uint entry = 1; entry < 3000; entry++)
        {
            if (!creatures.TryGet(entry, out LootTemplate? template) || template is null)
            {
                continue;
            }

            Loot loot = new();

            LootRoll.Fill(
                loot, template, references, items,
                LootFixture.Always(0f), LootFixture.First, LootFixture.Highest);

            rolled++;
            biggest = Math.Max(biggest, loot.SlotCount);

            if (loot.SlotCount > 0)
            {
                withSomething++;
            }

            Assert.True(loot.SlotCount <= Loot.MaxItems, $"entry {entry} produced {loot.SlotCount} slots");
        }

        Assert.True(rolled > 100, $"only {rolled} templates in the first 3000 entries");
        Assert.True(withSomething > 0, "nothing dropped anything even at a 0% roll");

        output.WriteLine($"rolled {rolled} templates, {withSomething} dropped something, biggest {biggest} slots");
    }

    private static IEnumerable<LootStoreItem> AllRows(LootTemplate template)
    {
        foreach (LootStoreItem row in template.Ungrouped)
        {
            yield return row;
        }

        foreach (LootGroup group in template.Groups.Values)
        {
            foreach (LootStoreItem row in group.ExplicitlyChanced)
            {
                yield return row;
            }

            foreach (LootStoreItem row in group.EqualChanced)
            {
                yield return row;
            }
        }
    }
}
