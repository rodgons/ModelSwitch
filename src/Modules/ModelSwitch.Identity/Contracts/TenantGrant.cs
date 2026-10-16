namespace ModelSwitch.Identity.Contracts;

/// <summary>
/// The Tenants an Administrator may see and act on: all of them, or an explicit list (CONTEXT.md, Tenant Grant).
/// An Owner always has the all-Tenants grant.
/// </summary>
public sealed class TenantGrant
{
    private readonly HashSet<Guid> _tenantIds;

    private TenantGrant(bool isAll, HashSet<Guid> tenantIds)
    {
        IsAll = isAll;
        _tenantIds = tenantIds;
    }

    public static TenantGrant AllTenants { get; } = new(isAll: true, []);

    public static TenantGrant None { get; } = new(isAll: false, []);

    public bool IsAll { get; }

    /// <summary>The explicitly granted Tenants. Empty for the all-Tenants grant, which names none.</summary>
    public IReadOnlyCollection<Guid> TenantIds => _tenantIds;

    public static TenantGrant Only(IEnumerable<Guid> tenantIds)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        return new TenantGrant(isAll: false, [.. tenantIds]);
    }

    public bool Allows(Guid tenantId) => IsAll || _tenantIds.Contains(tenantId);
}
