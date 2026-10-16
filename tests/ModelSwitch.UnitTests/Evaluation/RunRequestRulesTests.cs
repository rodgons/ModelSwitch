using ModelSwitch.Evaluation.Domain.Datasets;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;
using static ModelSwitch.UnitTests.Evaluation.EvaluatorTestSupport;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class RunRequestRulesTests
{
    private static RunConfigurationInput Config(
        TargetInput? target = null,
        PromptRefInput? prompt = null,
        GenerationParamsInput? parameters = null,
        EvaluatorInput? evaluator = null)
        => new(
            target ?? new TargetInput("balanced", null),
            prompt ?? new PromptRefInput(Guid.CreateVersion7(), 1),
            parameters,
            evaluator ?? new EvaluatorInput("exact_match", "1", null));

    private static CreateEvaluationRequest Request(IReadOnlyList<RunConfigurationInput?>? configurations = null, int? concurrency = null, decimal? maxEstimatedCost = null, string? name = "run")
        => new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, name, configurations ?? [Config()], concurrency, maxEstimatedCost);

    private static IReadOnlyDictionary<string, string[]> Errors(CreateEvaluationRequest request)
    {
        var result = RunRequestRules.Validate(request);
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        return result.Error.FieldErrors!;
    }

    [Fact]
    public void A_valid_request_defaults_concurrency_to_4_and_trims_the_name()
    {
        var result = RunRequestRules.Validate(Request(name: "  extraction: mini vs local "));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Concurrency.ShouldBe(4);
        result.Value.Name.ShouldBe("extraction: mini vs local");
        result.Value.Configurations.Single().Evaluator.Kind.ShouldBe("exact_match");
    }

    [Fact]
    public void Every_missing_top_level_field_is_reported_at_once()
    {
        var errors = Errors(new CreateEvaluationRequest(null, null, null, null, null, null, null));

        errors.Keys.Order(StringComparer.Ordinal).ShouldBe(["configurations", "datasetId", "datasetVersion", "name", "tenantId"]);
    }

    [Fact]
    public void There_are_one_to_six_configurations()
    {
        Errors(Request([])).Keys.ShouldBe(["configurations"]);
        Errors(Request([.. Enumerable.Range(0, 7).Select(_ => Config())])).Keys.ShouldBe(["configurations"]);
        RunRequestRules.Validate(Request([.. Enumerable.Range(0, 6).Select(_ => Config())])).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Concurrency_is_1_to_16_and_the_cost_cap_is_a_non_negative_amount_with_at_most_10_decimals()
    {
        Errors(Request(concurrency: 0)).Keys.ShouldBe(["concurrency"]);
        Errors(Request(concurrency: 17)).Keys.ShouldBe(["concurrency"]);
        Errors(Request(maxEstimatedCost: -1m)).Keys.ShouldBe(["maxEstimatedCost"]);
        Errors(Request(maxEstimatedCost: 0.00000000001m)).Keys.ShouldBe(["maxEstimatedCost"]);
        RunRequestRules.Validate(Request(concurrency: 16, maxEstimatedCost: 0m)).Value.MaxEstimatedCost.ShouldBe(0m);
    }

    [Fact]
    public void A_target_names_exactly_one_of_an_alias_and_a_provider_model()
    {
        Errors(Request([Config(target: new TargetInput("balanced", Guid.CreateVersion7()))])).Keys.ShouldBe(["configurations[0].target"]);
        Errors(Request([Config(target: new TargetInput(null, null))])).Keys.ShouldBe(["configurations[0].target"]);
        Errors(Request([Config(target: new TargetInput("Not An Alias", null))])).Keys.ShouldBe(["configurations[0].target.alias"]);
        RunRequestRules.Validate(Request([Config(target: new TargetInput(null, Guid.CreateVersion7()))])).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Generation_params_follow_the_inference_api_ranges()
    {
        var errors = Errors(Request([Config(parameters: new GenerationParamsInput(2.5, -0.1, null, 0))]));

        errors.Keys.Order(StringComparer.Ordinal).ShouldBe(["configurations[0].params.maxOutputTokens", "configurations[0].params.temperature", "configurations[0].params.topP"]);
    }

    [Fact]
    public void Evaluator_errors_are_keyed_under_the_configuration_with_the_registrys_relative_keys()
    {
        var errors = Errors(Request(
        [
            Config(),
            Config(prompt: new PromptRefInput(null, 0), evaluator: new EvaluatorInput("classification", "1", Json("""{"labels":["a","a"]}"""))),
            Config(evaluator: new EvaluatorInput("grader", "1", null)),
        ]));

        errors.Keys.Order(StringComparer.Ordinal).ShouldBe(
        [
            "configurations[1].evaluator.config.labels[1]",
            "configurations[1].prompt.id",
            "configurations[1].prompt.version",
            "configurations[2].evaluator.kind",
        ]);
    }

    [Fact]
    public void A_missing_configuration_part_is_reported_by_name()
    {
        var errors = Errors(Request([new RunConfigurationInput(null, null, null, null), null]));

        errors.Keys.Order(StringComparer.Ordinal).ShouldBe(["configurations[0].evaluator", "configurations[0].prompt", "configurations[0].target", "configurations[1]"]);
    }

    [Theory]
    [InlineData("exact_match", "{}", "extraction", true)]
    [InlineData("exact_match", "{}", "free_text", true)]
    [InlineData("json_fields", "{}", "extraction", true)]
    [InlineData("json_fields", "{}", "classification", false)]
    [InlineData("classification", """{"labels":["a"]}""", "classification", true)]
    [InlineData("classification", """{"labels":["a"]}""", "free_text", false)]
    [InlineData("llm_judge", """{"judgeAlias":"j","rubric":"r"}""", "free_text", true)]
    [InlineData("llm_judge", """{"judgeAlias":"j","rubric":"r"}""", "extraction", false)]
    [InlineData("reference_similarity", "{}", "free_text", true)]
    [InlineData("reference_similarity", "{}", "classification", false)]
    public void An_evaluator_must_fit_the_datasets_task_kind(string kind, string config, string taskName, bool fits)
    {
        TaskKinds.TryParse(taskName, out var task).ShouldBeTrue();
        var schema = task switch
        {
            TaskKind.Classification => Json("""{"taskKind":"classification","labels":["a"]}"""),
            TaskKind.Extraction => Json("""{"taskKind":"extraction","fields":["email"]}"""),
            _ => Json("""{"taskKind":"free_text"}"""),
        };

        var errors = EvaluatorFit.Check(Create(kind, config), task, schema, "configurations[0].evaluator.");

        errors.ContainsKey("configurations[0].evaluator.kind").ShouldBe(!fits);
    }

    [Fact]
    public void Classification_labels_must_cover_every_label_the_dataset_version_expects()
    {
        var schema = Json("""{"taskKind":"classification","labels":["ham","spam"]}""");

        EvaluatorFit.Check(Create("classification", """{"labels":["spam"]}"""), TaskKind.Classification, schema, "p.").Keys.ShouldBe(["p.config.labels"]);
        EvaluatorFit.Check(Create("classification", """{"labels":["spam","ham","other"]}"""), TaskKind.Classification, schema, "p.").ShouldBeEmpty();
    }

    [Fact]
    public void Json_fields_may_only_configure_fields_the_dataset_version_expects()
    {
        var schema = Json("""{"taskKind":"extraction","fields":["email","name"]}""");

        EvaluatorFit.Check(Create("json_fields", """{"fields":{"phone":{"comparator":"exact"},"email":{"comparator":"casefold"}}}"""), TaskKind.Extraction, schema, "p.")
            .Keys.ShouldBe(["p.config.fields.phone"]);
    }
}
