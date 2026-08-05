using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>One zone's link to a graveyard.</summary>
/// <param name="GraveyardId">A row in <c>WorldSafeLocs.dbc</c>, which is where the coordinates are.</param>
/// <param name="ZoneId">The zone a ghost has to be standing in for this to apply.</param>
/// <param name="Faction">
/// Which side may use it: 0 for either, 469 Alliance, 67 Horde.
/// </param>
public readonly record struct GraveyardZone(uint GraveyardId, uint ZoneId, uint Faction)
{
    /// <summary>Open to anyone. 570 of the 700 rows.</summary>
    public const uint FactionAny = 0;

    /// <summary>Alliance only.</summary>
    public const uint FactionAlliance = 469;

    /// <summary>Horde only.</summary>
    public const uint FactionHorde = 67;

    /// <summary>Whether a player of a given faction may release here.</summary>
    public bool AllowsFaction(uint faction) => Faction == FactionAny || Faction == faction;
}

/// <summary>
/// <c>game_graveyard_zone</c> — which graveyards a zone offers.
/// </summary>
/// <remarks>
/// Only the mapping. The coordinates are in <c>WorldSafeLocs.dbc</c>, because the vendored dump
/// predates the <c>game_graveyard</c> table newer AzerothCore reads them from.
/// <para>
/// A zone can list several, which is why this is a lookup to a list rather than to one row: a
/// contested zone usually has one graveyard per faction plus a neutral fallback.
/// </para>
/// </remarks>
public sealed class GraveyardStore
{
    private readonly Dictionary<uint, List<GraveyardZone>> _byZone = [];

    public int Count { get; private set; }

    /// <summary>How many zones have at least one graveyard.</summary>
    public int ZoneCount => _byZone.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _byZone.Clear();
        Count = 0;

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, ghost_zone, faction FROM game_graveyard_zone";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            GraveyardZone row = new(reader.GetUInt32(0), reader.GetUInt32(1), reader.GetUInt32(2));

            if (!_byZone.TryGetValue(row.ZoneId, out List<GraveyardZone>? zone))
            {
                zone = [];
                _byZone[row.ZoneId] = zone;
            }

            zone.Add(row);
            Count++;
        }
    }

    /// <summary>Every graveyard a zone offers, in no particular order.</summary>
    public IReadOnlyList<GraveyardZone> ForZone(uint zoneId) =>
        _byZone.TryGetValue(zoneId, out List<GraveyardZone>? zone) ? zone : [];

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} graveyard links across {ZoneCount} zones");
}
