using System.Globalization;
using MySql.Data.MySqlClient;
using WowEmu.Core;

namespace WowEmu.Data.Db;

/// <summary>
/// One row of <c>gameobject_template</c>: what an entry is, before one is placed in the world.
/// </summary>
/// <remarks>
/// A narrow read, like <see cref="CreatureTemplate"/>. The 32 <c>data0..data31</c> columns are the
/// type-specific block — what a door opens with, what a chest contains, where a teleporter goes —
/// and none of it is needed to draw the object. The phase that needs them widens this record and
/// its <c>SELECT</c> together.
/// </remarks>
public sealed record GameObjectTemplate(
    uint Entry,
    byte Type,
    uint DisplayId,
    string Name,
    uint Faction,
    uint Flags,
    float Size,

    /// <summary>
    /// The twenty-four <c>data</c> columns, whose meaning depends on <see cref="Type"/>.
    /// </summary>
    /// <remarks>
    /// A union in the client's own headers, and read as one here: for a chest <c>data0</c> is the
    /// lock id and <c>data1</c> the loot id, and for a door <c>data0</c> is the lock and
    /// <c>data1</c> is whether it starts open. The same column means different things, so nothing
    /// can name them generically.
    /// </remarks>
    uint[]? Data = null)
{
    /// <summary>How many <c>data</c> columns the table has. <c>MAX_GAMEOBJECT_DATA</c>.</summary>
    public const int DataCount = 24;

    /// <summary>A chest. <c>GAMEOBJECT_TYPE_CHEST</c>.</summary>
    public const byte TypeChest = 3;

    /// <summary>What has to be opened to get at it, or 0. <c>data0</c> on a chest or a door.</summary>
    public uint LockId => At(0);

    /// <summary>Which <c>gameobject_loot_template</c> row it holds. <c>data1</c> on a chest.</summary>
    public uint LootId => Type == TypeChest ? At(1) : 0;

    private uint At(int index) => Data is { } data && index < data.Length ? data[index] : 0;
}

/// <summary>
/// One row of <c>gameobject</c>: an object standing at a particular place.
/// </summary>
/// <remarks>
/// <see cref="SpawnId"/> is the table's <c>guid</c> column, and is not an <see cref="ObjectGuid"/> —
/// it is the counter one is built around. Creature and gameobject spawn ids overlap; what separates
/// them in a real guid is the high part.
/// </remarks>
public readonly record struct GameObjectSpawn(
    uint SpawnId,
    uint Entry,
    uint MapId,
    byte SpawnMask,
    uint PhaseMask,
    Position Position,
    float Rotation0,
    float Rotation1,
    float Rotation2,
    float Rotation3,
    byte AnimProgress,
    byte State)
{
    /// <inheritdoc cref="CreatureSpawn.SpawnsAtDifficulty"/>
    public bool SpawnsAtDifficulty(byte difficulty) => (SpawnMask & (1 << difficulty)) != 0;
}

/// <summary><c>gameobject_template</c>, loaded once at startup.</summary>
/// <remarks>
/// Keyed by a dictionary rather than an array indexed by entry, for the reason PLAN.md §4.5 records
/// about <c>creature_template</c>: entries are sparse and the largest is far beyond the count.
/// </remarks>
public sealed class GameObjectTemplateStore
{
    private readonly Dictionary<uint, GameObjectTemplate> _templates = [];

    public int Count => _templates.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _templates.Clear();

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText =
            @"SELECT entry, type, displayId, name, faction, flags, size,
                     data0, data1, data2, data3, data4, data5, data6, data7,
                     data8, data9, data10, data11, data12, data13, data14, data15,
                     data16, data17, data18, data19, data20, data21, data22, data23
              FROM gameobject_template";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint[] data = new uint[GameObjectTemplate.DataCount];

            for (int i = 0; i < data.Length; i++)
            {
                // Signed in the table and read unsigned: several rows carry -1 for "none", which is
                // how the column says nothing rather than a real value.
                int raw = reader.GetInt32(7 + i);
                data[i] = raw < 0 ? 0u : (uint)raw;
            }

            GameObjectTemplate template = new(
                Entry: reader.GetUInt32(0),
                Type: reader.GetByte(1),
                DisplayId: reader.GetUInt32(2),
                Name: reader.GetString(3),
                Faction: reader.GetUInt16(4),
                Flags: reader.GetUInt32(5),
                Size: reader.GetFloat(6),
                Data: data);

            _templates[template.Entry] = template;
        }
    }

    public bool TryGet(uint entry, out GameObjectTemplate? template) => _templates.TryGetValue(entry, out template);

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} gameobject templates");
}

/// <summary>The <c>gameobject</c> table, loaded once at startup and filed by map.</summary>
/// <remarks>
/// Same shape and same reasoning as <see cref="CreatureSpawnStore"/>: read whole at startup rather
/// than per map on demand, and filed only as far as the map, because grids are the game layer's
/// vocabulary and this project does not reference it.
/// </remarks>
public sealed class GameObjectSpawnStore
{
    private readonly Dictionary<uint, List<GameObjectSpawn>> _byMap = [];

    public int Count { get; private set; }

    public int MapCount => _byMap.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _byMap.Clear();
        Count = 0;

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT guid, id, map, spawnMask, phaseMask,
                   position_x, position_y, position_z, orientation,
                   rotation0, rotation1, rotation2, rotation3,
                   animprogress, state
            FROM gameobject
            """;

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint mapId = reader.GetUInt16(2);

            GameObjectSpawn spawn = new(
                SpawnId: reader.GetUInt32(0),
                Entry: reader.GetUInt32(1),
                MapId: mapId,
                SpawnMask: reader.GetByte(3),
                PhaseMask: reader.GetUInt32(4),
                Position: new Position(
                    reader.GetFloat(5),
                    reader.GetFloat(6),
                    reader.GetFloat(7),
                    reader.GetFloat(8)),
                Rotation0: reader.GetFloat(9),
                Rotation1: reader.GetFloat(10),
                Rotation2: reader.GetFloat(11),
                Rotation3: reader.GetFloat(12),
                AnimProgress: reader.GetByte(13),
                State: reader.GetByte(14));

            if (!_byMap.TryGetValue(mapId, out List<GameObjectSpawn>? spawns))
            {
                spawns = [];
                _byMap[mapId] = spawns;
            }

            spawns.Add(spawn);
            Count++;
        }
    }

    /// <summary>Every spawn on a map. Empty for a map with none.</summary>
    public IReadOnlyList<GameObjectSpawn> ForMap(uint mapId) =>
        _byMap.TryGetValue(mapId, out List<GameObjectSpawn>? spawns) ? spawns : [];

    /// <summary>Every map that has at least one spawn. See the creature store for why.</summary>
    public IReadOnlyCollection<uint> Maps => _byMap.Keys;

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} gameobject spawns across {MapCount} maps");
}
