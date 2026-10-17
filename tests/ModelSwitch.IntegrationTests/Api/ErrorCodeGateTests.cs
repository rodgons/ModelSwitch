using System.Reflection;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>The error-code gate (testing.md section 6): every code in the closed set has an integration test that triggers it.</summary>
public sealed class ErrorCodeGateTests
{
    [Fact]
    public void Every_error_code_is_triggered_by_a_tagged_test()
    {
        var tagged = typeof(TriggersErrorAttribute).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(method => method.GetCustomAttributes<TriggersErrorAttribute>())
            .Select(attribute => attribute.Code)
            .ToHashSet(StringComparer.Ordinal);
        var codes = ErrorCode.All.Select(code => code.Value).ToHashSet(StringComparer.Ordinal);

        codes.Except(tagged).ShouldBeEmpty("error codes without a [TriggersError] integration test");
        tagged.Except(codes).ShouldBeEmpty("[TriggersError] tags that name no error code");
    }
}
