using ModelSwitch.Identity.Contracts;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.Routing.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Routing.Application;

/// <summary>
/// <c>POST /admin/routing-policies/{policyId}/simulate</c>: the Routing Decision a request would get from a Policy Version, decided by the
/// real router on this instance's configuration snapshot (catalog, Prices, Tenant limits) and its circuits. It calls no Provider and writes
/// nothing, so every Administrator may run it; a Tenant outside the caller's grant answers 404.
/// </summary>
internal sealed class RoutingSimulationService(
    EfPolicyStore policies,
    IRouter router,
    IRoutingSnapshotSource snapshots,
    IProviderHealth health,
    ITenantGrantChecker grants)
{
    public async Task<Result<RoutingSimulationView>> SimulateAsync(Guid policyId, SimulateRoutingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = RoutingSimulation.Validate(request);
        if (input.IsFailure)
        {
            return input.Error;
        }

        if (input.Value.TenantId is { } tenantId)
        {
            var access = grants.CheckAccess(tenantId);
            if (access.IsFailure)
            {
                return access.Error;
            }
        }

        var policy = await policies.GetVersionOrLatestAsync(policyId, input.Value.Version, cancellationToken);
        if (policy is null)
        {
            return new Error(ErrorCode.NotFound, input.Value.Version is null ? "The routing policy was not found." : "The policy version was not found.");
        }

        return RoutingSimulation.Run(
            router,
            policy,
            input.Value,
            snapshots.Current,
            input.Value.TenantId is { } tenant ? snapshots.AccessFor(tenant) : null,
            health.OpenCircuits(),
            health.StateOf);
    }
}
