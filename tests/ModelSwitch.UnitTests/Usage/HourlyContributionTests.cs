using ModelSwitch.Usage.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Usage;

/// <summary>usage-and-budgets.md section 6: what one completed request adds to its usage_hourly row.</summary>
public sealed class HourlyContributionTests
{
    private static readonly Guid _tenant = Guid.CreateVersion7();
    private static readonly Guid _model = Guid.CreateVersion7();

    private static RequestCompletedEvent Completed(
        string status = "succeeded",
        Guid? model = null,
        bool hadFallback = false,
        int? durationMs = 420,
        int? ttftMs = null,
        int? cached = null,
        DateTimeOffset? startedAt = null)
        => new(
            Guid.CreateVersion7(),
            _tenant,
            "balanced",
            status,
            "reported",
            model,
            model,
            AttemptCount: 2,
            hadFallback,
            InputTokens: 100,
            cached,
            OutputTokens: 20,
            EstimatedCost: 0.0012m,
            "USD",
            startedAt ?? new DateTimeOffset(2026, 10, 15, 13, 59, 59, 999, TimeSpan.FromHours(2)),
            durationMs,
            ttftMs);

    [Fact]
    public void A_request_is_counted_in_the_utc_hour_it_started_in()
        => HourlyContribution.From(Completed()).Hour.ShouldBe(new DateTimeOffset(2026, 10, 15, 11, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Totals_are_copied_and_unknown_cached_tokens_count_as_zero()
    {
        var row = HourlyContribution.From(Completed(model: _model));

        row.ShouldSatisfyAllConditions(
            () => row.TenantId.ShouldBe(_tenant),
            () => row.ProviderModelId.ShouldBe(_model),
            () => row.Requests.ShouldBe(1),
            () => row.Attempts.ShouldBe(2),
            () => row.InputTokens.ShouldBe(100),
            () => row.CachedInputTokens.ShouldBe(0),
            () => row.OutputTokens.ShouldBe(20),
            () => row.EstimatedCost.ShouldBe(0.0012m),
            () => row.DurationMsSum.ShouldBe(420),
            () => row.LatencyHistogram[LatencyHistogram.BucketOf(420)].ShouldBe(1),
            () => row.TtftHistogram.Sum().ShouldBe(0));
    }

    [Theory]
    [InlineData("succeeded", 0)]
    [InlineData("failed", 1)]
    [InlineData("abandoned", 1)]
    [InlineData("cancelled", 0)]
    public void Failed_and_abandoned_requests_count_as_failed(string status, int failed)
        => HourlyContribution.From(Completed(status)).FailedRequests.ShouldBe(failed);

    [Fact]
    public void A_request_that_reached_no_provider_is_attributed_to_the_empty_provider_model()
        => HourlyContribution.From(Completed(model: null)).ProviderModelId.ShouldBe(HourlyContribution.NoProviderModel);

    [Fact]
    public void An_abandoned_request_without_a_duration_adds_nothing_to_the_latency_histogram()
    {
        var row = HourlyContribution.From(Completed("abandoned", durationMs: null));
        row.LatencyHistogram.Sum().ShouldBe(0);
        row.DurationMsSum.ShouldBe(0);
    }

    [Fact]
    public void Fallbacks_and_ttft_are_counted()
    {
        var row = HourlyContribution.From(Completed(hadFallback: true, ttftMs: 180, cached: 40));
        row.FallbackRequests.ShouldBe(1);
        row.TtftHistogram[LatencyHistogram.BucketOf(180)].ShouldBe(1);
        row.CachedInputTokens.ShouldBe(40);
    }

    [Fact]
    public void The_event_round_trips_through_its_json_payload()
    {
        var completed = Completed(model: _model, ttftMs: 12, cached: 3);
        RequestCompletedEvent.FromJson(completed.ToJson()).ShouldBe(completed);
        completed.ToJson().ShouldContain("\"requestedAlias\":\"balanced\"");
    }
}
