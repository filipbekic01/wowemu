using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>
/// One row of a <c>*_loot_template</c> table.
/// </summary>
/// <remarks>
/// Port of <c>LootStoreItem</c>. Two columns carry two meanings apiece, which is where most of the
/// difficulty in reading this table lives:
/// <list type="bullet">
/// <item><b><c>mincountOrRef</c> is a minimum count when positive and a reference id when
/// negative.</b> Reading it as a count regardless turns every shared drop list into a request for
/// a negative number of items.</item>
/// <item><b><c>ChanceOrQuestChance</c> is negative for a quest drop</b>, and the chance is its
/// absolute value. Reading the sign as a chance makes every quest item impossible.</item>
/// </list>
/// </remarks>
public sealed record LootStoreItem(
    uint Entry,
    uint ItemId,
    float Chance,
    ushort LootMode,
    byte GroupId,
    int MinCountOrReference,
    byte MaxCount)
{
    /// <summary>Whether the row points at another template rather than naming an item.</summary>
    public bool IsReference => MinCountOrReference < 0;

    /// <summary>Which template a reference row points at.</summary>
    public uint ReferenceId => IsReference ? (uint)(-MinCountOrReference) : 0;

    /// <summary>The smallest stack this row can produce. Meaningless for a reference.</summary>
    public uint MinCount => IsReference ? 0 : (uint)MinCountOrReference;

    /// <summary>Whether only someone on the right quest sees it.</summary>
    /// <remarks>
    /// The sign of the chance column, and nothing else. There is no separate flag.
    /// </remarks>
    public bool NeedsQuest => Chance < 0f;

    /// <summary>
    /// The real drop chance, as a percentage.
    /// </summary>
    /// <remarks>
    /// Always positive. A group member with a chance of exactly zero is <i>equal-chanced</i> rather
    /// than impossible — see <see cref="LootGroup"/>.
    /// </remarks>
    public float DropChance => MathF.Abs(Chance);

    /// <summary>Whether the row belongs to a group, which is rolled as a unit.</summary>
    public bool IsGrouped => GroupId > 0;
}

/// <summary>
/// One group within a loot template: at most one of its members drops.
/// </summary>
/// <remarks>
/// Port of <c>LootTemplate::LootGroup</c>. The two halves are rolled differently and in order:
/// <list type="number">
/// <item>the explicitly-chanced members, against one roll over 0-100, subtracting as it walks;</item>
/// <item>if none won, one of the equal-chanced members, picked uniformly.</item>
/// </list>
/// A member with a chance of exactly zero is in the second list, not the first. Treating it as a
/// 0% chance means a group of equal-chanced items never drops anything at all — which is most of
/// the groups in the table.
/// </remarks>
public sealed class LootGroup
{
    private readonly List<LootStoreItem> _explicitlyChanced = [];
    private readonly List<LootStoreItem> _equalChanced = [];

    public IReadOnlyList<LootStoreItem> ExplicitlyChanced => _explicitlyChanced;

    public IReadOnlyList<LootStoreItem> EqualChanced => _equalChanced;

    public int Count => _explicitlyChanced.Count + _equalChanced.Count;

    internal void Add(LootStoreItem item)
    {
        if (item.DropChance > 0f)
        {
            _explicitlyChanced.Add(item);
        }
        else
        {
            _equalChanced.Add(item);
        }
    }

    /// <summary>
    /// Picks at most one member.
    /// </summary>
    /// <remarks>
    /// The explicitly-chanced walk subtracts from a single roll rather than rolling per member, so
    /// the chances share one 0-100 space and cannot sum past certainty. Rolling each separately
    /// would let a group drop two items.
    /// </remarks>
    /// <param name="rollPercent">Draws a number in <c>[0, 100)</c>.</param>
    /// <param name="pick">Picks an index below the count it is given.</param>
    public LootStoreItem? Roll(Func<float> rollPercent, Func<int, int> pick)
    {
        ArgumentNullException.ThrowIfNull(rollPercent);
        ArgumentNullException.ThrowIfNull(pick);

        if (_explicitlyChanced.Count > 0)
        {
            float roll = rollPercent();

            foreach (LootStoreItem item in _explicitlyChanced)
            {
                if (item.DropChance >= 100f)
                {
                    return item;
                }

                roll -= item.DropChance;

                if (roll < 0f)
                {
                    return item;
                }
            }
        }

        if (_equalChanced.Count > 0)
        {
            return _equalChanced[pick(_equalChanced.Count)];
        }

        // Every explicitly-chanced member missed and there is nothing equal-chanced to fall back
        // on. An empty group is normal, not an error.
        return null;
    }
}

/// <summary>
/// Everything one loot id can drop: the ungrouped rows, and the groups.
/// </summary>
/// <remarks>
/// Port of <c>LootTemplate</c>. Ungrouped rows are rolled independently — a creature can drop all
/// of them, or none — while each group contributes at most one item.
/// </remarks>
public sealed class LootTemplate
{
    private readonly List<LootStoreItem> _ungrouped = [];
    private readonly Dictionary<byte, LootGroup> _groups = [];

    public IReadOnlyList<LootStoreItem> Ungrouped => _ungrouped;

    public IReadOnlyDictionary<byte, LootGroup> Groups => _groups;

    /// <summary>How many rows this template holds, groups included.</summary>
    public int Count
    {
        get
        {
            int total = _ungrouped.Count;

            foreach (LootGroup group in _groups.Values)
            {
                total += group.Count;
            }

            return total;
        }
    }

    internal void Add(LootStoreItem item)
    {
        if (!item.IsGrouped)
        {
            _ungrouped.Add(item);

            return;
        }

        if (!_groups.TryGetValue(item.GroupId, out LootGroup? group))
        {
            group = new LootGroup();
            _groups[item.GroupId] = group;
        }

        group.Add(item);
    }
}

/// <summary>
/// One <c>*_loot_template</c> table, loaded once at startup.
/// </summary>
/// <remarks>
/// The creature table is 336,755 rows, which is why it is read once and kept rather than queried
/// per kill: a database round trip inside the map tick is exactly the thing PLAN.md §4.2 forbids.
/// </remarks>
public sealed class LootStore(string tableName)
{
    private readonly Dictionary<uint, LootTemplate> _templates = [];

    /// <summary>Which table this was read from, for the startup log and error messages.</summary>
    public string TableName { get; } = tableName;

    /// <summary>How many loot ids are known.</summary>
    public int Count => _templates.Count;

    /// <summary>How many rows were read, across every id.</summary>
    public int RowCount { get; private set; }

    public bool TryGet(uint lootId, out LootTemplate? template) => _templates.TryGetValue(lootId, out template);

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _templates.Clear();
        RowCount = 0;

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT entry, item, ChanceOrQuestChance, lootmode, groupid, mincountOrRef, maxcount FROM {TableName}";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            LootStoreItem item = new(
                Entry: reader.GetUInt32(0),
                ItemId: reader.GetUInt32(1),
                Chance: reader.GetFloat(2),
                LootMode: reader.GetUInt16(3),
                GroupId: reader.GetByte(4),
                MinCountOrReference: reader.GetInt32(5),
                MaxCount: reader.GetByte(6));

            if (!_templates.TryGetValue(item.Entry, out LootTemplate? template))
            {
                template = new LootTemplate();
                _templates[item.Entry] = template;
            }

            template.Add(item);
            RowCount++;
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{RowCount} {TableName} rows across {Count} ids");
}
