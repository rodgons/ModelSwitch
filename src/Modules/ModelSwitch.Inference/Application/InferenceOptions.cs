using Microsoft.Extensions.Configuration;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// A static binding of a Model Alias to a Provider Model (<c>providerName/providerModelIdentifier</c>), optionally followed
/// by Fallbacks that are tried in order (<c>a/m1,b/m2</c>).
/// </summary>
internal sealed record StaticAliasBinding(string Alias, string ProviderName, string ProviderModelIdentifier)
{
    public IReadOnlyList<(string ProviderName, string ProviderModelIdentifier)> Fallbacks { get; init; } = [];

    /// <summary>The primary target followed by the Fallbacks, in order.</summary>
    public IReadOnlyList<(string ProviderName, string ProviderModelIdentifier)> Targets => [(ProviderName, ProviderModelIdentifier), .. Fallbacks];
}

/// <summary>
/// Deployment settings for the inference API. <c>Limits:MaxRequestBodyBytes</c> and <c>Limits:MaxMessages</c> are the
/// documented limits (api.md section 1). <c>Inference:StaticAliases:&lt;alias&gt;</c> is a Phase 1 stand-in for
/// Routing Policies: temporary, removed when Phase 2 routing lands.
/// </summary>
internal sealed class InferenceOptions
{
    public const long DefaultMaxRequestBodyBytes = 1024 * 1024;
    public const int DefaultMaxMessages = 256;

    public long MaxRequestBodyBytes { get; init; } = DefaultMaxRequestBodyBytes;

    public int MaxMessages { get; init; } = DefaultMaxMessages;

    public IReadOnlyList<StaticAliasBinding> StaticAliases { get; init; } = [];

    public static InferenceOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var aliases = new List<StaticAliasBinding>();
        foreach (var entry in configuration.GetSection("Inference:StaticAliases").GetChildren())
        {
            // "ollama/llama3.2:1b,openai/gpt-4.1-mini": comma-separated Candidates in order; in each, the first slash
            // separates the Provider name from the Provider's model identifier. A malformed Candidate drops the alias.
            var targets = new List<(string, string)>();
            foreach (var part in (entry.Value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var slash = part.IndexOf('/', StringComparison.Ordinal);
                if (slash > 0 && slash < part.Length - 1)
                {
                    targets.Add((part[..slash], part[(slash + 1)..]));
                }
                else
                {
                    targets.Clear();
                    break;
                }
            }

            if (targets.Count > 0)
            {
                aliases.Add(new StaticAliasBinding(entry.Key, targets[0].Item1, targets[0].Item2) { Fallbacks = targets[1..] });
            }
        }

        return new InferenceOptions
        {
            MaxRequestBodyBytes = long.TryParse(configuration["Limits:MaxRequestBodyBytes"], out var bytes) && bytes > 0 ? bytes : DefaultMaxRequestBodyBytes,
            MaxMessages = int.TryParse(configuration["Limits:MaxMessages"], out var messages) && messages > 0 ? messages : DefaultMaxMessages,
            StaticAliases = aliases,
        };
    }
}
