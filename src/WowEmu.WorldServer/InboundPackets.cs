using System.Collections.Concurrent;
using WowEmu.Protocol;

namespace WowEmu.WorldServer;

/// <summary>One packet waiting for the loop that may run it.</summary>
public readonly record struct InboundPacket(Opcode Opcode, byte[] Payload, PacketProcessing Processing);

/// <summary>
/// A session's inbound packets, in arrival order, handed out to whichever loop may run them.
/// </summary>
/// <remarks>
/// <b>One queue, not two.</b> PLAN.md §4.2 proposed splitting inbound packets into a world queue and
/// a map queue, to avoid upstream's head-of-line blocking. But that blocking is what preserves
/// <i>order</i>, and two queues silently lose it: a client sends movement and then a logout, the
/// world queue drains before the map queue, the logout is handled first, and the character is saved
/// where it used to be. That is not hypothetical — the M3 gate caught exactly it, and the stale
/// position reached the database.
/// <para>
/// So packets stay in one queue, and each loop takes from the front for as long as they belong to
/// it, stopping at the first that does not. Upstream gets the same effect by putting a rejected
/// packet back at the front of its queue; stopping is the same semantics without the moved-from
/// idiom. The wait is bounded by one tick, because the world loop drains before the map workers on
/// every tick.
/// </para>
/// <para>
/// No lock. The two consumers never run at the same time — the world loop drains every session, and
/// only then are the map workers started — so a peek followed by a dequeue cannot race another
/// consumer. Producers are the connection read tasks, which is what the concurrent queue handles.
/// </para>
/// </remarks>
public sealed class InboundPackets
{
    private readonly ConcurrentQueue<InboundPacket> _queue = new();

    /// <summary>How many packets are waiting.</summary>
    public int Count => _queue.Count;

    public void Enqueue(InboundPacket packet) => _queue.Enqueue(packet);

    /// <summary>
    /// Takes the next packet if the calling loop may run it.
    /// </summary>
    /// <param name="onMapWorker">Whether the caller is a map worker rather than the world loop.</param>
    /// <param name="hasMap">
    /// Whether the session's character is on a map. This decides where <see cref="PacketProcessing.Inplace"/>
    /// packets go: to the map when there is one, and to the world loop when there is not — a player
    /// at the character screen has no map, and its packets would otherwise never be drained at all.
    /// </param>
    /// <returns>False when the queue is empty, or the packet at the front belongs to the other loop.</returns>
    public bool TryDequeueFor(bool onMapWorker, bool hasMap, out InboundPacket packet)
    {
        if (!_queue.TryPeek(out packet))
        {
            return false;
        }

        if (RunsOnMapWorker(packet.Processing, hasMap) != onMapWorker)
        {
            // Not ours. Leaving it at the front is what keeps a session's packets in the order the
            // client sent them.
            packet = default;
            return false;
        }

        return _queue.TryDequeue(out packet);
    }

    /// <summary>Which loop may run a packet.</summary>
    public static bool RunsOnMapWorker(PacketProcessing processing, bool hasMap) => processing switch
    {
        PacketProcessing.ThreadSafe => true,
        PacketProcessing.Inplace => hasMap,
        _ => false,
    };
}
