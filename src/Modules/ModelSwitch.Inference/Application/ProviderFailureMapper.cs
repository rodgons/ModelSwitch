using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// Turns a classified Provider failure into the error recorded on the Attempt and, when the Attempt loop stops on it, the
/// error a client sees. A per-Attempt timeout is a Provider failure (<c>provider_unavailable</c>), never <c>deadline_exceeded</c>:
/// that code is reserved for the request Deadline actually being reached, which only the Attempt loop can tell. The messages are
/// fixed text: Provider messages are never copied (ADR-0010).
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
            return new Error(ErrorCode.ProviderUnavailable, "The Provider did not answer in time.");
        }

        return failure.Class == FailureClass.Transient
            ? new Error(ErrorCode.ProviderUnavailable, "The Provider is temporarily unavailable.")
            : new Error(ErrorCode.ProviderError, "The Provider could not complete the request.");
    }
}
