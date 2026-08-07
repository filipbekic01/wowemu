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
/// How a creature stands when it spawns. A row of <c>creature_addon</c> or
/// <c>creature_template_addon</c>.
/// </summary>
/// <param name="PathId">The patrol route, or 0.</param>
/// <param name="Mount">A mount display id to be drawn riding, or 0.</param>
/// <param name="Bytes1">
/// Stand state, pet talents, visibility flags and animation tier, packed one per byte. The pet
/// talent byte is <b>not</b> written through — upstream zeroes it, because it only means anything on
/// a pet and the column carries leftovers for everything else.
/// </param>
/// <param name="Bytes2">
/// Sheath state in the low byte and three more that upstream deliberately drops: the second is a
/// flags byte it leaves alone, and the last two are pet-rename and shapeshift, which a spawn row has
/// no business setting.
/// </param>
/// <param name="Emote">A looping emote — the blacksmith hammering, the guard leaning.</param>
public readonly record struct CreatureAddon(uint PathId, uint Mount, uint Bytes1, uint Bytes2, uint Emote)
{
    /// <summary>The stand state: standing, sitting, kneeling, asleep. Low byte of <see cref="Bytes1"/>.</summary>
    public byte StandState => (byte)(Bytes1 & 0xFF);

    /// <summary>Visibility flags — what makes a stealthed or invisible creature detectable.</summary>
    public byte VisibilityFlags => (byte)((Bytes1 >> 16) & 0xFF);

    /// <summary>Ground, hovering, flying or submerged. What the client animates against.</summary>
    public byte AnimationTier => (byte)((Bytes1 >> 24) & 0xFF);

    /// <summary>Weapons drawn or put away. Low byte of <see cref="Bytes2"/>.</summary>
    public byte SheathState => (byte)(Bytes2 & 0xFF);

    /// <summary>Whether this row says anything at all.</summary>
    public bool IsEmpty => Mount == 0 && Bytes1 == 0 && Bytes2 == 0 && Emote == 0;
}

/// <summary>
/// The per-spawn and per-entry extras, from <c>creature_addon</c> and <c>creature_template_addon</c>.
/// </summary>
/// <remarks>
/// <b>A spawn's row replaces its entry's outright rather than merging with it.</b> Upstream's
/// <c>GetCreatureAddon</c> returns the first of the two that exists and never combines them, so a
/// spawn row that sets only an emote also silently clears whatever sheath state its template
/// specified. Merging them field by field is the obvious improvement and is a different game.
/// <para>
/// The <c>auras</c> column is not read yet. 2,910 spawns and 1,227 entries list spells to apply on
/// spawn, and applying one needs the spell store and an aura path that reaches outside the map —
/// recorded as a gap rather than half-done.
/// </para>
/// </remarks>
public sealed class CreatureAddonStore
{
    private readonly Dictionary<uint, CreatureAddon> _bySpawn = [];
    private readonly Dictionary<uint, CreatureAddon> _byEntry = [];

    /// <summary>How many spawn addon rows were loaded.</summary>
    public int Count { get; private set; }

    /// <summary>How many entry addon rows were loaded.</summary>
    public int TemplateCount => _byEntry.Count;

    /// <summary>How many rows of either kind name a patrol route.</summary>
    public int PathCount { get; private set; }

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _bySpawn.Clear();
        _byEntry.Clear();
        Count = 0;
        PathCount = 0;

        await ReadInto(connection, "creature_addon", "guid", _bySpawn, cancellationToken)
            .ConfigureAwait(false);

        Count = _bySpawn.Count;

        await ReadInto(connection, "creature_template_addon", "entry", _byEntry, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReadInto(
        MySqlConnection connection,
        string table,
        string keyColumn,
        Dictionary<uint, CreatureAddon> into,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {keyColumn}, path_id, mount, bytes1, bytes2, emote FROM {table}";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            CreatureAddon addon = new(
                PathId: reader.GetUInt32(1),
                Mount: reader.GetUInt32(2),
                Bytes1: reader.GetUInt32(3),
                Bytes2: reader.GetUInt32(4),
                Emote: reader.GetUInt32(5));

            into[reader.GetUInt32(0)] = addon;

            if (addon.PathId != 0)
            {
                PathCount++;
            }
        }
    }

    /// <summary>
    /// The addon that applies to a spawn: its own if it has one, otherwise its entry's.
    /// </summary>
    /// <remarks>
    /// The whole row wins or loses, never a field of it — see the class remarks.
    /// </remarks>
    public CreatureAddon? For(uint spawnId, uint entry)
    {
        if (_bySpawn.TryGetValue(spawnId, out CreatureAddon spawn))
        {
            return spawn;
        }

        return _byEntry.TryGetValue(entry, out CreatureAddon template) ? template : null;
    }

    /// <summary>The route a spawn walks, or 0 if it has none.</summary>
    /// <remarks>
    /// Reads through the same fallback, which matters for the 19 entries whose route is named by
    /// their template rather than by each spawn.
    /// </remarks>
    public uint PathFor(uint spawnId, uint entry) => For(spawnId, entry)?.PathId ?? 0;

    /// <summary>Adds a spawn's addon directly. Tests and fixtures only.</summary>
    public void Add(uint spawnId, CreatureAddon addon)
    {
        _bySpawn[spawnId] = addon;
        Count = _bySpawn.Count;

        if (addon.PathId != 0)
        {
            PathCount++;
        }
    }

    /// <inheritdoc cref="Add(uint, CreatureAddon)"/>
    public void AddTemplate(uint entry, CreatureAddon addon) => _byEntry[entry] = addon;

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Count} creature addons and {TemplateCount} template addons, {PathCount} with a path");
}
