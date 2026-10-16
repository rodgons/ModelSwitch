namespace ModelSwitch.Registry.Domain;

/// <summary>An AES-256-GCM encrypted Provider secret (ADR-0007). A replacement is a new row; the old one is deleted.</summary>
internal sealed class ProviderSecret
{
    public Guid Id { get; init; }

    public Guid ProviderId { get; init; }

    public required byte[] Ciphertext { get; set; }

    public required byte[] Nonce { get; set; }

    public required byte[] Tag { get; set; }

    public short KeyVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
