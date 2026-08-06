using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Protocol;
using WowEmu.WorldServer;
using Xunit.Abstractions;

namespace WowEmu.Tests.Unit;

/// <summary>The gossip and vendor packets.</summary>
public sealed class GossipPacketTests
{
    private static readonly ObjectGuid Npc = ObjectGuid.Create(HighGuid.Unit, 823, 42);

    /// <summary>The gossip window's head reads back field by field.</summary>
    [Fact]
    public void AGossipMenu_ReadsBackFieldByField()
    {
        PacketWriter writer = new();

        GossipPackets.WriteGossipMenu(
            writer, Npc, menuId: 21, textId: 518,
            [new GossipLine(1, 1, false, 0, "I want to browse your goods", string.Empty)],
            []);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt64(out ulong npc));
        Assert.Equal(Npc.Value, npc);

        Assert.True(reader.TryReadUInt32(out uint menuId));
        Assert.Equal(21u, menuId);

        Assert.True(reader.TryReadUInt32(out uint textId));
        Assert.Equal(518u, textId);

        Assert.True(reader.TryReadUInt32(out uint lineCount));
        Assert.Equal(1u, lineCount);

        Assert.True(reader.TryReadUInt32(out uint index));
        Assert.Equal(1u, index);

        Assert.True(reader.TryReadUInt8(out byte icon));
        Assert.Equal(1, icon);

        Assert.True(reader.TryReadUInt8(out byte coded));
        Assert.Equal(0, coded);

        Assert.True(reader.TryReadUInt32(out uint boxMoney));
        Assert.Equal(0u, boxMoney);

        Assert.True(reader.TryReadCString(out string? text));
        Assert.Equal("I want to browse your goods", text);

        Assert.True(reader.TryReadCString(out string? boxText));
        Assert.Equal(string.Empty, boxText);

        Assert.True(reader.TryReadUInt32(out uint questCount));
        Assert.Equal(0u, questCount);

        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>
    /// The quests ride in the same packet as the gossip lines.
    /// </summary>
    /// <remarks>
    /// Which is what puts both in one window. Sending them separately produces two, and the client
    /// closes one at once.
    /// </remarks>
    [Fact]
    public void TheQuests_RideInTheSamePacket()
    {
        PacketWriter withQuest = new();

        GossipPackets.WriteGossipMenu(
            withQuest, Npc, 21, 518, [],
            [new QuestMenuEntry(5261, QuestMenuIcon.Available, 2, 0, false, "Eagan Peltskinner")]);

        PacketReader reader = new(withQuest.WrittenSpan.ToArray());
        reader.Skip(8 + 4 + 4 + 4);

        Assert.True(reader.TryReadUInt32(out uint questCount));
        Assert.Equal(1u, questCount);

        Assert.True(reader.TryReadUInt32(out uint questId));
        Assert.Equal(5261u, questId);
    }

    /// <summary>
    /// The npc-text packet always writes eight blocks.
    /// </summary>
    /// <remarks>
    /// The client reads a fixed eight probability-and-text groups and picks between them. Writing
    /// only the one with something in it leaves it reading the rest of the packet as text.
    /// </remarks>
    [Fact]
    public void TheNpcText_AlwaysWritesEightBlocks()
    {
        PacketWriter writer = new();
        GossipPackets.WriteNpcText(writer, 518, "Hello there.");

        // Four for the id, then eight blocks of: probability, two strings, language, six emote
        // words. With empty strings that is 4 + 8 × (4 + 1 + 1 + 4 + 24) = 276, plus the one real
        // pair of strings.
        int emptyBlock = 4 + 1 + 1 + 4 + (6 * 4);
        int firstBlock = 4 + ("Hello there.".Length + 1) * 2 + 4 + (6 * 4);

        Assert.Equal(4 + firstBlock + (7 * emptyBlock), writer.WrittenSpan.Length);

        PacketReader reader = new(writer.WrittenSpan.ToArray());

        Assert.True(reader.TryReadUInt32(out uint textId));
        Assert.Equal(518u, textId);

        // Only the first block has any probability, so the client always picks it.
        Assert.True(reader.TryReadSingle(out float probability));
        Assert.Equal(1.0f, probability);

        Assert.True(reader.TryReadCString(out string? male));
        Assert.Equal("Hello there.", male);
    }

