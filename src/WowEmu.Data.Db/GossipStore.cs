using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>
/// What an NPC offers. <c>NPCFlags</c>, in <c>UNIT_NPC_FLAGS</c>.
/// </summary>
/// <remarks>
/// A gossip menu option carries a mask of these, and it is shown only if the NPC actually has the
/// matching bit. That is how one shared menu row — "I want to browse your goods" is menu 0, option
/// 1 — serves every vendor in the game.
/// </remarks>
public static class NpcFlags
{
    public const uint Gossip = 0x00000001;
    public const uint QuestGiver = 0x00000002;
    public const uint Trainer = 0x00000010;
    public const uint ClassTrainer = 0x00000020;
    public const uint ProfessionTrainer = 0x00000040;
    public const uint Vendor = 0x00000080;
    public const uint VendorAmmo = 0x00000100;
    public const uint VendorFood = 0x00000200;
    public const uint VendorPoison = 0x00000400;
    public const uint VendorReagent = 0x00000800;
    public const uint Repair = 0x00001000;
    public const uint FlightMaster = 0x00002000;
    public const uint SpiritHealer = 0x00004000;
    public const uint Innkeeper = 0x00010000;
    public const uint Banker = 0x00020000;
    public const uint Auctioneer = 0x00200000;
    public const uint StableMaster = 0x00400000;
}

/// <summary>What a gossip option does when clicked. <c>GossipOptionIcon</c> and <c>_id</c>.</summary>
public static class GossipOption
{
    public const byte None = 0;
    public const byte Gossip = 1;
    public const byte QuestGiver = 2;
    public const byte Vendor = 3;
    public const byte Taxi = 4;
    public const byte Trainer = 5;
    public const byte SpiritHealer = 6;
    public const byte Innkeeper = 8;
    public const byte Banker = 9;
    public const byte Battlefield = 11;
    public const byte Auctioneer = 12;
    public const byte StableMaster = 14;
    public const byte Armorer = 15;
    public const byte Unlearntalents = 16;
}

/// <summary>One line in a gossip menu.</summary>
/// <param name="NpcFlagRequired">
/// The NPC must have this flag for the line to appear. It is what makes the shared menu 0 work: its
/// options are "browse your goods", "train me", "make this inn your home", and each is filtered by
/// the flag the NPC actually carries.
/// </param>
/// <param name="ActionMenuId">The menu this option opens, if it just leads somewhere else.</param>
public sealed record GossipMenuOption(
    uint MenuId,
    uint OptionId,
    byte Icon,
    string Text,
    byte OptionType,
    uint NpcFlagRequired,
    uint ActionMenuId,
    uint BoxMoney,
    bool BoxCoded,
    string BoxText);

/// <summary>
/// The gossip tables: which menu an NPC opens, what it says, and what is on it.
/// </summary>
/// <remarks>
/// Three tables that only make sense together. <c>creature_template.gossip_menu_id</c> names a
/// menu; <c>gossip_menu</c> maps that to one or more <c>npc_text</c> ids; <c>gossip_menu_option</c>
/// holds the clickable lines.
/// <para>
/// <b>A menu can have several text rows.</b> Upstream picks between them with conditions; with no
/// condition system the first is taken, which is what a player with no special standing would see
/// anyway.
/// </para>
/// </remarks>
public sealed class GossipStore
{
    private readonly Dictionary<uint, List<uint>> _menuTexts = [];
    private readonly Dictionary<uint, List<GossipMenuOption>> _menuOptions = [];
    private readonly Dictionary<uint, string> _npcText = [];

    /// <summary>How many menus have a text row.</summary>
    public int MenuCount => _menuTexts.Count;

    /// <summary>How many clickable lines there are, across every menu.</summary>
    public int OptionCount { get; private set; }

    /// <summary>How many <c>npc_text</c> rows carry something to say.</summary>
    public int TextCount => _npcText.Count;

