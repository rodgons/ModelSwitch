namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// Marks a test that makes the gateway produce an error code from the closed set (docs/api.md section 2) and asserts it, e.g.
/// <c>[TriggersError("budget_exceeded")]</c>. <c>ErrorCodeGateTests</c> fails for any code no test is tagged with (testing.md section 6).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class TriggersErrorAttribute(string code) : Attribute
{
    public string Code { get; } = code;
}
