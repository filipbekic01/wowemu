using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>
/// Which spells are ranks of the same spell.
/// </summary>
/// <remarks>
/// <c>spell_ranks</c>, and <c>SpellMgr::LoadSpellRanks</c>. The DBC does not say that Fireball rank
/// 3 supersedes rank 2 — nothing in the client's data does — so the chains come from a curated
/// world table, keyed by the first spell in each chain.
/// <para>
/// Without it a player who trains rank 2 keeps rank 1 in their spellbook and on their bars, both
/// castable, and the weaker one is the one that stays where their finger already is.
/// </para>
/// </remarks>
public sealed class SpellRankStore
{
    /// <summary>Rank within its chain, 1-based, and the chain it belongs to.</summary>
    private readonly Dictionary<uint, (uint First, byte Rank)> _bySpell = [];

    /// <summary>Every spell of a chain, in rank order, keyed by the chain's first spell.</summary>
    private readonly Dictionary<uint, List<uint>> _chains = [];

    /// <summary>How many spells are part of some chain.</summary>
    public int Count => _bySpell.Count;

    /// <summary>How many distinct chains there are.</summary>
    public int ChainCount => _chains.Count;

    /// <summary>
    /// Whether this spell has ranks at all.
    /// </summary>
    /// <remarks>
    /// Most do not. A spell outside every chain is its own thing and supersedes nothing, which is
    /// the answer for the great majority of the spell table.
    /// </remarks>
    public bool IsRanked(uint spellId) => _bySpell.ContainsKey(spellId);

    /// <summary>Which rank this is, 1-based. Zero for a spell with no ranks.</summary>
    public byte RankOf(uint spellId) =>
        _bySpell.TryGetValue(spellId, out (uint First, byte Rank) entry) ? entry.Rank : (byte)0;

    /// <summary>The first spell of this one's chain, or the spell itself when it has no ranks.</summary>
    public uint FirstOf(uint spellId) =>
        _bySpell.TryGetValue(spellId, out (uint First, byte Rank) entry) ? entry.First : spellId;

    /// <summary>Every spell in the same chain, lowest rank first. Empty for an unranked spell.</summary>
    public IReadOnlyList<uint> ChainOf(uint spellId) =>
        _bySpell.TryGetValue(spellId, out (uint First, byte Rank) entry)
        && _chains.TryGetValue(entry.First, out List<uint>? chain)
            ? chain
            : [];

    /// <summary>
    /// Whether <paramref name="spellId"/> is a higher rank of <paramref name="other"/>.
    /// </summary>
    /// <remarks>
    /// Both conditions matter: same chain <i>and</i> higher rank. Comparing ranks alone would make
    /// Fireball rank 3 supersede Frostbolt rank 2, since both are simply "rank 3" and "rank 2".
    /// </remarks>
    public bool Supersedes(uint spellId, uint other)
    {
        if (!_bySpell.TryGetValue(spellId, out (uint First, byte Rank) mine)
            || !_bySpell.TryGetValue(other, out (uint First, byte Rank) theirs))
        {
            return false;
        }

        return mine.First == theirs.First && mine.Rank > theirs.Rank;
    }

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _bySpell.Clear();
        _chains.Clear();

        await using MySqlCommand command = connection.CreateCommand();

        // Ordered so each chain arrives lowest rank first, which is the order the list wants and
        // saves sorting 3,500 rows into 1,200 tiny lists afterwards.
        command.CommandText =
            "SELECT first_spell_id, spell_id, `rank` FROM spell_ranks ORDER BY first_spell_id, `rank`";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint first = reader.GetUInt32(0);
            uint spellId = reader.GetUInt32(1);
            byte rank = reader.GetByte(2);

            _bySpell[spellId] = (first, rank);

            if (!_chains.TryGetValue(first, out List<uint>? chain))
            {
                _chains[first] = chain = [];
            }

            chain.Add(spellId);
        }
    }
}
