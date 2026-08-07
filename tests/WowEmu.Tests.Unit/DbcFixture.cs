using WowEmu.Data.Client;

namespace WowEmu.Tests.Unit;

/// <summary>
/// Builds <see cref="DbcStore{TEntry}"/> instances out of literal rows.
/// </summary>
/// <remarks>
/// A store can only be loaded from a <c>.dbc</c> file, which is the right constraint for production
/// and the wrong one for a test that wants three rows with known values. Reaching in through
/// reflection keeps the production type honest — the alternative is a public factory that exists
/// only for tests and that nothing stops real code from calling.
/// </remarks>
internal static class DbcFixture
{
    /// <summary>A store of the given rows, keyed by whatever <paramref name="id"/> returns.</summary>
    public static DbcStore<TEntry> Store<TEntry>(Func<TEntry, uint> id, params TEntry[] rows)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(rows);

        DbcStore<TEntry> store = (DbcStore<TEntry>)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(DbcStore<TEntry>));

        Dictionary<uint, TEntry> map = [];

        foreach (TEntry row in rows)
        {
            map[id(row)] = row;
        }

        typeof(DbcStore<TEntry>)
            .GetField(
                "_entries",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(store, map);

        return store;
    }
}
