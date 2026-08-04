using WowEmu.Core;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The millisecond clock's wraparound arithmetic — a Phase 0 exit criterion in PLAN.md §6.
/// </summary>
/// <remarks>
/// The counter is 32-bit milliseconds since start, so it wraps every ~49.7 days. Every timestamp in
/// the game layer is one of these, and every comparison goes through <see cref="MsTime.Diff"/>.
/// Widening the type would make these tests meaningless and change behaviour in the code that
/// depends on the modular arithmetic.
/// </remarks>
public sealed class MsTimeTests
{
    [Fact]
    public void Diff_IsSimpleSubtraction_WhenNoWrapHappened()
    {
        Assert.Equal(500u, MsTime.Diff(1000, 1500));
        Assert.Equal(0u, MsTime.Diff(1000, 1000));
    }

    /// <summary>
    /// The case the whole design exists for: the counter rolled over between the two samples, so
    /// the "old" value is numerically larger.
    /// </summary>
    [Fact]
    public void Diff_BridgesTheWrap_WhenOldIsLargerThanNew()
    {
        // 100 ms before the wrap, to 50 ms after it.
        uint before = uint.MaxValue - 100;
        const uint After = 50;

        Assert.Equal(150u, MsTime.Diff(before, After));
    }

    /// <summary>
    /// Upstream's wrap formula is <c>(0xFFFFFFFF - old) + new</c>, which is one short of the true
    /// modular difference — the wrap from <c>0xFFFFFFFF</c> to <c>0</c> is one millisecond but
    /// measures as zero. Reproduced rather than corrected: this arithmetic decides when timers
    /// fire, and a version that differs from the C++ server by a millisecond at the wrap is a
    /// worse problem than the millisecond itself.
    /// </summary>
    [Fact]
    public void Diff_AtTheExactBoundary_KeepsUpstreamsOffByOne()
    {
        Assert.Equal(0u, MsTime.Diff(uint.MaxValue, 0));
        Assert.Equal(uint.MaxValue, MsTime.Diff(0, uint.MaxValue));
    }

    /// <summary>
    /// A naive <c>new - old</c> would return roughly 4.29 billion here instead of 150 — the bug
    /// this arithmetic exists to prevent, and one that only shows up 49 days into an uptime.
    /// </summary>
    [Fact]
    public void Diff_DoesNotReturnTheNaiveUnderflow()
    {
        uint before = uint.MaxValue - 100;
        const uint After = 50;

        uint naive = unchecked(After - before);

        Assert.NotEqual(naive, MsTime.Diff(before, After));
        Assert.True(MsTime.Diff(before, After) < 1000);
    }

    [Fact]
    public void Now_MovesForward()
    {
        uint first = MsTime.Now;
        Thread.Sleep(5);
        uint second = MsTime.Now;

        Assert.True(MsTime.Diff(first, second) >= 1, "the clock did not advance over a 5 ms sleep");
    }

    [Fact]
    public void DiffToNow_MeasuresElapsedTime()
    {
        uint start = MsTime.Now;
        Thread.Sleep(10);

        uint elapsed = MsTime.DiffToNow(start);

        Assert.InRange(elapsed, 1u, 5000u);
    }
}

/// <summary>The tick-sampled wall clock.</summary>
public sealed class GameTimeTests
{
    [Fact]
    public void Now_IsAUnixTimestampNearTheRealClock()
    {
        GameTime.UpdateGameTimers();

        long expected = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.InRange(GameTime.Now, expected - 5, expected + 5);
    }

    [Fact]
    public void UpdateGameTimers_RecordsTheTickLength()
    {
        GameTime.UpdateGameTimers();
        Thread.Sleep(15);
        GameTime.UpdateGameTimers();

        Assert.InRange(GameTime.LastTickDiff, 1u, 5000u);
    }

    [Fact]
    public void Uptime_IsNotNegative()
    {
        GameTime.UpdateGameTimers();

        Assert.True(GameTime.Uptime < 60, "a freshly started test host should not report a long uptime");
    }
}
