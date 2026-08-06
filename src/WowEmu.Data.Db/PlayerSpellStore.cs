using System.Globalization;
using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>One spell a race and class combination starts knowing.</summary>
/// <param name="RaceMask">A bit per race, one-based. <b>Zero means every race.</b></param>
/// <param name="ClassMask">A bit per class, one-based. Zero means every class.</param>
public readonly record struct PlayerCreateSpell(uint RaceMask, uint ClassMask, uint SpellId)
{
    /// <summary>Whether this row applies to a race and class.</summary>
    /// <remarks>
    /// A mask of zero is "everyone", which is how the eighty-odd rows every character shares are
    /// stored. Testing the bit without that shortcut leaves every character knowing nothing.
    /// </remarks>
    public bool AppliesTo(byte race, byte characterClass) =>
        (RaceMask == 0 || (RaceMask & (1u << (race - 1))) != 0)
        && (ClassMask == 0 || (ClassMask & (1u << (characterClass - 1))) != 0);
}

/// <summary>
/// What each race and class begins able to cast.
/// </summary>
/// <remarks>
/// <b>The data is in <c>playercreateinfo_spell</c>, not <c>playercreateinfo_spell_custom</c>.</b>
/// The current C++ reads the <c>_custom</c> table, and in the vendored dump that table is empty —
/// the 182 real rows are in the older one. The fourth place the two reference trees have diverged,
/// after <c>creature_template_model</c>, <c>game_graveyard</c> and <c>EndText</c>. Both are read
/// here, so either shape works.
/// </remarks>
public sealed class PlayerSpellStore
{
    private readonly List<PlayerCreateSpell> _spells = [];

    public int Count => _spells.Count;

    /// <summary>Every spell one race and class starts with, in the table's own order.</summary>
    public IEnumerable<uint> For(byte race, byte characterClass)
    {
        foreach (PlayerCreateSpell spell in _spells)
        {
            if (spell.AppliesTo(race, characterClass))
            {
                yield return spell.SpellId;
            }
        }
    }

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _spells.Clear();

        // Both tables, because which one holds the data depends on how old the dump is. A row in
        // each for the same spell is harmless: the spellbook is a set.
        foreach (string table in (string[])["playercreateinfo_spell", "playercreateinfo_spell_custom"])
        {
            await LoadTableAsync(connection, table, cancellationToken).ConfigureAwait(false);
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Count} starting spell rows");

    private async Task LoadTableAsync(
        MySqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT racemask, classmask, Spell FROM {table}";

        try
        {
            await using MySqlDataReader reader =
                (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _spells.Add(new PlayerCreateSpell(
                    reader.GetUInt32(0), reader.GetUInt32(1), reader.GetUInt32(2)));
            }
        }
        catch (MySqlException)
        {
            // One of the two names is absent on any given dump. Missing is the normal case for
            // whichever one this server's data does not use.
        }
    }
}

/// <summary>One spell a trainer will teach.</summary>
/// <param name="RequiredSpellId">
/// The previous rank, which must already be known. <b>Stored as a negative <c>SpellID</c></b> on
/// some rows — see <see cref="TrainerStore"/>.
/// </param>
public readonly record struct TrainerSpell(
    uint TrainerId,
    uint SpellId,
    uint MoneyCost,
    ushort RequiredSkill,
    ushort RequiredSkillRank,
    byte RequiredLevel,
    uint RequiredSpellId);

/// <summary>
/// <c>npc_trainer</c>, loaded once at startup.
/// </summary>
/// <remarks>
/// <b>A negative <c>SpellID</c> is a reference to another trainer's whole list</b> — the fifth
/// table to overload a column's sign that way, after the loot tables, <c>npc_vendor</c> and the two
/// quest columns. References are flattened at load, so nothing downstream sees one.
/// </remarks>
public sealed class TrainerStore
{
    private readonly Dictionary<uint, List<TrainerSpell>> _byTrainer = [];

    public int Count => _byTrainer.Count;

    public int RowCount { get; private set; }

    /// <summary>What one creature entry teaches.</summary>
    public IReadOnlyList<TrainerSpell> For(uint creatureEntry) =>
        _byTrainer.TryGetValue(creatureEntry, out List<TrainerSpell>? spells) ? spells : [];

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _byTrainer.Clear();
        RowCount = 0;

        Dictionary<uint, List<Row>> raw = [];

        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT ID, SpellID, MoneyCost, ReqSkillLine, ReqSkillRank, ReqLevel FROM npc_trainer";

            await using MySqlDataReader reader =
                (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                uint trainer = reader.GetUInt32(0);

                if (!raw.TryGetValue(trainer, out List<Row>? rows))
                {
                    rows = [];
                    raw[trainer] = rows;
                }

                rows.Add(new Row(
                    // Signed on purpose: negative is a reference to another trainer.
                    SpellIdOrReference: reader.GetInt32(1),
                    MoneyCost: reader.GetUInt32(2),
                    RequiredSkill: reader.GetUInt16(3),
                    RequiredSkillRank: reader.GetUInt16(4),
                    RequiredLevel: reader.GetByte(5)));

                RowCount++;
            }
        }

        foreach (uint trainer in raw.Keys)
        {
            List<TrainerSpell> flattened = [];

            Flatten(trainer, raw, flattened, depth: 0);
            _byTrainer[trainer] = flattened;
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{RowCount} npc_trainer rows across {Count} trainers");

    private readonly record struct Row(
        int SpellIdOrReference, uint MoneyCost, ushort RequiredSkill, ushort RequiredSkillRank, byte RequiredLevel);

    /// <inheritdoc cref="VendorStore"/>
    private static void Flatten(uint trainer, Dictionary<uint, List<Row>> raw, List<TrainerSpell> into, int depth)
    {
        const int MaxReferenceDepth = 10;

        if (depth > MaxReferenceDepth || !raw.TryGetValue(trainer, out List<Row>? rows))
        {
            return;
        }

        foreach (Row row in rows)
        {
            if (row.SpellIdOrReference < 0)
            {
                Flatten((uint)(-row.SpellIdOrReference), raw, into, depth + 1);

                continue;
            }

            into.Add(new TrainerSpell(
                TrainerId: trainer,
                SpellId: (uint)row.SpellIdOrReference,
                MoneyCost: row.MoneyCost,
                RequiredSkill: row.RequiredSkill,
                RequiredSkillRank: row.RequiredSkillRank,
                RequiredLevel: row.RequiredLevel,
                RequiredSpellId: 0));
        }
    }
}
