using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelSwitch.FailureInjectionTests.Support;
using Shouldly;

[assembly: AssemblyFixture(typeof(AmplificationReportWriter))]

namespace ModelSwitch.FailureInjectionTests.Support;

/// <summary>
/// Retry amplification of one scenario: <c>provider_calls / client_requests</c> (testing.md section 3). Provider calls are counted
/// by the Mock Provider (<c>GET /__stats</c>), not by the gateway, so the number cannot flatter the gateway's own bookkeeping.
/// <paramref name="Bound"/> is the ceiling the scenario asserts the ratio against, and <paramref name="BoundReason"/> says where it comes from.
/// </summary>
public sealed record RetryAmplification(string Scenario, int ClientRequests, int ProviderCalls, double Bound, string BoundReason)
{
    public double Ratio => ClientRequests == 0 ? 0 : (double)ProviderCalls / ClientRequests;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"retry amplification [{Scenario}]: {ProviderCalls} provider calls / {ClientRequests} client requests = {Ratio:0.00} (bound {Bound:0.00}: {BoundReason})");
}

/// <summary>
/// Collects the amplification of every scenario that ran in this process. <see cref="AmplificationReportWriter"/> writes it out when
/// the run ends, so the benchmark report quotes measured numbers (roadmap Phase 6, docs/benchmarks/retry-amplification.md).
/// </summary>
public static class AmplificationReport
{
    private static readonly ConcurrentQueue<RetryAmplification> _rows = new();
    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    public static IReadOnlyCollection<RetryAmplification> Rows => [.. _rows];

    /// <summary>Records one scenario's amplification and asserts it stays within <paramref name="bound"/>.</summary>
    public static RetryAmplification Measure(string scenario, int clientRequests, int providerCalls, double bound, string boundReason)
    {
        var row = new RetryAmplification(scenario, clientRequests, providerCalls, bound, boundReason);
        _rows.Enqueue(row);
        TestContext.Current.TestOutputHelper?.WriteLine(row.ToString());
        row.Ratio.ShouldBeLessThanOrEqualTo(bound, $"retry amplification of '{scenario}' is bounded by {boundReason}");
        return row;
    }

    private static IEnumerable<RetryAmplification> Ordered => _rows.OrderBy(r => r.Scenario, StringComparer.Ordinal);

    /// <summary>A Markdown table for the benchmark report.</summary>
    public static string ToMarkdown()
    {
        var table = new StringBuilder("| Scenario | Client requests | Provider calls | Amplification | Asserted bound |\n|---|---|---|---|---|\n");
        foreach (var row in Ordered)
        {
            table.Append(CultureInfo.InvariantCulture, $"| {row.Scenario} | {row.ClientRequests} | {row.ProviderCalls} | {row.Ratio:0.00} | ≤ {row.Bound:0.##} ({row.BoundReason}) |\n");
        }

        return table.ToString();
    }

    /// <summary>The same rows as JSON, for tooling.</summary>
    public static string ToJson()
        => JsonSerializer.Serialize(
            Ordered.Select(r => new { scenario = r.Scenario, clientRequests = r.ClientRequests, providerCalls = r.ProviderCalls, ratio = Math.Round(r.Ratio, 4), bound = r.Bound, boundReason = r.BoundReason }),
            _indented);
}

/// <summary>
/// Writes <see cref="AmplificationReport"/> to <c>retry-amplification.json</c> and <c>retry-amplification.md</c> when the test assembly
/// finishes. The directory is <c>MODELSWITCH_AMPLIFICATION_REPORT_DIR</c> if set, otherwise <c>TestResults</c> under the test output
/// directory. Nothing is written when no scenario measured anything (a filtered run).
/// </summary>
public sealed class AmplificationReportWriter : IAsyncDisposable
{
    public const string DirectoryVariable = "MODELSWITCH_AMPLIFICATION_REPORT_DIR";

    public static string Directory
        => Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "TestResults");

    public async ValueTask DisposeAsync()
    {
        if (AmplificationReport.Rows.Count == 0)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        await File.WriteAllTextAsync(Path.Combine(Directory, "retry-amplification.json"), AmplificationReport.ToJson());
        await File.WriteAllTextAsync(Path.Combine(Directory, "retry-amplification.md"), AmplificationReport.ToMarkdown());
    }
}
