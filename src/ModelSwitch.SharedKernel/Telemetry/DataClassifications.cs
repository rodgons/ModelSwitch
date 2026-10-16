using Microsoft.Extensions.Compliance.Classification;

namespace ModelSwitch.SharedKernel.Telemetry;

/// <summary>
/// The data classes that logging redacts (security.md section 6). A <c>[LoggerMessage]</c> parameter marked <see cref="SecretDataAttribute"/>
/// or <see cref="ContentDataAttribute"/> is erased by the redactor the hosts register, whatever sink the log line goes to.
/// </summary>
public static class DataClassifications
{
    public const string TaxonomyName = "ModelSwitch";

    /// <summary>Credentials: API keys, Provider secrets, passwords, the master key.</summary>
    public static DataClassification Secret { get; } = new(TaxonomyName, nameof(Secret));

    /// <summary>Prompt or completion content (ADR-0010).</summary>
    public static DataClassification Content { get; } = new(TaxonomyName, nameof(Content));
}

/// <summary>Marks a value as a secret: erased from logs.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SecretDataAttribute() : DataClassificationAttribute(DataClassifications.Secret);

/// <summary>Marks a value as prompt or completion content: erased from logs.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ContentDataAttribute() : DataClassificationAttribute(DataClassifications.Content);
