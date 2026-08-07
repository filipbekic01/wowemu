using System.Globalization;
using MySql.Data.MySqlClient;
using WowEmu.Core;

namespace WowEmu.Data.Db;

/// <summary>
/// One row of <c>creature</c>: a particular creature standing at a particular place.
/// </summary>
/// <remarks>
/// The template says what a murloc is; this says that one is at these coordinates on this map. Every
/// field here overrides or supplements the template for this one spawn.
/// <para>
/// <see cref="SpawnId"/> is the table's <c>guid</c> column — upstream's <c>Creature::m_spawnId</c>.
/// It is not an <see cref="ObjectGuid"/>: it is the counter such a guid is built around, and the
/// same number is reused across maps by gameobjects.
/// </para>
/// <para>
/// A struct, and a narrow one. There are ~146,000 of these resident for the life of the process, so
/// columns belonging to systems no phase has built are deliberately not read. Waypoint indices are
/// still absent; wander distance, movement type, respawn timers and equipment arrived with the
/// phases that needed them, which is how this is meant to grow.
/// </para>
/// </remarks>
public readonly record struct CreatureSpawn(
    uint SpawnId,
    uint Entry,
    uint MapId,
    byte SpawnMask,
    uint PhaseMask,
    uint ModelId,
    Position Position,
    uint CurrentHealth,
    uint CurrentMana,
    uint NpcFlags,
    uint UnitFlags,
    uint DynamicFlags,
    float WanderDistance,
    byte MovementType,
    uint RespawnDelaySeconds,
    sbyte EquipmentId)
{
    /// <summary>
    /// Whether this spawn exists at a given difficulty.
    /// </summary>
    /// <remarks>
    /// The mask is a bit per difficulty. Only 78 rows in the whole table exclude difficulty 0 — 54
    /// of them in Icecrown Citadel — but ignoring the mask puts every one of them into the
    /// normal-mode world, where they are visible and targetable and should not exist.
    /// </remarks>
    public bool SpawnsAtDifficulty(byte difficulty) => (SpawnMask & (1 << difficulty)) != 0;
}

/// <summary>
/// The <c>creature</c> table, loaded once at startup and filed by map.
/// </summary>
/// <remarks>
/// Read whole rather than per map on demand: 146,000 rows is one sequential scan at startup against
/// 109 separate round trips, and PLAN.md §4.5 makes startup time a tracked metric.
/// <para>
/// Filing stops at the map. Grids are the game layer's vocabulary, not the database's — see
/// <c>MapCoordinates</c> — and <c>WowEmu.Data.Db</c> does not reference the layer that owns them.
/// </para>
/// </remarks>
public sealed class CreatureSpawnStore
{
    private readonly Dictionary<uint, List<CreatureSpawn>> _byMap = [];

    /// <summary>How many spawns were loaded, across every map.</summary>
    public int Count { get; private set; }

    /// <summary>How many maps have at least one spawn.</summary>
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
            SELECT guid, id, map, spawnMask, phaseMask, modelid,
                   position_x, position_y, position_z, orientation,
                   curhealth, curmana, npcflag, unit_flags, dynamicflags,
                   spawndist, MovementType, spawntimesecs, equipment_id
            FROM creature
            """;

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint mapId = reader.GetUInt16(2);

            CreatureSpawn spawn = new(
                SpawnId: reader.GetUInt32(0),
                Entry: reader.GetUInt32(1),
                MapId: mapId,
                SpawnMask: reader.GetByte(3),
                PhaseMask: reader.GetUInt32(4),
                ModelId: reader.GetUInt32(5),
                Position: new Position(
                    reader.GetFloat(6),
                    reader.GetFloat(7),
                    reader.GetFloat(8),
                    reader.GetFloat(9)),
                CurrentHealth: reader.GetUInt32(10),
                CurrentMana: reader.GetUInt32(11),
                NpcFlags: reader.GetUInt32(12),
                UnitFlags: reader.GetUInt32(13),
                DynamicFlags: reader.GetUInt32(14),
                WanderDistance: reader.GetFloat(15),
                MovementType: reader.GetByte(16),
                RespawnDelaySeconds: reader.GetUInt32(17),

                // Signed on purpose: -1 means "pick one at random", and reading the column as
                // unsigned turns 176 armed spawns into ones asking for outfit 255.
                EquipmentId: reader.GetSByte(18));

            if (!_byMap.TryGetValue(mapId, out List<CreatureSpawn>? spawns))
            {
                spawns = [];
                _byMap[mapId] = spawns;
            }

            spawns.Add(spawn);
            Count++;
        }
    }

    /// <summary>
    /// Every spawn on a map. Empty for a map with none, which most instance maps legitimately are.
    /// </summary>
    public IReadOnlyList<CreatureSpawn> ForMap(uint mapId) =>
        _byMap.TryGetValue(mapId, out List<CreatureSpawn>? spawns) ? spawns : [];

    /// <summary>Every map that has at least one spawn.</summary>
    /// <remarks>
    /// So that per-map indexes can be built up front instead of on whichever tick a player first
    /// arrives on the map — which is a login spike paid by a player rather than by startup.
    /// </remarks>
    public IReadOnlyCollection<uint> Maps => _byMap.Keys;

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} creature spawns across {MapCount} maps");
}
