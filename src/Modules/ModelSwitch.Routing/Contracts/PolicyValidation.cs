#pragma warning disable CS1573 // Record parameters are documented only where they need explanation.

using ModelSwitch.SharedKernel.Errors;

namespace ModelSwitch.Routing.Contracts;

/// <summary>What the validator needs to know about the Registry to resolve names.</summary>
/// <param name="Models">Provider Model ids by <c>providerName/providerModelId</c>.</param>
/// <param name="ProviderNames">All Provider names, for <c>allowedProviders</c>.</param>
public sealed record PolicyReferenceData(IReadOnlyDictionary<string, Guid> Models, IReadOnlySet<string> ProviderNames);

public sealed record PolicyFieldError(string Path, string Message);

/// <param name="Definition">Set when there are no errors.</param>
/// <param name="ResolvedCandidates">Every referenced <c>providerName/providerModelId</c> with its Provider Model id, stored beside the definition.</param>
public sealed record PolicyValidationResult(
    IReadOnlyList<PolicyFieldError> Errors,
    PolicyDefinition? Definition,
    IReadOnlyDictionary<string, Guid> ResolvedCandidates)
{
    public bool IsValid => Errors.Count == 0;

    /// <summary>The <c>400 validation_failed</c> error, one entry per field path. Null when valid.</summary>
    public Error? ToError() => IsValid
        ? null
        : Error.Validation(
            "The routing policy definition is invalid.",
            Errors.GroupBy(e => e.Path, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal));
}

public interface IPolicyDefinitionValidator
{
    /// <summary>Structure (the embedded JSON Schema's rules) then semantic checks. Never throws on bad input.</summary>
    PolicyValidationResult Validate(string definitionJson, PolicyReferenceData references);
}
