namespace ModelSwitch.Identity.Domain;

/// <summary>An immutable entry describing a sensitive administrative action (CONTEXT.md). Append-only in the database.</summary>
internal sealed class AuditRecord
{
    public Guid Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Null for the system (Owner bootstrap) and for a failed sign-in as an unknown account.</summary>
    public Guid? AdministratorId { get; init; }

    public required string Action { get; init; }

    public Guid? TenantId { get; init; }

    public required string TargetType { get; init; }

    public string? TargetId { get; init; }

    /// <summary>JSON: <c>before</c>, <c>after</c> and extra keys. Never secrets or content.</summary>
    public required string Details { get; init; }

    public System.Net.IPAddress? SourceIp { get; init; }

    public Guid? RequestId { get; init; }

    public string? TraceId { get; init; }
}
