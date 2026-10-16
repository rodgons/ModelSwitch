using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Domain;

/// <summary>
/// What one completed Inference Request adds to its <c>usage_hourly</c> row (data-model.md, usage-and-budgets.md section 6). The row
/// is keyed by Tenant, Provider Model, alias, Request Origin and the UTC hour the request started in. Rows only ever grow, so adding a contribution
/// is an upsert that sums columns and histograms.
/// </summary>
internal sealed record HourlyContribution(
    Guid TenantId,
    Guid ProviderModelId,
    string RequestedAlias,
    string Origin,
    DateTimeOffset Hour,
    int Requests,
    int FailedRequests,
    int FallbackRequests,
    int Attempts,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal EstimatedCost,
    long DurationMsSum,
    int[] LatencyHistogram,
    int[] TtftHistogram)
{
    /// <summary>The key of requests that never reached a Provider (no Provider Model to attribute them to).</summary>
    public static readonly Guid NoProviderModel = Guid.Empty;

    /// <summary>
    /// Failed and abandoned requests count as failed; a cancelled one is neither a success nor a failure of the gateway. Unknown
    /// token counts count as zero. A request without a duration (abandoned) adds nothing to the latency histogram. An
    /// event without an origin was written before the rollup kept one, when every request came from an API Key, so it counts as <c>api</c>.
    /// </summary>
    public static HourlyContribution From(RequestCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var started = completed.StartedAt.ToUniversalTime();
        return new HourlyContribution(
            completed.TenantId,
            completed.ProviderModelId ?? NoProviderModel,
            completed.RequestedAlias,
            completed.Origin ?? RequestOrigins.ToWire(RequestOrigin.Api),
            new DateTimeOffset(started.Year, started.Month, started.Day, started.Hour, 0, 0, TimeSpan.Zero),
            Requests: 1,
            FailedRequests: completed.Status is "failed" or "abandoned" ? 1 : 0,
            FallbackRequests: completed.HadFallback ? 1 : 0,
            Attempts: completed.AttemptCount,
            InputTokens: completed.InputTokens ?? 0,
            CachedInputTokens: completed.CachedInputTokens ?? 0,
            OutputTokens: completed.OutputTokens ?? 0,
            completed.EstimatedCost,
            DurationMsSum: completed.DurationMs ?? 0,
            Domain.LatencyHistogram.Of(completed.DurationMs),
            Domain.LatencyHistogram.Of(completed.TtftMs));
    }
}
