using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.FailureInjectionTests.Support;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Registry.Contracts;
using Shouldly;

namespace ModelSwitch.FailureInjectionTests.Scenarios;

/// <summary>testing.md section 3: "Provider 503 x2 then OK".</summary>
[Slow]
public sealed class Provider503ThenOkScenario(GatewayFixture gateway) : ScenarioTests(gateway), IClassFixture<GatewayFixture>
{
    [Fact]
    public async Task Two_503s_then_ok_is_one_retry_and_one_fallback_with_every_call_recorded()
    {
        await ResetAsync();
        var alias = await AddAliasAsync("s503", FastPolicy, GatewayFixture.PrimaryModel, GatewayFixture.FallbackModel);
        await Gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=503;times=2");

        var outcome = await SendAsync(alias);

        outcome.StatusCode.ShouldBe(200);
        (await AttemptsAsync(outcome.RequestId)).ShouldBe(
        [
            ["primary", "failed", "transient", "provider_unavailable", "503", "mock-echo"],
            ["retry", "failed", "transient", "provider_unavailable", "503", "mock-echo"],
            ["fallback", "succeeded", "", "", "", "mock-fallback"],
        ]);
        (await RequestAsync(outcome.RequestId)).ShouldBe(["succeeded", "", "3", "true"]);
        var calls = await ProviderCallsAsync();
        calls[GatewayFixture.PrimaryModel].ShouldBe(2);
        calls[GatewayFixture.FallbackModel].ShouldBe(1);
        await AssertConsistentAccountingAsync(outcome.RequestId);
        (await MeasureAmplificationAsync("503 x2 then ok", FastPolicyMaxAttempts, MaxAttemptsBound)).Ratio.ShouldBe(3.0);
    }
}

/// <summary>testing.md section 3: "Provider 429 with Retry-After: 1" and "Retry-After: 120".</summary>
[Slow]
public sealed class Provider429Scenario(GatewayFixture gateway) : ScenarioTests(gateway), IClassFixture<GatewayFixture>
{
    [Fact]
    public async Task A_short_Retry_After_is_waited_out_and_the_retry_succeeds_within_the_deadline()
    {
        await ResetAsync();
        var alias = await AddAliasAsync("s429short", FastPolicy, GatewayFixture.PrimaryModel, GatewayFixture.FallbackModel);
        await Gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=429;retry-after=1;times=1");

        var outcome = await SendAsync(alias);

        outcome.StatusCode.ShouldBe(200);
        outcome.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900), "the gateway honours Retry-After: 1");
        outcome.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "and still answers inside the 5 s Deadline");
        (await AttemptsAsync(outcome.RequestId)).ShouldBe(
        [
            ["primary", "failed", "transient", "provider_unavailable", "429", "mock-echo"],
            ["retry", "succeeded", "", "", "", "mock-echo"],
        ]);
        (await RequestAsync(outcome.RequestId)).ShouldBe(["succeeded", "", "2", "false"]);
        await AssertConsistentAccountingAsync(outcome.RequestId);
        (await MeasureAmplificationAsync("429 Retry-After 1", FastPolicyMaxAttempts, MaxAttemptsBound)).Ratio.ShouldBe(2.0);
    }

    [Fact]
    public async Task A_long_Retry_After_is_not_waited_out_and_the_request_falls_back_at_once()
    {
        // The first request through a cold host pays for JIT and connection setup; the bound below measures waiting, not warm-up.
        await SendAsync(GatewayFixture.ChatAlias);
        await ResetAsync();
        var alias = await AddAliasAsync("s429long", FastPolicy, GatewayFixture.PrimaryModel, GatewayFixture.FallbackModel);
        await Gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=429;retry-after=120");

        var outcome = await SendAsync(alias);

        outcome.StatusCode.ShouldBe(200);
        // Regression (flaky under full-suite CPU load at 900 ms): the bound only has to prove the 120 s
        // Retry-After was not honoured; the Attempt rows below prove there was no same-model retry.
        outcome.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "no wait");
        (await AttemptsAsync(outcome.RequestId)).ShouldBe(
        [
            ["primary", "failed", "transient", "provider_unavailable", "429", "mock-echo"],
            ["fallback", "succeeded", "", "", "", "mock-fallback"],
        ]);
        var calls = await ProviderCallsAsync();
        calls[GatewayFixture.PrimaryModel].ShouldBe(1, "no same-model retry");
        await AssertConsistentAccountingAsync(outcome.RequestId);
        (await MeasureAmplificationAsync("429 Retry-After 120", FastPolicyMaxAttempts, MaxAttemptsBound)).Ratio.ShouldBe(2.0);
    }
}

/// <summary>testing.md section 3: "Provider hangs (no headers)".</summary>
[Slow]
public sealed class ProviderHangScenario(GatewayFixture gateway) : ScenarioTests(gateway), IClassFixture<GatewayFixture>
{
    [Fact]
    public async Task A_hang_is_ambiguous_is_not_retried_on_the_same_model_and_falls_back()
    {
        await ResetAsync();
        var alias = await AddAliasAsync(
            "shang",
            """{"deadlineMs":30000,"attemptTimeoutMs":300,"maxAttempts":4,"maxRetriesPerCandidate":1,"backoff":{"baseMs":5,"maxMs":10,"jitter":"full"}}""",
            GatewayFixture.PrimaryModel,
            GatewayFixture.FallbackModel);
        await Gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "hang");

        var outcome = await SendAsync(alias);

