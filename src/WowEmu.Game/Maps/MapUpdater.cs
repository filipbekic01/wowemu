using System.Collections.Concurrent;

namespace WowEmu.Game.Maps;

/// <summary>
/// The worker pool that map updates run on.
/// </summary>
/// <remarks>
/// Port of <c>MapUpdater</c>. Work is scheduled for a tick and then waited on, so no two updates of
/// the same map ever overlap and the caller knows every map has finished before it moves on — which
/// is the barrier the whole threading model rests on.
/// <para>
/// <b>Dedicated threads, not the thread pool.</b> The pool is where every database continuation and
/// socket callback already lives, and map updates must not queue behind a burst of those: a tick
/// that cannot start because the pool is saturated is a stall the pool's own heuristics will take
/// hundreds of milliseconds to notice. These threads exist to be available.
/// </para>
/// <para>
/// A pool of size zero runs work inline on the calling thread. That is not a degenerate case to
/// tolerate — it is how the tests run, and how a single-core machine should behave.
/// </para>
/// </remarks>
public sealed class MapUpdater : IDisposable
{
    private readonly BlockingCollection<Action>? _queue;
    private readonly List<Thread> _workers = [];
    private readonly ManualResetEventSlim _idle = new(initialState: true);

    private int _pending;

    /// <summary>Starts <paramref name="threadCount"/> workers. Zero means run inline.</summary>
    public MapUpdater(int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threadCount);

        if (threadCount == 0)
        {
            return;
        }

        _queue = [];

        for (int i = 0; i < threadCount; i++)
        {
            Thread worker = new(WorkerLoop)
            {
                IsBackground = true,
                Name = $"map-worker-{i}",
            };

            _workers.Add(worker);
            worker.Start();
        }
    }

    /// <summary>How many workers are running. Zero means updates happen inline.</summary>
    public int WorkerCount => _workers.Count;

    /// <summary>Queues one unit of work for this tick.</summary>
    public void Schedule(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_queue is null)
        {
            work();
            return;
        }

        // Counted before queueing. Incrementing after would let a worker finish the item and drop
        // the count to zero before this thread had raised it, and Wait would return immediately.
        if (Interlocked.Increment(ref _pending) == 1)
        {
            _idle.Reset();
        }

        _queue.Add(work);
    }

    /// <summary>Blocks until everything scheduled since the last wait has finished.</summary>
    public void Wait()
    {
        if (_queue is not null)
        {
            _idle.Wait();
        }
    }

    private void WorkerLoop()
    {
        foreach (Action work in _queue!.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception exception)
            {
                // A map that throws must not take its worker with it, or the pool bleeds threads
                // until the tick can never complete. The map's own update logs the detail.
                Failed?.Invoke(exception);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                {
                    _idle.Set();
                }
            }
        }
    }

    /// <summary>Raised when a scheduled update throws. The tick continues regardless.</summary>
    public event Action<Exception>? Failed;

    public void Dispose()
    {
        _queue?.CompleteAdding();

        foreach (Thread worker in _workers)
        {
            worker.Join(TimeSpan.FromSeconds(5));
        }

        _queue?.Dispose();
        _idle.Dispose();
    }
}
