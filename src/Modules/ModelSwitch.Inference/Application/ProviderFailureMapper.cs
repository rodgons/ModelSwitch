using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// Turns a classified Provider failure into the error a client sees. Phase 1 stub for a single Attempt: with no retries
/// or Fallback, every failure surfaces directly. The messages are fixed text: Provider messages are never copied (ADR-0010).
/// </summary>
internal static class ProviderFailureMapper
{
    public static Error ToError(ProviderFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure.Code == ProviderFailureCodes.ContextLengthExceeded)
        {
            return new Error(ErrorCode.ContextLengthExceeded, "The request exceeds the model's context length.");
        }

        if (failure.Code is ProviderFailureCodes.Timeout or ProviderFailureCodes.EdgeTimeout)
        {
            return new Error(ErrorCode.DeadlineExceeded, "The Provider did not answer in time.");
        }

        return failure.Class == FailureClass.Transient
            ? new Error(ErrorCode.ProviderUnavailable, "The Provider is temporarily unavailable.")
            : new Error(ErrorCode.ProviderError, "The Provider could not complete the request.");
    }
}
