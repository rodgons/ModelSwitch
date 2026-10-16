using ModelSwitch.Registry.Contracts;
using ModelSwitch.Routing.Application;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;
using static ModelSwitch.UnitTests.Routing.RoutingFixtures;

namespace ModelSwitch.UnitTests.Routing;

/// <summary>api.md section 3, "Routing": simulating the Routing Decision of a hypothetical request through the real router.</summary>
public sealed class RoutingSimulationTests
{
    private static readonly Router _router = new();

    private static SimulateRoutingRequest Request(
        int? estimatedInputTokens = 1_000,
        int? maxOutputTokens = 500,
        string? taskType = null,
        string?[]? requires = null,
        int? version = null)
        => new(version, TenantId: null, taskType, requires, estimatedInputTokens, maxOutputTokens);

    private static RoutingSimulationView Run(
        PolicyDefinition definition,
        SimulateRoutingRequest? request = null,
        TenantModelAccess? access = null,
        Dictionary<Guid, TimeSpan>? open = null,
        Func<Guid, CircuitState>? stateOf = null,
        RoutingSnapshot? catalog = null)
    {
        var input = RoutingSimulation.Validate(request ?? Request()).Value;
        var policy = new PolicySnapshot(PolicyId, "balanced-policy", 3, definition);
        return RoutingSimulation.Run(_router, policy, input, catalog ?? Snapshot(definition), access, open ?? [], stateOf ?? (_ => CircuitState.Closed));
    }

    // ---- validation ----

    [Fact]
    public void The_input_token_estimate_is_required_and_bounded()
    {
        RoutingSimulation.Validate(Request(estimatedInputTokens: null)).Error.FieldErrors!.Keys.ShouldBe(["estimatedInputTokens"]);
        RoutingSimulation.Validate(Request(estimatedInputTokens: -1)).Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        RoutingSimulation.Validate(Request(estimatedInputTokens: 10_000_001)).IsFailure.ShouldBeTrue();
        RoutingSimulation.Validate(Request(estimatedInputTokens: 0)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Every_bad_field_is_reported_at_once()
    {
        var errors = RoutingSimulation.Validate(Request(maxOutputTokens: 0, taskType: "Not Valid", requires: ["streaming", null, "bad name!"], version: 0)).Error.FieldErrors!;

        errors.Keys.ShouldBe(["maxOutputTokens", "taskType", "requires", "version"], ignoreOrder: true);
    }

    [Fact]
    public void Required_capabilities_are_deduplicated_and_an_absent_list_requires_nothing()
    {
        RoutingSimulation.Validate(Request(requires: ["jsonSchema", "streaming", "jsonSchema"])).Value.Requires.ShouldBe(["jsonSchema", "streaming"]);
        RoutingSimulation.Validate(Request()).Value.Requires.ShouldBeEmpty();
    }

    // ---- the decision ----

    [Fact]
    public void Eligible_candidates_are_listed_in_rank_order_with_their_worst_case_cost()
    {
        var view = Run(Definition(PolicyStrategy.Cost, candidates: [Pricey, Cheap]));

        view.ErrorCode.ShouldBeNull();
        view.Version.ShouldBe(3);
        view.Strategy.ShouldBe("cost");
        view.EffectiveMaxOutputTokens.ShouldBe(500);
        view.Candidates.Select(c => (c.ProviderModelId, c.Status, c.Rank)).ShouldBe([(Cheap, "eligible", (int?)1), (Pricey, "eligible", 2)]);
        view.Candidates[0].WorstCaseEstimatedCost.ShouldBe(0.002m);
        view.Candidates[0].ProviderName.ShouldBe("p");
        view.Candidates[0].Model.ShouldBe("model-1");
    }

    [Fact]
    public void Excluded_candidates_keep_their_reason_and_the_worst_case_when_they_have_a_price()
    {
        var view = Run(Definition(candidates: [Cheap, Mid]), Request(requires: ["jsonSchema"]), catalog: Snapshot(
            Definition(),
            Model(Cheap, capabilities: Capabilities(jsonSchema: false)),
            Model(Mid, input: null, output: null)));

        view.ErrorCode.ShouldBe("no_eligible_model");
        view.Candidates.Select(c => (c.Status, c.ExclusionReason)).ShouldBe([("excluded", "capability_missing:jsonSchema"), ("excluded", "no_price")]);
        view.Candidates[0].WorstCaseEstimatedCost.ShouldBe(0.002m);
        view.Candidates[1].WorstCaseEstimatedCost.ShouldBeNull();
    }

    [Fact]
    public void Candidates_cut_by_max_candidates_are_shown_as_truncated_after_the_ranked_ones()
        => Run(Definition(candidates: [Cheap, Mid, Pricey], maxCandidates: 1)).Candidates.Select(c => (c.ProviderModelId, c.Status, c.Rank))
            .ShouldBe([(Cheap, "eligible", (int?)1), (Mid, "truncated", null), (Pricey, "truncated", null)]);

    [Fact]
    public void Each_candidate_shows_this_instances_circuit_state_and_open_circuits_exclude()
    {
        var view = Run(
            Definition(candidates: [Cheap, Mid]),
            open: new Dictionary<Guid, TimeSpan> { [Cheap] = TimeSpan.FromSeconds(12) },
            stateOf: id => id == Cheap ? CircuitState.Open : CircuitState.HalfOpen);

        view.Candidates.Select(c => (c.ProviderModelId, c.CircuitState, c.Status)).ShouldBe([(Mid, "half_open", "eligible"), (Cheap, "open", "excluded")]);
    }

    [Fact]
    public void Every_circuit_open_is_provider_unavailable_with_the_shortest_break()
    {
        var view = Run(
            Definition(candidates: [Cheap]),
            open: new Dictionary<Guid, TimeSpan> { [Cheap] = TimeSpan.FromMilliseconds(1_500) },
            stateOf: _ => CircuitState.Open);

        view.ErrorCode.ShouldBe("provider_unavailable");
        view.RetryAfterMs.ShouldBe(1_500);
    }

    [Fact]
    public void The_tenants_request_cost_limit_applies_but_its_alias_allow_list_does_not()
    {
        var view = Run(Definition(candidates: [Cheap]), access: new TenantModelAccess(["other-alias"], MaxRequestCost: 0.001m));

        view.ErrorCode.ShouldBe("request_cost_limit_exceeded");
        view.Candidates.Single().ExclusionReason.ShouldBe("over_request_cost_limit");
    }

    [Fact]
    public void The_matched_rule_and_the_task_type_decide_the_candidate_set()
    {
        var definition = Definition(
            PolicyStrategy.Rules,
            candidates: [],
            rules: [new PolicyRule(["extraction"], [], [Pricey])],
            defaults: [Cheap]);

        var matched = Run(definition, Request(taskType: "extraction"));
        var fallback = Run(definition);

        matched.RuleMatched.ShouldBe(0);
        matched.Candidates.Single().ProviderModelId.ShouldBe(Pricey);
        fallback.RuleMatched.ShouldBeNull();
        fallback.Candidates.Single().ProviderModelId.ShouldBe(Cheap);
    }

    [Fact]
    public void A_candidate_missing_from_the_catalog_has_no_name_and_is_model_disabled()
    {
        var view = Run(Definition(candidates: [Cheap]), catalog: Snapshot(Definition(), Model(Mid)));

        view.Candidates.Single().ShouldSatisfyAllConditions(
            c => c.ProviderName.ShouldBeNull(),
            c => c.Model.ShouldBeNull(),
            c => c.ExclusionReason.ShouldBe("model_disabled"));
    }
}
