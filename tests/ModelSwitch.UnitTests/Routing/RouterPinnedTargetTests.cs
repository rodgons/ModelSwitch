using ModelSwitch.Routing.Application;
using ModelSwitch.Routing.Contracts;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;
using static ModelSwitch.UnitTests.Routing.RoutingFixtures;

namespace ModelSwitch.UnitTests.Routing;

/// <summary>Evaluation targets (evaluation.md section 4): a frozen Policy Version for an alias, and a pinned Provider Model.</summary>
public sealed class RouterPinnedTargetTests
{
    private static readonly Guid _unlisted = new("0192f0c8-0000-7000-8000-000000000009");
    private readonly Router _router = new();

    private static PolicySnapshot OldVersion(params Guid[] candidates)
        => new(PolicyId, "balanced-policy", 3, Definition(candidates: candidates, defaultMaxOutputTokens: 55));

    // ---- pinned Policy Version ----

    [Fact]
    public void A_pinned_policy_version_is_routed_on_instead_of_the_latest()
    {
        var input = Input(Snapshot(Definition()), maxOutputTokens: null) with { PinnedPolicy = OldVersion(Pricey) };

        var decision = _router.Decide(input).Decision!;

        decision.PolicyId.ShouldBe(PolicyId);
        decision.PolicyVersion.ShouldBe(3);
        decision.Ordered.Select(c => c.ProviderModelId).ShouldBe([Pricey]);
        decision.EffectiveMaxOutputTokens.ShouldBe(55, "the pinned version's own defaults apply");
        decision.Pinned.ShouldBeFalse();
    }

    [Fact]
    public void A_pinned_policy_version_still_needs_the_alias_to_exist()
    {
        var input = Input(Snapshot(Definition()), alias: "gone") with { PinnedPolicy = OldVersion(Pricey) };

        _router.Decide(input).Error!.Code.ShouldBe(ErrorCode.ModelNotFound);
    }

    [Fact]
    public void A_pinned_policy_version_still_needs_the_alias_to_be_allowed()
    {
        var input = Input(Snapshot(Definition()), allowedAliases: ["fast"]) with { PinnedPolicy = OldVersion(Pricey) };

        _router.Decide(input).Error!.Code.ShouldBe(ErrorCode.ModelNotAllowed);
    }