    /// <summary>
    /// An empty vendor list is a different shape from a list with no entries.
    /// </summary>
    /// <remarks>
    /// A count of zero followed by an error byte. Writing the ordinary form leaves the client
    /// waiting for a byte that never comes.
    /// </remarks>
    [Fact]
    public void AnEmptyVendorList_HasItsOwnShape()
    {
        PacketWriter writer = new();
        GossipPackets.WriteVendorList(writer, Npc, []);

        Assert.Equal(8 + 1 + 1, writer.WrittenSpan.Length);

        PacketReader reader = new(writer.WrittenSpan.ToArray());
        reader.Skip(8);

        Assert.True(reader.TryReadUInt8(out byte count));
        Assert.Equal(0, count);

        Assert.True(reader.TryReadUInt8(out byte error));
        Assert.Equal(0, error);
    }

    /// <summary>
    /// A vendor slot is one-based, and unlimited stock is -1.
    /// </summary>
    /// <remarks>
    /// The client subtracts one before sending a purchase back, so a zero-based slot buys the item
    /// before the one clicked — or nothing, for the first. And a stock of zero greys the line out
    /// as sold, which is not what an unlimited supply should look like.
    /// </remarks>
    [Fact]
    public void AVendorLine_IsOneBasedWithUnlimitedStockAsMinusOne()
    {
        PacketWriter writer = new();

        GossipPackets.WriteVendorList(writer, Npc,
            [new VendorLine(1, 25, 1542, -1, 35, 55, 1, 0)]);

        PacketReader reader = new(writer.WrittenSpan.ToArray());
        reader.Skip(8);

        Assert.True(reader.TryReadUInt8(out byte count));
        Assert.Equal(1, count);

        Assert.True(reader.TryReadUInt32(out uint slot));
        Assert.Equal(1u, slot);

        Assert.True(reader.TryReadUInt32(out uint itemId));
        Assert.Equal(25u, itemId);

        Assert.True(reader.TryReadUInt32(out uint displayId));
        Assert.Equal(1542u, displayId);

        Assert.True(reader.TryReadUInt32(out uint inStock));
        Assert.Equal(0xFFFFFFFFu, inStock);

        Assert.True(reader.TryReadUInt32(out uint price));
        Assert.Equal(35u, price);
    }

    /// <summary>
    /// The buy-failure parameter appears only when it is non-zero.
    /// </summary>
    /// <remarks>
    /// The packet's length is how the client tells whether one is there. Writing a zero shifts the
    /// reason byte into it, and the client reads a reason of zero — "can't find item" — whatever
    /// really went wrong.
    /// </remarks>
    [Fact]
    public void TheBuyFailureParameter_IsOptional()
    {
        PacketWriter without = new();
        GossipPackets.WriteBuyFailed(without, Npc, 25, BuyResult.NotEnoughMoney);

        PacketWriter with = new();
        GossipPackets.WriteBuyFailed(with, Npc, 25, BuyResult.RankRequire, parameter: 3);

        Assert.Equal(8 + 4 + 1, without.WrittenSpan.Length);
        Assert.Equal(8 + 4 + 4 + 1, with.WrittenSpan.Length);

        Assert.Equal((byte)BuyResult.NotEnoughMoney, without.WrittenSpan[^1]);
    }
}

/// <summary>The gossip and vendor tables, over the real vendored rows.</summary>
public sealed class GossipStoreTests(ITestOutputHelper output)
{
    private static CancellationToken TestToken => CancellationToken.None;

    [RequiresWorldDatabaseFact]
    public async Task TheStores_LoadEveryRow()
    {
        GossipStore gossip = new();
        VendorStore vendors = new();

        await gossip.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await vendors.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        Assert.True(gossip.MenuCount > 4_000, $"only {gossip.MenuCount} menus");
        Assert.True(gossip.OptionCount > 3_000, $"only {gossip.OptionCount} options");
        Assert.True(gossip.TextCount > 6_000, $"only {gossip.TextCount} texts");
        Assert.True(vendors.RowCount > 37_000, $"only {vendors.RowCount} vendor rows");

        output.WriteLine($"{gossip}; {vendors}");
    }

