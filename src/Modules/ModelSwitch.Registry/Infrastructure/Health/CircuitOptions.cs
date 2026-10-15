using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ModelSwitch.Registry.Infrastructure.Health;

/// <summary>
/// Circuit breaker and health snapshot settings (<c>Circuit:*</c>, routing-and-resilience.md section 5). Invalid or missing
/// values fall back to the documented defaults.
/// </summary>
internal sealed record CircuitOptions
{
    public TimeSpan SamplingWindow { get; init; } = TimeSpan.FromSeconds(30);

    public int MinimumCalls { get; init; } = 10;

    public double FailureRatio { get; init; } = 0.5;

    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBreakDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Names this instance's rows in <c>registry.provider_health_snapshot</c>.</summary>
    public string InstanceId { get; init; } = Environment.MachineName;

    public static CircuitOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new CircuitOptions();
        var section = configuration.GetSection("Circuit");
        return new CircuitOptions
        {
            SamplingWindow = Seconds(section["SamplingWindowSeconds"], defaults.SamplingWindow),
            MinimumCalls = int.TryParse(section["MinimumCalls"], CultureInfo.InvariantCulture, out var calls) && calls >= 2 ? calls : defaults.MinimumCalls,
            FailureRatio = double.TryParse(section["FailureRatio"], CultureInfo.InvariantCulture, out var ratio) && ratio is > 0 and <= 1 ? ratio : defaults.FailureRatio,
            BreakDuration = Seconds(section["BreakSeconds"], defaults.BreakDuration),
            MaxBreakDuration = Seconds(section["MaxBreakSeconds"], defaults.MaxBreakDuration),
            SnapshotInterval = Seconds(section["SnapshotIntervalSeconds"], defaults.SnapshotInterval),
            InstanceId = string.IsNullOrWhiteSpace(section["InstanceId"]) ? defaults.InstanceId : section["InstanceId"]!,
        };
    }

    private static TimeSpan Seconds(string? value, TimeSpan fallback)
        => double.TryParse(value, CultureInfo.InvariantCulture, out var seconds) && seconds > 0.5 ? TimeSpan.FromSeconds(seconds) : fallback;
}
