using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// Phase 1 stub of <see cref="IInferenceService"/>: resolves the alias from <see cref="InferenceOptions.StaticAliases"/>,
/// binds one Adapter and makes exactly one Attempt. Temporary until Phase 2 replaces the alias lookup with Routing and
/// adds the Attempt loop, limits and usage recording. It persists nothing, and never logs content (ADR-0010).
/// </summary>
internal sealed class InferenceService(InferenceOptions options, IProviderCatalog catalog, IAdapterFactory adapters) : IInferenceService
{
    public async Task<Result<InferenceResult>> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var binding = options.StaticAliases.FirstOrDefault(a => string.Equals(a.Alias, request.Alias, StringComparison.Ordinal));
        if (binding is null)
        {
            return new Error(ErrorCode.ModelNotFound, "The requested model does not exist.");
        }

        var target = await catalog.FindEnabledAsync(binding.ProviderName, binding.ProviderModelIdentifier, cancellationToken);
        if (target is null)
        {
            return new Error(ErrorCode.NoEligibleModel, "No eligible model can serve this request.");
        }

        var bound = await adapters.BindAsync(target, cancellationToken);
        if (bound.IsFailure)
        {
            return bound.Error;
        }

        var outcome = await bound.Value.Adapter.CompleteAsync(bound.Value.Context, request.Chat, cancellationToken);
        return outcome switch
        {
            CompletionOutcome.Success success => new InferenceResult(request.RequestId, request.Alias, success.Text, success.FinishReason, success.Usage, 1),
            CompletionOutcome.Failure failure => ProviderFailureMapper.ToError(failure.Error),
            _ => new Error(ErrorCode.InternalError, "An internal error occurred."),
        };
    }
}
