using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Domain;

/// <summary>The request-level figures derived from a request's Attempts when it settles (data-model.md, <c>inference_request</c>).</summary>
internal sealed record RequestSummary(
    int AttemptCount,
    bool HadFallback,
    Guid? SelectedProviderModelId,
    int? TotalInputTokens,
    int? TotalOutputTokens,
    Money? TotalEstimatedCost,
    string UsageStatus)
{
    public static RequestSummary From(IReadOnlyList<AttemptRecord> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        // An Attempt skipped because its circuit was open never reached a Provider: it has no usage and is not an Attempt for
        // the maxAttempts bound, so it takes no part in the totals or the usage status.
        var called = attempts.Where(a => a.Status != AttemptStatus.SkippedCircuitOpen).ToList();

        return new RequestSummary(
            AttemptCount: called.Count,
            HadFallback: attempts.Any(a => a.Kind == AttemptKind.Fallback),
            SelectedProviderModelId: attempts.LastOrDefault(a => a.Status == AttemptStatus.Succeeded)?.ProviderModelId,
            TotalInputTokens: Sum(called.Select(a => a.InputTokens)),
            TotalOutputTokens: Sum(called.Select(a => a.OutputTokens)),
            TotalEstimatedCost: called.Any(a => a.EstimatedCost is not null)
                ? called.Where(a => a.EstimatedCost is not null).Aggregate(Money.Zero, (sum, a) => sum + a.EstimatedCost!.Value)
                : null,
            UsageStatus: StatusOf(called));
    }

    /// <summary>
    /// The worst source over the Attempts (reported &lt; estimated &lt; unavailable), or <c>partial</c> when reported and
    /// non-reported Attempts are mixed (usage-and-budgets.md section 2). A request with no Attempts spent nothing, so its
    /// usage is fully known: <c>reported</c>.
    /// </summary>
    private static string StatusOf(IReadOnlyList<AttemptRecord> called)
    {
        var sources = called.Select(a => a.UsageSource).Distinct().ToList();
        if (sources.Count == 0 || sources is [UsageSource.Reported])
        {
            return "reported";
        }

        if (sources.Contains(UsageSource.Reported))
        {
            return "partial";
        }

        return sources.Contains(UsageSource.Unavailable) ? "unavailable" : "estimated";
    }

    private static int? Sum(IEnumerable<int?> values)
    {
        long total = 0;
        var any = false;
        foreach (var value in values)
        {
            if (value is { } v)
            {
                total += v;
                any = true;
            }
        }

        return any ? (int)Math.Min(total, int.MaxValue) : null;
    }
}
