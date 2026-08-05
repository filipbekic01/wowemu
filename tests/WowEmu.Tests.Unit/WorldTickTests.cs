using WowEmu.Data.Client;
using WowEmu.Game.Maps;
using WowEmu.Protocol;
using WowEmu.WorldServer;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The rule that decides which loop runs a packet, and in what order.
/// </summary>
/// <remarks>
/// This exists because getting it wrong wrote the wrong data to the database. PLAN.md §4.2 proposed
/// two queues to avoid upstream's head-of-line blocking; two queues lose arrival order, and the M3
/// gate caught a character being saved at its pre-movement position because the logout on one queue
/// overtook the movement on the other.
/// </remarks>
public sealed class InboundPacketOrderingTests
{
    /// <summary>The regression. Movement then logout must be run in that order, not that convenience.</summary>
    [Fact]
    public void MovementBeforeLogout_IsNotOvertakenByTheWorldLoop()
    {
        InboundPackets queue = new();

        queue.Enqueue(Packet(Opcode.MSG_MOVE_HEARTBEAT, PacketProcessing.ThreadSafe));
        queue.Enqueue(Packet(Opcode.CMSG_LOGOUT_REQUEST, PacketProcessing.ThreadUnsafe));

        // The world loop runs first every tick, and must find nothing it may run: the movement is
        // at the front and belongs to the map worker.
        Assert.False(queue.TryDequeueFor(onMapWorker: false, hasMap: true, out _));

        // The map worker takes the movement, and stops at the logout rather than running it.
        Assert.True(queue.TryDequeueFor(onMapWorker: true, hasMap: true, out InboundPacket first));
        Assert.Equal(Opcode.MSG_MOVE_HEARTBEAT, first.Opcode);
        Assert.False(queue.TryDequeueFor(onMapWorker: true, hasMap: true, out _));

        // Only now, on the next tick, does the world loop see the logout.
        Assert.True(queue.TryDequeueFor(onMapWorker: false, hasMap: true, out InboundPacket second));
        Assert.Equal(Opcode.CMSG_LOGOUT_REQUEST, second.Opcode);
    }

    /// <summary>A run of packets for one loop all come out together, in order.</summary>
    [Fact]
    public void ConsecutivePacketsForOneLoop_AllDrainInOrder()
    {
        InboundPackets queue = new();

        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue(Packet(Opcode.MSG_MOVE_HEARTBEAT, PacketProcessing.ThreadSafe));
        }

        queue.Enqueue(Packet(Opcode.CMSG_LOGOUT_REQUEST, PacketProcessing.ThreadUnsafe));

        int drained = 0;

        while (queue.TryDequeueFor(onMapWorker: true, hasMap: true, out _))
        {
            drained++;
        }

        Assert.Equal(5, drained);
        Assert.Equal(1, queue.Count);
    }

    /// <summary>
    /// An <c>Inplace</c> packet goes to the world loop when the session has no map.
    /// </summary>
    /// <remarks>
    /// A player at the character screen has no map, so nothing would ever drain its packets if these
    /// went to a map worker — the client would sit there with an unanswered request.
    /// </remarks>
    [Fact]
    public void InplacePackets_FollowWhetherThereIsAMap()
    {
        Assert.False(InboundPackets.RunsOnMapWorker(PacketProcessing.Inplace, hasMap: false));
        Assert.True(InboundPackets.RunsOnMapWorker(PacketProcessing.Inplace, hasMap: true));

        // The other two never depend on it.
        Assert.True(InboundPackets.RunsOnMapWorker(PacketProcessing.ThreadSafe, hasMap: false));
        Assert.False(InboundPackets.RunsOnMapWorker(PacketProcessing.ThreadUnsafe, hasMap: true));
    }

    [Fact]
    public void EmptyQueue_HandsOutNothing()
    {
        InboundPackets queue = new();

        Assert.False(queue.TryDequeueFor(onMapWorker: false, hasMap: false, out _));
        Assert.False(queue.TryDequeueFor(onMapWorker: true, hasMap: true, out _));
        Assert.Equal(0, queue.Count);
    }

    private static InboundPacket Packet(Opcode opcode, PacketProcessing processing) =>
        new(opcode, [], processing);
}

/// <summary>The worker pool map updates run on.</summary>
public sealed class MapUpdaterTests
{
    /// <summary>Zero workers runs inline. That is the test configuration, and a valid deployment.</summary>
    [Fact]
    public void WithNoWorkers_WorkRunsInline()
    {
        using MapUpdater updater = new(0);

        int thread = 0;
        updater.Schedule(() => thread = Environment.CurrentManagedThreadId);
        updater.Wait();

        Assert.Equal(Environment.CurrentManagedThreadId, thread);
        Assert.Equal(0, updater.WorkerCount);
    }

