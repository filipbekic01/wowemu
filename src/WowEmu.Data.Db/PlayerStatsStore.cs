using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>Base stats for one race, class and level.</summary>
public readonly record struct LevelStats(
    uint Strength,
    uint Agility,
    uint Stamina,
    uint Intellect,
    uint Spirit);

/// <summary>Base health and mana for one class and level.</summary>
public readonly record struct ClassLevelStats(uint BaseHealth, uint BaseMana);

/// <summary>
/// <c>player_levelstats</c> and <c>player_classlevelstats</c>, loaded once at startup.
/// </summary>
/// <remarks>
/// Two tables because the data splits two ways: the five attributes vary by race as well as class,
/// while health and mana depend only on class. A character needs both, so a lookup here returns the
/// combination.
/// <para>
/// About 5,700 rows total, read once with a raw <see cref="MySqlDataReader"/> — same reasoning as
/// <see cref="PlayerCreateInfoStore"/>: the <c>world</c> database is bulk-loaded and never written.
/// </para>
/// </remarks>
public sealed class PlayerStatsStore
{
    private readonly Dictionary<(byte Race, byte Class, byte Level), LevelStats> _levelStats = [];
    private readonly Dictionary<(byte Class, byte Level), ClassLevelStats> _classStats = [];

    public int LevelStatCount => _levelStats.Count;

    public int ClassStatCount => _classStats.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _levelStats.Clear();
        _classStats.Clear();

        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT race, class, level, str, agi, sta, inte, spi FROM player_levelstats";

            await using MySqlDataReader reader =
                (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _levelStats[(reader.GetByte(0), reader.GetByte(1), reader.GetByte(2))] = new LevelStats(
                    (uint)reader.GetInt32(3),
                    (uint)reader.GetInt32(4),
                    (uint)reader.GetInt32(5),
                    (uint)reader.GetInt32(6),
                    (uint)reader.GetInt32(7));
            }
        }

        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT class, level, basehp, basemana FROM player_classlevelstats";

            await using MySqlDataReader reader =
                (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _classStats[(reader.GetByte(0), reader.GetByte(1))] = new ClassLevelStats(
                    (uint)reader.GetInt32(2),
                    (uint)reader.GetInt32(3));
            }
        }
    }

    /// <summary>
    /// Looks up the combined stats. False when either table has no row, which means the character
    /// cannot be built correctly — better to refuse than to log in with zero health.
    /// </summary>
    public bool TryGet(
        byte race,
        byte characterClass,
        byte level,
        out LevelStats levelStats,
        out ClassLevelStats classStats)
    {
        classStats = default;

        return _levelStats.TryGetValue((race, characterClass, level), out levelStats)
            && _classStats.TryGetValue((characterClass, level), out classStats);
    }
}
