using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>
/// One outfit a creature entry can be seen carrying. A row of <c>creature_equip_template</c>.
/// </summary>
/// <remarks>
/// Three item ids: main hand, off hand, ranged. They are <i>item template</i> ids and never become
/// real items — the client looks each up for a model and draws it, and nothing about them is
/// carried, looted or dropped.
/// </remarks>
public readonly record struct CreatureEquipment(uint MainHand, uint OffHand, uint Ranged)
{
    /// <summary>How many weapon slots a creature displays. <c>MAX_EQUIPMENT_ITEMS</c>.</summary>
    public const int SlotCount = 3;

    /// <summary>The item in one of the three slots.</summary>
    public uint this[int slot] => slot switch
    {
        0 => MainHand,
        1 => OffHand,
        2 => Ranged,
        _ => 0,
    };
}

/// <summary>
/// What creatures visibly carry, from <c>creature_equip_template</c>.
/// </summary>
/// <remarks>
/// Keyed by <b>creature entry and variant</b>, not by spawn. A <c>creature</c> row's
/// <c>equipment_id</c> selects which variant of its entry's outfit that particular spawn wears —
/// which is how the same guard entry appears with a sword in one city and a mace in another.
/// <para>
/// In the vendored data <c>equipment_id</c> is only ever 0 (96,587 spawns, no weapons), 1 (49,183)
/// or -1 (176, pick at random), and only 12 of the 10,711 entries have more than one variant to pick
/// from. The variant machinery is nearly always answering the same question — but the twelve that
/// need it are real, and keying on the entry alone would silently give them all the same weapon.
/// </para>
/// </remarks>
public sealed class CreatureEquipStore
{
    private readonly Dictionary<uint, Dictionary<byte, CreatureEquipment>> _byEntry = [];

    /// <summary>How many outfits were loaded.</summary>
    public int Count { get; private set; }

    /// <summary>How many creature entries have any at all.</summary>
    public int EntryCount => _byEntry.Count;

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _byEntry.Clear();
        Count = 0;

        await using MySqlCommand command = connection.CreateCommand();

        // Ordered so a random pick is reproducible from a seed: an unordered scan would hand the
        // same seed a different outfit run to run, and PLAN §9 makes seeded comparison the sharpest
        // tool there is.
        command.CommandText =
            """
            SELECT CreatureID, ID, ItemID1, ItemID2, ItemID3
            FROM creature_equip_template
            ORDER BY CreatureID, ID
            """;

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint entry = reader.GetUInt32(0);

            if (!_byEntry.TryGetValue(entry, out Dictionary<byte, CreatureEquipment>? variants))
            {
                variants = [];
                _byEntry[entry] = variants;
            }

            variants[reader.GetByte(1)] = new CreatureEquipment(
                reader.GetUInt32(2), reader.GetUInt32(3), reader.GetUInt32(4));

            Count++;
        }
    }

    /// <summary>
    /// The outfit a spawn wears, or null for none.
    /// </summary>
    /// <param name="entry">The creature template entry.</param>
    /// <param name="equipmentId">
    /// The spawn's <c>equipment_id</c>. <b>Zero means no weapons and −1 means pick one at random</b>
    /// — they are not the same thing, and treating −1 as an ordinary id finds nothing and disarms
    /// the 176 spawns that use it.
    /// </param>
    /// <param name="urand">
    /// The roll for the random case. Only consulted when <paramref name="equipmentId"/> is −1, which
    /// is upstream's behaviour and matters: drawing unconditionally would consume the generator on
    /// every one of 146,000 spawns and put every later draw out of step with the C++.
    /// </param>
    public CreatureEquipment? For(uint entry, sbyte equipmentId, Func<uint, uint, uint> urand)
    {
        ArgumentNullException.ThrowIfNull(urand);

        if (equipmentId == 0
            || !_byEntry.TryGetValue(entry, out Dictionary<byte, CreatureEquipment>? variants)
            || variants.Count == 0)
        {
            return null;
        }

        if (equipmentId > 0)
        {
            return variants.TryGetValue((byte)equipmentId, out CreatureEquipment found) ? found : null;
        }

        // Upstream advances an iterator by urand(0, size - 1) over an ordered map, so the draw picks
        // a position rather than an id — the ids need not be contiguous, and indexing by the rolled
        // number would miss whenever they are not.
        uint index = urand(0, (uint)variants.Count - 1);

        foreach (KeyValuePair<byte, CreatureEquipment> variant in variants.OrderBy(v => v.Key))
        {
            if (index-- == 0)
            {
                return variant.Value;
            }
        }

        return null;
    }

    /// <summary>Adds an outfit directly. Tests and fixtures only.</summary>
    public void Add(uint entry, byte variantId, CreatureEquipment equipment)
    {
        if (!_byEntry.TryGetValue(entry, out Dictionary<byte, CreatureEquipment>? variants))
        {
            variants = [];
            _byEntry[entry] = variants;
        }

        variants[variantId] = equipment;
        Count++;
    }

    /// <summary>A description of the loaded contents, for the startup log.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} creature outfits across {EntryCount} entries");
}
