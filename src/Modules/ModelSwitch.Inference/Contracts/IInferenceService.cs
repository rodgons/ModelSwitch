using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Contracts;

/// <summary>
/// What an Inference Request asks to be served by (evaluation.md section 4). HTTP traffic always names a <see cref="ModelAlias"/>;
/// an Evaluation Run freezes its target at creation, as a <see cref="PinnedPolicyVersion"/> or a <see cref="PinnedProviderModel"/>.
/// </summary>
public abstract record InferenceTarget
{
    private InferenceTarget()
    {
    }

    /// <summary>A Model Alias routed on the latest Policy Version of its policy.</summary>
    public static InferenceTarget For(string alias) => new ModelAlias(alias);

    /// <summary>A Model Alias routed on the given Policy Version, whatever version the alias resolves to now.</summary>
    public static InferenceTarget For(string alias, Guid policyId, int policyVersion) => new PinnedPolicyVersion(alias, policyId, policyVersion);

    /// <summary>One Provider Model, bypassing routing.</summary>
    public static InferenceTarget Pinned(Guid providerModelId) => new PinnedProviderModel(providerModelId);

    /// <summary>A Model Alias, routed on its policy's latest Policy Version in the configuration snapshot.</summary>
    public sealed record ModelAlias(string Alias) : InferenceTarget;

    /// <summary>
    /// A Model Alias routed on a fixed Policy Version (immutable, so the run is reproducible). The alias must still exist and the
    /// Tenant must still be allowed it; the version's Candidates are checked for Eligibility against the current catalog. An
    /// unknown version is <c>model_not_found</c>.
    /// </summary>
    public sealed record PinnedPolicyVersion(string Alias, Guid PolicyId, int PolicyVersion) : InferenceTarget;

    /// <summary>
    /// One Provider Model, bypassing routing ("pinned" mode): a single Candidate under the policy-independent execution settings
    /// (<c>PinnedRouting</c>). The Budget, the Tenant's per-request cost limit and its allowed aliases still apply: unless the Tenant
    /// may use every alias, the model must be a Candidate of at least one alias it is allowed (<c>model_not_allowed</c> otherwise).
    /// </summary>
    public sealed record PinnedProviderModel(Guid ProviderModelId) : InferenceTarget;
}

/// <summary>One chat completion asked of the gateway by a Tenant, for a Model Alias or (Evaluation only) a pinned target.</summary>
/// <param name="RequestId">The Inference Request ID (UUIDv7), also returned in <c>X-Request-Id</c>.</param>
/// <param name="TenantId">From the authenticated API Key, never from the request body; for Evaluation traffic, the run's Tenant.</param>
/// <param name="Alias">
/// The Model Alias the client asked for. When the request is built from an <see cref="InferenceTarget"/> it is the target's alias, or
/// empty for a <see cref="InferenceTarget.PinnedProviderModel"/> (whose requested name is then <c>provider/model</c>).
/// </param>
/// <param name="Chat">The validated, provider-neutral request.</param>
public sealed record InferenceRequest(Guid RequestId, Guid TenantId, string Alias, NeutralChatRequest Chat)
{
    private readonly InferenceTarget? _target;

    /// <summary>A request for <paramref name="target"/>; <see cref="Alias"/> follows from it.</summary>
    public InferenceRequest(Guid requestId, Guid tenantId, InferenceTarget target, NeutralChatRequest chat)
        : this(requestId, tenantId, AliasOf(target), chat)
    {
        _target = target;
    }

