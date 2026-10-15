using System.Net.Http.Headers;
using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.Adapters.OpenAICompatible.Wire;

/// <summary>
/// Maps HTTP statuses and Provider error codes onto <see cref="ProviderFailure"/> (docs/routing-and-resilience.md section 3).
/// Provider error text is never read, only the machine-readable <c>code</c> and <c>type</c>.
/// </summary>
internal static class FailureClassifier
{
    /// <summary>Classifies a non-2xx response. <paramref name="providerCode"/> and <paramref name="providerType"/> come from its error body, when it had one.</summary>
    public static ProviderFailure FromStatus(int status, string? providerCode, string? providerType, bool contextLength, TimeSpan? retryAfter)
    {
        var (failureClass, code) = Classify(status, providerCode, providerType, contextLength);
        return new ProviderFailure(failureClass, code, status, retryAfter, RequestSent: true);
    }

    /// <summary>Classifies an error object carried inside a 200 response or a stream frame.</summary>
    public static ProviderFailure FromInnerError(ChatPayloadParser error)
    {
        var (failureClass, code) = Classify(error.ErrorStatus ?? 0, error.ErrorCode, error.ErrorType, error.ErrorMentionsContextLength);
        return new ProviderFailure(failureClass, code, HttpStatus: 200, RetryAfter: null, RequestSent: true);
    }

    public static ProviderFailure InvalidResponse(int? status = 200) =>
        new(FailureClass.Transient, ProviderFailureCodes.InvalidProviderResponse, status, null, RequestSent: true);

    public static ProviderFailure ConnectionReset() =>
        new(FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset, null, null, RequestSent: true);

    /// <summary>Maps an exception from the HTTP stack to a failure, or returns false for exceptions that must propagate (cancellation by the caller, bugs).</summary>
    public static bool TryMap(Exception exception, CancellationToken callerToken, out ProviderFailure failure)
    {
        failure = null!;
        switch (exception)
        {
            case OperationCanceledException when callerToken.IsCancellationRequested:
                return false;
            case OperationCanceledException:
                // The Attempt timeout fired: the request was on the wire and may have been processed.
                failure = new ProviderFailure(FailureClass.Ambiguous, ProviderFailureCodes.Timeout, null, null, RequestSent: true);
                return true;
            case HttpRequestException
            {
                HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError
            }:
                failure = new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ConnectFailed, null, null, RequestSent: false);
                return true;
            case HttpRequestException or IOException:
                failure = ConnectionReset();
                return true;
            default:
                return false;
        }
    }

    public static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, TimeProvider time)
    {
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static (FailureClass Class, string Code) Classify(int status, string? providerCode, string? providerType, bool contextLength)
    {
        if (IsBillingCode(providerCode) || IsBillingCode(providerType))
        {
            return (FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted);
        }

        return status switch
        {
            402 => (FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted),
            429 => (FailureClass.Transient, ProviderFailureCodes.RateLimited),
            400 when providerCode == "context_length_exceeded" || contextLength => (FailureClass.Permanent, ProviderFailureCodes.ContextLengthExceeded),
            400 => (FailureClass.Permanent, ProviderFailureCodes.BadRequest),
            401 => (FailureClass.Permanent, ProviderFailureCodes.Unauthorized),
            403 => (FailureClass.Permanent, ProviderFailureCodes.Forbidden),
            404 => (FailureClass.Permanent, ProviderFailureCodes.NotFound),
            408 => (FailureClass.Transient, ProviderFailureCodes.RequestTimeout),
            409 => (FailureClass.Transient, ProviderFailureCodes.Conflict),
            413 => (FailureClass.Permanent, ProviderFailureCodes.PayloadTooLarge),
            422 => (FailureClass.Permanent, ProviderFailureCodes.Unprocessable),
            425 => (FailureClass.Transient, ProviderFailureCodes.TooEarly),
            524 => (FailureClass.Ambiguous, ProviderFailureCodes.EdgeTimeout),
            >= 500 => (FailureClass.Transient, ProviderFailureCodes.ServerError),
            >= 400 => (FailureClass.Permanent, ProviderFailureCodes.ClientError),
            _ when providerCode == "rate_limit_exceeded" || providerType == "rate_limit_error" => (FailureClass.Transient, ProviderFailureCodes.RateLimited),
            _ => (FailureClass.Transient, ProviderFailureCodes.ServerError),
        };
    }

    private static bool IsBillingCode(string? code) =>
        code is "insufficient_quota" or "credit_balance_exhausted" or "organization_spend_limit_exceeded";
}
