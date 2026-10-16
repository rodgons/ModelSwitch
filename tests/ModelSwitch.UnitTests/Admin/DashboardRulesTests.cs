using ModelSwitch.Admin.Application;
using ModelSwitch.Usage.Contracts;
using Shouldly;

namespace ModelSwitch.UnitTests.Admin;

/// <summary>How the dashboard sums up Provider Health across instances and folds usage into Providers.</summary>
public sealed class DashboardRulesTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_aggregate_state_is_the_worst_reported_by_a_fresh_instance()
    {
        ProviderHealthRules.Aggregate([("closed", _now), ("half_open", _now), ("open", _now.AddSeconds(-30))], _now).ShouldBe("open");
        ProviderHealthRules.Aggregate([("closed", _now), ("half_open", _now)], _now).ShouldBe("half_open");
        ProviderHealthRules.Aggregate([("closed", _now)], _now).ShouldBe("closed");
    }

    [Fact]
    public void A_stale_instance_is_left_out_and_no_fresh_instance_is_unknown()
    {
        var stale = _now - ProviderHealthRules.Freshness - TimeSpan.FromSeconds(1);

        ProviderHealthRules.Aggregate([("open", stale), ("closed", _now)], _now).ShouldBe("closed");
        ProviderHealthRules.Aggregate([("open", stale)], _now).ShouldBe(ProviderHealthRules.Unknown);
        ProviderHealthRules.Aggregate([], _now).ShouldBe(ProviderHealthRules.Unknown);
    }

    [Fact]
    public void No_traffic_has_no_rate_rather_than_zero()
    {
        ProviderHealthRules.Rate(0, 0).ShouldBeNull();
        ProviderHealthRules.Rate(1, 4).ShouldBe(0.25);
    }

    [Fact]
    public void Success_and_fallback_rates_are_derived_from_the_counts()
    {
        var figures = DashboardService.Figures(new OverviewFigures(10, 2, 3, 0, 0, 0m, null, null, null, null, Exact: true));

        figures.SuccessRate.ShouldBe(0.8);
        figures.FallbackRate.ShouldBe(0.3);
        DashboardService.Figures(new OverviewFigures(0, 0, 0, 0, 0, 0m, null, null, null, null, Exact: true)).SuccessRate.ShouldBeNull();
    }

    [Fact]
    public void Grouping_by_provider_sums_the_provider_models_of_one_provider()
    {
        var provider = Guid.CreateVersion7();
        var modelA = Guid.CreateVersion7();
        var modelB = Guid.CreateVersion7();
        var names = new Dictionary<Guid, ModelRef>
        {
            [modelA] = new(modelA, provider, "openai", "gpt-a"),
            [modelB] = new(modelB, provider, "openai", "gpt-b"),
        };
        var period = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        UsageRow[] rows =
        [
            new(period, null, modelA, "balanced", 2, 1, 100, 10, 20, 0.5m),
            new(period, null, modelB, "balanced", 3, 0, 50, 0, 5, 0.25m),
        ];

        var byProvider = DashboardService.FoldProviders(rows, names, byProvider: true, byModel: false).ShouldHaveSingleItem();
        byProvider.ProviderName.ShouldBe("openai");
        byProvider.Model.ShouldBeNull();
        byProvider.Requests.ShouldBe(5);
        byProvider.FailedRequests.ShouldBe(1);
        byProvider.InputTokens.ShouldBe(150);
        byProvider.EstimatedCost.ShouldBe(0.75m);

        DashboardService.FoldProviders(rows, names, byProvider: true, byModel: true).Count.ShouldBe(2);
    }
}

/// <summary>The request detail publishes the stored Routing Decision as a typed shape, so the dashboard needs no hand-written one.</summary>
public sealed class RoutingDecisionViewTests
{
    [Fact]
    public void The_stored_document_maps_to_the_typed_view()
    {
        var first = Guid.CreateVersion7();
        var excluded = Guid.CreateVersion7();
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            $$"""{"strategy":"rules","ruleMatched":0,"maxOutputTokens":1024,"ordered":["{{first}}"],"excluded":[{"model":"{{excluded}}","reason":"capability_missing"}]}""");

        var view = RoutingDecisionView.From(node).ShouldNotBeNull();

        view.Strategy.ShouldBe("rules");
        view.RuleMatched.ShouldBe(0);
        view.MaxOutputTokens.ShouldBe(1024);
        view.Ordered.ShouldBe([first]);
        view.Excluded.ShouldBe([new ExcludedCandidateView(excluded, "capability_missing")]);
    }

    [Fact]
    public void A_request_routing_never_reached_has_no_decision_and_a_null_rule_is_none()
    {
        RoutingDecisionView.From(null).ShouldBeNull();
        RoutingDecisionView.From(System.Text.Json.Nodes.JsonNode.Parse("""{"strategy":"static","ruleMatched":null,"ordered":[],"excluded":[]}"""))!.RuleMatched.ShouldBeNull();
    }
}
