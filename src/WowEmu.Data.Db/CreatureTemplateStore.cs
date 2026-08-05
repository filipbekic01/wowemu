using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>
/// One row of <c>creature_template</c>: what an entry <i>is</i>, before any particular one is placed
/// in the world.
/// </summary>
/// <remarks>
/// A subset of upstream's 87 columns — the ones a creature needs to exist and render. The rest are
/// loot, quests, trainers, spells and AI, and each phase that needs them widens this record and the
/// <c>SELECT</c> that fills it. Nothing else has to change, which is why this is a narrow read
/// rather than <c>SELECT *</c>.
/// <para>
/// A class rather than a struct: there are ~30,000 of them, they are handed out by reference on
/// every spawn, and copying 100-odd bytes per lookup would be the wrong trade.
/// </para>
/// </remarks>
public sealed record CreatureTemplate(
    uint Entry,
    string Name,
    string SubName,
    uint ModelId1,
    uint ModelId2,
    uint ModelId3,
    uint ModelId4,
    byte MinLevel,
    byte MaxLevel,
    byte Expansion,
    uint Faction,
    uint NpcFlags,
    float SpeedWalk,
    float SpeedRun,
    float Scale,
    byte Rank,
    byte UnitClass,
    uint UnitFlags,
    uint UnitFlags2,
    uint DynamicFlags,
    byte CreatureType,
    uint TypeFlags,
    byte Family,
    float HealthModifier,
    float ManaModifier,
    float ArmorModifier,
    byte MovementType,
    bool RegeneratesHealth,
    float MinDamage,
    float MaxDamage,
    float DamageModifier,
    uint BaseAttackTime,
    uint RangeAttackTime,
    uint AttackPower,
    uint RangedAttackPower,
    uint FlagsExtra,

    /// <summary>Row in <c>creature_loot_template</c>. <b>Zero means it drops nothing.</b></summary>
    /// <remarks>
    /// Usually the same as <see cref="Entry"/>, and often not — several entries share one list, and
    /// assuming the entry is the loot id gives a third of the game the wrong drops.
    /// </remarks>
    uint LootId,
    uint MinGold,
    uint MaxGold,

    /// <summary>Which gossip menu right-clicking opens. Zero means it has no gossip of its own.</summary>
    uint GossipMenuId)
{
    /// <summary>
    /// Picks one of the up-to-four display ids the entry may use.
    /// </summary>
    /// <remarks>
    /// Port of <c>CreatureTemplate::GetRandomValidModelId</c>. Zero slots are skipped rather than
    /// treated as a model: an entry with only <c>modelid1</c> set must always return that one, and
    /// picking uniformly across all four slots would leave three quarters of them invisible.
    /// </remarks>
    public uint GetRandomValidModelId(Func<uint, uint, uint> pick)
    {
        ArgumentNullException.ThrowIfNull(pick);

        Span<uint> valid = stackalloc uint[4];
        int count = 0;

        foreach (uint modelId in (ReadOnlySpan<uint>)[ModelId1, ModelId2, ModelId3, ModelId4])
        {
            if (modelId != 0)
            {
                valid[count++] = modelId;
            }
        }

        return count == 0 ? 0 : valid[(int)pick(0, (uint)count - 1)];
    }
}

/// <summary>
/// One row of <c>creature_model_info</c>: the physical size of a display id, and its opposite-gender
/// twin.
/// </summary>
/// <remarks>
/// This is not cosmetic data. <c>BoundingRadius</c> and <c>CombatReach</c> are what the client uses
/// to decide where a creature physically is, so a display id with no row here produces a creature
/// that cannot be clicked and that melee cannot reach.
/// </remarks>
public readonly record struct CreatureModelInfo(
    uint DisplayId,
    float BoundingRadius,
    float CombatReach,
    byte Gender,
    uint DisplayIdOtherGender);

/// <summary>
/// Resolves a display id to its model info.
/// </summary>
/// <remarks>
/// Narrower than <see cref="CreatureTemplateStore"/> on purpose: building a creature needs to look
/// a display id up and nothing else, and taking the whole store would mean no creature could be
/// built without a database behind it.
/// </remarks>
public interface ICreatureModelSource
{
    /// <summary>
    /// The model info for a display id. False when there is no row, which is a creature that would
    /// render with no size and could not be clicked.
    /// </summary>
    bool TryGetModel(uint displayId, out CreatureModelInfo model);
}

/// <summary>
/// <c>creature_template</c> and <c>creature_model_info</c>, loaded once at startup.
/// </summary>
/// <remarks>
/// Two tables in one store because neither answers the question on its own: the template names a
/// display id, and only the model info says how big it is. A spawn needs both or it renders wrongly.
/// <para>
/// Read with a raw <see cref="MySqlDataReader"/> per PLAN.md §5.2 — <c>world</c> is bulk-loaded once
/// and never written.
/// </para>
/// <para>
/// Keyed by a dictionary, deliberately. PLAN.md §4.5 records the trap: upstream stores templates in
/// an array sized by the largest entry, which for 3,460,603 is 27.7 MB of mostly-null references for
/// 29,928 real rows.
/// </para>
/// </remarks>
public sealed class CreatureTemplateStore : ICreatureModelSource
{
    private readonly Dictionary<uint, CreatureTemplate> _templates = [];
    private readonly Dictionary<uint, CreatureModelInfo> _models = [];

