using ModelSwitch.Routing.Application;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;
using static ModelSwitch.UnitTests.Routing.RoutingFixtures;

namespace ModelSwitch.UnitTests.Routing;

public sealed class RouterTests
{
    private readonly Router _router = new();

    // ---- alias resolution and tenant access ----

    [Fact]
    public void Unknown_alias_is_model_not_found_without_a_decision()
    {
        var outcome = _router.Decide(Input(Snapshot(Definition()), alias: "nope"));

        outcome.Error!.Code.ShouldBe(ErrorCode.ModelNotFound);
        outcome.Decision.ShouldBeNull();
    }

    [Fact]
    public void Alias_pointing_at_a_missing_policy_is_model_not_found()
    {
        var snapshot = new RoutingSnapshot(
            new Dictionary<string, Guid> { ["balanced"] = PolicyId },
            new Dictionary<Guid, PolicySnapshot>(),
            new Dictionary<Guid, CandidateModel>());

        _router.Decide(Input(snapshot)).Error!.Code.ShouldBe(ErrorCode.ModelNotFound);
    }

    [Theory]
    [InlineData(new[] { "fast" }, false)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "fast", "balanced" }, true)]
    [InlineData(new[] { "*" }, true)]
    public void Tenant_alias_allow_list_decides_model_not_allowed(string[] allowed, bool expectedAllowed)
    {
        var outcome = _router.Decide(Input(Snapshot(Definition()), allowedAliases: allowed));

        outcome.IsSuccess.ShouldBe(expectedAllowed);
        if (!expectedAllowed)
        {
            outcome.Error!.Code.ShouldBe(ErrorCode.ModelNotAllowed);
        }
    }

    [Fact]
    public void Unknown_alias_wins_over_not_allowed()
    {
        _router.Decide(Input(Snapshot(Definition()), alias: "nope", allowedAliases: ["fast"])).Error!.Code.ShouldBe(ErrorCode.ModelNotFound);
    }

    // ---- eligibility: one case per reason code ----

    public static TheoryData<string, string> ExclusionCases() => new()
    {
        { EligibilityReason.ModelDisabled, "model disabled" },
        { EligibilityReason.ModelDisabled, "provider disabled" },
        { EligibilityReason.ModelDisabled, "model missing from the catalog" },
        { EligibilityReason.CapabilityMissing("jsonSchema"), "jsonSchema required" },
        { EligibilityReason.CapabilityMissing("streaming"), "streaming required" },
        { EligibilityReason.CapabilityMissing("jsonObject"), "jsonObject required" },
        { EligibilityReason.CapabilityMissing("top_p"), "unsupported parameter required" },
        { EligibilityReason.ContextWindow, "input plus output exceeds the window" },
        { EligibilityReason.ContextWindow, "output exceeds the model max output" },
        { EligibilityReason.ProviderNotAllowed, "provider not in allowedProviders" },
        { EligibilityReason.NoPrice, "no price" },
        { EligibilityReason.OverRequestCostLimit, "policy cost limit" },
        { EligibilityReason.OverRequestCostLimit, "tenant cost limit" },
        { EligibilityReason.CircuitOpen, "circuit open" },
    };

    [Theory]
    [MemberData(nameof(ExclusionCases))]
    public void Each_reason_code_excludes_the_candidate_and_is_recorded(string reason, string scenario)
    {
        // Candidate "Cheap" is the one under test; Mid and Pricey stay eligible so the request still succeeds.
        var (snapshot, input) = Scenario(scenario);

        var outcome = _router.Decide(input);

        outcome.IsSuccess.ShouldBeTrue(scenario);
        outcome.Decision!.Excluded.ShouldBe([new ExcludedCandidate(Cheap, reason)], scenario);
        outcome.Decision.Ordered.Select(c => c.ProviderModelId).ShouldBe([Mid, Pricey], scenario);
        snapshot.ShouldNotBeNull();
    }

    private static (RoutingSnapshot Snapshot, RoutingInput Input) Scenario(string scenario)
    {
        var definition = Definition();
        var mid = Model(Mid, input: 3m, output: 6m);
        var pricey = Model(Pricey, input: 10m, output: 20m);
        CandidateModel cheap = Model(Cheap, input: 1m, output: 2m);
        string[]? required = null;
        int inputTokens = 1_000;
        int? maxOut = 500;
        decimal? tenantLimit = null;
        Dictionary<Guid, TimeSpan>? circuits = null;
        RoutingSnapshot? snapshot = null;

        switch (scenario)
        {
            case "model disabled":
                cheap = Model(Cheap, modelEnabled: false);
                break;
            case "provider disabled":
                cheap = Model(Cheap, provider: "down", providerEnabled: false);
                break;
            case "model missing from the catalog":
                snapshot = Snapshot(definition, mid, pricey);
                break;
            case "jsonSchema required":
                cheap = Model(Cheap, capabilities: Capabilities(jsonSchema: false));
                required = ["jsonSchema"];
                break;
            case "streaming required":
                cheap = Model(Cheap, capabilities: Capabilities(streaming: false));
                required = ["streaming"];
                break;
            case "jsonObject required":
                cheap = Model(Cheap, capabilities: Capabilities(jsonObject: false));
                required = ["jsonObject"];
                break;
            case "unsupported parameter required":
                cheap = Model(Cheap, capabilities: Capabilities(parameters: ["temperature"]));
                mid = Model(Mid, input: 3m, output: 6m, capabilities: Capabilities(parameters: ["top_p"]));
                pricey = Model(Pricey, input: 10m, output: 20m, capabilities: Capabilities(parameters: ["top_p"]));
                required = ["top_p"];
                break;
            case "input plus output exceeds the window":
                cheap = Model(Cheap, capabilities: Capabilities(contextWindow: 1_400));
                break;
            case "output exceeds the model max output":
                cheap = Model(Cheap, capabilities: Capabilities(maxOutputTokens: 100));
                break;
            case "provider not in allowedProviders":
                definition = Definition(allowedProviders: ["good"]);
                cheap = Model(Cheap, provider: "bad");
                mid = Model(Mid, provider: "good", input: 3m, output: 6m);
                pricey = Model(Pricey, provider: "good", input: 10m, output: 20m);
                break;
            case "no price":
                cheap = Model(Cheap, input: null, output: null);
                break;
            case "policy cost limit":
                definition = Definition(maxRequestCost: 0.0015m);
                cheap = Model(Cheap, input: 1m, output: 20m); // 0.001 + 0.01
                mid = Model(Mid, input: 0.1m, output: 0.2m);
                pricey = Model(Pricey, input: 0.1m, output: 0.2m);
                break;
            case "tenant cost limit":
                tenantLimit = 0.0015m;
                cheap = Model(Cheap, input: 1m, output: 20m);
                mid = Model(Mid, input: 0.1m, output: 0.2m);
                pricey = Model(Pricey, input: 0.1m, output: 0.2m);
                break;
            case "circuit open":
                circuits = new Dictionary<Guid, TimeSpan> { [Cheap] = TimeSpan.FromSeconds(10) };
                break;
            default:
                throw new InvalidOperationException(scenario);
        }

        snapshot ??= Snapshot(definition, cheap, mid, pricey);
        return (snapshot, Input(snapshot, required: required, inputTokens: inputTokens, maxOutputTokens: maxOut, tenantMaxRequestCost: tenantLimit, openCircuits: circuits));
    }

    [Fact]
    public void Context_window_boundary_is_inclusive()
    {
        // 1000 + 500 = 1500 fits exactly in a 1500 window.
        var snapshot = Snapshot(Definition(candidates: [Cheap]), Model(Cheap, capabilities: Capabilities(contextWindow: 1_500)));

        _router.Decide(Input(snapshot)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Cost_limit_boundary_is_inclusive()
    {
        // Cheap worst case is exactly 0.002.
        var snapshot = Snapshot(Definition(candidates: [Cheap], maxRequestCost: 0.002m));

        _router.Decide(Input(snapshot)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_smaller_of_policy_and_tenant_cost_limits_applies()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap, Mid], maxRequestCost: 0.01m));

        var outcome = _router.Decide(Input(snapshot, tenantMaxRequestCost: 0.004m));

        outcome.Decision!.Ordered.Select(c => c.ProviderModelId).ShouldBe([Cheap]);
        outcome.Decision.Excluded.ShouldBe([new ExcludedCandidate(Mid, EligibilityReason.OverRequestCostLimit)]);
    }

    [Fact]
    public void Each_candidate_records_the_first_failing_filter_in_documented_order()
    {
        // Disabled beats missing capability beats context window beats no price.
        var snapshot = Snapshot(
            Definition(candidates: [Cheap, Mid]),
            Model(Cheap, modelEnabled: false, capabilities: Capabilities(jsonSchema: false), input: null, output: null),
            Model(Mid, capabilities: Capabilities(jsonSchema: false, contextWindow: 10), input: null, output: null));

        var outcome = _router.Decide(Input(snapshot, required: ["jsonSchema"]));

        outcome.Decision!.Excluded.ShouldBe(
        [
            new ExcludedCandidate(Cheap, EligibilityReason.ModelDisabled),
            new ExcludedCandidate(Mid, EligibilityReason.CapabilityMissing("jsonSchema")),
        ]);
    }

    [Fact]
    public void Input_tokens_are_estimated_with_the_models_own_tokenizer_and_fall_back_when_undeclared()
    {
        var small = Model(Cheap, capabilities: Capabilities(contextWindow: 2_000, tokenizer: "o200k_base"));
        var big = Model(Mid, capabilities: Capabilities(contextWindow: 2_000, tokenizer: "llama3"));
        var undeclared = Model(Pricey, capabilities: Capabilities(contextWindow: 2_000));
        var input = Input(Snapshot(Definition(), small, big, undeclared)) with
        {
            EstimatedInputTokens = new InputTokenEstimate(new Dictionary<string, int> { ["o200k_base"] = 900, ["llama3"] = 1_700 }, Fallback: 1_600),
        };

        var outcome = _router.Decide(input);

        outcome.Decision!.Ordered.Select(c => c.ProviderModelId).ShouldBe([Cheap]);
        outcome.Decision.Excluded.Select(e => (e.ProviderModelId, e.Reason)).ShouldBe(
            [(Mid, EligibilityReason.ContextWindow), (Pricey, EligibilityReason.ContextWindow)]);
    }

    // ---- empty eligible list ----

    [Fact]
    public void All_candidates_excluded_by_non_circuit_reasons_is_no_eligible_model_naming_distinct_reasons()
    {
        var snapshot = Snapshot(
            Definition(candidates: [Cheap, Mid]),
            Model(Cheap, input: null, output: null),
            Model(Mid, input: null, output: null));

        var outcome = _router.Decide(Input(snapshot));

        outcome.Error!.Code.ShouldBe(ErrorCode.NoEligibleModel);
        outcome.Error.Message.ShouldContain("no_price");
        outcome.Error.Message.Split("no_price").Length.ShouldBe(2, "the reason is named once");
        outcome.Error.Message.ShouldNotContain(Cheap.ToString());
        outcome.Decision!.Excluded.Count.ShouldBe(2);
        outcome.RetryAfter.ShouldBeNull();
    }

    [Fact]
    public void All_candidates_excluded_only_by_the_request_cost_limit_is_request_cost_limit_exceeded()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap, Mid], maxRequestCost: 0.0000001m));

        var outcome = _router.Decide(Input(snapshot));

        outcome.Error!.Code.ShouldBe(ErrorCode.RequestCostLimitExceeded);
        outcome.Decision!.Excluded.Select(e => e.Reason).ShouldBe([EligibilityReason.OverRequestCostLimit, EligibilityReason.OverRequestCostLimit]);
        outcome.RetryAfter.ShouldBeNull();
    }

    [Fact]
    public void The_request_cost_limit_mixed_with_another_reason_stays_no_eligible_model()
    {
        var snapshot = Snapshot(
            Definition(candidates: [Cheap, Mid], maxRequestCost: 0.0000001m),
            Model(Mid, input: null, output: null));

        _router.Decide(Input(snapshot)).Error!.Code.ShouldBe(ErrorCode.NoEligibleModel);
    }

    [Fact]
    public void All_candidates_excluded_only_by_open_circuits_is_provider_unavailable_with_the_shortest_break()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap, Mid]));
        var circuits = new Dictionary<Guid, TimeSpan> { [Cheap] = TimeSpan.FromSeconds(30), [Mid] = TimeSpan.FromSeconds(8) };

        var outcome = _router.Decide(Input(snapshot, openCircuits: circuits));

        outcome.Error!.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        outcome.RetryAfter.ShouldBe(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void A_mix_of_open_circuit_and_another_reason_is_no_eligible_model()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap, Mid]), Model(Cheap), Model(Mid, input: null, output: null));

        var outcome = _router.Decide(Input(snapshot, openCircuits: new() { [Cheap] = TimeSpan.FromSeconds(5) }));

        outcome.Error!.Code.ShouldBe(ErrorCode.NoEligibleModel);
        outcome.RetryAfter.ShouldBeNull();
    }

    [Fact]
    public void An_empty_candidate_set_is_no_eligible_model()
    {
        var snapshot = Snapshot(Definition(PolicyStrategy.Rules, rules: [], defaults: []));

        _router.Decide(Input(snapshot)).Error!.Code.ShouldBe(ErrorCode.NoEligibleModel);
    }

    // ---- ranking ----

    [Fact]
    public void Static_strategy_keeps_listed_order_even_when_a_later_candidate_is_cheaper()
    {
        var outcome = _router.Decide(Input(Snapshot(Definition(candidates: [Pricey, Cheap, Mid]))));

        outcome.Decision!.Ordered.Select(c => c.ProviderModelId).ShouldBe([Pricey, Cheap, Mid]);
    }

    [Fact]
    public void Cost_strategy_orders_by_ascending_worst_case_cost()
    {
        var outcome = _router.Decide(Input(Snapshot(Definition(PolicyStrategy.Cost, candidates: [Pricey, Mid, Cheap]))));

        outcome.Decision!.Ordered.Select(c => c.ProviderModelId).ShouldBe([Cheap, Mid, Pricey]);
        outcome.Decision.Ordered.Select(c => c.WorstCaseEstimatedCost).ShouldBe([0.002m, 0.006m, 0.02m]);
    }

    [Fact]
    public void Worst_case_cost_uses_the_estimate_plus_the_full_output_allowance()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap]));

        var outcome = _router.Decide(Input(snapshot, inputTokens: 2_000_000, maxOutputTokens: 1_000_000) with { });

        // Window is 100k so this is excluded; use a roomy model to check the arithmetic.
        outcome.Error!.Code.ShouldBe(ErrorCode.NoEligibleModel);
        var roomy = Snapshot(Definition(candidates: [Cheap]), Model(Cheap, input: 1m, output: 2m, capabilities: Capabilities(contextWindow: 10_000_000, maxOutputTokens: 10_000_000)));
        _router.Decide(Input(roomy, inputTokens: 2_000_000, maxOutputTokens: 1_000_000)).Decision!.Ordered[0].WorstCaseEstimatedCost.ShouldBe(4m);
    }

    [Fact]
    public void Cost_ties_are_broken_by_listed_order_then_by_provider_model_id()
    {
        var snapshot = Snapshot(
            Definition(PolicyStrategy.Cost, candidates: [Pricey, Cheap, Mid]),
            Model(Cheap, input: 1m, output: 1m),
            Model(Mid, input: 1m, output: 1m),
            Model(Pricey, input: 1m, output: 1m));

        var outcome = _router.Decide(Input(snapshot));

        outcome.Decision!.Ordered.Select(c => c.ProviderModelId).ShouldBe([Pricey, Cheap, Mid]);
    }

    [Fact]
    public void Cost_ranking_is_deterministic_across_repeated_decisions()
    {
        var input = Input(Snapshot(Definition(PolicyStrategy.Cost)));

        var first = _router.Decide(input).Decision!.Ordered.Select(c => c.ProviderModelId).ToList();

        for (var i = 0; i < 20; i++)
        {
            _router.Decide(input).Decision!.Ordered.Select(c => c.ProviderModelId).ShouldBe(first);
        }
    }

    [Fact]
    public void Ranking_orders_only_eligible_candidates()
    {
        var snapshot = Snapshot(
            Definition(PolicyStrategy.Cost),
            Model(Cheap, input: null, output: null),
            Model(Mid, input: 3m, output: 6m),
            Model(Pricey, input: 10m, output: 20m));

        var outcome = _router.Decide(Input(snapshot));

        outcome.Decision!.Ordered.Select(c => c.ProviderModelId).ShouldBe([Mid, Pricey]);
        outcome.Decision.Excluded.ShouldBe([new ExcludedCandidate(Cheap, EligibilityReason.NoPrice)]);
    }

    [Theory]
    [InlineData(PolicyStrategy.Static)]
    [InlineData(PolicyStrategy.Cost)]
    public void Truncates_to_max_candidates_after_ranking(PolicyStrategy strategy)
    {
        var snapshot = Snapshot(Definition(strategy, candidates: [Pricey, Mid, Cheap], maxCandidates: 2));

        var ids = _router.Decide(Input(snapshot)).Decision!.Ordered.Select(c => c.ProviderModelId).ToList();

        ids.ShouldBe(strategy == PolicyStrategy.Cost ? [Cheap, Mid] : [Pricey, Mid]);
    }

    // ---- rules strategy ----

    private static PolicyDefinition RulesPolicy() => Definition(
        PolicyStrategy.Rules,
        candidates: [],
        rules:
        [
            new PolicyRule(["extraction", "classification"], [], [Mid, Cheap]),
            new PolicyRule(null, ["jsonSchema"], [Pricey]),
            new PolicyRule(["chat"], ["jsonObject"], [Cheap, Pricey]),
        ],
        defaults: [Cheap, Mid]);

    [Theory]
    [InlineData("extraction", new string[0], 0, new[] { "mid", "cheap" })]
    [InlineData("classification", new[] { "jsonSchema" }, 0, new[] { "mid", "cheap" })] // first matching rule wins
    [InlineData(null, new[] { "jsonSchema" }, 1, new[] { "pricey" })]
    [InlineData("chat", new[] { "jsonObject" }, 2, new[] { "cheap", "pricey" })]
    [InlineData("chat", new string[0], null, new[] { "cheap", "mid" })] // rule 2 needs jsonObject, so default
    [InlineData(null, new string[0], null, new[] { "cheap", "mid" })]
    [InlineData("summarize", new string[0], null, new[] { "cheap", "mid" })]
    public void Rules_strategy_uses_the_first_matching_rules_candidates_in_listed_order(string? taskType, string[] required, int? expectedRule, string[] expected)
    {
        var ids = new Dictionary<string, Guid> { ["cheap"] = Cheap, ["mid"] = Mid, ["pricey"] = Pricey };
        var snapshot = Snapshot(RulesPolicy());

        var outcome = _router.Decide(Input(snapshot, taskType: taskType, required: required));

        outcome.Decision!.RuleMatched.ShouldBe(expectedRule);
        outcome.Decision.Ordered.Select(c => c.ProviderModelId).ShouldBe(expected.Select(n => ids[n]).ToList());
    }

    [Fact]
    public void Non_rules_strategies_never_report_a_matched_rule()
    {
        _router.Decide(Input(Snapshot(Definition()))).Decision!.RuleMatched.ShouldBeNull();
    }

    // ---- decision content ----

    [Fact]
    public void Decision_records_policy_version_effective_max_output_and_execution_settings()
    {
        var snapshot = Snapshot(Definition(defaultMaxOutputTokens: 321));

        var decision = _router.Decide(Input(snapshot, maxOutputTokens: null)).Decision!;

        decision.PolicyId.ShouldBe(PolicyId);
        decision.PolicyVersion.ShouldBe(7);
        decision.Strategy.ShouldBe(PolicyStrategy.Static);
        decision.EffectiveMaxOutputTokens.ShouldBe(321);
        decision.Execution.ShouldBe(ExecutionSettings.Default);
    }

    [Fact]
    public void Request_max_output_overrides_the_policy_default()
    {
        _router.Decide(Input(Snapshot(Definition(defaultMaxOutputTokens: 321)), maxOutputTokens: 77)).Decision!.EffectiveMaxOutputTokens.ShouldBe(77);
    }

    [Fact]
    public void Required_capabilities_are_checked_in_ordinal_order_so_the_recorded_reason_is_stable()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap]), Model(Cheap, capabilities: Capabilities(jsonSchema: false, jsonObject: false)));

        var outcome = _router.Decide(Input(snapshot, required: ["jsonSchema", "jsonObject"]));

        outcome.Decision!.Excluded.Single().Reason.ShouldBe("capability_missing:jsonObject");
    }

    [Fact]
    public void Router_does_not_mutate_its_input()
    {
        var circuits = new Dictionary<Guid, TimeSpan> { [Cheap] = TimeSpan.FromSeconds(1) };
        var input = Input(Snapshot(Definition()), openCircuits: circuits);

        _router.Decide(input);

        circuits.Count.ShouldBe(1);
        input.Snapshot.Models.Count.ShouldBe(3);
    }
}
