namespace ModelSwitch.Registry.Domain;

internal enum AdapterType
{
    OpenAiCompatible,
    Ollama,
}

internal sealed class Provider
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    public AdapterType AdapterType { get; set; }

    public required string Endpoint { get; set; }

    /// <summary>A Credential Reference: <c>env:NAME</c> or <c>db:&lt;uuid&gt;</c>. Never the secret itself.</summary>
    public string? CredentialReference { get; set; }

    public int RequestTimeoutMs { get; set; }

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
