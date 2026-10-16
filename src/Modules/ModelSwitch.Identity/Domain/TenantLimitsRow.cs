namespace ModelSwitch.Identity.Domain;

/// <summary>One Tenant's limits (<c>identity.tenant_limits</c>, 1:1 with the Tenant). A null limit means unlimited.</summary>
internal sealed class TenantLimitsRow
{
    public Guid TenantId { get; init; }

    public int? RequestsPerMinute { get; set; }

    public int? MaxConcurrent { get; set; }

    public decimal? MaxRequestCost { get; set; }

    public decimal? BudgetAmount { get; set; }

    public string? BudgetPeriod { get; set; }

    public string[] AllowedAliases { get; set; } = ["*"];

    public DateTimeOffset UpdatedAt { get; set; }
}