    /// <summary>
    /// The text id a menu opens with, or zero.
    /// </summary>
    /// <remarks>
    /// The first row, because conditions are what upstream uses to pick between several and there
    /// is no condition system. The first is what an ordinary player sees.
    /// </remarks>
    public uint TextIdFor(uint menuId) =>
        _menuTexts.TryGetValue(menuId, out List<uint>? texts) && texts.Count > 0 ? texts[0] : 0;

    /// <summary>The clickable lines on a menu, in the order the table lists them.</summary>
    public IReadOnlyList<GossipMenuOption> OptionsFor(uint menuId) =>
        _menuOptions.TryGetValue(menuId, out List<GossipMenuOption>? options) ? options : [];

    /// <summary>
    /// What an <c>npc_text</c> row actually says.
    /// </summary>
    /// <remarks>
    /// Only the first of the eight text slots, and only the male variant. The eight are alternatives
    /// chosen by probability, and the second column is what a female character hears — both need
    /// more of the packet than this phase sends.
    /// </remarks>
    public string TextFor(uint textId) => _npcText.GetValueOrDefault(textId, string.Empty);

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _menuTexts.Clear();
        _menuOptions.Clear();
        _npcText.Clear();
        OptionCount = 0;

        await LoadMenusAsync(connection, cancellationToken).ConfigureAwait(false);
        await LoadOptionsAsync(connection, cancellationToken).ConfigureAwait(false);
        await LoadTextsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{MenuCount} gossip menus, {OptionCount} options, {TextCount} texts");

    private async Task LoadMenusAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT entry, text_id FROM gossip_menu ORDER BY entry, text_id";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint menuId = reader.GetUInt32(0);

            if (!_menuTexts.TryGetValue(menuId, out List<uint>? texts))
            {
                texts = [];
                _menuTexts[menuId] = texts;
            }

