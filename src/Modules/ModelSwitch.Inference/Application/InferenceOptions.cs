using Microsoft.Extensions.Configuration;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// Deployment settings for the inference API. <c>Limits:MaxRequestBodyBytes</c> and <c>Limits:MaxMessages</c> are the
/// documented limits (api.md section 1). <c>Streaming:KeepAliveSeconds</c> is the SSE keep-alive interval.
/// </summary>
internal sealed class InferenceOptions
{
    public const long DefaultMaxRequestBodyBytes = 1024 * 1024;
    public const int DefaultMaxMessages = 256;
    public const int DefaultKeepAliveSeconds = 15;

    public long MaxRequestBodyBytes { get; init; } = DefaultMaxRequestBodyBytes;

    public int MaxMessages { get; init; } = DefaultMaxMessages;

    /// <summary>How long a stream may stay quiet before a <c>: keep-alive</c> comment goes out (api.md section 1).</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(DefaultKeepAliveSeconds);

    public static InferenceOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new InferenceOptions
        {
            MaxRequestBodyBytes = long.TryParse(configuration["Limits:MaxRequestBodyBytes"], out var bytes) && bytes > 0 ? bytes : DefaultMaxRequestBodyBytes,
            MaxMessages = int.TryParse(configuration["Limits:MaxMessages"], out var messages) && messages > 0 ? messages : DefaultMaxMessages,
            KeepAliveInterval = TimeSpan.FromSeconds(int.TryParse(configuration["Streaming:KeepAliveSeconds"], out var keepAlive) && keepAlive > 0 ? keepAlive : DefaultKeepAliveSeconds),
        };
    }
}
