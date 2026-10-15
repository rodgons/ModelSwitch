using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace ModelSwitch.FailureInjectionTests.Support;

/// <summary>
/// Retry amplification of one scenario: <c>provider_calls / client_requests</c> (testing.md section 3). Provider calls are counted
/// by the Mock Provider (<c>GET /__stats</c>), not by the gateway, so the number cannot flatter the gateway's own bookkeeping.
/// </summary>
public sealed record RetryAmplification(string Scenario, int ClientRequests, int ProviderCalls)
{
    public double Ratio => ClientRequests == 0 ? 0 : (double)ProviderCalls / ClientRequests;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"retry amplification [{Scenario}]: {ProviderCalls} provider calls / {ClientRequests} client requests = {Ratio:0.00}");
}

/// <summary>
/// Collects the amplification of every scenario that ran in this process so the Phase 6 benchmark report can render them
/// (roadmap Phase 6, "retry-amplification numbers"). Today it is only written to the test output.
/// </summary>
public static class AmplificationReport
{
    private static readonly ConcurrentQueue<RetryAmplification> _rows = new();

    public static IReadOnlyCollection<RetryAmplification> Rows => [.. _rows];

    public static void Record(RetryAmplification row)
    {
        _rows.Enqueue(row);
        TestContext.Current.TestOutputHelper?.WriteLine(row.ToString());
    }

    /// <summary>A Markdown table for the benchmark report template.</summary>
    public static string ToMarkdown()
    {
        var table = new StringBuilder("| Scenario | Client requests | Provider calls | Amplification |\n|---|---|---|---|\n");
        foreach (var row in _rows.OrderBy(r => r.Scenario, StringComparer.Ordinal))
        {
            table.Append(CultureInfo.InvariantCulture, $"| {row.Scenario} | {row.ClientRequests} | {row.ProviderCalls} | {row.Ratio:0.00} |\n");
        }

        return table.ToString();
    }
}
