namespace ModelSwitch.Usage.Contracts;

/// <summary>
/// The Estimated Cost an Evaluation Run has recorded, for its <c>maxEstimatedCost</c> check (evaluation.md section 4, "Spend"). Both
/// sums come from what settlement booked (USD), never from a Provider's charge; a request that is still running counts as 0.
/// </summary>
public interface IEvaluationSpend
{
    /// <summary>
    /// The Estimated Cost of every Inference Request recorded for the run (candidate and judge calls, Request Origin
    /// <c>evaluation</c>). Scans the run's requests, so the worker calls it once when it claims the run, not per Case.
    /// </summary>
    Task<decimal> RunEstimatedCostAsync(Guid evaluationRunId, CancellationToken cancellationToken);

    /// <summary>The Estimated Cost of the given Inference Requests: the sum of their Usage Records (a failed request's ambiguous Attempts included).</summary>
    Task<decimal> RequestsEstimatedCostAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken);
}
