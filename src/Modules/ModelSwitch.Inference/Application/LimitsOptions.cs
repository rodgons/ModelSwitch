using Microsoft.Extensions.Configuration;

namespace ModelSwitch.Inference.Application;

/// <summary>What happens to a request when Valkey cannot answer (ADR-0003).</summary>
internal enum LimitsFailureMode
{
    /// <summary>Refuse with <c>503 limits_unavailable</c>. The default: a limit that silently stops working is a larger risk than an outage.</summary>
    Closed,

    /// <summary>Let the request through and count it in <c>modelswitch.limits.degraded</c>.</summary>
    Open,
}

/// <summary>
/// <c>Limits:FailureMode</c> (<c>Open</c> or <c>Closed</c>, default <c>Closed</c>) and <c>Limits:ValkeyTimeoutMs</c> (default 50, the
/// budget of one limits call, routing-and-resilience.md section 7).
/// </summary>
internal sealed class LimitsOptions
{
    public const int DefaultValkeyTimeoutMs = 50;

    /// <summary>The slack added to a request's Deadline to size its lease, so a request that ends at the Deadline still holds it.</summary>
    public static readonly TimeSpan LeaseGrace = TimeSpan.FromSeconds(5);

    public LimitsFailureMode FailureMode { get; init; } = LimitsFailureMode.Closed;

    public TimeSpan ValkeyTimeout { get; init; } = TimeSpan.FromMilliseconds(DefaultValkeyTimeoutMs);

    public static LimitsOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new LimitsOptions
        {
            FailureMode = Enum.TryParse<LimitsFailureMode>(configuration["Limits:FailureMode"], ignoreCase: true, out var mode) ? mode : LimitsFailureMode.Closed,
            ValkeyTimeout = TimeSpan.FromMilliseconds(int.TryParse(configuration["Limits:ValkeyTimeoutMs"], out var ms) && ms > 0 ? ms : DefaultValkeyTimeoutMs),
        };
    }
}
