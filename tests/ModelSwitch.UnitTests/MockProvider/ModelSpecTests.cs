using ModelSwitch.MockProvider;
using Shouldly;

namespace ModelSwitch.UnitTests.MockProvider;

public sealed class ModelSpecTests
{
    [Fact]
    public void Mock_echo_is_an_echo_model()
    {
        ModelSpec.TryParse("mock-echo", out var spec, out _).ShouldBeTrue();

        spec!.Echo.ShouldBeTrue();
        spec.Latency.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Mock_fixed_reads_latency_and_tokens_from_the_query()
    {
        ModelSpec.TryParse("mock-fixed?latency=300&tokens=50", out var spec, out _).ShouldBeTrue();

        spec!.Name.ShouldBe("mock-fixed");
        spec.Echo.ShouldBeFalse();
        spec.Latency.ShouldBe(TimeSpan.FromMilliseconds(300));
        spec.Tokens.ShouldBe(50);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mock-fixed?tokens=abc")]
    [InlineData("mock-fixed?color=red")]
    [InlineData("mock-fixed?tokens")]
    public void Invalid_models_are_rejected(string model)
    {
        ModelSpec.TryParse(model, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Usage_override_parses_cached_input_and_output()
    {
        UsageOverride.TryParse("cached=900;input=1000,output=7", out var usage, out _).ShouldBeTrue();

        usage.ShouldBe(new UsageOverride(1000, 7, 900));
    }

    [Fact]
    public void Usage_override_rejects_unknown_directives()
    {
        UsageOverride.TryParse("tokens=1", out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNullOrWhiteSpace();
    }
}
