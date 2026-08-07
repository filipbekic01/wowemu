using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WowEmu.Core;
using WowEmu.Data.Db;
using WowEmu.Game;
using WowEmu.Game.Maps;

namespace WowEmu.WorldServer;

/// <summary>
/// The world tick: the loop everything in the game layer runs on.
/// </summary>
/// <remarks>
/// Port of <c>WorldUpdateLoop</c> and the session half of <c>World::Update</c>. PLAN.md §4.2 calls
/// this the single most important architectural constraint in the port, and the ordering inside
/// <see cref="Update"/> is the whole of it:
/// <list type="number">
/// <item><b>Drain the scheduler.</b> Continuations of anything a previous tick awaited resume here,
/// on this thread, rather than on the thread pool.</item>
/// <item><b>Update sessions.</b> World-queue packets run — logins, character operations, logouts.
/// These add and remove players from maps.</item>
/// <item><b>Update maps.</b> Only now do the map workers run, so nothing on a map worker can be
/// racing a login. That, not a lock, is what makes <c>Map</c> safe.</item>
/// </list>
/// <para>
/// Its own thread, not the thread pool. The tick must be able to run when the pool is saturated;
/// that is precisely when falling behind is most expensive.
/// </para>
/// </remarks>
public sealed class WorldLoop : BackgroundService
{
    private readonly SessionRegistry _sessions;
    private readonly MapManager _maps;
    private readonly ILogger<WorldLoop> _logger;
    private readonly WorldServerOptions _options;
    private readonly TickScheduler _scheduler;
    private readonly IInventoryRepository _resets;

    private uint _sinceSave;
    private uint _sinceReport;

    /// <summary>
    /// When each shared reset next falls. <c>default</c> until the first tick has scheduled them.
    /// </summary>
    private DateTime _nextDailyReset;
    private DateTime _nextWeeklyReset;
    private DateTime _nextMonthlyReset;

    public WorldLoop(
        SessionRegistry sessions,
        MapManager maps,
        IInventoryRepository resets,
        IOptions<WorldServerOptions> options,
        ILogger<WorldLoop> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _sessions = sessions;
        _maps = maps;
        _resets = resets;
        _logger = logger;
        _options = options.Value;
        _scheduler = new TickScheduler("world", exception => Log.TickWorkFailed(logger, exception));
    }

    /// <summary>The scheduler sessions post deferred work to. Drained at the top of every tick.</summary>
    public TickScheduler Scheduler => _scheduler;

    /// <summary>How many ticks have run. Diagnostics only.</summary>
    public long TickCount { get; private set; }

