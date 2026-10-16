namespace ModelSwitch.Admin.Application;

/// <summary>
/// How the dashboard sums up per-instance Provider Health (routing-and-resilience.md section 5, ADR-0005). Circuits are per instance,
/// so the aggregate is the worst state any instance reports, counting only instances that wrote recently.
/// </summary>
internal static class ProviderHealthRules
{
    /// <summary>Four snapshot intervals (15 s): an instance silent for longer is shown as stale and left out of the aggregate.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);

    /// <summary>The window of recent Attempts the error rate and latency are computed over.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(15);

    public const string Unknown = "unknown";

    public static bool IsStale(DateTimeOffset capturedAt, DateTimeOffset now) => now - capturedAt > Freshness;

    /// <summary><c>open</c> if any fresh instance is open, else <c>half_open</c> if any is, else <c>closed</c>; <c>unknown</c> with no fresh instance.</summary>
    public static string Aggregate(IEnumerable<(string State, DateTimeOffset CapturedAt)> instances, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var fresh = instances.Where(i => !IsStale(i.CapturedAt, now)).Select(i => i.State).ToList();
        if (fresh.Count == 0)
        {
            return Unknown;
        }

        return fresh.Contains("open") ? "open" : fresh.Contains("half_open") ? "half_open" : "closed";
    }

    /// <summary>Failures over Attempts; null when there were none, so "no traffic" is never shown as 0 % errors.</summary>
    public static double? Rate(long part, long whole) => whole == 0 ? null : (double)part / whole;
}
