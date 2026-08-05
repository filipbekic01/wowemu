using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>Where a freshly created character of a given race and class starts.</summary>
public readonly record struct PlayerCreateInfo(
    byte Race,
    byte Class,
    ushort Map,
    uint Zone,
    float PositionX,
    float PositionY,
    float PositionZ,
    float Orientation);

/// <summary>An extra item a race and class starts with, beyond its <c>CharStartOutfit</c>.</summary>
public readonly record struct PlayerCreateItem(byte Race, byte Class, uint ItemId, uint Amount);

/// <summary>
/// The <c>playercreateinfo</c> table, loaded once at startup.
/// </summary>
/// <remarks>
/// The first slice of the <c>world</c> database, imported on demand exactly as PLAN.md §5.2
/// describes — Phase 4 brings in the other ~10 tables it needs, Phase 10 another ~40.
/// <para>
/// Read with a raw <see cref="MySqlDataReader"/> rather than EF, also per §5.2: <c>world</c> is
/// bulk-loaded once at startup and never written, so an ORM's change tracking is pure overhead on
/// a metric (startup time) that actually matters.
/// </para>
/// <para>
/// The 62 rows here are why character creation does not need DBC files: race and class validity,
/// and every starting position, are content data rather than client data.
/// </para>
/// </remarks>
public sealed class PlayerCreateInfoStore
{
    private readonly Dictionary<(byte Race, byte Class), PlayerCreateInfo> _entries = [];
    private readonly Dictionary<(byte Race, byte Class), List<PlayerCreateItem>> _items = [];

    /// <summary>How many race/class combinations are known.</summary>
    public int Count => _entries.Count;

    /// <summary>How many extra starting items are configured, across every race and class.</summary>
    public int ItemCount { get; private set; }

    /// <summary>Loads the table. Called once, before the server accepts connections.</summary>
    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT race, class, map, zone, position_x, position_y, position_z, orientation FROM playercreateinfo";

        _entries.Clear();

        // Scoped, because the second query runs on the same connection: MySQL allows one open
        // reader at a time, and leaving this one alive fails the next command rather than queueing
        // it.
        await using (MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                byte race = reader.GetByte(0);
                byte characterClass = reader.GetByte(1);

                _entries[(race, characterClass)] = new PlayerCreateInfo(
                    race,
                    characterClass,
                    (ushort)reader.GetInt32(2),
                    (uint)reader.GetInt32(3),
                    reader.GetFloat(4),
                    reader.GetFloat(5),
                    reader.GetFloat(6),
                    reader.GetFloat(7));
            }
        }

        await LoadItemsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <c>playercreateinfo_item</c>.
    /// </summary>
    /// <remarks>
    /// <c>amount</c> is a signed <c>tinyint</c>, and upstream reads a negative as "this many, but
    /// take it away again" for a race/class override. Nothing in the vendored data uses it, so a
    /// negative is skipped rather than handed out as a stack of 4 billion.
    /// </remarks>
    private async Task LoadItemsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        _items.Clear();
        ItemCount = 0;

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT race, class, itemid, amount FROM playercreateinfo_item";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            byte race = reader.GetByte(0);
            byte characterClass = reader.GetByte(1);
            int amount = reader.GetInt32(3);

            if (amount <= 0)
            {
                continue;
            }

            if (!_items.TryGetValue((race, characterClass), out List<PlayerCreateItem>? forPair))
            {
                forPair = [];
                _items[(race, characterClass)] = forPair;
            }

            forPair.Add(new PlayerCreateItem(race, characterClass, reader.GetUInt32(2), (uint)amount));
            ItemCount++;
        }
    }

    /// <summary>
    /// The extra items a race and class starts with.
    /// </summary>
    /// <remarks>
    /// <b>This is not where the starting gear is.</b> The vendored table carries a single row in
    /// the whole database; the outfit comes from <c>CharStartOutfit.dbc</c>. This is for whatever a
    /// server has chosen to add on top, and is almost always empty.
    /// </remarks>
    public IReadOnlyList<PlayerCreateItem> ItemsFor(byte race, byte characterClass) =>
        _items.TryGetValue((race, characterClass), out List<PlayerCreateItem>? items) ? items : [];

    /// <summary>
    /// Looks up a starting point. A missing entry means the race/class pair does not exist — which
    /// is exactly how an invalid combination is rejected, without a separate validity table.
    /// </summary>
    public bool TryGet(byte race, byte characterClass, out PlayerCreateInfo info) =>
        _entries.TryGetValue((race, characterClass), out info);

    /// <summary>Connection string for the <c>world</c> database.</summary>
    public const string ConnectionStringVariable = "WOWEMU_WORLD_CONNECTION";

    /// <summary>Matches the schema created by <c>docker/mysql-init</c>. Development only.</summary>
    public const string DefaultConnectionString =
        "server=127.0.0.1;port=3306;database=wowemu_world;user=wowemu;password=wowemu";

    /// <inheritdoc cref="AuthDatabase.ResolveConnectionString"/>
    public static string ResolveConnectionString(string? configured = null)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return string.IsNullOrWhiteSpace(configured) ? DefaultConnectionString : configured;
    }

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} race/class start positions");
}
