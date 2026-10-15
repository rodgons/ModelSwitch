namespace ModelSwitch.Adapters.OpenAICompatible;

/// <summary>
/// Per-Provider switches for the OpenAI-compatible Adapter (docs/provider-adapters.md section 2). Defaults match OpenAI itself.
/// </summary>
public sealed record OpenAICompatibleOptions
{
    /// <summary>
    /// The header that carries the credential. <c>Authorization</c> sends <c>Bearer &lt;secret&gt;</c>; any other name
    /// (for example <c>api-key</c> on Azure-style endpoints) sends the bare secret.
    /// </summary>
    public string AuthHeaderName { get; init; } = "Authorization";

    /// <summary>Send <c>max_tokens</c> instead of <c>max_completion_tokens</c>, for endpoints that reject the newer field.</summary>
    public bool UseLegacyMaxTokens { get; init; }

    /// <summary>
    /// OpenRouter only: pin the request to these upstreams with <c>provider: { only: [...], allow_fallbacks: false }</c>, so an
    /// Attempt's Price is known and failures are not hidden by OpenRouter's own fallbacks. Null or empty sends no <c>provider</c> member.
    /// </summary>
    public IReadOnlyList<string>? PinnedUpstreams { get; init; }
}
