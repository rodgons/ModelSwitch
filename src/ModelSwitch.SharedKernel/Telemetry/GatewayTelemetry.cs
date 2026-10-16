using System.Diagnostics;

namespace ModelSwitch.SharedKernel.Telemetry;

/// <summary>
/// The names every module shares for traces and metrics (observability.md sections 2 and 3). One <see cref="ActivitySource"/> and one
/// meter name, both <c>ModelSwitch</c>. Span attributes are metadata only: ids, names, counts and outcomes, never prompt or
/// completion content (ADR-0010, security.md section 6).
/// </summary>
public static class GatewayTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> and meter name. Instruments are created through <c>IMeterFactory</c> with this name.</summary>
    public const string Name = "ModelSwitch";

    /// <summary>The source of every ModelSwitch span.</summary>
    public static ActivitySource Source { get; } = new(Name);

    /// <summary>Span names of the request path (observability.md section 2).</summary>
    public static class Spans
    {
        public const string Auth = "modelswitch.auth";
        public const string Limits = "modelswitch.limits";
        public const string Route = "modelswitch.route";
        public const string Reserve = "modelswitch.reserve";
        public const string Attempt = "modelswitch.attempt";
        public const string Settle = "modelswitch.settle";
    }

    /// <summary>Span attribute names. <c>gen_ai.*</c> follow the OTel GenAI conventions for metadata only.</summary>
    public static class Attributes
    {
        public const string RequestId = "modelswitch.request_id";
        public const string TenantId = "modelswitch.tenant_id";
        public const string CacheHit = "modelswitch.auth.cache_hit";
        public const string Result = "modelswitch.result";
        public const string PolicyId = "modelswitch.policy_id";
        public const string PolicyVersion = "modelswitch.policy_version";
        public const string Candidates = "modelswitch.candidates";
        public const string Chosen = "modelswitch.chosen_provider_model_id";
        public const string Provider = "modelswitch.provider_id";
        public const string AttemptKind = "modelswitch.attempt.kind";
        public const string AttemptNumber = "modelswitch.attempt.number";
        public const string Outcome = "modelswitch.outcome";
        public const string ErrorClass = "modelswitch.error_class";
        public const string ErrorCode = "modelswitch.error_code";
        public const string RequestModel = "gen_ai.request.model";
        public const string ResponseModel = "gen_ai.response.model";
        public const string InputTokens = "gen_ai.usage.input_tokens";
        public const string OutputTokens = "gen_ai.usage.output_tokens";
    }

    /// <summary>Metric label names. <c>tenant</c> is deliberately absent: Tenant count is unbounded.</summary>
    public static class Labels
    {
        public const string Alias = "alias";
        public const string Provider = "provider";
        public const string Model = "model";
        public const string Status = "status";
        public const string ErrorCode = "error_code";
        public const string AttemptKind = "attempt_kind";
        public const string Streaming = "streaming";
        public const string Direction = "direction";
        public const string Source = "source";
        public const string Kind = "kind";
    }

    /// <summary>Starts a ModelSwitch span, or returns null when nothing listens (no SDK, or sampled out).</summary>
    public static Activity? StartSpan(string name, ActivityKind kind = ActivityKind.Internal) => Source.StartActivity(name, kind);
}
