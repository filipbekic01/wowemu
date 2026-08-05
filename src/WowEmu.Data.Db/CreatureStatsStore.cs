using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>Base stats for a creature of one unit class at one level.</summary>
/// <remarks>
/// Health is per expansion because a level-70 mob in Outland is not a level-70 mob in Azeroth: the
/// same level means very different health depending on which expansion's content it belongs to.
/// The template's <c>exp</c> column picks the slot.
/// </remarks>
public readonly record struct CreatureBaseStats(
    uint BaseHealthClassic,
    uint BaseHealthBurningCrusade,
    uint BaseHealthWrath,
    uint BaseMana,
    uint BaseArmor,
    uint AttackPower,
    uint RangedAttackPower)
{
    /// <summary>
    /// Base health for an expansion, falling back to classic for an out-of-range value.
    /// </summary>
    /// <remarks>
    /// The fallback matters: a bad <c>exp</c> would otherwise index off the end, and a creature with
    /// zero health is one the client draws as a corpse.
    /// </remarks>
    public uint BaseHealthFor(byte expansion) => expansion switch
    {
        1 => BaseHealthBurningCrusade,
        2 => BaseHealthWrath,
        _ => BaseHealthClassic,
    };
}

/// <summary>
/// <c>creature_classlevelstats</c>, loaded once at startup.
/// </summary>
/// <remarks>
/// The creature-side counterpart of <see cref="PlayerStatsStore"/>, and the reason a creature does
/// not carry its own health in the spawn row: 400 rows describe every level and unit class, and the
/// template scales them with <c>Health_mod</c>, <c>Mana_mod</c> and <c>Armor_mod</c>.
/// </remarks>
public sealed class CreatureStatsStore
{
    private readonly Dictionary<(byte Level, byte UnitClass), CreatureBaseStats> _stats = [];

    public int Count => _stats.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _stats.Clear();

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT level, class, basehp0, basehp1, basehp2, basemana, basearmor,
                   attackpower, rangedattackpower
            FROM creature_classlevelstats
            """;

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _stats[(reader.GetByte(0), reader.GetByte(1))] = new CreatureBaseStats(
                reader.GetUInt16(2),
                reader.GetUInt16(3),
                reader.GetUInt16(4),
                reader.GetUInt16(5),
                reader.GetUInt16(6),
                reader.GetUInt16(7),
                reader.GetUInt16(8));
        }
    }

    public bool TryGet(byte level, byte unitClass, out CreatureBaseStats stats) =>
        _stats.TryGetValue((level, unitClass), out stats);

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} creature level/class stat rows");
}
