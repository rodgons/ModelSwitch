using ModelSwitch.Evaluation.Domain.Runs;

namespace ModelSwitch.Evaluation.Domain;

/// <summary>A run's state as far as deleting its Comparison is concerned.</summary>
/// <param name="Status">One of <see cref="EvaluationRunStatuses"/>.</param>
/// <param name="HasCompletedAt">True once the run's outcome and aggregates are stored.</param>
internal sealed record RunState(string Status, bool HasCompletedAt);

/// <summary>
/// When evaluation content may be deleted (evaluation.md section 8, data-model.md "Retention"). Content is removed Comparison first:
/// a Dataset or Prompt that a Comparison still uses is kept, so no run is left pointing at a missing Dataset or Prompt, and a
/// Comparison waits until the worker has nothing more to write for any of its runs.
/// </summary>
internal static class DeletionRules
{
    public static bool CanDeleteSource(int comparisonsUsingIt) => comparisonsUsingIt == 0;

    /// <summary>
    /// Every run must be finished with its outcome stored. A run cancelled while running is <c>cancelled</c> before its in-flight Cases
    /// are recorded and its aggregates stored (<c>completed_at</c> still null), so it blocks the delete until the worker is done.
    /// </summary>
    public static bool CanDeleteComparison(IEnumerable<RunState> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return runs.All(r => RunStatusRules.IsTerminal(r.Status) && r.HasCompletedAt);
    }
}
