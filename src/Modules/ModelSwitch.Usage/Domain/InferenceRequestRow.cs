namespace ModelSwitch.Usage.Domain;

/// <summary>A row of <c>usage.inference_request</c>. Metadata only: never prompt or completion content (ADR-0010).</summary>
internal sealed class InferenceRequestRow
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string Origin { get; init; }

    public Guid? ApiKeyId { get; init; }

    public Guid? EvaluationRunId { get; init; }

    public string? ClientRequestId { get; init; }

    public required string RequestedAlias { get; init; }

    public string? TaskType { get; init; }

    public bool Streaming { get; init; }

    public int InputBytes { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public required string UsageStatus { get; init; }

    public required string TraceId { get; init; }

    // Filled in when the request settles (UPDATE), so they are null/zero at insert.
    public Guid? PolicyId { get; init; }

    public int? PolicyVersion { get; init; }

    public Guid? SelectedProviderModelId { get; init; }

    public string? RoutingDecision { get; init; }

    public string? ErrorCode { get; init; }

    public short AttemptCount { get; init; }

    public bool HadFallback { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int? DurationMs { get; init; }

    public int? TtftMs { get; init; }

    public int? TotalInputTokens { get; init; }

    public int? TotalOutputTokens { get; init; }

    public decimal? TotalEstimatedCost { get; init; }
}

/// <summary>A row of <c>usage.provider_attempt</c>.</summary>
internal sealed class ProviderAttemptRow
{
    public Guid Id { get; init; }

    public Guid InferenceRequestId { get; init; }

    public short AttemptNumber { get; init; }

    public Guid ProviderId { get; init; }

    public Guid ProviderModelId { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }

    public string? ErrorClass { get; init; }

    public string? ErrorCode { get; init; }

    public short? HttpStatus { get; init; }

    public string? ProviderErrorType { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; init; }

    public int DurationMs { get; init; }

    public int? TtftMs { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public decimal? EstimatedCost { get; init; }
}
