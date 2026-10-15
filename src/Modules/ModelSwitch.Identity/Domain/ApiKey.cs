namespace ModelSwitch.Identity.Domain;

internal sealed class ApiKey
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string Name { get; set; }

    /// <summary>The Key Prefix: the 8 characters after <c>msk_</c>. Unique, non-secret.</summary>
    public required string KeyPrefix { get; init; }

    /// <summary>HMAC-SHA256 of the full key under the pepper (ADR-0006). 32 bytes.</summary>
    public required byte[] KeyHash { get; init; }

    public required string[] Scopes { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? CreatedByAdminId { get; init; }
}
