namespace ModelSwitch.IntegrationTests.Infrastructure;

/// <summary>
/// Marks the test that proves a route works end to end, e.g. <c>[HappyPath("POST /v1/chat/completions")]</c>.
/// <c>EndpointHappyPathGateTests</c> fails for any route in the app that has no such test (testing.md section 6).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class HappyPathAttribute(string route) : Attribute
{
    public string Route { get; } = route;
}
