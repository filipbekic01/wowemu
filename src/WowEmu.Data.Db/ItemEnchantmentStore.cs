using MySql.Data.MySqlClient;

namespace WowEmu.Data.Db;

/// <summary>One possible outcome of a random-property roll, and how likely it is.</summary>
public readonly record struct EnchantmentChance(uint EnchantmentId, float Chance);

/// <summary>
/// Which random suffix an item rolls, and how often.
/// </summary>
/// <remarks>
/// <c>item_enchantment_template</c>, and <c>LoadRandomEnchantmentsTable</c>. <b>An item template's
/// <c>RandomProperty</c> and <c>RandomSuffix</c> are not ids into the DBC</b> — they are keys into
/// this table, which is what turns one number on a sword into "of the Bear" or "of the Eagle".
/// Looking the column up in the DBC directly finds some unrelated row and applies it.
/// </remarks>
public sealed class ItemEnchantmentStore
{
    private readonly Dictionary<uint, List<EnchantmentChance>> _byEntry = [];

    /// <summary>How many entries have outcomes.</summary>
    public int Count => _byEntry.Count;

    /// <summary>Every outcome for an entry, in table order. Empty for an unknown one.</summary>
    public IReadOnlyList<EnchantmentChance> OutcomesFor(uint entry) =>
        _byEntry.TryGetValue(entry, out List<EnchantmentChance>? outcomes) ? outcomes : [];

    /// <summary>
    /// Rolls one outcome.
    /// </summary>
    /// <param name="rollPercent">A draw over [0, 100).</param>
    /// <returns>The enchantment id, or zero when nothing was picked.</returns>
    /// <remarks>
    /// <b>The chances do not have to add up to 100, and usually do not.</b> A first pass walks the
    /// running sum against the roll; if the total falls short the whole draw is repeated, scaled
    /// down to the total that actually exists. Treating a short total as "nothing happened" leaves
    /// most items with no suffix at all, and the table looks perfectly reasonable while it does.
    /// </remarks>
    public uint Roll(uint entry, Func<float> rollPercent)
    {
        ArgumentNullException.ThrowIfNull(rollPercent);

        if (entry == 0 || !_byEntry.TryGetValue(entry, out List<EnchantmentChance>? outcomes))
        {
            return 0;
        }

        if (Pick(outcomes, rollPercent(), out uint picked, out float total))
        {
            return picked;
        }

        // The second draw is over the total that exists rather than over 100, which is what makes a
        // table summing to 40% still hand out a suffix every time.
        float scaled = rollPercent() / 100f * total;

        return Pick(outcomes, scaled, out picked, out _) ? picked : 0;
    }

    private static bool Pick(
        List<EnchantmentChance> outcomes, float roll, out uint picked, out float total)
    {
        total = 0f;

        foreach (EnchantmentChance outcome in outcomes)
        {
            total += outcome.Chance;

            if (total > roll)
            {
                picked = outcome.EnchantmentId;

                return true;
            }
        }

        picked = 0;

        return false;
    }

    public async Task LoadAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _byEntry.Clear();

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT entry, ench, chance FROM item_enchantment_template";

        await using MySqlDataReader reader =
            (MySqlDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uint entry = reader.GetUInt32(0);
            uint enchantment = reader.GetUInt32(1);
            float chance = reader.GetFloat(2);

            // Upstream's own bounds. A zero or negative chance is a row that can never be picked,
            // and keeping it would make the running total wrong for everything after it.
            if (chance <= 0.000001f || chance > 100.0f)
            {
                continue;
            }

            if (!_byEntry.TryGetValue(entry, out List<EnchantmentChance>? outcomes))
            {
                _byEntry[entry] = outcomes = [];
            }

            outcomes.Add(new EnchantmentChance(enchantment, chance));
        }
    }
}