    /// <summary>What serves the request. Defaults to <see cref="Alias"/> on its latest Policy Version.</summary>
    public InferenceTarget Target
    {
        get => _target ?? new InferenceTarget.ModelAlias(Alias);
        init => _target = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The API Key that made the request. Required for <see cref="RequestOrigin.Api"/> traffic; null for Evaluation traffic.</summary>
    public Guid? ApiKeyId { get; init; }

    /// <summary>Api for HTTP traffic; Evaluation for requests an Evaluation Run generates (then <see cref="EvaluationRunId"/> is set).</summary>
    public RequestOrigin Origin { get; init; } = RequestOrigin.Api;

    /// <summary>The Evaluation Run that generated the request: set exactly when <see cref="Origin"/> is Evaluation.</summary>
    public Guid? EvaluationRunId { get; init; }

    /// <summary>
    /// The Request Origin invariants of <c>inference_request</c> (data-model.md): Evaluation traffic carries its run and no API Key,
    /// and only Evaluation traffic carries a run. Null when they hold. (An Api request's key is enforced by the database.)
    /// </summary>
    public string? OriginViolation() => Origin switch
    {
        RequestOrigin.Evaluation when EvaluationRunId is null => "An Evaluation request must name its Evaluation Run.",
        RequestOrigin.Evaluation when ApiKeyId is not null => "An Evaluation request has no API Key.",
        RequestOrigin.Api when EvaluationRunId is not null => "Only an Evaluation request names an Evaluation Run.",
        _ => null,
    };

    private static string AliasOf(InferenceTarget target) => target switch
    {
        InferenceTarget.ModelAlias alias => alias.Alias,
        InferenceTarget.PinnedPolicyVersion pinned => pinned.Alias,
        InferenceTarget.PinnedProviderModel => string.Empty,
        null => throw new ArgumentNullException(nameof(target)),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary><c>X-ModelSwitch-Task</c>, already validated: the Task Type that rule-based routing matches on.</summary>
    public string? TaskType { get; init; }

    /// <summary><c>X-Client-Request-Id</c>, already validated.</summary>
    public string? ClientRequestId { get; init; }

    /// <summary>Size of the request body in bytes, recorded as <c>input_bytes</c> (a size, never content).</summary>
    public int InputBytes { get; init; }

    /// <summary>
    /// <c>X-ModelSwitch-Timeout-Ms</c>: a Deadline shorter than the Routing Policy's. It can shorten the Deadline, never extend it.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>A successful completion. <see cref="Text"/> is content (ADR-0010): it is never logged or persisted.</summary>
/// <param name="RequestId">The Inference Request ID.</param>
/// <param name="Alias">The Model Alias, echoed instead of the Provider Model ID (FR-02); <c>provider/model</c> for a pinned Provider Model.</param>
/// <param name="Text">The completion text.</param>
/// <param name="FinishReason">Why the Provider stopped generating.</param>
/// <param name="Usage">As reported by the Provider; null when it reported none.</param>
/// <param name="Attempts">The number of Attempts made.</param>
/// <remarks>
/// The init-only members are for in-process callers (an Evaluation Run's Case Result); the HTTP response never shows them.
/// </remarks>
public sealed record InferenceResult(Guid RequestId, string Alias, string Text, FinishReason FinishReason, NeutralUsage? Usage, int Attempts)
{
    /// <summary>The Provider Model that produced the completion (<c>selected_provider_model_id</c>).</summary>
    public Guid? ProviderModelId { get; init; }

    /// <summary>Input tokens summed over the request's Attempts, as recorded on <c>inference_request.total_input_tokens</c>; null when none were known.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Output tokens summed over the request's Attempts (<c>total_output_tokens</c>); null when none were known.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>
    /// The request's Estimated Cost: the sum of its Attempts' Usage Records at the Price in force, the same amount settlement books
    /// against the Budget (<c>total_estimated_cost</c>), retries and Fallbacks included.
    /// </summary>
    public Money? EstimatedCost { get; init; }

    /// <summary>From the moment the request was received to the completion.</summary>
    public TimeSpan Latency { get; init; }

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("RequestId = ").Append(RequestId).Append(", Alias = ").Append(Alias).Append(", Text = <redacted>");
        return true;
    }
}

/// <summary>
/// The deep seam of the system (architecture.md section 4): everything between a validated request and a normalized
/// completion. The HTTP endpoint and, later, Evaluation both call it in-process.
/// </summary>
/// <remarks>
/// Routes the request's <see cref="InferenceRequest.Target"/> on the current configuration snapshot (held for the whole request),
/// reserves its worst case against the Tenant's Budget, then runs the Attempt loop (retries, backoff, Fallback, Deadline) over the
/// Routing Decision's Candidates, and settles. The Tenant's Rate and Concurrency Limits are not taken here: the HTTP use case takes
/// them (<see cref="ILimiter"/>), so Evaluation traffic, which calls this directly, is not limited by them (evaluation.md section 5).
/// A request whose Request Origin invariants do not hold (<see cref="InferenceRequest.OriginViolation"/>) throws
/// <see cref="ArgumentException"/>: that is a caller bug, not a client error.
/// </remarks>
public interface IInferenceService
{
    /// <summary>
    /// Fails with <c>model_not_found</c> (unknown alias, Policy Version or Provider Model), <c>model_not_allowed</c> (the Tenant may
    /// not use it), <c>no_eligible_model</c>, <c>request_cost_limit_exceeded</c>, <c>budget_exceeded</c>, <c>provider_unavailable</c>
    /// (with a retry-after when known), <c>provider_error</c>, <c>deadline_exceeded</c> or <c>context_length_exceeded</c>.
    /// </summary>
    Task<Result<InferenceResult>> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Streams the completion. Routing, retries and Fallback run until a Provider has produced the First Token (the first
    /// content chunk) and only then does this return, so a failure up to that point is an ordinary error (the same set as
    /// <see cref="CompleteAsync"/>, with <c>no_eligible_model</c> also for a model without the <c>streaming</c> Capability).
    /// After that the stream carries the failure itself (<see cref="StreamPart.Interrupted"/>); it is never replayed.
    /// </summary>
    Task<Result<StreamingCompletion>> StreamAsync(InferenceRequest request, CancellationToken cancellationToken);
}
