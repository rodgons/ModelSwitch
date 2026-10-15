namespace ModelSwitch.Usage.Contracts;

/// <summary>An estimated input size and how it was obtained (the <c>estimate_method</c> recorded with usage).</summary>
/// <param name="Tokens">Estimated input tokens, including the per-message overhead.</param>
/// <param name="Method"><c>tokenizer:o200k_base</c>, <c>tokenizer:cl100k_base</c> or <c>bytes_div_3</c>.</param>
public sealed record InputTokenEstimate(int Tokens, string Method);

/// <summary>
/// Estimates the input tokens of a chat request before the Provider has answered (routing-and-resilience.md section 1,
/// "Worst-case cost"). An estimate, not an exact count. Message text is content (ADR-0010): it is counted, never stored or logged.
/// </summary>
public interface IInputTokenEstimator
{
    /// <summary>Tokens added per message for role and framing, on top of the text itself.</summary>
    const int PerMessageOverhead = 4;

    /// <summary>
    /// Counts <paramref name="messageTexts"/> with the tokenizer named by the Provider Model's
    /// <c>capabilities.tokenizer</c> (<c>o200k_base</c> or <c>cl100k_base</c>), plus <see cref="PerMessageOverhead"/> per
    /// message. A null or unknown tokenizer falls back to <c>ceil(utf8Bytes / 3)</c>, which deliberately overestimates.
    /// </summary>
    InputTokenEstimate Estimate(IReadOnlyList<string> messageTexts, string? tokenizer);
}
