using System.Text.Json;

namespace ModelSwitch.Usage.Domain;

/// <summary>
/// The payload of <c>usage.request_completed</c> (data-model.md, <c>outbox_event</c>): written by settlement and by the sweeper,
/// read by the usage rollup. Metadata only (ADR-0010). One record for both sides, so the shape cannot drift. <c>ProviderModelId</c> is
/// the Provider Model the request is attributed to in rollups: the one that succeeded, else the last one called, else null.
/// <c>Origin</c> is the Request Origin (<c>api</c> or <c>evaluation</c>); events written before it was recorded have none, and the
/// rollup reads them as <c>api</c>, the only origin that existed then.
/// </summary>
internal sealed record RequestCompletedEvent(
    Guid RequestId,
    Guid TenantId,
    string RequestedAlias,
    string Status,
    string UsageStatus,
    Guid? ProviderModelId,
    Guid? SelectedProviderModelId,
    int AttemptCount,
    bool HadFallback,
    int? InputTokens,
    int? CachedInputTokens,
    int? OutputTokens,
    decimal EstimatedCost,
    string Currency,
    DateTimeOffset StartedAt,
    int? DurationMs,
    int? TtftMs,
    string? Origin = null)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, _json);

    public static RequestCompletedEvent FromJson(string json)
        => JsonSerializer.Deserialize<RequestCompletedEvent>(json, _json) ?? throw new JsonException("The usage.request_completed payload is empty.");
}
