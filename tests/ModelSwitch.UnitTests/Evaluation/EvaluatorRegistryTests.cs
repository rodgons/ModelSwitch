using ModelSwitch.Evaluation.Domain.Evaluators;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;
using static ModelSwitch.UnitTests.Evaluation.EvaluatorTestSupport;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class EvaluatorRegistryTests
{
    [Theory]
    [InlineData("exact_match", "{}", false)]
    [InlineData("json_fields", "{}", false)]
    [InlineData("classification", """{"labels":["a"]}""", false)]
    [InlineData("reference_similarity", "{}", false)]
    [InlineData("llm_judge", """{"judgeAlias":"judge","rubric":"Is it correct?"}""", true)]
    public void Every_kind_is_created_at_version_1(string kind, string config, bool requiresJudge)
    {
        var evaluator = Create(kind, config);

        evaluator.Kind.ShouldBe(kind);
        evaluator.Version.ShouldBe("1");
        evaluator.RequiresJudge.ShouldBe(requiresJudge);
        (evaluator is IJudgedEvaluator).ShouldBe(requiresJudge);
        (evaluator is ILocalEvaluator).ShouldBe(!requiresJudge);
    }

    [Fact]
    public void An_unknown_kind_is_a_validation_error_on_kind()
    {
        var result = EvaluatorRegistry.Create("bleu", "1", Json("{}"));

        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        result.Error.FieldErrors!.Keys.ShouldBe(["kind"]);
    }

    [Fact]
    public void An_unknown_version_is_a_validation_error_on_version()
    {
        var result = EvaluatorRegistry.Create("exact_match", "2", Json("{}"));

        result.Error.FieldErrors!.Keys.ShouldBe(["version"]);
    }

    [Fact]
    public void A_missing_config_is_an_empty_config()
        => EvaluatorRegistry.Create("exact_match", "1", null).IsSuccess.ShouldBeTrue();

    [Fact]
    public void An_unknown_config_property_is_a_validation_error_so_typos_are_caught()
    {
        var result = EvaluatorRegistry.Create("exact_match", "1", Json("""{"casefold":true}"""));

        result.Error.FieldErrors!.Keys.ShouldBe(["config.casefold"]);
    }

    [Fact]
    public void A_config_that_is_not_an_object_is_a_validation_error()
        => EvaluatorRegistry.Create("exact_match", "1", Json("[]")).Error.FieldErrors!.Keys.ShouldBe(["config"]);

    [Theory]
    [InlineData("exact_match", """{"caseFold":"yes"}""", "config.caseFold")]
    [InlineData("json_fields", """{"defaultComparator":"fuzzy"}""", "config.defaultComparator")]
    [InlineData("json_fields", """{"passThreshold":1.5}""", "config.passThreshold")]
    [InlineData("json_fields", """{"fields":{"age":{"comparator":"number","tolerance":-1}}}""", "config.fields.age.tolerance")]
    [InlineData("json_fields", """{"fields":{"name":{"comparator":"exact","tolerance":1}}}""", "config.fields.name.tolerance")]
    [InlineData("json_fields", """{"fields":{"name":"exact"}}""", "config.fields.name")]
    [InlineData("json_fields", """{"fields":{"name":{"comparator":"exact","x":1}}}""", "config.fields.name.x")]
    [InlineData("classification", "{}", "config.labels")]
    [InlineData("classification", """{"labels":[]}""", "config.labels")]
    [InlineData("classification", """{"labels":["a","a"]}""", "config.labels[1]")]
    [InlineData("classification", """{"labels":["unparseable"]}""", "config.labels[0]")]
    [InlineData("classification", """{"labels":[" a"]}""", "config.labels[0]")]
    [InlineData("classification", """{"labels":[1]}""", "config.labels[0]")]
    [InlineData("reference_similarity", """{"passThreshold":-0.1}""", "config.passThreshold")]
    [InlineData("llm_judge", """{"rubric":"r"}""", "config.judgeAlias")]
    [InlineData("llm_judge", """{"judgeAlias":"j"}""", "config.rubric")]
    [InlineData("llm_judge", """{"judgeAlias":"j","rubric":" "}""", "config.rubric")]
    public void Invalid_config_is_a_validation_error_keyed_by_path(string kind, string config, string path)
    {
        var result = EvaluatorRegistry.Create(kind, "1", Json(config));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCode.ValidationFailed);
        result.Error.FieldErrors!.Keys.ShouldContain(path);
    }

    [Fact]
    public void Every_evaluator_kind_has_a_scoring_test_class()
    {
        // testing.md section 6: kind "reference_similarity" needs ReferenceSimilarityEvaluatorTests, and so on.
        var testClasses = typeof(EvaluatorRegistryTests).Assembly.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var missing = EvaluatorRegistry.Kinds
            .Select(kind => string.Concat(kind.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..])) + "EvaluatorTests")
            .Where(name => !testClasses.Contains(name));

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Every_evaluator_kind_is_registered()
        => EvaluatorRegistry.Kinds.ShouldBe(["classification", "exact_match", "json_fields", "llm_judge", "reference_similarity"], ignoreOrder: true);
}