        outcome.StatusCode.ShouldBe(200);
        // Flake fix: the upper bound was 3 s against a 300 ms attempt timeout, which full-suite CPU load could exceed. The Deadline is
        // now 30 s and the ceiling 20 s: the hang must end at the attempt timeout (lower bound) rather than ride to the Deadline, and
        // that no same-model retry happened is asserted exactly below (one call, two Attempts), not through the clock.
        outcome.Elapsed.ShouldBeInRange(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(20));
        (await AttemptsAsync(outcome.RequestId)).ShouldBe(
        [
            ["primary", "ambiguous", "ambiguous", "provider_unavailable", "", "mock-echo"],
            ["fallback", "succeeded", "", "", "", "mock-fallback"],
        ]);
        (await RequestAsync(outcome.RequestId)).ShouldBe(["succeeded", "", "2", "true"]);
        var calls = await ProviderCallsAsync();
        calls[GatewayFixture.PrimaryModel].ShouldBe(1, "an ambiguous Attempt may have been billed, so it is never retried on the same model");
        await AssertConsistentAccountingAsync(outcome.RequestId);

        // usage-and-budgets.md section 4: the ambiguous Attempt may have been billed, so it is charged its worst case. The request sets
        // no max_tokens and the policy no defaultMaxOutputTokens, so the output ceiling is 1024; the fixture prices input at 1 and
        // output at 2 per million tokens.
        var charged = (await RowsAsync(
            "select r.usage_source, coalesce(r.estimate_method,''), r.input_tokens::text, coalesce(r.output_tokens::text,''), r.total_cost::text, a.estimated_cost::text " +
            "from usage.usage_record r join usage.provider_attempt a on a.id = r.provider_attempt_id " +
            $"where r.inference_request_id = '{outcome.RequestId}' and a.status = 'ambiguous'")).Single();
        charged[..4].ShouldBe(["unavailable", "reservation_upper_bound", charged[2], ""]);
        var inputTokens = int.Parse(charged[2], CultureInfo.InvariantCulture);
        inputTokens.ShouldBeGreaterThan(0, "the input is the gateway's estimate");
        var worstCase = ((inputTokens * 1m) + (1024 * 2m)) / 1_000_000m;
        decimal.Parse(charged[4], CultureInfo.InvariantCulture).ShouldBe(worstCase);
        decimal.Parse(charged[5], CultureInfo.InvariantCulture).ShouldBe(worstCase, "the Attempt's estimated_cost is its Usage Record's cost");
        (await MeasureAmplificationAsync("provider hang", FastPolicyMaxAttempts, MaxAttemptsBound)).Ratio.ShouldBe(2.0);
    }
}

/// <summary>testing.md section 3: "Provider 401".</summary>
[Slow]
public sealed class Provider401Scenario(GatewayFixture gateway) : ScenarioTests(gateway), IClassFixture<GatewayFixture>
{
    [Fact]
    public async Task A_401_is_not_retried_falls_back_and_counts_toward_the_circuit()
    {
        await ResetAsync();
        var alias = await AddAliasAsync("s401", FastPolicy, GatewayFixture.PrimaryModel, GatewayFixture.FallbackModel);
        await Gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=401");

        var outcome = await SendAsync(alias);

        outcome.StatusCode.ShouldBe(200);
        (await AttemptsAsync(outcome.RequestId)).ShouldBe(
        [
            ["primary", "failed", "permanent", "provider_error", "401", "mock-echo"],
            ["fallback", "succeeded", "", "", "", "mock-fallback"],
        ]);
        (await ProviderCallsAsync())[GatewayFixture.PrimaryModel].ShouldBe(1);
        var echo = await ProviderModelIdAsync(GatewayFixture.PrimaryModel);
        var health = Gateway.Services.GetRequiredService<IProviderHealth>().Snapshot().Single(h => h.ProviderModelId == echo);
        health.WindowCalls.ShouldBe(1);
        health.WindowFailureRate.ShouldBe(1.0, "an operator problem (401) counts toward the circuit");
        await AssertConsistentAccountingAsync(outcome.RequestId);
        (await MeasureAmplificationAsync("provider 401", FastPolicyMaxAttempts, MaxAttemptsBound)).Ratio.ShouldBe(2.0);
    }
}

/// <summary>testing.md section 3: "Invalid JSON from the Provider".</summary>
[Slow]
public sealed class InvalidJsonScenario(GatewayFixture gateway) : ScenarioTests(gateway), IClassFixture<GatewayFixture>
{
    [Fact]
    public async Task Invalid_json_is_transient_and_retried_on_the_same_model()
    {
        await ResetAsync();
        var alias = await AddAliasAsync("sjson", FastPolicy, GatewayFixture.PrimaryModel, GatewayFixture.FallbackModel);
        await Gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "garbage;times=1");

        var outcome = await SendAsync(alias);

        outcome.StatusCode.ShouldBe(200);
        (await AttemptsAsync(outcome.RequestId)).ShouldBe(
        [
            ["primary", "failed", "transient", "provider_unavailable", "200", "mock-echo"],
            ["retry", "succeeded", "", "", "", "mock-echo"],
        ]);
        (await RequestAsync(outcome.RequestId)).ShouldBe(["succeeded", "", "2", "false"]);
        await AssertConsistentAccountingAsync(outcome.RequestId);
        (await MeasureAmplificationAsync("invalid json", FastPolicyMaxAttempts, MaxAttemptsBound)).Ratio.ShouldBe(2.0);
    }
}
