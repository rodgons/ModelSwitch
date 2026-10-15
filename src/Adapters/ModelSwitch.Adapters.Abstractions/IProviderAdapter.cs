namespace ModelSwitch.Adapters.Abstractions;

/// <summary>
/// Translates the gateway's neutral contract to one Provider's wire protocol (docs/provider-adapters.md section 1).
/// An Adapter contains no business rules: no routing, retries, cost or limits (ADR-0004).
/// </summary>
/// <remarks>
/// Rules every implementation follows: it never throws for Provider or network failures (it returns
/// <see cref="CompletionOutcome.Failure"/> or <see cref="StreamEvent.Failed"/>); it throws only for programming errors or
/// <see cref="OperationCanceledException"/> from the caller's token; it honours the token immediately, including mid-stream;
/// it sets <see cref="ProviderFailure.RequestSent"/> correctly; it never logs request or response bodies or credential headers;
/// and it streams without buffering.
/// </remarks>
public interface IProviderAdapter
{
    /// <summary>The Adapter type name stored on a Provider: <c>openai_compatible</c> or <c>ollama</c>.</summary>
    string AdapterType { get; }

    Task<CompletionOutcome> CompleteAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct);

    /// <summary>
    /// Streams the completion. The sequence always ends with exactly one <see cref="StreamEvent.Finish"/> or
    /// <see cref="StreamEvent.Failed"/>. <see cref="StreamEvent.Usage"/> may come before or after <see cref="StreamEvent.Finish"/>.
    /// Disposing the enumerator aborts the HTTP response.
    /// </summary>
    IAsyncEnumerable<StreamEvent> StreamAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct);

    Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct);

    /// <summary>Optional helper for the registration UI.</summary>
    Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct);
}
