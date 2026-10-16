using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Evaluation.Application;
using ModelSwitch.Evaluation.Domain.Runs;
using ModelSwitch.Evaluation.Domain.Templates;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Usage.Contracts;
using Shouldly;
using static ModelSwitch.UnitTests.Evaluation.EvaluatorTestSupport;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class RunInferenceTests
{
    private static readonly Guid _policyId = Guid.CreateVersion7();

    private static RunConfiguration Configuration(FrozenTarget target, FrozenJudge? judge = null)
        => new(
            new FrozenDataset(Guid.CreateVersion7(), 1, new string('0', 64), "extraction"),
            target,
            new FrozenPrompt(Guid.CreateVersion7(), 1, Json("""{"messages":[]}""")),
            new GenerationParams(0, 0.9, 7, 256),
            new FrozenEvaluator("llm_judge", "1", Json("{}")) { Judge = judge },
            4,
            "test");

    [Fact]
    public void The_candidate_call_is_evaluation_traffic_on_the_frozen_policy_version_with_the_generation_params()
    {
        var runId = Guid.CreateVersion7();
        var configuration = Configuration(FrozenTarget.ForAlias("balanced", _policyId, 3));
        var prompt = new RenderedPrompt([new PromptMessage("system", "s"), new PromptMessage("user", "u"), new PromptMessage("assistant", "a")], Json("""{"type":"json_object"}"""));

        var request = RunInference.Candidate(Guid.CreateVersion7(), Guid.CreateVersion7(), runId, configuration, prompt);

        request.Origin.ShouldBe(RequestOrigin.Evaluation);
        request.EvaluationRunId.ShouldBe(runId);
        request.ApiKeyId.ShouldBeNull();
        request.TaskType.ShouldBe("extraction");
        request.Target.ShouldBe(InferenceTarget.For("balanced", _policyId, 3));
        request.Chat.Messages.Select(m => m.Role).ShouldBe([MessageRole.System, MessageRole.User, MessageRole.Assistant]);
        request.Chat.Temperature.ShouldBe(0);
        request.Chat.TopP.ShouldBe(0.9);
        request.Chat.Seed.ShouldBe(7);
        request.Chat.MaxOutputTokens.ShouldBe(256);
        request.Chat.ResponseFormat.ShouldBeOfType<ResponseFormat.JsonObject>();
        request.OriginViolation().ShouldBeNull();
    }

    [Fact]
    public void A_pinned_provider_model_target_bypasses_routing()
    {
        var model = Guid.CreateVersion7();

        RunInference.Target(FrozenTarget.ForProviderModel(model, "p/m")).ShouldBe(InferenceTarget.Pinned(model));
    }

    [Fact]
    public void The_judge_call_goes_to_the_frozen_judge_alias_without_the_candidates_params()
    {
        var configuration = Configuration(FrozenTarget.ForAlias("balanced", _policyId, 3), new FrozenJudge("judge", _policyId, 5));

        var request = RunInference.Judge(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), configuration, [new PromptMessage("user", "x")]);

        request.Target.ShouldBe(InferenceTarget.For("judge", _policyId, 5));
        request.Origin.ShouldBe(RequestOrigin.Evaluation);
        request.Chat.Temperature.ShouldBeNull();
        request.Chat.ResponseFormat.ShouldBeOfType<ResponseFormat.Text>();
    }

    [Fact]
    public void A_json_schema_response_format_defaults_its_name_and_strictness()
    {
        var format = RunInference.ResponseFormatOf(Json("""{"type":"json_schema","schema":{"type":"object"}}""")).ShouldBeOfType<ResponseFormat.JsonSchema>();
        format.Name.ShouldBe(RunInference.DefaultSchemaName);
        format.Strict.ShouldBeFalse();

        var named = RunInference.ResponseFormatOf(Json("""{"type":"json_schema","schema":{},"name":"contact","strict":true}""")).ShouldBeOfType<ResponseFormat.JsonSchema>();
        named.Name.ShouldBe("contact");
        named.Strict.ShouldBeTrue();

        RunInference.ResponseFormatOf(null).ShouldBeOfType<ResponseFormat.Text>();
        RunInference.ResponseFormatOf(Json("""{"type":"text"}""")).ShouldBeOfType<ResponseFormat.Text>();
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    public void Csv_fields_are_quoted_only_when_rfc_4180_requires_it(string? value, string expected)
    {
        RunExporter.CsvField(value).ShouldBe(expected);
    }
}
