using Xunit.v3;

namespace ModelSwitch.FailureInjectionTests.Support;

/// <summary>Tags a test class <c>Category=slow</c> (testing.md section 1), so a filter on that trait can skip or select it.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SlowAttribute : Attribute, ITraitAttribute
{
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new("Category", "slow")];
}
