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

    /// <summary>How many race/class combinations are known.</summary>
    public int Count => _entries.Count;

    /// <summary>Loads the table. Called once, before the server accepts connections.</summary>
    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT race, class, map, zone, position_x, position_y, position_z, orientation FROM playercreateinfo";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        _entries.Clear();

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
