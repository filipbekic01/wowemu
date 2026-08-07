using WowEmu.WorldServer;

namespace WowEmu.Tests.Unit;

/// <summary>
/// The startup timing report.
/// </summary>
/// <remarks>
/// PLAN.md §6 Phase 4 budgets startup at thirty seconds, and until this existed nothing measured it
/// — which mattered because the tables read at startup have roughly doubled since that was written.
/// Measured against a real database it comes out at 4.7 seconds, so the budget is not close; the
/// point of the report is that the next person can see that without instrumenting anything.
/// </remarks>
public sealed class StartupReportTests
{
    /// <summary>A phase is recorded even when the work inside it throws.</summary>
    /// <remarks>
    /// A startup that dies on a missing table should still say how far it got and how long that
    /// took. Recording only on success loses exactly the run worth reading.
    /// </remarks>
    [Fact]
    public async Task AFailedPhase_IsStillRecorded()
    {
        StartupReport report = new();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await report.MeasureAsync("doomed", () => throw new InvalidOperationException("no table")));

        Assert.Contains(report.Slowest, p => p.Name == "doomed");
    }

    /// <summary>Phases come back slowest first, which is the order worth reading.</summary>
    [Fact]
    public async Task Phases_AreOrderedSlowestFirst()
    {
        StartupReport report = new();

        await report.MeasureAsync("quick", () => Task.CompletedTask);
        await report.MeasureAsync("slow", () => Task.Delay(60));

        Assert.Equal("slow", report.Slowest[0].Name);
    }

    /// <summary>
    /// Only phases worth naming appear in the summary.
    /// </summary>
    /// <remarks>
    /// A dozen sub-millisecond entries would bury the one that costs three seconds, which is the
    /// opposite of what a report is for.
    /// </remarks>
    [Fact]
    public async Task TheSummary_NamesOnlyThePhasesThatCost()
    {
        StartupReport report = new();

        await report.MeasureAsync("trivial", () => Task.CompletedTask);
        await report.MeasureAsync("expensive", () => Task.Delay(150));

        string summary = report.Summary();

        Assert.Contains("expensive", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("trivial", summary, StringComparison.Ordinal);
    }

    /// <summary>An empty report still says how long it took.</summary>
    /// <remarks>
    /// A run with nothing above the naming threshold has no breakdown, and the summary must not
    /// come out as a dangling separator.
    /// </remarks>
    [Fact]
    public void AReportWithNoNotablePhases_IsStillReadable()
    {
        string summary = new StartupReport().Summary();

        Assert.Contains("total", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("—", summary, StringComparison.Ordinal);
    }

    /// <summary>A fresh report is inside the budget, and the budget is the one PLAN names.</summary>
    [Fact]
    public void TheBudget_IsThirtySeconds()
    {
        Assert.Equal(30, StartupReport.Budget.TotalSeconds);
        Assert.False(new StartupReport().OverBudget);
    }
}