            texts.Add(reader.GetUInt32(1));
        }
    }

    private async Task LoadOptionsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT menu_id, id, option_icon, IFNULL(option_text, ''), option_id, npc_option_npcflag,
                   action_menu_id, box_money, box_coded, IFNULL(box_text, '')
            FROM gossip_menu_option
            ORDER BY menu_id, id
            """;

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            GossipMenuOption option = new(
                MenuId: reader.GetUInt32(0),
                OptionId: reader.GetUInt32(1),
                Icon: (byte)reader.GetUInt32(2),
                Text: reader.GetString(3),
                OptionType: reader.GetByte(4),
                NpcFlagRequired: reader.GetUInt32(5),
                ActionMenuId: reader.GetUInt32(6),
                BoxMoney: reader.GetUInt32(7),
                BoxCoded: reader.GetByte(8) != 0,
                BoxText: reader.GetString(9));

            if (!_menuOptions.TryGetValue(option.MenuId, out List<GossipMenuOption>? options))
            {
                options = [];
                _menuOptions[option.MenuId] = options;
            }

            options.Add(option);
            OptionCount++;
        }
    }

    /// <remarks>
    /// Only <c>text0_0</c> of the eight slots is read. The rest are alternatives picked by
    /// probability and a female variant, neither of which this phase's packet carries.
    /// </remarks>
    private async Task LoadTextsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ID, IFNULL(text0_0, '') FROM npc_text";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string text = reader.GetString(1);

            if (!string.IsNullOrEmpty(text))
            {
                _npcText[reader.GetUInt32(0)] = text;
            }
        }
    }
}

/// <summary>One line of a vendor's stock.</summary>
/// <remarks>
/// A <b>negative <c>item</c> in the table is a reference to another vendor's whole list</b>, not an
/// item — the same overloaded-sign trick as <c>mincountOrRef</c> in the loot tables. References are
/// flattened away at load, so nothing downstream ever sees one.
/// </remarks>
/// <param name="MaxCount">
/// How many are in stock, or <b>zero meaning unlimited</b> — which is nearly every row. Reading it
/// as a real count sells out every vendor in the game before the first purchase.
/// </param>
/// <param name="ExtendedCost">
/// A row in <c>ItemExtendedCost.dbc</c>: honour, arena points or tokens instead of gold. Anything
/// with one is not purchasable here.
/// </param>
public readonly record struct VendorItem(
    uint Entry,
    int Slot,
    uint ItemId,
    byte MaxCount,
    uint RestockSeconds,
    uint ExtendedCost)
{
    /// <summary>Whether the item is bought with money rather than with tokens.</summary>
    public bool IsGoldPurchase => ExtendedCost == 0;
}

/// <summary><c>npc_vendor</c>, loaded once at startup.</summary>
public sealed class VendorStore
{
    /// <summary>The client's own ceiling on a vendor list. <c>MAX_VENDOR_ITEMS</c>.</summary>
    public const int MaxItems = 150;

    private readonly Dictionary<uint, List<VendorItem>> _byVendor = [];

    public int Count => _byVendor.Count;

    public int RowCount { get; private set; }

    /// <summary>What one creature entry sells, in the table's own order.</summary>
    public IReadOnlyList<VendorItem> For(uint creatureEntry) =>
        _byVendor.TryGetValue(creatureEntry, out List<VendorItem>? items) ? items : [];

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _byVendor.Clear();
        RowCount = 0;

        // Read whole first, references and all, because a reference can point at a vendor whose
        // own rows have not been read yet — resolving as we go would depend on entry order.
        Dictionary<uint, List<VendorRow>> raw = [];

        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT entry, slot, item, maxcount, incrtime, ExtendedCost FROM npc_vendor ORDER BY entry, slot";

            await using MySqlDataReader reader =
                (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                uint entry = reader.GetUInt32(0);

                if (!raw.TryGetValue(entry, out List<VendorRow>? rows))
                {
                    rows = [];
                    raw[entry] = rows;
                }

                rows.Add(new VendorRow(
                    Slot: reader.GetInt32(1),

                    // Signed on purpose: negative is a reference to another vendor.
                    ItemIdOrReference: reader.GetInt32(2),
                    MaxCount: reader.GetByte(3),
                    RestockSeconds: reader.GetUInt32(4),
                    ExtendedCost: reader.GetUInt32(5)));

                RowCount++;
            }
        }

        foreach (uint entry in raw.Keys)
        {
            List<VendorItem> flattened = [];

            Flatten(entry, raw, flattened, depth: 0);
            _byVendor[entry] = flattened;
        }
    }

    /// <summary>One row exactly as the table holds it, before references are resolved.</summary>
    private readonly record struct VendorRow(
        int Slot, int ItemIdOrReference, byte MaxCount, uint RestockSeconds, uint ExtendedCost);

    /// <summary>
    /// Expands one vendor's rows, following references into other vendors' lists.
    /// </summary>
    /// <remarks>
    /// The depth is bounded because the data is not guaranteed acyclic and a cycle here would hang
    /// startup rather than produce a wrong answer.
    /// </remarks>
    private static void Flatten(
        uint entry, Dictionary<uint, List<VendorRow>> raw, List<VendorItem> into, int depth)
    {
        const int MaxReferenceDepth = 10;

        if (depth > MaxReferenceDepth || !raw.TryGetValue(entry, out List<VendorRow>? rows))
        {
            return;
        }

        foreach (VendorRow row in rows)
        {
            if (row.ItemIdOrReference < 0)
            {
                Flatten((uint)(-row.ItemIdOrReference), raw, into, depth + 1);

                continue;
            }

            into.Add(new VendorItem(
                Entry: entry,
                Slot: row.Slot,
                ItemId: (uint)row.ItemIdOrReference,
                MaxCount: row.MaxCount,
                RestockSeconds: row.RestockSeconds,
                ExtendedCost: row.ExtendedCost));
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{RowCount} npc_vendor rows across {Count} vendors");
}
