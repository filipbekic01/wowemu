using System.Diagnostics;
using System.Globalization;

namespace WowEmu.WorldServer;

/// <summary>
/// Where startup time goes, and whether it is still inside its budget.
/// </summary>
/// <remarks>
/// PLAN.md §6 Phase 4 budgets the whole of startup at under thirty seconds. A budget nothing
/// measures is a number in a document, and the tables this server reads have roughly doubled since
/// it was written — 112,797 waypoints, 43,020 creature addons, 10,731 outfits and the rest all
/// arrived after it.
/// <para>
/// <b>Per phase, not just a total.</b> A total says the budget was missed; the phases say which
/// table to look at. The whole point of writing it down is that the next person does not have to
/// bisect the startup path to find out.
/// </para>
/// </remarks>
public sealed class StartupReport
{
    /// <summary>What PLAN.md §6 Phase 4 allows for the whole of startup.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly List<(string Name, double Milliseconds)> _phases = [];
    private readonly long _started = Stopwatch.GetTimestamp();

    /// <summary>How long startup has taken so far.</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    /// <summary>Whether startup has run past its budget.</summary>
    public bool OverBudget => Elapsed > Budget;

    /// <summary>Times a phase, whatever it throws.</summary>
    /// <remarks>
    /// The phase is recorded even when the work inside it fails, so a startup that dies on a missing
    /// table still reports how far it got and how long that took.
    /// </remarks>
    public async Task MeasureAsync(string name, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        long started = Stopwatch.GetTimestamp();

        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _phases.Add((name, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
    }

    /// <summary>The phases in the order they ran, slowest first.</summary>
    public IReadOnlyList<(string Name, double Milliseconds)> Slowest =>
        [.. _phases.OrderByDescending(p => p.Milliseconds)];

    /// <summary>
    /// A one-line summary: the total, then the phases worth naming.
    /// </summary>
    /// <remarks>
    /// Only the phases above a tenth of a second are named. A dozen sub-millisecond entries would
    /// bury the two that actually cost something, which is the opposite of what a report is for.
    /// </remarks>
    public string Summary()
    {
        const double WorthNamingMs = 100;

        IEnumerable<string> named = Slowest
            .Where(p => p.Milliseconds >= WorthNamingMs)
            .Select(p => string.Create(
                CultureInfo.InvariantCulture, $"{p.Name} {p.Milliseconds / 1000:F1}s"));

        string breakdown = string.Join(", ", named);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Elapsed.TotalSeconds:F1}s total{(breakdown.Length > 0 ? $" — {breakdown}" : string.Empty)}");
    }
}
