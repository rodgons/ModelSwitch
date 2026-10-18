namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// Marks a test that makes the gateway send a documented response header and asserts it, e.g.
/// <c>[AssertsHeader("X-ModelSwitch-Attempts")]</c>. <c>ResponseHeaderGateTests</c> fails for any header in a "Response headers"
/// table of docs/api.md that no test is tagged with (testing.md section 6).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AssertsHeaderAttribute(string header) : Attribute
{
    public string Header { get; } = header;
}
