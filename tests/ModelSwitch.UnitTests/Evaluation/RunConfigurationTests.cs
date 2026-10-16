using ModelSwitch.Evaluation.Domain.Runs;
using Shouldly;
using static ModelSwitch.UnitTests.Evaluation.EvaluatorTestSupport;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class RunConfigurationTests
{
    private static readonly Guid _datasetId = Guid.Parse("01920000-0000-7000-8000-000000000001");
    private static readonly Guid _policyId = Guid.Parse("01920000-0000-7000-8000-000000000002");
    private static readonly Guid _promptId = Guid.Parse("01920000-0000-7000-8000-000000000003");

    private static RunConfiguration Configuration(
        string evaluatorConfig = """{"labels":["spam","ham"]}""",
        int concurrency = 4,
        string gatewayVersion = "1.0.0+abc",
        GenerationParams? parameters = null,
        int policyVersion = 3)
        => new(
            new FrozenDataset(_datasetId, 2, new string('a', 64), "classification"),
            FrozenTarget.ForAlias("balanced", _policyId, policyVersion),
            new FrozenPrompt(_promptId, 1, Json("""{"messages":[{"role":"user","content":"{{text}}"}]}""")),
            parameters ?? new GenerationParams(0, null, 7, 256),
            new FrozenEvaluator("classification", "1", Json(evaluatorConfig)),
            concurrency,
            gatewayVersion);

    [Fact]
    public void The_hash_is_64_lower_case_hex_and_stable()
    {
        var hash = Configuration().Sha256();

        hash.ShouldMatch("^[0-9a-f]{64}$");
        Configuration().Sha256().ShouldBe(hash);
    }

    [Fact]
    public void Concurrency_and_the_gateway_build_version_do_not_change_the_hash()
    {
        Configuration(concurrency: 16, gatewayVersion: "2.0.0").Sha256().ShouldBe(Configuration().Sha256());
    }

    [Fact]
    public void Member_order_and_spacing_in_the_evaluator_config_do_not_change_the_hash()
    {
        Configuration("""{ "labels" : ["spam","ham"] , "x":{"b":1,"a":2}}""").Sha256()
            .ShouldBe(Configuration("""{"x":{"a":2,"b":1},"labels":["spam","ham"]}""").Sha256());
    }

    [Fact]
    public void Every_choice_that_changes_what_runs_changes_the_hash()
    {
        var baseline = Configuration().Sha256();

        Configuration(policyVersion: 4).Sha256().ShouldNotBe(baseline);
        Configuration(parameters: new GenerationParams(0.5, null, 7, 256)).Sha256().ShouldNotBe(baseline);
        Configuration("""{"labels":["ham","spam"]}""").Sha256().ShouldNotBe(baseline, "label order is part of the config as written");
    }

    [Fact]
    public void Absent_generation_params_are_omitted_rather_than_written_as_null()
    {
        var json = Configuration(parameters: new GenerationParams(null, null, null, null)).ToJson();

        json.ShouldContain("\"params\":{}");
        json.ShouldNotContain("temperature");
    }

    [Fact]
    public void A_configuration_round_trips_through_its_stored_json()
    {
        var original = Configuration() with { Evaluator = new FrozenEvaluator("llm_judge", "1", Json("""{"judgeAlias":"judge","rubric":"r"}""")) { Judge = new FrozenJudge("judge", _policyId, 9) } };

        var parsed = RunConfiguration.Parse(original.ToJson());

        parsed.Sha256().ShouldBe(original.Sha256());
        parsed.Target.Alias.ShouldBe("balanced");
        parsed.Target.PolicyVersion.ShouldBe(3);
        parsed.Evaluator.Judge.ShouldBe(new FrozenJudge("judge", _policyId, 9));
        parsed.Params.Seed.ShouldBe(7);
        parsed.Concurrency.ShouldBe(4);
        parsed.GatewayVersion.ShouldBe("1.0.0+abc");
    }

    [Fact]
    public void A_pinned_provider_model_target_is_labelled_provider_slash_model()
    {
        var target = FrozenTarget.ForProviderModel(_policyId, "mock-openai/mock-echo");

        target.Label.ShouldBe("mock-openai/mock-echo");
        FrozenTarget.ForAlias("balanced", _policyId, 3).Label.ShouldBe("balanced@v3");
    }
}
