using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.Registry.Infrastructure.Health;

/// <summary>Which Provider failures count against a circuit (routing-and-resilience.md section 5).</summary>
internal static class CircuitFailureRules
{
    /// <summary>
    /// Transient and ambiguous failures count, and so do the permanent failures that mean the Provider is misconfigured
    /// (401, 403, billing/quota): an operator problem should not fail every request. Request errors (400, 404, 413, 422, other
    /// 4xx) do not, or one Tenant's bad requests would trip the breaker for everyone.
    /// </summary>
    public static bool Counts(ProviderFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Class switch
        {
            FailureClass.Transient or FailureClass.Ambiguous => true,
            _ => failure.Code is ProviderFailureCodes.Unauthorized or ProviderFailureCodes.Forbidden or ProviderFailureCodes.QuotaExhausted,
        };
    }
}
