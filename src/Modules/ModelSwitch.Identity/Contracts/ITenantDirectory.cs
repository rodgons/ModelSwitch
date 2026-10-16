namespace ModelSwitch.Identity.Contracts;

/// <summary>
/// Whether a Tenant exists, for modules that keep Tenant data in their own schema (Evaluation) and must refuse to create it for an
/// unknown Tenant. It does not check the Tenant Grant; callers do that first with <see cref="ITenantGrantChecker"/>.
/// </summary>
public interface ITenantDirectory
{
    Task<bool> ExistsAsync(Guid tenantId, CancellationToken cancellationToken);
}
