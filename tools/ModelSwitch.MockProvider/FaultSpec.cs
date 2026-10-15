using System.Globalization;

namespace ModelSwitch.MockProvider;

/// <summary>
/// A scripted fault, parsed from <c>X-Mock-Fault</c> or the <c>POST /__scenarios</c> body (docs/provider-adapters.md section 5).
/// Directives are separated by <c>;</c>: <c>status=503;times=2</c>, <c>hang</c>, <c>cut-after=5</c>, <c>no-usage</c>, <c>garbage</c>,
/// <c>latency=300</c>, <c>chunk-delay=200</c>, <c>retry-after=2</c>, <c>error=insufficient_quota</c>, <c>inline-error=rate_limit_exceeded</c>.
/// </summary>
internal sealed record FaultSpec
{
    public int? Status { get; init; }

    /// <summary>The fault applies to the first N matching requests only; later requests succeed. Null means always.</summary>
    public int? Times { get; init; }

    public bool Hang { get; init; }

    public int? CutAfter { get; init; }

    /// <summary>A pause before every streamed chunk, so a stream lasts long enough for a client to abort it mid-way.</summary>
    public TimeSpan? ChunkDelay { get; init; }

    public bool NoUsage { get; init; }

    public bool Garbage { get; init; }

    public TimeSpan? Latency { get; init; }

    /// <summary>Sent verbatim as the <c>Retry-After</c> header: delay-seconds or an HTTP-date.</summary>
    public string? RetryAfter { get; init; }

    /// <summary>The Provider error code placed in the error body of a <see cref="Status"/> fault.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>When set, the response is a 200 that carries an error object with this code.</summary>
    public string? InlineErrorCode { get; init; }

    public static bool TryParse(string? text, out FaultSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The fault script is empty.";
            return false;
        }

        var result = new FaultSpec();
        foreach (var directive in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = directive.IndexOf('=', StringComparison.Ordinal);
            var key = (separator < 0 ? directive : directive[..separator]).ToLowerInvariant();
            var value = separator < 0 ? null : directive[(separator + 1)..];

            switch (key)
            {
                case "status" when TryInt(value, 100, 599, out var status):
                    result = result with { Status = status };
                    break;
                case "times" when TryInt(value, 1, int.MaxValue, out var times):
                    result = result with { Times = times };
                    break;
                case "cut-after" when TryInt(value, 0, int.MaxValue, out var cut):
                    result = result with { CutAfter = cut };
                    break;
                case "chunk-delay" when TryInt(value, 0, 600_000, out var chunkDelay):
                    result = result with { ChunkDelay = TimeSpan.FromMilliseconds(chunkDelay) };
                    break;
                case "latency" when TryInt(value, 0, 600_000, out var latency):
                    result = result with { Latency = TimeSpan.FromMilliseconds(latency) };
                    break;
                case "hang" when value is null:
                    result = result with { Hang = true };
                    break;
                case "no-usage" when value is null:
                    result = result with { NoUsage = true };
                    break;
                case "garbage" when value is null:
                    result = result with { Garbage = true };
                    break;
                case "retry-after" when !string.IsNullOrEmpty(value):
                    result = result with { RetryAfter = value };
                    break;
                case "error" when !string.IsNullOrEmpty(value):
                    result = result with { ErrorCode = value };
                    break;
                case "inline-error" when !string.IsNullOrEmpty(value):
                    result = result with { InlineErrorCode = value };
                    break;
                default:
                    error = $"Unknown or invalid fault directive '{directive}'.";
                    return false;
            }
        }

        spec = result;
        return true;
    }

    private static bool TryInt(string? value, int min, int max, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= min && parsed <= max;
}
