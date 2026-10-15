namespace ModelSwitch.Routing.Domain;

internal sealed class RoutingPolicy
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    public int LatestVersion { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>An immutable Policy Version. Rows are only ever inserted.</summary>
internal sealed class PolicyVersion
{
    public Guid PolicyId { get; init; }

    public int Version { get; init; }

    /// <summary>The definition as authored (candidates by <c>providerName/providerModelId</c>).</summary>
    public required string DefinitionJson { get; init; }

    /// <summary>Authored candidate name to Provider Model id, fixed when the version was saved.</summary>
    public required string ResolvedCandidatesJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? CreatedByAdminId { get; init; }
}

/// <summary>A client-facing Model Alias. It always resolves to its policy's latest version.</summary>
internal sealed class ModelAlias
{
    public required string Alias { get; init; }

    public Guid PolicyId { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