    public int TemplateCount => _templates.Count;

    public int ModelCount => _models.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _templates.Clear();
        _models.Clear();

        await using (MySqlCommand command = connection.CreateCommand())
        {
            // `rank` is a reserved word in MySQL 8 and has to be quoted; unquoted it is a syntax
            // error rather than a missing column, which reads like a broken query.
            command.CommandText =
                """
                SELECT entry, name, IFNULL(subname, ''), modelid1, modelid2, modelid3, modelid4,
                       minlevel, maxlevel, exp, faction, npcflag, speed_walk, speed_run, scale,
                       `rank`, unit_class, unit_flags, unit_flags2, dynamicflags, type, type_flags,
                       family, Health_mod, Mana_mod, Armor_mod, MovementType, RegenHealth,
                       mindmg, maxdmg, dmg_multiplier, baseattacktime, rangeattacktime,
                       attackpower, rangedattackpower, flags_extra,
                       lootid, mingold, maxgold, gossip_menu_id
                FROM creature_template
                """;

            await using MySqlDataReader reader =
                (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The unsigned getters are not stylistic. `unit_flags` reaches 2,181,300,992 and
                // `phaseMask` the full 32 bits, both past Int32.MaxValue, so GetInt32 would throw
                // on real rows — and only on the handful of entries that use the high bit.
                CreatureTemplate template = new(
                    Entry: reader.GetUInt32(0),
                    Name: reader.GetString(1),
                    SubName: reader.GetString(2),
                    ModelId1: reader.GetUInt32(3),
                    ModelId2: reader.GetUInt32(4),
                    ModelId3: reader.GetUInt32(5),
                    ModelId4: reader.GetUInt32(6),
                    MinLevel: reader.GetByte(7),
                    MaxLevel: reader.GetByte(8),
                    Expansion: (byte)reader.GetInt16(9),
                    Faction: reader.GetUInt16(10),
                    NpcFlags: reader.GetUInt32(11),
                    SpeedWalk: reader.GetFloat(12),
                    SpeedRun: reader.GetFloat(13),
                    Scale: reader.GetFloat(14),
                    Rank: reader.GetByte(15),
                    UnitClass: reader.GetByte(16),
                    UnitFlags: reader.GetUInt32(17),
                    UnitFlags2: reader.GetUInt32(18),
                    DynamicFlags: reader.GetUInt32(19),
                    CreatureType: reader.GetByte(20),
                    TypeFlags: reader.GetUInt32(21),
                    Family: (byte)reader.GetSByte(22),
                    HealthModifier: reader.GetFloat(23),
                    ManaModifier: reader.GetFloat(24),
                    ArmorModifier: reader.GetFloat(25),
                    MovementType: reader.GetByte(26),
                    RegeneratesHealth: reader.GetByte(27) != 0,
                    MinDamage: reader.GetFloat(28),
                    MaxDamage: reader.GetFloat(29),
                    DamageModifier: reader.GetFloat(30),
                    BaseAttackTime: reader.GetUInt32(31),
                    RangeAttackTime: reader.GetUInt32(32),
                    AttackPower: reader.GetUInt32(33),
                    RangedAttackPower: reader.GetUInt16(34),
                    FlagsExtra: reader.GetUInt32(35),
                    LootId: reader.GetUInt32(36),
                    MinGold: reader.GetUInt32(37),
                    MaxGold: reader.GetUInt32(38),
                    GossipMenuId: reader.GetUInt32(39));

                _templates[template.Entry] = template;
            }
        }

        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT DisplayID, BoundingRadius, CombatReach, Gender, DisplayID_Other_Gender FROM creature_model_info";

            await using MySqlDataReader reader =
                (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                uint displayId = reader.GetUInt32(0);

                _models[displayId] = new CreatureModelInfo(
                    displayId,
                    reader.GetFloat(1),
                    reader.GetFloat(2),
                    reader.GetByte(3),
                    reader.GetUInt32(4));
            }
        }
    }

    public bool TryGetTemplate(uint entry, out CreatureTemplate? template) =>
        _templates.TryGetValue(entry, out template);

    /// <summary>Every loaded template, in no particular order.</summary>
    /// <remarks>
    /// For sweeps over the whole table — sanity checks and startup reports. Gameplay looks entries
    /// up by key; nothing on a hot path should be walking 29,928 rows.
    /// </remarks>
    public IEnumerable<CreatureTemplate> All => _templates.Values;

    /// <inheritdoc/>
    public bool TryGetModel(uint displayId, out CreatureModelInfo model) =>
        _models.TryGetValue(displayId, out model);

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{TemplateCount} creature templates, {ModelCount} models");
}
