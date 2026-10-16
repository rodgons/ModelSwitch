using Microsoft.EntityFrameworkCore;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary><see cref="IEvaluationRunCost"/> over <c>usage.inference_request</c> and <c>usage.usage_record</c>.</summary>
internal sealed class EfEvaluationRunCost(IDbContextFactory<UsageDbContext> contexts) : IEvaluationRunCost
{
    public async Task<decimal> RunEstimatedCostAsync(Guid evaluationRunId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.InferenceRequests.AsNoTracking()
            .Where(r => r.EvaluationRunId == evaluationRunId)
            .SumAsync(r => r.TotalEstimatedCost ?? 0m, cancellationToken);
    }

    public async Task<decimal> RequestsEstimatedCostAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestIds);
        if (requestIds.Count == 0)
        {
            return 0m;
        }

        var ids = requestIds.ToArray();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.UsageRecords.AsNoTracking()
            .Where(u => ids.Contains(u.InferenceRequestId))
            .SumAsync(u => u.TotalCost, cancellationToken);
    }
}
