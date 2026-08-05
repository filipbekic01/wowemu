using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>
/// <c>player_xp_for_level</c> — how much experience each level costs.
/// </summary>
/// <remarks>
/// A table rather than a formula because Blizzard's curve is not one: it is hand-tuned per level,
/// with visible flat spots where content was added. 79 rows, one per level below the cap.
/// <para>
/// The row for level <c>N</c> is the experience needed to get <b>from</b> N <b>to</b> N+1, not the
/// running total. Reading it as a total makes every level after the first cost far too much.
/// </para>
/// </remarks>
public sealed class PlayerXpStore
{
    private readonly Dictionary<byte, uint> _xpForLevel = [];

    /// <summary>How many levels the table describes.</summary>
    public int Count => _xpForLevel.Count;

    /// <summary>
    /// The highest level the table covers, which is one below the level cap.
    /// </summary>
    /// <remarks>
    /// There is no row for the cap itself: a character at the cap has nowhere to go, so the cost of
    /// its next level is undefined rather than zero.
    /// </remarks>
    public byte MaxLevel { get; private set; }

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _xpForLevel.Clear();
        MaxLevel = 0;

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Level, Experience FROM player_xp_for_level";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            byte level = reader.GetByte(0);

            _xpForLevel[level] = reader.GetUInt32(1);
            MaxLevel = Math.Max(MaxLevel, level);
        }
    }

    /// <summary>
    /// Experience needed to leave <paramref name="level"/>.
    /// </summary>
    /// <remarks>
    /// Zero for a level the table does not cover, which the caller must read as "cannot level any
    /// further" rather than as "levels instantly" — a zero requirement compared with <c>&gt;=</c>
    /// is always satisfied.
    /// </remarks>
    public uint XpToLeave(byte level) => _xpForLevel.GetValueOrDefault(level);

    /// <summary>Whether a level can be left at all — that is, whether it is below the cap.</summary>
    public bool CanLevelPast(byte level) => _xpForLevel.ContainsKey(level);

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} experience-per-level rows to level {MaxLevel}");
}
