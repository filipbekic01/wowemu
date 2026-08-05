using WowEmu.Core;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The tick-bound scheduler: rule 3 of the threading model in PLAN.md §4.2.
/// </summary>
/// <remarks>
/// What these tests actually pin down is that asynchronous work cannot touch game state at an
/// arbitrary moment. Continuations wait until the loop drains them, the drain is bounded so a burst
/// cannot stall a tick, and work queued during a drain waits for the next one instead of extending
/// the current one.
/// </remarks>
public sealed class TickSchedulerTests
{
    [Fact]
    public void QueuedWork_DoesNotRunUntilDrained()
    {
        TickScheduler scheduler = new("test");
        scheduler.Attach();

        bool ran = false;
        scheduler.Factory.StartNew(() => ran = true);

        Assert.False(ran);
        Assert.Equal(1, scheduler.PendingCount);

        Assert.Equal(1, scheduler.Drain());
        Assert.True(ran);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Drain_RunsWorkOnTheLoopThread()
    {
        TickScheduler scheduler = new("test");
        scheduler.Attach();

        int loopThread = Environment.CurrentManagedThreadId;
        int ranOn = -1;

        scheduler.Factory.StartNew(() => ranOn = Environment.CurrentManagedThreadId);
        scheduler.Drain();

        Assert.Equal(loopThread, ranOn);
    }

    /// <summary>
    /// The point of the whole exercise: an <c>await</c> of thread-pool work resumes on the loop, so
    /// gameplay code can be written linearly and still only touch state from one thread.
    /// </summary>
    [Fact]
    public void AwaitedWork_ResumesOnTheLoopThread()
    {
        TickScheduler scheduler = new("world");
        scheduler.Attach();

        int loopThread = Environment.CurrentManagedThreadId;
        int resumedOn = -1;
        bool finished = false;

        using ManualResetEventSlim backgroundDone = new();

        _ = scheduler.Factory.StartNew(async () =>
        {
            await Task.Run(() => backgroundDone.Set());

            resumedOn = Environment.CurrentManagedThreadId;
            finished = true;
        });

        // Drain repeatedly, the way a tick loop would, until the continuation has come back.
        //
        // Bounded by a deadline rather than an iteration count. The continuation has to make a round
        // trip through the thread pool, and under a full-suite run the pool is contended enough that
        // a hundred one-millisecond ticks is not reliably long enough — which fails as "the
        // scheduler is broken" when the scheduler is fine. Ten seconds is far longer than the round
        // trip ever takes and still fails promptly if the continuation genuinely never arrives.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);

        while (!finished && DateTime.UtcNow < deadline)
        {
            scheduler.Drain();
            Thread.Sleep(1);
        }

        Assert.True(finished, "the awaited continuation never resumed on the scheduler");
        Assert.Equal(loopThread, resumedOn);
    }

    /// <summary>
    /// A completion storm must not blow the tick. Whatever the budget cannot take waits — and says
    /// so through <see cref="TickScheduler.DeferredLastDrain"/>, because silently deferred work is
    /// indistinguishable from work that never arrived.
    /// </summary>
    [Fact]
    public void Drain_StopsAtTheItemBudget_AndReportsWhatIsLeft()
    {
        TickScheduler scheduler = new("bounded") { MaxItemsPerDrain = 10 };
        scheduler.Attach();

        int ran = 0;
        for (int i = 0; i < 25; i++)
        {
            scheduler.Factory.StartNew(() => Interlocked.Increment(ref ran));
        }

        Assert.Equal(10, scheduler.Drain());
        Assert.Equal(10, ran);
        Assert.Equal(15, scheduler.DeferredLastDrain);

        scheduler.Drain();
        scheduler.Drain();

        Assert.Equal(25, ran);
        Assert.Equal(0, scheduler.DeferredLastDrain);
    }

    /// <summary>
    /// Work queued <i>by</i> a drain belongs to the next tick. Without the up-front snapshot, a
    /// continuation that re-queues itself would spin inside one drain until the time budget expired.
    /// </summary>
    [Fact]
    public void Drain_DefersWorkQueuedDuringItself()
    {
        TickScheduler scheduler = new("reentrant");
        scheduler.Attach();

        int ran = 0;

        scheduler.Factory.StartNew(() =>
        {
            ran++;
            scheduler.Factory.StartNew(() => ran++);
        });

        Assert.Equal(1, scheduler.Drain());
        Assert.Equal(1, ran);
        Assert.Equal(1, scheduler.PendingCount);

        Assert.Equal(1, scheduler.Drain());
        Assert.Equal(2, ran);
    }

    [Fact]
    public void Drain_OnAnEmptyQueue_DoesNothing()
    {
        TickScheduler scheduler = new("idle");
        scheduler.Attach();

        Assert.Equal(0, scheduler.Drain());
    }

    [Fact]
    public void AssertOwnerThread_PassesOnTheLoop_AndThrowsElsewhere()
    {
        TickScheduler scheduler = new("map-0");
        scheduler.Attach();

        scheduler.AssertOwnerThread();

        // A real thread, not Task.Run: blocking on a queued task lets the runtime inline it onto
        // the waiting thread, which would make this pass for the wrong reason.
        InvalidOperationException error =
            Assert.IsType<InvalidOperationException>(RunOnAnotherThread(scheduler.AssertOwnerThread));

        Assert.Contains("map-0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionsFromPostedCallbacks_ReachTheErrorHandler_AndDoNotStopTheDrain()
    {
        List<Exception> caught = [];
        TickScheduler scheduler = new("resilient", caught.Add);
        scheduler.Attach();

        SynchronizationContext context = SynchronizationContext.Current!;
        bool laterWorkRan = false;

        context.Post(_ => throw new InvalidOperationException("boom"), null);
        context.Post(_ => laterWorkRan = true, null);

        scheduler.Drain();

        Assert.Single(caught);
        Assert.Equal("boom", caught[0].Message);
        Assert.True(laterWorkRan, "one failing callback stopped the rest of the tick");
    }

    [Fact]
    public void Attach_InstallsASynchronizationContext()
    {
        TickScheduler scheduler = new("ctx");
        scheduler.Attach();

        Assert.IsType<TickSynchronizationContext>(SynchronizationContext.Current);
        Assert.True(scheduler.IsOwnerThread);
    }

    /// <summary>Blocking on the loop from outside it is a deadlock, so it is refused outright.</summary>
    [Fact]
    public void Send_FromAnotherThread_Throws()
    {
        TickScheduler scheduler = new("ctx");
        scheduler.Attach();

        SynchronizationContext context = SynchronizationContext.Current!;

        Assert.IsType<InvalidOperationException>(RunOnAnotherThread(() => context.Send(_ => { }, null)));
    }

    /// <summary>Runs an action on a dedicated thread and returns whatever it threw, if anything.</summary>
    private static Exception? RunOnAnotherThread(Action action)
    {
        Exception? thrown = null;

        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                thrown = exception;
            }
        });

        thread.Start();
        thread.Join();

        return thrown;
    }

    [Fact]
    public void Send_OnTheLoopThread_RunsInline()
    {
        TickScheduler scheduler = new("ctx");
        scheduler.Attach();

        bool ran = false;
        SynchronizationContext.Current!.Send(_ => ran = true, null);

        Assert.True(ran);
    }

    [Fact]
    public void TotalExecuted_CountsEverythingRun()
    {
        TickScheduler scheduler = new("counter");
        scheduler.Attach();

        for (int i = 0; i < 5; i++)
        {
            scheduler.Factory.StartNew(() => { });
        }

        scheduler.Drain();

        Assert.Equal(5, scheduler.TotalExecuted);
    }
}
