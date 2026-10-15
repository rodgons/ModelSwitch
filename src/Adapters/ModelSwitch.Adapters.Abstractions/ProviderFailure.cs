namespace ModelSwitch.Adapters.Abstractions;

/// <summary>Classification of a Provider failure (docs/routing-and-resilience.md section 3).</summary>
public enum FailureClass
{
    /// <summary>Safe to retry on the same Provider Model and to fall back from.</summary>
    Transient,

    /// <summary>Retrying the same call cannot help.</summary>
    Permanent,

    /// <summary>The request may have succeeded and been billed. Never retried on the same Provider Model.</summary>
    Ambiguous,
}

/// <summary>
/// A classified, sanitized Provider failure. <see cref="Code"/> is one of <see cref="ProviderFailureCodes"/>; no Provider free text is kept.
/// <see cref="RequestSent"/> is false only when the failure happened before any request bytes left the process.
/// </summary>
public sealed record ProviderFailure(
    FailureClass Class,
    string Code,
    int? HttpStatus,
    TimeSpan? RetryAfter,
    bool RequestSent);

/// <summary>The sanitized failure codes an Adapter may report. Provider messages are mapped onto these, never copied.</summary>
public static class ProviderFailureCodes
{
    public const string ConnectFailed = "connect_failed";
    public const string Timeout = "timeout";
    public const string ConnectionReset = "connection_reset";
    public const string EdgeTimeout = "edge_timeout";
    public const string BadRequest = "bad_request";
    public const string ContextLengthExceeded = "context_length_exceeded";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string PayloadTooLarge = "payload_too_large";
    public const string Unprocessable = "unprocessable";
    public const string ClientError = "client_error";
    public const string RequestTimeout = "request_timeout";
    public const string Conflict = "conflict";
    public const string TooEarly = "too_early";
    public const string RateLimited = "rate_limited";
    public const string QuotaExhausted = "quota_exhausted";
    public const string ServerError = "server_error";
    public const string InvalidProviderResponse = "invalid_provider_response";
}