    /// <summary>The longest single tick seen, in milliseconds.</summary>
    public double LongestTickMs { get; private set; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(
            () => Run(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void Run(CancellationToken stoppingToken)
    {
        // Claims this thread, and installs the synchronization context that catches a bare `await`
        // in gameplay code — without it, a forgotten ConfigureAwait resumes on the thread pool and
        // touches map state from the wrong thread with nothing to say so.
        _scheduler.Attach();

        Log.TickStarted(_logger, _options.MinWorldUpdateTimeMs, _maps.ActiveMaps.Count);

        uint previous = MsTime.Now;

        while (!stoppingToken.IsCancellationRequested)
        {
            uint now = MsTime.Now;
            uint diff = MsTime.Diff(previous, now);

            if (diff < _options.MinWorldUpdateTimeMs)
            {
                // Upstream sleeps the remainder rather than spinning. A tick that ran in under a
                // millisecond has nothing to do; burning a core to discover that again immediately
                // helps nobody.
                Thread.Sleep((int)(_options.MinWorldUpdateTimeMs - diff));
                continue;
            }

            previous = now;

            long started = Stopwatch.GetTimestamp();
            TickPhases phases = Update(diff);
            double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            TickCount++;

            if (elapsedMs > LongestTickMs)
            {
                LongestTickMs = elapsedMs;
            }

            if (elapsedMs > _options.SlowTickThresholdMs)
            {
                // The total on its own says a tick was slow but never which of the four phases did
                // it, and the phases have nothing in common: one is deferred continuations, one is
                // packet handling, one is the maps, one is the database. Guessing between them from
                // a single number is how the wrong thing gets optimised.
                Log.SlowTick(
                    _logger, elapsedMs, diff, _sessions.Count,
                    phases.DrainMs, phases.PacketsMs, phases.MapsMs, phases.SaveMs);
            }
        }

        Log.TickStopped(_logger, TickCount, LongestTickMs);
    }

    /// <summary>How long each phase of one tick took. Only read when the tick was slow.</summary>
    private readonly record struct TickPhases(
        double DrainMs,
        double PacketsMs,
        double MapsMs,
        double SaveMs);

    private TickPhases Update(uint diff)
    {
        // 1. Deferred work. Anything a handler awaited last tick resumes here.
        long phaseStarted = Stopwatch.GetTimestamp();

        int executed = _scheduler.Drain();

        if (executed > 0 && _scheduler.DeferredLastDrain > 0)
        {
            // Deferring silently looks exactly like work that never arrived.
            Log.TickWorkDeferred(_logger, _scheduler.DeferredLastDrain);
        }

        double drainMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        IReadOnlyList<WorldSession> sessions = _sessions.Snapshot();

        // 2. World-queue packets. Logins and logouts move players on and off maps, which is only
        //    safe here — before any map worker starts.
        phaseStarted = Stopwatch.GetTimestamp();

        foreach (WorldSession session in sessions)
        {
            session.DrainWorldPackets(diff);
        }

        double packetsMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        // 3. Maps. From here until Update returns, map state belongs to the workers.
        phaseStarted = Stopwatch.GetTimestamp();
        _maps.Update(diff);
        double mapsMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        phaseStarted = Stopwatch.GetTimestamp();
        PeriodicSave(diff, sessions);
        PeriodicResets(sessions);
        PeriodicReport(diff);
        double saveMs = Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds;

        return new TickPhases(drainMs, packetsMs, mapsMs, saveMs);
    }

    /// <summary>
    /// Runs the shared daily, weekly and monthly quest resets when their moment passes.
    /// </summary>
    /// <remarks>
    /// <b>Driven by the wall clock, not by an interval.</b> These are server-wide instants: a
    /// character who did a daily a minute before the reset may do it again a minute after. Counting
    /// twenty-four hours from when each character did it instead lets a player walk their own reset
    /// later every day, and a server restart would reset the countdown for everyone.
    /// <para>
    /// Both halves are needed. The database delete covers every character on the realm, including
    /// the ones offline; the in-memory clear covers the ones logged in, whose state would otherwise
    /// be written back over the delete at their next save.
    /// </para>
    /// </remarks>
    private void PeriodicResets(IReadOnlyList<WorldSession> sessions)
    {
        DateTime now = DateTime.UtcNow;

        Run(QuestResetPeriod.Daily, ref _nextDailyReset, QuestResetTime.NextDaily);
        Run(QuestResetPeriod.Weekly, ref _nextWeeklyReset, QuestResetTime.NextWeekly);
        Run(QuestResetPeriod.Monthly, ref _nextMonthlyReset, QuestResetTime.NextMonthly);

        void Run(QuestResetPeriod period, ref DateTime next, Func<DateTime, DateTime> schedule)
        {
            if (next == default)
            {
                // First tick after startup. Scheduled, not fired: the rows on disk are whatever
                // survived the last reset, and firing here would wipe them on every restart.
                next = schedule(now);

                return;
            }

            if (now < next)
            {
                return;
            }

            next = schedule(now);

            foreach (WorldSession session in sessions)
            {
                session.ResetQuests(period);
            }

            _ = _scheduler.Factory.StartNew(
                () => _resets.ResetAllQuestsAsync(period, CancellationToken.None),
                CancellationToken.None).Unwrap();

            // Into a local: the analyzer objects to ToString() inside a log call.
            string name = period.ToString();

            Log.QuestsReset(_logger, name, next);
        }
    }

    /// <summary>
    /// Writes every logged-in character's position back to the database, on a timer.
    /// </summary>
    /// <remarks>
    /// The first thing in the server that could not exist before there was a tick. Until now a
    /// character was saved only on logout and on disconnect, so a server that crashed lost every
    /// player's progress since they logged in.
    /// <para>
    /// Saves are started, not awaited: the tick must not wait on the database. Each resumes on this
    /// scheduler, so the session is touched from the world thread as the rules require.
    /// </para>
    /// </remarks>
    private void PeriodicSave(uint diff, IReadOnlyList<WorldSession> sessions)
    {
        _sinceSave += diff;

        if (_sinceSave < _options.PlayerSaveIntervalMs)
        {
            return;
        }

        _sinceSave = 0;
        int saved = 0;

        foreach (WorldSession session in sessions)
        {
            if (session.HasPlayerInWorld)
            {
                _ = _scheduler.Factory.StartNew(
                    () => session.SavePlayerAsync(CancellationToken.None),
                    CancellationToken.None).Unwrap();

                saved++;
            }
        }

        if (saved > 0)
        {
            Log.PeriodicSave(_logger, saved);
        }
    }

    private void PeriodicReport(uint diff)
    {
        _sinceReport += diff;

        if (_sinceReport < _options.TickReportIntervalMs)
        {
            return;
        }

        _sinceReport = 0;

        Log.TickReport(
            _logger,
            TickCount,
            _sessions.Count,
            _maps.ActiveMaps.Count,
            LongestTickMs);

        // Reset so the next window reports its own worst case rather than the worst ever seen,
        // which after a slow startup would never change again.
        LongestTickMs = 0;
    }

    public override void Dispose()
    {
        base.Dispose();
        _maps.Dispose();
    }
}
