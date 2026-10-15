namespace ModelSwitch.SharedKernel.Errors;

/// <summary>
/// The closed set of error codes from docs/api.md section 2. Instances cannot be created outside this type.
/// </summary>
public sealed class ErrorCode
{
    public static readonly ErrorCode InvalidRequest = new("invalid_request", 400, "invalid_request_error");
    public static readonly ErrorCode UnsupportedParameter = new("unsupported_parameter", 400, "invalid_request_error");
    public static readonly ErrorCode ContextLengthExceeded = new("context_length_exceeded", 400, "invalid_request_error");
    public static readonly ErrorCode InvalidApiKey = new("invalid_api_key", 401, "authentication_error");
    public static readonly ErrorCode BudgetExceeded = new("budget_exceeded", 402, "budget_error");
    public static readonly ErrorCode RequestCostLimitExceeded = new("request_cost_limit_exceeded", 402, "budget_error");
    public static readonly ErrorCode InsufficientScope = new("insufficient_scope", 403, "permission_error");
    public static readonly ErrorCode TenantSuspended = new("tenant_suspended", 403, "permission_error");
    public static readonly ErrorCode ModelNotAllowed = new("model_not_allowed", 403, "permission_error");
    public static readonly ErrorCode ModelNotFound = new("model_not_found", 404, "invalid_request_error");
    public static readonly ErrorCode PayloadTooLarge = new("payload_too_large", 413, "invalid_request_error");
    public static readonly ErrorCode UnsupportedMediaType = new("unsupported_media_type", 415, "invalid_request_error");
    public static readonly ErrorCode NoEligibleModel = new("no_eligible_model", 422, "routing_error");
    public static readonly ErrorCode RateLimitExceeded = new("rate_limit_exceeded", 429, "rate_limit_error");
    public static readonly ErrorCode ConcurrencyLimitExceeded = new("concurrency_limit_exceeded", 429, "rate_limit_error");
    public static readonly ErrorCode ProviderError = new("provider_error", 502, "provider_error");
    public static readonly ErrorCode ProviderUnavailable = new("provider_unavailable", 503, "provider_error");
    public static readonly ErrorCode LimitsUnavailable = new("limits_unavailable", 503, "api_error");
    public static readonly ErrorCode DeadlineExceeded = new("deadline_exceeded", 504, "timeout_error");
    public static readonly ErrorCode StreamInterrupted = new("stream_interrupted", null, "provider_error");
    public static readonly ErrorCode RunCostCapReached = new("run_cost_cap_reached", null, "budget_error");
    public static readonly ErrorCode InternalError = new("internal_error", 500, "api_error");
    public static readonly ErrorCode ValidationFailed = new("validation_failed", 400, "invalid_request_error");
    public static readonly ErrorCode Forbidden = new("forbidden", 403, "permission_error");
    public static readonly ErrorCode NotFound = new("not_found", 404, "invalid_request_error");
    public static readonly ErrorCode Conflict = new("conflict", 409, "invalid_request_error");

    public static readonly IReadOnlyList<ErrorCode> All =
    [
        InvalidRequest,
        UnsupportedParameter,
        ContextLengthExceeded,
        InvalidApiKey,
        BudgetExceeded,
        RequestCostLimitExceeded,
        InsufficientScope,
        TenantSuspended,
        ModelNotAllowed,
        ModelNotFound,
        PayloadTooLarge,
        UnsupportedMediaType,
        NoEligibleModel,
        RateLimitExceeded,
        ConcurrencyLimitExceeded,
        ProviderError,
        ProviderUnavailable,
        LimitsUnavailable,
        DeadlineExceeded,
        StreamInterrupted,
        RunCostCapReached,
        InternalError,
        ValidationFailed,
        Forbidden,
        NotFound,
        Conflict,
    ];

    private static readonly Dictionary<string, ErrorCode> _byCode = All.ToDictionary(c => c.Value, StringComparer.Ordinal);

    private ErrorCode(string value, int? httpStatus, string type)
    {
        Value = value;
        HttpStatus = httpStatus;
        Type = type;
    }

    /// <summary>The snake_case wire value (`error.code`).</summary>
    public string Value { get; }

    /// <summary>HTTP status, or null for codes that are never an HTTP response (SSE event, Evaluation Run error).</summary>
    public int? HttpStatus { get; }

    /// <summary>The OpenAI-compatible `error.type` grouping.</summary>
    public string Type { get; }

    public static bool TryParse(string? value, out ErrorCode code)
    {
        if (value is not null && _byCode.TryGetValue(value, out var found))
        {
            code = found;
            return true;
        }

        code = InternalError;
        return false;
    }

    public override string ToString() => Value;
}
