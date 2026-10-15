using Microsoft.Extensions.Configuration;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// Deployment settings for the inference API. <c>Limits:MaxRequestBodyBytes</c> and <c>Limits:MaxMessages</c> are the
/// documented limits (api.md section 1).
/// </summary>
internal sealed class InferenceOptions
{
    public const long DefaultMaxRequestBodyBytes = 1024 * 1024;
    public const int DefaultMaxMessages = 256;

    public long MaxRequestBodyBytes { get; init; } = DefaultMaxRequestBodyBytes;

    public int MaxMessages { get; init; } = DefaultMaxMessages;

    public static InferenceOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new InferenceOptions
        {
            MaxRequestBodyBytes = long.TryParse(configuration["Limits:MaxRequestBodyBytes"], out var bytes) && bytes > 0 ? bytes : DefaultMaxRequestBodyBytes,
            MaxMessages = int.TryParse(configuration["Limits:MaxMessages"], out var messages) && messages > 0 ? messages : DefaultMaxMessages,
        };
    }
}
