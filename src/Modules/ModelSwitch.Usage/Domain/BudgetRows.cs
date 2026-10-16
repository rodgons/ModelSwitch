namespace ModelSwitch.Usage.Domain;

/// <summary>A row of <c>usage.budget</c>: the Tenant's standing monthly limit. No row means no Budget.</summary>
internal sealed class BudgetRow
{
    public Guid TenantId { get; init; }

    public decimal LimitAmount { get; init; }

    public required string Currency { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A row of <c>usage.budget_period</c>: one Tenant, one calendar month.</summary>
internal sealed class BudgetPeriodRow
{
    public Guid TenantId { get; init; }

    public DateOnly PeriodStart { get; init; }

    public decimal? LimitAmount { get; init; }

    public decimal ReservedAmount { get; init; }

    public decimal SettledAmount { get; init; }

    public required string Currency { get; init; }
}

/// <summary>A row of <c>usage.reservation</c>: the worst-case hold of one Inference Request.</summary>
internal sealed class ReservationRow
{
    public Guid Id { get; init; }

    public Guid InferenceRequestId { get; init; }

    public Guid TenantId { get; init; }

    public DateOnly PeriodStart { get; init; }

    public decimal Amount { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Equal to the request's <c>started_at</c>, which is the partition key needed to find the request again.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? SettledAt { get; init; }
}

/// <summary>A row of <c>usage.usage_record</c>. Authoritative for what a request cost (usage-and-budgets.md section 1).</summary>
internal sealed class UsageRecordRow
{
    public Guid Id { get; init; }

    public Guid InferenceRequestId { get; init; }

    /// <summary>Null only for the sweeper's record of an abandoned request, which has no Attempt.</summary>
    public Guid? ProviderAttemptId { get; init; }

    public Guid TenantId { get; init; }

    public Guid? ProviderModelId { get; init; }

    public Guid? ModelPriceId { get; init; }

    public int? InputTokens { get; init; }

    public int? CachedInputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public required string UsageSource { get; init; }

    public string? EstimateMethod { get; init; }

    public decimal InputCost { get; init; }

    public decimal OutputCost { get; init; }

    public decimal TotalCost { get; init; }

    public required string Currency { get; init; }

    public DateTimeOffset RecordedAt { get; init; }
}
