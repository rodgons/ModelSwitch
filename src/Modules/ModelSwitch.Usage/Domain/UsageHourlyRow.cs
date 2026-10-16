namespace ModelSwitch.Usage.Domain;

/// <summary>A <c>usage.usage_hourly</c> row: the rollup the worker keeps per Tenant, Provider Model, alias, Request Origin and hour (data-model.md).</summary>
internal sealed class UsageHourlyRow
{
    public Guid TenantId { get; set; }

    /// <summary><see cref="Guid.Empty"/> for requests that reached no Provider.</summary>
    public Guid ProviderModelId { get; set; }

    public string RequestedAlias { get; set; } = string.Empty;

    /// <summary>The Request Origin: <c>api</c> or <c>evaluation</c>.</summary>
    public string Origin { get; set; } = string.Empty;

    public DateTimeOffset Hour { get; set; }

    public int Requests { get; set; }

    public int FailedRequests { get; set; }

    public int FallbackRequests { get; set; }

    public int Attempts { get; set; }

    public long InputTokens { get; set; }

    public long CachedInputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal EstimatedCost { get; set; }

    public long DurationMsSum { get; set; }

    public int[] LatencyHistogram { get; set; } = [];

    public int[] TtftHistogram { get; set; } = [];
}
