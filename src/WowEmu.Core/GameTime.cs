using System.Diagnostics;

namespace WowEmu.Core;

/// <summary>
/// The server's monotonic millisecond clock.
/// </summary>
/// <remarks>
/// Port of <c>getMSTime()</c> / <c>getMSTimeDiff()</c> in <c>src/common/Utilities/Timer.h</c>.
/// <para>
/// <b>The 32-bit width is load-bearing and the wraparound arithmetic must be preserved.</b> The
/// counter is milliseconds since process start in a <see cref="uint"/>, so it wraps roughly every
/// 49.7 days. Everything that stores a timestamp — spell cooldowns, movement, aura durations,
/// respawns — stores one of these, and <see cref="Diff"/> is what makes comparisons keep working
/// across the wrap. Widening it to 64 bits would silently change behaviour in code that relies on
/// the modular arithmetic, so it stays 32.
/// </para>
/// </remarks>
public static class MsTime
{
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Milliseconds since the process started, wrapping at <see cref="uint.MaxValue"/>.</summary>
    public static uint Now =>
        (uint)(long)Stopwatch.GetElapsedTime(StartTimestamp, Stopwatch.GetTimestamp()).TotalMilliseconds;

    /// <summary>
    /// Elapsed milliseconds from <paramref name="oldMsTime"/> to <paramref name="newMsTime"/>,
    /// correct across a single wrap of the counter.
    /// </summary>
    /// <remarks>
    /// When the old value is the larger one, the counter is assumed to have wrapped between them
    /// rather than time having run backwards. That assumption is what makes a 49-day-old timestamp
    /// indistinguishable from a fresh one — which is fine, because nothing holds one that long.
    /// </remarks>
    public static uint Diff(uint oldMsTime, uint newMsTime) =>
        oldMsTime > newMsTime
            ? (uint.MaxValue - oldMsTime) + newMsTime
            : newMsTime - oldMsTime;

    /// <summary>Elapsed milliseconds since <paramref name="oldMsTime"/>.</summary>
    public static uint DiffToNow(uint oldMsTime) => Diff(oldMsTime, Now);
}

/// <summary>
/// Wall-clock time as the game sees it: whole seconds since the Unix epoch, sampled once per world
/// tick rather than read from the OS on every call.
/// </summary>
/// <remarks>
/// Port of <c>src/server/game/Time/GameTime.h</c>. Gameplay code must read <see cref="Now"/>
/// instead of <see cref="DateTimeOffset.UtcNow"/> so that everything inside one tick agrees on what
/// time it is — otherwise two systems in the same tick can land on different seconds and produce
/// off-by-one durations that only reproduce under load.
/// </remarks>
public static class GameTime
{
    private static long _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static uint _uptime;
    private static uint _lastTickMs = MsTime.Now;

    /// <summary>When the server started.</summary>
    public static DateTimeOffset StartTime { get; } = DateTimeOffset.UtcNow;

    /// <summary>Unix seconds, as of the current tick.</summary>
    public static long Now => Interlocked.Read(ref _now);

    /// <summary>Whole seconds the server has been running.</summary>
    public static uint Uptime => Volatile.Read(ref _uptime);

    /// <summary>Milliseconds elapsed during the tick that just ran.</summary>
    public static uint LastTickDiff { get; private set; }

    /// <summary>
    /// Advances the clock. Called once per world tick, from the world loop and nowhere else.
    /// </summary>
    public static void UpdateGameTimers()
    {
        uint currentMs = MsTime.Now;

        LastTickDiff = MsTime.Diff(_lastTickMs, currentMs);
        _lastTickMs = currentMs;

        long currentSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Interlocked.Exchange(ref _now, currentSeconds);
        Volatile.Write(ref _uptime, (uint)(currentSeconds - StartTime.ToUnixTimeSeconds()));
    }
}
