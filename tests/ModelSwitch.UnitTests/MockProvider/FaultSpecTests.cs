using ModelSwitch.MockProvider;
using Shouldly;

namespace ModelSwitch.UnitTests.MockProvider;

public sealed class FaultSpecTests
{
    [Fact]
    public void Status_with_times_parses_both_directives()
    {
        FaultSpec.TryParse("status=503;times=2", out var spec, out var error).ShouldBeTrue(error);

        spec!.Status.ShouldBe(503);
        spec.Times.ShouldBe(2);
    }

    [Fact]
    public void Flag_directives_parse_without_values()
    {
        FaultSpec.TryParse("hang;no-usage;garbage", out var spec, out _).ShouldBeTrue();

        spec!.Hang.ShouldBeTrue();
        spec.NoUsage.ShouldBeTrue();
        spec.Garbage.ShouldBeTrue();
    }

    [Fact]
    public void Cut_after_latency_retry_after_and_error_codes_parse()
    {
        FaultSpec.TryParse("cut-after=5; latency=300; retry-after=2; status=429; error=insufficient_quota; inline-error=rate_limit_exceeded", out var spec, out _)
            .ShouldBeTrue();

        spec!.CutAfter.ShouldBe(5);
        spec.Latency.ShouldBe(TimeSpan.FromMilliseconds(300));
        spec.RetryAfter.ShouldBe("2");
        spec.ErrorCode.ShouldBe("insufficient_quota");
        spec.InlineErrorCode.ShouldBe("rate_limit_exceeded");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("status")]
    [InlineData("status=abc")]
    [InlineData("status=99")]
    [InlineData("status=600")]
    [InlineData("times=0")]
    [InlineData("hang=1")]
    [InlineData("explode")]
    public void Invalid_scripts_are_rejected_with_a_message(string text)
    {
        FaultSpec.TryParse(text, out var spec, out var error).ShouldBeFalse();

        spec.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Times_limits_a_fault_to_the_first_n_requests_of_a_scenario()
    {
        var store = new ScenarioStore();
        FaultSpec.TryParse("status=503;times=2", out var spec, out _).ShouldBeTrue();

        var applied = Enumerable.Range(0, 4).Select(_ => store.ShouldApply("scenario-1", "status=503;times=2", spec!)).ToList();

        applied.ShouldBe([true, true, false, false]);
    }

    [Fact]
    public void Times_counters_are_independent_per_scenario()
    {
        var store = new ScenarioStore();
        FaultSpec.TryParse("status=503;times=1", out var spec, out _).ShouldBeTrue();

        store.ShouldApply("a", "status=503;times=1", spec!).ShouldBeTrue();
        store.ShouldApply("b", "status=503;times=1", spec!).ShouldBeTrue();
        store.ShouldApply("a", "status=503;times=1", spec!).ShouldBeFalse();
    }

    [Fact]
    public void A_fault_without_times_always_applies()
    {
        var store = new ScenarioStore();
        FaultSpec.TryParse("status=500", out var spec, out _).ShouldBeTrue();

        Enumerable.Range(0, 5).ShouldAllBe(_ => store.ShouldApply("a", "status=500", spec!));
    }
}
