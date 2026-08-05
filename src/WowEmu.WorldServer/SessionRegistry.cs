using System.Collections.Concurrent;

namespace WowEmu.WorldServer;

/// <summary>
/// The sessions the world loop updates.
/// </summary>
/// <remarks>
/// Sessions arrive and leave on accept and disconnect tasks, but are only ever <i>read</i> by the
/// world tick — so the collection is concurrent and everything it hands out is a snapshot. A
/// session that disconnects mid-tick is still drained once more, which is harmless: its queues are
/// empty and its connection is closed.
/// </remarks>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<WorldSession, byte> _sessions = [];

    /// <summary>How many sessions are connected.</summary>
    public int Count => _sessions.Count;

    public void Add(WorldSession session) => _sessions[session] = 0;

    public void Remove(WorldSession session) => _sessions.TryRemove(session, out _);

    /// <summary>A snapshot, safe to iterate while sessions come and go.</summary>
    public IReadOnlyList<WorldSession> Snapshot() => [.. _sessions.Keys];
}
