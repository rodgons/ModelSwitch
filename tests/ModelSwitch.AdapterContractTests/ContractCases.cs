using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.AdapterContractTests;

/// <summary>Theory data for the contract suite. Each row of the failure-classification table in docs/routing-and-resilience.md is here.</summary>
public static class ContractCases
{
    /// <summary>HTTP status, expected class and expected code. Statuses outside the table's listed ones cover the "unlisted" rows.</summary>
    public static TheoryData<int, FailureClass, string> HttpStatuses() => new()
    {
        { 400, FailureClass.Permanent, ProviderFailureCodes.BadRequest },
        { 401, FailureClass.Permanent, ProviderFailureCodes.Unauthorized },
        { 402, FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted },
        { 403, FailureClass.Permanent, ProviderFailureCodes.Forbidden },
        { 404, FailureClass.Permanent, ProviderFailureCodes.NotFound },
        { 408, FailureClass.Transient, ProviderFailureCodes.RequestTimeout },
        { 409, FailureClass.Transient, ProviderFailureCodes.Conflict },
        { 413, FailureClass.Permanent, ProviderFailureCodes.PayloadTooLarge },
        { 422, FailureClass.Permanent, ProviderFailureCodes.Unprocessable },
        { 425, FailureClass.Transient, ProviderFailureCodes.TooEarly },
        { 429, FailureClass.Transient, ProviderFailureCodes.RateLimited },
        { 500, FailureClass.Transient, ProviderFailureCodes.ServerError },
        { 502, FailureClass.Transient, ProviderFailureCodes.ServerError },
        { 503, FailureClass.Transient, ProviderFailureCodes.ServerError },
        { 504, FailureClass.Transient, ProviderFailureCodes.ServerError },
        { 529, FailureClass.Transient, ProviderFailureCodes.ServerError },
        { 524, FailureClass.Ambiguous, ProviderFailureCodes.EdgeTimeout },

        // Unlisted: any other 5xx is transient, any other 4xx is permanent.
        { 507, FailureClass.Transient, ProviderFailureCodes.ServerError },
        { 424, FailureClass.Permanent, ProviderFailureCodes.ClientError },
        { 498, FailureClass.Permanent, ProviderFailureCodes.ClientError },
    };

    /// <summary>Provider error codes that turn a 429 into a permanent billing failure.</summary>
    public static TheoryData<string> BillingErrorCodes() => ["insufficient_quota", "credit_balance_exhausted", "organization_spend_limit_exceeded"];

    /// <summary>Reported cached tokens (input is always 1000) and the expected <see cref="NeutralUsage.CachedInputTokens"/>.</summary>
    public static TheoryData<int?, int?> CachedTokens() => new()
    {
        { null, null },
        { 0, 0 },
        { 900, 900 },
        { 1000, 1000 },
        { 1500, null },
    };

    public static TheoryData<bool> Streaming() => [false, true];

    public static TheoryData<bool, FinishReason> FinishReasons()
    {
        var data = new TheoryData<bool, FinishReason>();
        foreach (var streaming in new[] { false, true })
        {
            foreach (var reason in Enum.GetValues<FinishReason>())
            {
                data.Add(streaming, reason);
            }
        }

        return data;
    }

    /// <summary>Streaming flag, inner error kind, expected class.</summary>
    public static TheoryData<bool, InnerErrorKind, FailureClass> InnerErrors()
    {
        var data = new TheoryData<bool, InnerErrorKind, FailureClass>();
        foreach (var streaming in new[] { false, true })
        {
            data.Add(streaming, InnerErrorKind.RateLimit, FailureClass.Transient);
            data.Add(streaming, InnerErrorKind.Quota, FailureClass.Permanent);
            data.Add(streaming, InnerErrorKind.Unspecified, FailureClass.Transient);
        }

        return data;
    }
}
