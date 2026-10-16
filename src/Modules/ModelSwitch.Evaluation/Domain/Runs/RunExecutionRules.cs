using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Jobs;

namespace ModelSwitch.Evaluation.Domain.Runs;

/// <summary>
/// The run's cost cap (evaluation.md section 4, "Spend"). Before each Case starts, the run's Estimated Cost so far (candidate and
/// judge, from recorded usage) plus the worst case of the Cases still in flight plus this Case's worst case must stay within
/// <c>maxEstimatedCost</c>. Counting the in-flight worst cases keeps the cap honest with <c>concurrency</c> above 1, where several
/// Cases are started before any of them has settled.
/// </summary>
internal static class RunCostCap
{
    public static bool AllowsNextCase(decimal? cap, decimal spent, decimal inFlightWorstCase, decimal caseWorstCase)
        => cap is not { } limit || spent + inFlightWorstCase + caseWorstCase <= limit;
}

/// <summary>Resuming a run after a worker crash (NFR-02): a Case that already has a Case Result is never run again.</summary>
internal static class RunResume
{
    public static IReadOnlyList<Guid> Pending(IReadOnlyList<Guid> casesInOrder, IReadOnlySet<Guid> withResult)
    {
        ArgumentNullException.ThrowIfNull(casesInOrder);
        ArgumentNullException.ThrowIfNull(withResult);
        return [.. casesInOrder.Where(id => !withResult.Contains(id))];
    }

    /// <summary>
    /// A stored <c>skipped</c> Case Result means an earlier pass hit the cost cap (skipped means nothing else), so a resumed run starts
    /// no new Case: the Cases still without a result are skipped too. The earlier worker died between writing the skipped results and
    /// finishing the run.
    /// </summary>
    public static bool CostCapAlreadyReached(IEnumerable<string> storedStatuses)
    {
        ArgumentNullException.ThrowIfNull(storedStatuses);
        return storedStatuses.Any(s => s == CaseResultStatuses.Skipped);
    }
}

/// <summary>What a claimed <c>evaluation.run</c> job does with its run.</summary>
internal enum RunStart
{
    /// <summary>A queued run: mark it running and process every Case.</summary>
    Start,

    /// <summary>A running run whose previous worker died: process the Cases that have no result yet.</summary>
    Resume,

    /// <summary>A finished run (succeeded, failed or cancelled): nothing to do.</summary>
    Skip,
}

/// <summary>The Evaluation Run state machine: <c>queued → running → succeeded | failed | cancelled</c>, and <c>queued → cancelled</c>.</summary>
internal static class RunStatusRules
{
    public static bool IsTerminal(string status)
        => status is EvaluationRunStatuses.Succeeded or EvaluationRunStatuses.Failed or EvaluationRunStatuses.Cancelled;

    public static bool CanCancel(string status) => !IsTerminal(status);

    public static RunStart OnClaim(string status) => status switch
    {
        EvaluationRunStatuses.Queued => RunStart.Start,
        EvaluationRunStatuses.Running => RunStart.Resume,
        _ => RunStart.Skip,
    };

    /// <summary>
    /// An unfinished run whose <c>evaluation.run</c> job can never run again: the job is <c>dead</c> (a worker killed during the final
    /// attempt, which the handler never sees), <c>succeeded</c> without finishing the run, or gone (<paramref name="jobStatus"/>
    /// <see langword="null"/>: removed by retention). A <c>pending</c> or <c>running</c> job will still claim and finish the run.
    /// </summary>
    public static bool IsOrphaned(string runStatus, string? jobStatus)
        => !IsTerminal(runStatus) && jobStatus is null or JobStatuses.Dead or JobStatuses.Succeeded;

    /// <summary>Cancellation: once the run is no longer <c>running</c>, in-flight Cases finish and nothing new starts.</summary>
    public static bool MayStartCase(string status) => status == EvaluationRunStatuses.Running;

    /// <summary>
    /// The run's final status and <c>error</c>: the cost cap fails it, cancellation keeps it cancelled. The cap counts as reached when
    /// this pass hit it or when any stored Case Result is <c>skipped</c> (<paramref name="storedSkipped"/>): an earlier pass hit it and
    /// died before finishing the run, so the outcome follows the stored results, not only this pass.
    /// </summary>
    public static (string Status, string? Error) Outcome(bool costCapReached, bool cancelled, int storedSkipped)
    {
        if (costCapReached || storedSkipped > 0)
        {
            return (EvaluationRunStatuses.Failed, ErrorCode.RunCostCapReached.Value);
        }

        return cancelled ? (EvaluationRunStatuses.Cancelled, null) : (EvaluationRunStatuses.Succeeded, null);
    }

    /// <summary><c>cases_failed</c> counts the Case Results that did not pass and were attempted: failed and errored.</summary>
    public static bool CountsAsFailed(string caseStatus) => caseStatus is CaseResultStatuses.Failed or CaseResultStatuses.Errored;
}

/// <summary>Run-to-run variance of the quality number over runs of the same configuration (evaluation.md section 6).</summary>
/// <param name="Runs">Runs with a quality value.</param>
/// <param name="Min">The lowest quality value.</param>
/// <param name="Max">The highest quality value.</param>
/// <param name="Mean">The mean quality value.</param>
/// <param name="StdDev">Sample standard deviation (n - 1).</param>
internal sealed record RunVariance(int Runs, decimal Min, decimal Max, decimal Mean, decimal StdDev)
{
    public const int Scale = 6;

    /// <summary>Null unless at least two runs have a quality value.</summary>
    public static RunVariance? Of(IEnumerable<decimal?> qualities)
    {
        var values = qualities.Where(q => q is not null).Select(q => q!.Value).ToArray();
        if (values.Length < 2)
        {
            return null;
        }

        var mean = values.Sum() / values.Length;
        var squares = values.Sum(v => (v - mean) * (v - mean));
        var stdDev = (decimal)Math.Sqrt((double)(squares / (values.Length - 1)));
        return new RunVariance(values.Length, values.Min(), values.Max(), Round(mean), Round(stdDev));
    }

    private static decimal Round(decimal value) => decimal.Round(value, Scale, MidpointRounding.ToEven);
}
