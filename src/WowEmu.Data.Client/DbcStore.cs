namespace WowEmu.Data.Client;

/// <summary>Builds one entry from a record. Implemented per store.</summary>
public delegate TEntry DbcEntryFactory<out TEntry>(in DbcRecord record);

/// <summary>
/// A loaded DBC table, indexed by the record's id column.
/// </summary>
/// <remarks>
/// Ids are sparse — <c>Map.dbc</c> jumps from 1 to 13 to 30 — so this is a dictionary rather than
/// an array. Upstream builds an index array sized to the largest id, which for some stores wastes
/// more memory than the table itself occupies.
/// </remarks>
#pragma warning disable CA1000 // Static Load on a generic type reads better than a separate factory
public sealed class DbcStore<TEntry>
{
    private readonly Dictionary<uint, TEntry> _entries;

    private DbcStore(string name, Dictionary<uint, TEntry> entries)
    {
        Name = name;
        _entries = entries;
    }

    /// <summary>File this store was loaded from.</summary>
    public string Name { get; }

    public int Count => _entries.Count;

    /// <summary>Every entry, in no particular order.</summary>
    public IEnumerable<TEntry> Entries => _entries.Values;

    /// <summary>Every id present.</summary>
    public IEnumerable<uint> Ids => _entries.Keys;

    /// <summary>
    /// Loads a store from <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Full path to the <c>.dbc</c> file.</param>
    /// <param name="format">Format string describing every column in the file.</param>
    /// <param name="idField">Column holding the record id — the <c>n</c> in the format string.</param>
    /// <param name="factory">Builds an entry from a record.</param>
    public static DbcStore<TEntry> Load(
        string path,
        string format,
        int idField,
        DbcEntryFactory<TEntry> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        DbcFile file = DbcFile.Load(path, format);
        Dictionary<uint, TEntry> entries = new(file.RecordCount);

        file.ForEachRecord((in DbcRecord record) => entries[record.GetUInt32(idField)] = factory(record));

        return new DbcStore<TEntry>(file.Name, entries);
    }

    public bool TryGet(uint id, out TEntry entry) => _entries.TryGetValue(id, out entry!);

    /// <summary>Looks up an entry, or throws if it is missing.</summary>
    /// <remarks>
    /// For call sites where a missing row means the data is wrong rather than the input — a race
    /// that has no <c>ChrRaces</c> row, say. Failing loudly beats rendering an invisible character.
    /// </remarks>
    public TEntry Get(uint id) =>
        _entries.TryGetValue(id, out TEntry? entry)
            ? entry
            : throw new KeyNotFoundException($"{Name}: no record with id {id}.");

    public bool Contains(uint id) => _entries.ContainsKey(id);
}
#pragma warning restore CA1000
