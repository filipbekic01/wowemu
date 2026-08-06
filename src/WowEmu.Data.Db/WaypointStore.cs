using System.Globalization;
using MySql.Data.MySqlClient;
using WowEmu.Core;

namespace WowEmu.Data.Db;

/// <summary>
/// One stop on a patrol route. A row of <c>waypoint_data</c>.
/// </summary>
/// <param name="Position">Where to stand, and which way to face on arrival.</param>
/// <param name="DelayMs">How long to wait there before moving on. Usually zero.</param>
/// <param name="MoveType">0 walk, 1 run, 2 fly. Upstream's <c>WaypointMoveType</c>.</param>
/// <remarks>
/// The <c>orientation</c> column is only meaningful where the route pauses — a creature walking
/// through a point faces the next one. Upstream stores 0 for the vast majority and turns the
/// creature only when <see cref="DelayMs"/> is non-zero.
/// </remarks>
public readonly record struct Waypoint(Position Position, uint DelayMs, byte MoveType)
{
    /// <summary>Whether the creature stops here rather than passing through.</summary>
    public bool IsPause => DelayMs > 0;

    /// <summary>Whether it runs to this point rather than walking. <c>WAYPOINT_MOVE_TYPE_RUN</c>.</summary>
    public bool IsRun => MoveType == 1;
}

/// <summary>
/// The patrol routes, from <c>waypoint_data</c>.
/// </summary>
/// <remarks>
/// A path is a numbered list of points, and a spawn joins one through
/// <c>creature_addon.path_id</c> — <b>not</b> through its own guid, despite the column comment on
/// <c>waypoint_data.id</c> saying "Creature GUID". For most routes the two happen to be the same
/// number, which is exactly why relying on it would work in testing and fail on the ones that differ.
/// <para>
/// Loaded whole at startup: 112,797 points across 5,516 paths is one sequential scan, against a
/// query every time a patrolling creature is spawned.
/// </para>
/// </remarks>
public sealed class WaypointStore
{
    private readonly Dictionary<uint, List<Waypoint>> _paths = [];

    /// <summary>How many points were loaded, across every path.</summary>
    public int Count { get; private set; }

    /// <summary>How many distinct routes there are.</summary>
    public int PathCount => _paths.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _paths.Clear();
        Count = 0;

        await using MySqlCommand command = connection.CreateCommand();

        // Ordered by point, so each path arrives already in walking order and nothing has to sort
        // 5,516 lists afterwards. The point numbers themselves are not always contiguous.
        command.CommandText =
            """
            SELECT id, position_x, position_y, position_z, orientation, delay, move_type
            FROM waypoint_data
            ORDER BY id, point
            """;

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint pathId = reader.GetUInt32(0);

            Waypoint point = new(
                new Position(reader.GetFloat(1), reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4)),
                DelayMs: reader.GetUInt32(5),
                MoveType: (byte)reader.GetInt32(6));

            if (!_paths.TryGetValue(pathId, out List<Waypoint>? points))
            {
                points = [];
                _paths[pathId] = points;
            }

            points.Add(point);
            Count++;
        }
    }

    /// <summary>
    /// The route with a given id, or empty when there is none.
    /// </summary>
    /// <remarks>
    /// Empty is a real answer: 35 of the 5,290 patrolling spawns name a path that does not exist.
    /// They stand still, which is what upstream does with them too.
    /// </remarks>
    public IReadOnlyList<Waypoint> Path(uint pathId) =>
        _paths.TryGetValue(pathId, out List<Waypoint>? points) ? points : [];

    /// <summary>Adds a path directly. Tests and fixtures only.</summary>
    public void Add(uint pathId, IEnumerable<Waypoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        List<Waypoint> list = [.. points];

        _paths[pathId] = list;
        Count += list.Count;
    }

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} waypoints across {PathCount} paths");
}

/// <summary>
/// The per-spawn extras, from <c>creature_addon</c>.
/// </summary>
/// <remarks>
/// Only <c>path_id</c> is read. The table also carries mount, emote, stand state and a list of auras
/// to apply on spawn, none of which have anywhere to go yet — reading them now would mean carrying
/// 34,311 rows of fields nothing looks at, and the row is added to when a phase needs it, the same
/// way <c>CreatureSpawn</c> grew.
/// </remarks>
public sealed class CreatureAddonStore
{
    private readonly Dictionary<uint, uint> _pathBySpawn = [];

    /// <summary>How many addon rows were loaded.</summary>
    public int Count { get; private set; }

    /// <summary>How many of them name a patrol route.</summary>
    public int PathCount => _pathBySpawn.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _pathBySpawn.Clear();
        Count = 0;

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT guid, path_id FROM creature_addon";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Count++;

            uint pathId = reader.GetUInt32(1);

            // Most rows exist for the other columns and name no route. Storing the zeros would
            // treble the dictionary for nothing.
            if (pathId != 0)
            {
                _pathBySpawn[reader.GetUInt32(0)] = pathId;
            }
        }
    }

    /// <summary>The route a spawn walks, or 0 if it has none.</summary>
    public uint PathFor(uint spawnId) => _pathBySpawn.GetValueOrDefault(spawnId);

    /// <summary>Names a spawn's route directly. Tests and fixtures only.</summary>
    public void Add(uint spawnId, uint pathId)
    {
        _pathBySpawn[spawnId] = pathId;
        Count++;
    }

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} creature addons, {PathCount} with a path");
}
