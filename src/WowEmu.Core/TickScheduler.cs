using System.Collections.Concurrent;

namespace WowEmu.Core;

/// <summary>
/// A <see cref="TaskScheduler"/> owned by a single loop — the world tick or one map worker — that
/// runs continuations only at a known point in that loop.
/// </summary>
/// <remarks>
/// This is rule 3 of the threading model in PLAN.md §4.2: <b>database results, and every other
/// asynchronous completion, resolve on a tick-bound scheduler and never on the raw thread pool.</b>
/// <para>
/// It is what lets gameplay code be written as linear <c>await</c> and still obey rule 1 — a
/// <c>WorldObject</c> is only ever touched on its own map's task. Upstream cannot do this: its
/// character-creation path is a four-hop callback chain precisely because C++ has no way to say
/// "resume this function on the map thread". Here, <c>await CharacterDb.QueryAsync(...)</c> resumes
/// on the loop that started it, and the code reads top to bottom.
/// </para>
/// <para>
/// <b>The drain is bounded.</b> A burst of completions — a thousand queries answering at once —
/// must not turn one tick into a five-second stall, so a drain executes at most
/// <see cref="MaxItemsPerDrain"/> items and stops early if <see cref="MaxDrainTime"/> elapses.
/// Whatever is left waits for the next tick. When that happens <see cref="DeferredLastDrain"/> is
/// non-zero, which callers should log rather than ignore: silently deferring work looks exactly
/// like work that never arrived.
/// </para>
/// <para>
/// Continuations are never inlined. <see cref="TryExecuteTaskInline"/> always refuses, so a
/// continuation cannot start running in the middle of whatever queued it — the whole point is that
/// work happens at one predictable place in the tick.
/// </para>
/// </remarks>
public sealed class TickScheduler : TaskScheduler
{
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly Action<Exception>? _errorHandler;

    private int _ownerThreadId;

    /// <summary>Creates a scheduler. <paramref name="errorHandler"/> sees exceptions from posted callbacks.</summary>
    public TickScheduler(string name, Action<Exception>? errorHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        _errorHandler = errorHandler;
        Factory = new TaskFactory(CancellationToken.None, TaskCreationOptions.None, TaskContinuationOptions.None, this);
    }

    /// <summary>Identifies this scheduler in logs and assertion messages.</summary>
    public string Name { get; }

    /// <summary>Schedules work onto this loop from anywhere.</summary>
    public TaskFactory Factory { get; }

    /// <summary>Hard cap on items executed per drain.</summary>
    public int MaxItemsPerDrain { get; init; } = 4096;

    /// <summary>Wall-clock cap on one drain. Checked between items, so one slow item can overrun it.</summary>
    public TimeSpan MaxDrainTime { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Work waiting to run.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>Items executed since the scheduler was created.</summary>
    public long TotalExecuted { get; private set; }

    /// <summary>
    /// Items the last drain left behind because it hit a budget. Non-zero means the loop is behind.
    /// </summary>
    public int DeferredLastDrain { get; private set; }

    /// <summary>The thread this scheduler runs on, once <see cref="Attach"/> has been called.</summary>
    public int OwnerThreadId => Volatile.Read(ref _ownerThreadId);

    /// <summary>Whether the calling thread is the one this scheduler drains on.</summary>
    public bool IsOwnerThread => Environment.CurrentManagedThreadId == OwnerThreadId;

    public override int MaximumConcurrencyLevel => 1;

    /// <summary>
    /// Claims the calling thread as the owner and installs a matching
    /// <see cref="SynchronizationContext"/> on it.
    /// </summary>
    /// <remarks>
    /// The synchronization context is what catches a bare <c>await</c> — one without
    /// <c>ConfigureAwait(false)</c> — because <c>await</c> looks at the context before it looks at
    /// the task scheduler. Without it, gameplay code that forgets <c>ConfigureAwait</c> would
    /// resume on the thread pool, touching map state from the wrong thread, and nothing would
    /// complain until something corrupted much later.
    /// </remarks>
    public void Attach()
    {
        Volatile.Write(ref _ownerThreadId, Environment.CurrentManagedThreadId);
        SynchronizationContext.SetSynchronizationContext(new TickSynchronizationContext(this));
    }

    /// <summary>
    /// Runs queued work, up to the budget. Call once per tick, from the owning loop.
    /// </summary>
    /// <returns>How many items ran.</returns>
    public int Drain()
    {
        // Snapshot first: work queued *by* this drain belongs to the next tick, otherwise a
        // continuation that re-queues itself would spin here until the time budget expired.
        int available = Math.Min(_queue.Count, MaxItemsPerDrain);
        int executed = 0;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        while (executed < available && _queue.TryDequeue(out WorkItem item))
        {
            Execute(item);
            executed++;

            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= MaxDrainTime)
            {
                break;
            }
        }

        TotalExecuted += executed;
        DeferredLastDrain = _queue.Count;

        return executed;
    }

    /// <summary>
    /// Throws if the caller is not on this scheduler's thread.
    /// </summary>
    /// <remarks>
    /// Cheap enough to leave in release builds. Call it at the entry to anything that mutates map
    /// or session state — an immediate, obvious failure beats data corruption discovered an hour
    /// later.
    /// </remarks>
    public void AssertOwnerThread()
    {
        if (!IsOwnerThread)
        {
            throw new InvalidOperationException(
                $"'{Name}' state was touched from thread {Environment.CurrentManagedThreadId}, " +
                $"but it may only be touched from its own thread ({OwnerThreadId}).");
        }
    }

    protected override void QueueTask(Task task) => _queue.Enqueue(new WorkItem(task, null, null));

    /// <summary>Always refuses: continuations run at the drain point, never inline.</summary>
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks() =>
        [.. _queue.ToArray().Where(item => item.Task is not null).Select(item => item.Task!)];

    internal void Post(SendOrPostCallback callback, object? state) =>
        _queue.Enqueue(new WorkItem(null, callback, state));

    private void Execute(WorkItem item)
    {
        if (item.Task is not null)
        {
            // Exceptions are captured onto the task itself; nothing to catch here.
            TryExecuteTask(item.Task);
            return;
        }

        try
        {
            item.Callback?.Invoke(item.State);
        }
        catch (Exception exception)
        {
            // One bad callback must not take the tick down with it.
            if (_errorHandler is null)
            {
                throw;
            }

            _errorHandler(exception);
        }
    }

    private readonly record struct WorkItem(Task? Task, SendOrPostCallback? Callback, object? State);
}

/// <summary>
/// The <see cref="SynchronizationContext"/> half of <see cref="TickScheduler"/>.
/// </summary>
/// <remarks>
/// <see cref="Send"/> runs inline when already on the owning thread and otherwise throws rather
/// than blocking: waiting for the tick loop from outside it is a deadlock in every case where the
/// tick loop is itself waiting on the caller.
/// </remarks>
public sealed class TickSynchronizationContext(TickScheduler scheduler) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => scheduler.Post(d, state);

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);

        if (scheduler.IsOwnerThread)
        {
            d(state);
            return;
        }

        throw new InvalidOperationException(
            $"Blocking Send onto '{scheduler.Name}' from another thread would deadlock the tick. Post instead.");
    }

    public override SynchronizationContext CreateCopy() => new TickSynchronizationContext(scheduler);
}