    [Fact]
    public void A_pinned_policy_versions_candidates_are_checked_for_eligibility_against_the_current_catalog()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap]), Model(Cheap), Model(Pricey, modelEnabled: false));
        var input = Input(snapshot) with { PinnedPolicy = OldVersion(Pricey) };

        var outcome = _router.Decide(input);

        outcome.Error!.Code.ShouldBe(ErrorCode.NoEligibleModel);
        outcome.Decision!.Excluded.ShouldBe([new ExcludedCandidate(Pricey, EligibilityReason.ModelDisabled)]);
    }

    // ---- pinned Provider Model ----

    [Fact]
    public void A_pinned_provider_model_bypasses_the_policy_with_the_policy_independent_settings()
    {
        var input = Input(Snapshot(Definition(candidates: [Cheap])), maxOutputTokens: null) with { PinnedProviderModelId = Cheap };

        var decision = _router.Decide(input).Decision!;

        decision.Pinned.ShouldBeTrue();
        decision.PolicyId.ShouldBeNull();
        decision.PolicyVersion.ShouldBeNull();
        decision.RuleMatched.ShouldBeNull();
        decision.Ordered.Select(c => c.ProviderModelId).ShouldBe([Cheap]);
        decision.Execution.ShouldBe(PinnedRouting.Execution);
        decision.EffectiveMaxOutputTokens.ShouldBe(PinnedRouting.DefaultMaxOutputTokens);
    }

    [Fact]
    public void A_pinned_provider_model_keeps_the_requests_max_output()
    {
        var input = Input(Snapshot(Definition()), maxOutputTokens: 42) with { PinnedProviderModelId = Mid };

        _router.Decide(input).Decision!.EffectiveMaxOutputTokens.ShouldBe(42);
    }

    [Fact]
    public void An_unknown_pinned_provider_model_is_model_not_found()
    {
        var outcome = _router.Decide(Input(Snapshot(Definition())) with { PinnedProviderModelId = _unlisted });

        outcome.Error!.Code.ShouldBe(ErrorCode.ModelNotFound);
        outcome.Decision.ShouldBeNull();
    }

    [Fact]
    public void Every_alias_allows_any_pinned_provider_model_even_one_no_policy_lists()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap]), Model(Cheap), Model(_unlisted));

        _router.Decide(Input(snapshot, allowedAliases: ["*"]) with { PinnedProviderModelId = _unlisted }).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_pinned_provider_model_must_be_reachable_through_an_allowed_alias()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap]), Model(Cheap), Model(Mid));

        _router.Decide(Input(snapshot, allowedAliases: ["balanced"]) with { PinnedProviderModelId = Cheap }).IsSuccess.ShouldBeTrue();
        _router.Decide(Input(snapshot, allowedAliases: ["balanced"]) with { PinnedProviderModelId = Mid }).Error!.Code.ShouldBe(ErrorCode.ModelNotAllowed);
        _router.Decide(Input(snapshot, allowedAliases: ["fast"]) with { PinnedProviderModelId = Cheap }).Error!.Code.ShouldBe(ErrorCode.ModelNotAllowed);
    }

    [Fact]
    public void Rule_and_default_candidates_make_a_pinned_provider_model_reachable()
    {
        var definition = Definition(
            PolicyStrategy.Rules,
            candidates: [],
            rules: [new PolicyRule(["extraction"], [], [Mid])],
            defaults: [Pricey]);
        var snapshot = Snapshot(definition);

        snapshot.AliasesReaching(Mid).ShouldBe(["balanced"]);
        snapshot.AliasesReaching(Pricey).ShouldBe(["balanced"]);
        snapshot.AliasesReaching(Cheap).ShouldBeEmpty();
        _router.Decide(Input(snapshot, allowedAliases: ["balanced"]) with { PinnedProviderModelId = Pricey }).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_disabled_pinned_provider_model_is_no_eligible_model()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap]), Model(Cheap, providerEnabled: false));

        var outcome = _router.Decide(Input(snapshot) with { PinnedProviderModelId = Cheap });

        outcome.Error!.Code.ShouldBe(ErrorCode.NoEligibleModel);
        outcome.Decision!.Pinned.ShouldBeTrue();
        outcome.Decision.Excluded.ShouldBe([new ExcludedCandidate(Cheap, EligibilityReason.ModelDisabled)]);
    }

    [Fact]
    public void A_pinned_provider_model_lacking_a_required_capability_is_no_eligible_model()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap]), Model(Cheap, capabilities: Capabilities(jsonSchema: false)));

        _router.Decide(Input(snapshot, required: [CapabilityNames.JsonSchema]) with { PinnedProviderModelId = Cheap }).Error!.Code.ShouldBe(ErrorCode.NoEligibleModel);
    }

    [Fact]
    public void Only_the_tenants_request_cost_limit_applies_to_a_pinned_provider_model()
    {
        // Worst case of Pricey: 1000 x 10/1e6 + 500 x 20/1e6 = 0.02.
        var snapshot = Snapshot(Definition(maxRequestCost: 0.001m));

        _router.Decide(Input(snapshot) with { PinnedProviderModelId = Pricey }).IsSuccess.ShouldBeTrue("the policy's maxRequestCost does not apply");
        _router.Decide(Input(snapshot, tenantMaxRequestCost: 0.01m) with { PinnedProviderModelId = Pricey }).Error!.Code.ShouldBe(ErrorCode.RequestCostLimitExceeded);
    }

    [Fact]
    public void A_pinned_provider_model_with_an_open_circuit_is_provider_unavailable_with_its_break()
    {
        var input = Input(Snapshot(Definition()), openCircuits: new() { [Cheap] = TimeSpan.FromSeconds(12) }) with { PinnedProviderModelId = Cheap };

        var outcome = _router.Decide(input);

        outcome.Error!.Code.ShouldBe(ErrorCode.ProviderUnavailable);
        outcome.RetryAfter.ShouldBe(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void An_unpriced_pinned_provider_model_is_no_eligible_model()
    {
        var snapshot = Snapshot(Definition(candidates: [Cheap]), Model(Cheap, input: null, output: null));

        var outcome = _router.Decide(Input(snapshot) with { PinnedProviderModelId = Cheap });

        outcome.Error!.Code.ShouldBe(ErrorCode.NoEligibleModel);
        outcome.Decision!.Excluded.Single().Reason.ShouldBe(EligibilityReason.NoPrice);
    }
}