    /// <summary>
    /// A negative item in npc_vendor is a reference to another vendor's list.
    /// </summary>
    /// <remarks>
    /// The same overloaded-sign trick as <c>mincountOrRef</c> in the loot tables — and the same
    /// failure if it is read unsigned: the column is a signed <c>mediumint</c>, so
    /// <c>GetUInt32</c> throws outright rather than producing a wrong answer. That is the lucky
    /// case; the loot table's version fails silently.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task ANegativeVendorItem_IsAReferenceAndIsFlattenedAway()
    {
        VendorStore vendors = new();
        await vendors.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        int expanded = 0;

        for (uint entry = 1; entry < 100_000; entry++)
        {
            foreach (VendorItem item in vendors.For(entry))
            {
                // Nothing downstream should ever see a reference.
                Assert.True(item.ItemId > 0, $"vendor {entry} still has a reference row");
                expanded++;
            }
        }

        Assert.True(expanded > 0, "no vendor rows at all");

        output.WriteLine($"{expanded} flattened rows from {vendors.RowCount} table rows");
    }

    /// <summary>
    /// Menu 0 is shared by nearly every service NPC, and its lines are filtered by NPC flag.
    /// </summary>
    /// <remarks>
    /// That filter is the whole mechanism: one row saying "I want to browse your goods" serves
    /// every vendor in the game, and appears only on an NPC with the vendor bit. Ignoring the mask
    /// offers stabling and flight paths from a shopkeeper.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task TheSharedMenu_FiltersItsLinesByNpcFlag()
    {
        GossipStore gossip = new();
        await gossip.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        IReadOnlyList<GossipMenuOption> shared = gossip.OptionsFor(0);

        Assert.NotEmpty(shared);

        GossipMenuOption? browse = null;

        foreach (GossipMenuOption option in shared)
        {
            if (option.OptionType == GossipOption.Vendor)
            {
                browse = option;
                break;
            }
        }

        Assert.NotNull(browse);
        Assert.Equal(NpcFlags.Vendor, browse.NpcFlagRequired);

        output.WriteLine(
            $"menu 0 has {shared.Count} options; the vendor line is '{browse.Text}' "
            + $"gated on flag 0x{browse.NpcFlagRequired:X}");
    }

    /// <summary>A known vendor sells what it should.</summary>
    [RequiresWorldDatabaseFact]
    public async Task AKnownVendor_SellsWhatItShould()
    {
        VendorStore vendors = new();
        ItemTemplateStore items = new();

        await vendors.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await items.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        // Brother Danil, the Northshire Abbey provisioner.
        IReadOnlyList<VendorItem> stock = vendors.For(1247);

        Assert.NotEmpty(stock);

        List<string> names = [];

        foreach (VendorItem line in stock)
        {
            if (items.TryGet(line.ItemId, out ItemTemplate? template) && template is not null)
            {
                names.Add(template.Name);
            }
        }

        Assert.NotEmpty(names);

        output.WriteLine($"vendor 1247 sells {stock.Count}: {string.Join(", ", names)}");
    }

    /// <summary>
    /// Every menu an NPC opens has something to say.
    /// </summary>
    /// <remarks>
    /// A menu with no text row opens a window with an empty body. Not fatal, and worth knowing
    /// the size of — the count is reported rather than asserted to zero, because the data really
    /// does have some.
    /// </remarks>
    [RequiresWorldDatabaseFact]
    public async Task MenusWithoutText_AreCounted()
    {
        GossipStore gossip = new();
        CreatureTemplateStore creatures = new();

        await gossip.LoadAsync(WorldDatabase.ConnectionString, TestToken);
        await creatures.LoadAsync(WorldDatabase.ConnectionString, TestToken);

        int withMenu = 0;
        int silent = 0;

        foreach (CreatureTemplate template in creatures.All)
        {
            if (template.GossipMenuId == 0)
            {
                continue;
            }

            withMenu++;

            if (gossip.TextIdFor(template.GossipMenuId) == 0)
            {
                silent++;
            }
        }

        Assert.True(withMenu > 0, "no creature has a gossip menu — is the column being read?");

        output.WriteLine($"{withMenu} creatures with a gossip menu, {silent} whose menu has no text row");
    }
}