    /// <summary>
    /// Wait is a barrier. Without it the world loop would move on while maps were still running,
    /// and the next tick's session pass would touch map state from under a worker.
    /// </summary>
    [Fact]
    public void Wait_ReturnsOnlyAfterEverythingScheduledHasRun()
    {
        using MapUpdater updater = new(4);

        int completed = 0;

        for (int i = 0; i < 64; i++)
        {
            updater.Schedule(() =>
            {
                Thread.Sleep(1);
                Interlocked.Increment(ref completed);
            });
        }

        updater.Wait();

        Assert.Equal(64, Volatile.Read(ref completed));
    }

    /// <summary>Work really does leave the calling thread.</summary>
    [Fact]
    public void WithWorkers_WorkRunsOffTheCallingThread()
    {
        using MapUpdater updater = new(2);

        int caller = Environment.CurrentManagedThreadId;
        int observed = caller;

        updater.Schedule(() => observed = Environment.CurrentManagedThreadId);
        updater.Wait();

        Assert.NotEqual(caller, observed);
    }

    /// <summary>
    /// A map that throws must not take its worker with it, or the pool bleeds threads until the
    /// barrier can never be satisfied and the whole server stops ticking.
    /// </summary>
    [Fact]
    public void AThrowingUpdate_DoesNotKillTheWorker()
    {
        using MapUpdater updater = new(1);

        List<Exception> failures = [];
        updater.Failed += failures.Add;

        updater.Schedule(() => throw new InvalidOperationException("boom"));
        updater.Wait();

        int ranAfter = 0;
        updater.Schedule(() => ranAfter = 1);
        updater.Wait();

        Assert.Single(failures);
        Assert.Equal(1, ranAfter);
    }
}

/// <summary>
/// The 4-phase round-robin.
/// </summary>
/// <remarks>
/// PLAN.md §4.5 records the trap these pin down: a map out of phase is updated with a gameplay diff
/// of zero, which is <b>not</b> a skipped tick. Three ticks in four are a session-only pass, and
/// reading that as "the map did not tick" sends you looking for a bug that is not there.
/// </remarks>
public sealed class MapRoundRobinTests
{
    [Fact]
    public void AContinent_GetsAFullDiffOnceEveryFourTicks()
    {
        MapManager maps = NewManager();
        Map map = maps.GetMap(0);

        for (int tick = 0; tick < 8; tick++)
        {
            maps.Update(10);
        }

        Assert.Equal(8, map.TotalUpdates);
        Assert.Equal(2, map.FullUpdates);
    }

    /// <summary>
    /// The full diff is the time accumulated since the last one, not one tick's worth.
    /// </summary>
    /// <remarks>
    /// A map updated once every four ticks that was told about one tick of time would run its timers
    /// at a quarter speed — everything in the world would happen four times too slowly, consistently
    /// enough to look like a design decision.
    /// </remarks>
    [Fact]
    public void TheFullDiff_IsTheTimeAccumulatedSinceTheLastOne()
    {
        MapManager maps = NewManager();
        Map map = maps.GetMap(0);

        // Phase 0 is the continent phase, so the very first tick is a full one.
        maps.Update(10);
        Assert.Equal(1, map.FullUpdates);

        // Three session-only passes, then the next full update.
        maps.Update(10);
        maps.Update(10);
        maps.Update(10);
        Assert.Equal(1, map.FullUpdates);

        maps.Update(10);
        Assert.Equal(2, map.FullUpdates);
    }

    [Fact]
    public void ThePhase_AdvancesAndWrapsAtFour()
    {
        MapManager maps = NewManager();

        Assert.Equal(0, maps.CurrentPhase);

        for (int tick = 1; tick <= MapManager.PhaseCount; tick++)
        {
            maps.Update(10);
            Assert.Equal(tick % MapManager.PhaseCount, maps.CurrentPhase);
        }
    }

    /// <summary>Sessions are serviced on every tick, whatever the phase.</summary>
    [Fact]
    public void EveryTick_UpdatesEveryMap()
    {
        MapManager maps = NewManager();
        Map first = maps.GetMap(0);
        Map second = maps.GetMap(1);

        for (int tick = 0; tick < 4; tick++)
        {
            maps.Update(10);
        }

        Assert.Equal(4, first.TotalUpdates);
        Assert.Equal(4, second.TotalUpdates);
    }

    private static MapManager NewManager() =>
        new(new TerrainManager(Path.GetTempPath()));
}
