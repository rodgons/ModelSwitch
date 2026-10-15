using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Contracts;

/// <summary>One non-streaming chat completion asked of the gateway by a Tenant for a Model Alias.</summary>
/// <param name="RequestId">The Inference Request ID (UUIDv7), also returned in <c>X-Request-Id</c>.</param>
/// <param name="TenantId">From the authenticated API Key, never from the request body.</param>
/// <param name="Alias">The Model Alias the client asked for.</param>
/// <param name="Chat">The validated, provider-neutral request.</param>
public sealed record InferenceRequest(Guid RequestId, Guid TenantId, string Alias, NeutralChatRequest Chat)
{
    /// <summary>The API Key that made the request. Required for <see cref="RequestOrigin.Api"/> traffic.</summary>
    public Guid? ApiKeyId { get; init; }

    /// <summary>Api for HTTP traffic; Evaluation for requests an Evaluation Run generates (then <see cref="EvaluationRunId"/> is set).</summary>
    public RequestOrigin Origin { get; init; } = RequestOrigin.Api;

    public Guid? EvaluationRunId { get; init; }

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
/// <param name="Alias">The Model Alias, echoed instead of the Provider Model ID (FR-02).</param>
/// <param name="Text">The completion text.</param>
/// <param name="FinishReason">Why the Provider stopped generating.</param>
/// <param name="Usage">As reported by the Provider; null when it reported none.</param>
/// <param name="Attempts">The number of Attempts made.</param>
public sealed record InferenceResult(Guid RequestId, string Alias, string Text, FinishReason FinishReason, NeutralUsage? Usage, int Attempts)
{
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
/// Routes the request on the current configuration snapshot (held for the whole request), then runs the Attempt loop
/// (retries, backoff, Fallback, Deadline) over the Routing Decision's Candidates. Limits arrive behind this same interface.
/// </remarks>
public interface IInferenceService
{
    /// <summary>
    /// Fails with <c>model_not_found</c> (unknown alias), <c>model_not_allowed</c> (the Tenant may not use it),
    /// <c>no_eligible_model</c>, <c>provider_unavailable</c> (with a retry-after when known), <c>provider_error</c>,
    /// <c>deadline_exceeded</c> or <c>context_length_exceeded</c>.
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
