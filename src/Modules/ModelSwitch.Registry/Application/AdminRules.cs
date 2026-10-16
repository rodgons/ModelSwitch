using System.Text.Json;
using System.Text.RegularExpressions;
using ModelSwitch.Registry.Domain;

namespace ModelSwitch.Registry.Application;

/// <summary>
/// The pure validation rules of the Provider, Provider Model and Price admin writes. Each method adds to the
/// field-keyed error map that becomes the <c>errors</c> member of the <c>400 validation_failed</c> response.
/// </summary>
internal static partial class AdminRules
{
    public const int MaxIdentifierLength = 200;
    public const int MaxDisplayNameLength = 200;
    public const int MinTimeoutMs = 1_000;
    public const int MaxTimeoutMs = 600_000;
    public const int DefaultTimeoutMs = 30_000;

    /// <summary>numeric(20,10): ten integer digits and ten decimal places.</summary>
    public const decimal MaxPrice = 9_999_999_999m;
    public const int MaxPriceScale = 10;

    public static readonly string[] Tokenizers = ["o200k_base", "cl100k_base"];

    private static readonly string[] _capabilityKeys = ["streaming", "jsonObject", "jsonSchema", "contextWindow", "maxOutputTokens", "parameters", "tokenizer"];

    public static string? ProviderName(string? name, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !NamePattern().IsMatch(trimmed))
        {
            // Policy Candidates are written providerName/providerModelId, so a name never contains '/'.
            errors["name"] = ["The name must be 1 to 63 characters: lower-case letters, digits, '.', '_' or '-', starting with a letter or digit."];
            return null;
        }

        return trimmed;
    }

    public static bool TryParseAdapterType(string? wire, out AdapterType type)
    {
        switch (wire)
        {
            case "openai_compatible":
                type = AdapterType.OpenAiCompatible;
                return true;
            case "ollama":
                type = AdapterType.Ollama;
                return true;
            default:
                type = default;
                return false;
        }
    }

    public static string ToWire(AdapterType type) => type == AdapterType.Ollama ? "ollama" : "openai_compatible";

    public static int? RequestTimeout(int? timeoutMs, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (timeoutMs is null)
        {
            return null;
        }

        if (timeoutMs is < MinTimeoutMs or > MaxTimeoutMs)
        {
            errors["requestTimeoutMs"] = [$"The request timeout must be between {MinTimeoutMs} and {MaxTimeoutMs} ms."];
            return null;
        }

        return timeoutMs;
    }

    /// <summary>Exactly one of <c>secret</c> or <c>envVar</c>. Returns the <c>env:NAME</c> reference, or null for a secret or an error.</summary>
    public static string? Credential(CredentialRequest? credential, Dictionary<string, string[]> errors, out bool isSecret)
    {
        ArgumentNullException.ThrowIfNull(errors);
        isSecret = false;
        if (credential is null)
        {
            return null;
        }

        var hasSecret = credential.Secret is not null;
        var hasEnvVar = credential.EnvVar is not null;
        if (hasSecret == hasEnvVar)
        {
            errors["credential"] = ["Give exactly one of 'secret' or 'envVar'."];
            return null;
        }

        if (hasSecret)
        {
            isSecret = true;
            return null;
        }

        var name = credential.EnvVar!.Trim();
        if (!EnvVarPattern().IsMatch(name) || name.StartsWith("MODELSWITCH__", StringComparison.OrdinalIgnoreCase))
        {
            errors["credential.envVar"] = ["The environment variable name must match [A-Za-z_][A-Za-z0-9_]* and must not start with MODELSWITCH__."];
            return null;
        }

        return "env:" + name;
    }

    public static string? ProviderModelIdentifier(string? identifier, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var trimmed = identifier?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxIdentifierLength || trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            errors["providerModelId"] = [$"The Provider's model id must be 1 to {MaxIdentifierLength} characters with no whitespace."];
            return null;
        }

        return trimmed;
    }

    public static string? DisplayName(string? displayName, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxDisplayNameLength)
        {
            errors["displayName"] = [$"The display name must be 1 to {MaxDisplayNameLength} characters."];
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Checks the Capabilities document (data-model.md, <c>provider_model.capabilities</c>) and returns it as JSON text, or null on
    /// error. Unknown keys are rejected so a typo cannot silently make a model ineligible.
    /// </summary>
    public static string? Capabilities(JsonElement? capabilities, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (capabilities is not { } root || root.ValueKind != JsonValueKind.Object)
        {
            errors["capabilities"] = ["Capabilities must be a JSON object."];
            return null;
        }

        var before = errors.Count;
        foreach (var property in root.EnumerateObject())
        {
            CheckCapability(property, errors);
        }

        if (errors.Count == before
            && root.TryGetProperty("contextWindow", out var window) && root.TryGetProperty("maxOutputTokens", out var output)
            && output.GetInt32() > window.GetInt32())
        {
            errors["capabilities.maxOutputTokens"] = ["It cannot exceed contextWindow."];
        }

        return errors.Count == before ? root.GetRawText() : null;
    }

    /// <summary>A Price in USD per million tokens: non-negative, within numeric(20,10).</summary>
    public static decimal? PricePerMillion(decimal? value, string field, bool required, Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (value is null)
        {
            if (required)
            {
                errors[field] = ["It is required."];
            }

            return null;
        }

        if (value < 0 || value > MaxPrice || value != decimal.Round(value.Value, MaxPriceScale))
        {
            errors[field] = [$"It must be between 0 and {MaxPrice} with at most {MaxPriceScale} decimal places (USD per million tokens)."];
            return null;
        }

        return value;
    }

    private static void CheckCapability(JsonProperty property, Dictionary<string, string[]> errors)
    {
        var path = "capabilities." + property.Name;
        var value = property.Value;
        switch (property.Name)
        {
            case "streaming" or "jsonObject" or "jsonSchema":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors[path] = ["It must be true or false."];
                }

                break;
            case "contextWindow" or "maxOutputTokens":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var tokens) || tokens < 1)
                {
                    errors[path] = ["It must be a positive integer."];
                }

                break;
            case "parameters":
                if (value.ValueKind != JsonValueKind.Array
                    || value.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(i.GetString())))
                {
                    errors[path] = ["It must be an array of parameter names."];
                }

                break;
            case "tokenizer":
                if (value.ValueKind != JsonValueKind.String || !Tokenizers.Contains(value.GetString(), StringComparer.Ordinal))
                {
                    errors[path] = [$"The tokenizer must be one of: {string.Join(", ", Tokenizers)}."];
                }

                break;
            default:
                errors[path] = [$"Unknown capability. Known: {string.Join(", ", _capabilityKeys)}."];
                break;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex EnvVarPattern();
}
