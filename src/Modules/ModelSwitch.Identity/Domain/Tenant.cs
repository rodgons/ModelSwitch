namespace ModelSwitch.Identity.Domain;

internal sealed class Tenant
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Active;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
