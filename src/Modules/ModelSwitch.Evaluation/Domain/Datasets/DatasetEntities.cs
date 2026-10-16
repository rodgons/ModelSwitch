namespace ModelSwitch.Evaluation.Domain.Datasets;

/// <summary>A named collection of Cases of one Tenant, maintained over time as immutable Dataset Versions.</summary>
internal sealed class Dataset
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string Name { get; init; }

    /// <summary><c>extraction</c>, <c>classification</c> or <c>free_text</c> (<see cref="TaskKinds"/>).</summary>
    public required string TaskKind { get; init; }

    /// <summary>The newest Dataset Version, or 0 before the first upload. Moved only by an atomic increment.</summary>
    public int LatestVersion { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>An immutable snapshot of a Dataset's Cases. Rows are only ever inserted.</summary>
internal sealed class DatasetVersion
{
    public Guid DatasetId { get; init; }

    public int Version { get; init; }

    public int CaseCount { get; init; }

    /// <summary>Lower-case hex SHA-256 of the canonical Cases (<see cref="DatasetJsonl"/>).</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>The expected-output schema: the label set for classification, the field names for extraction.</summary>
    public required string ScoringSchemaJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? CreatedByAdminId { get; init; }
}

/// <summary>One input with its expected result inside a Dataset Version (table <c>eval_case</c>). Rows are only ever inserted.</summary>
internal sealed class Case
{
    public Guid Id { get; init; }

    public Guid DatasetId { get; init; }

    public int DatasetVersion { get; init; }

    /// <summary>The JSONL <c>id</c>, unique within the version, which matches Case Results across versions.</summary>
    public required string ExternalId { get; init; }

    public required string InputJson { get; init; }

    public required string ExpectedJson { get; init; }

    public string? MetadataJson { get; init; }

    /// <summary>The Case's position in the upload, from 0.</summary>
    public int Ordinal { get; init; }
}
