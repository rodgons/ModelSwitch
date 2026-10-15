using Microsoft.Extensions.Configuration;

namespace ModelSwitch.Inference.Application;

/// <summary>A static binding of a Model Alias to one Provider Model (<c>providerName/providerModelIdentifier</c>).</summary>
internal sealed record StaticAliasBinding(string Alias, string ProviderName, string ProviderModelIdentifier);

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
            // "ollama/llama3.2:1b": the first slash separates the Provider name from the Provider's model identifier.
            var value = entry.Value ?? string.Empty;
            var slash = value.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && slash < value.Length - 1)
            {
                aliases.Add(new StaticAliasBinding(entry.Key, value[..slash], value[(slash + 1)..]));
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
