using ModelSwitch.Evaluation.Domain.Evaluators;
using Shouldly;
using static ModelSwitch.UnitTests.Evaluation.EvaluatorTestSupport;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class ExactMatchEvaluatorTests
{
    [Fact]
    public void Output_equal_to_expected_after_trimming_passes_with_score_one()
    {
        var score = Score("exact_match", "{}", "\"Paris\"", "  Paris\n");

        score.Status.ShouldBe(CaseStatus.Passed);
        score.Score.ShouldBe(1m);
        score.Predicted!.Value.GetString().ShouldBe("Paris");
    }

    [Fact]
    public void Case_differences_fail_unless_case_fold_is_on()
    {
        Score("exact_match", "{}", "\"Paris\"", "paris").Status.ShouldBe(CaseStatus.Failed);
        Score("exact_match", "{}", "\"Paris\"", "paris").Score.ShouldBe(0m);
        Score("exact_match", """{"caseFold":true}""", "\"Paris\"", "PARIS").Status.ShouldBe(CaseStatus.Passed);
    }

    [Fact]
    public void A_non_string_expected_value_is_compared_as_compact_json()
        => Score("exact_match", "{}", "{ \"a\" : 1 }", "{\"a\":1}").Status.ShouldBe(CaseStatus.Passed);
}

public sealed class JsonFieldsEvaluatorTests
{
    private const string _expected = """{"name":"Ana Silva","email":"ana@example.com"}""";

    [Fact]
    public void All_fields_matching_passes_with_score_one_and_predicted_is_the_parsed_object()
    {
        var score = Score("json_fields", "{}", _expected, """{"name":"Ana Silva","email":"ana@example.com","extra":1}""");

        score.Status.ShouldBe(CaseStatus.Passed);
        score.Score.ShouldBe(1m);
        score.Predicted!.Value.GetProperty("extra").GetInt32().ShouldBe(1);
        score.ScoreDetails.GetProperty("parseFailure").GetBoolean().ShouldBeFalse();
        score.ScoreDetails.GetProperty("fields").GetProperty("email").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void The_score_is_the_fraction_of_expected_fields_matched_and_the_default_pass_threshold_is_one()
    {
        var score = Score("json_fields", "{}", _expected, """{"name":"Ana Silva","email":"ANA@example.com"}""");

        score.Score.ShouldBe(0.5m);
        score.Status.ShouldBe(CaseStatus.Failed);
        score.ScoreDetails.GetProperty("fields").GetProperty("email").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void A_pass_threshold_below_one_passes_partial_matches()
        => Score("json_fields", """{"passThreshold":0.5}""", _expected, """{"name":"Ana Silva"}""").Status.ShouldBe(CaseStatus.Passed);

    [Fact]
    public void A_missing_field_does_not_match_unless_null_is_expected()
    {
        Score("json_fields", "{}", _expected, """{"name":"Ana Silva"}""").Score.ShouldBe(0.5m);
        Score("json_fields", "{}", """{"name":null,"email":"bob@example.org"}""", """{"email":"bob@example.org"}""").Score.ShouldBe(1m);
        Score("json_fields", "{}", """{"name":null}""", """{"name":null}""").Score.ShouldBe(1m);
        Score("json_fields", "{}", """{"name":null}""", """{"name":"Bob"}""").Score.ShouldBe(0m);
    }

    [Fact]
    public void Casefold_comparator_ignores_case_and_surrounding_whitespace()
    {
        const string config = """{"fields":{"email":{"comparator":"casefold"}}}""";

        Score("json_fields", config, _expected, """{"name":"Ana Silva","email":" ANA@Example.com "}""").Score.ShouldBe(1m);
        Score("json_fields", config, _expected, """{"name":"ana silva","email":"ana@example.com"}""").Score.ShouldBe(0.5m);
    }

    [Fact]
    public void The_default_comparator_applies_to_fields_without_their_own()
        => Score("json_fields", """{"defaultComparator":"casefold"}""", _expected, """{"name":"ANA SILVA","email":"ana@EXAMPLE.com"}""").Score.ShouldBe(1m);

    [Fact]
    public void Exact_comparator_compares_json_values_numbers_by_value_and_strings_ordinally()
    {
        Score("json_fields", "{}", """{"n":1.0,"o":{"a":[1,2]}}""", """{"n":1,"o":{"a":[1,2]}}""").Score.ShouldBe(1m);
        Score("json_fields", "{}", """{"o":{"a":[1,2]}}""", """{"o":{"a":[2,1]}}""").Score.ShouldBe(0m);
        Score("json_fields", "{}", """{"s":"a"}""", """{"s":"a "}""").Score.ShouldBe(0m);
        Score("json_fields", "{}", """{"n":1}""", """{"n":"1"}""").Score.ShouldBe(0m);
    }

    [Fact]
    public void Number_comparator_accepts_values_within_the_tolerance_and_numeric_strings()
    {
        const string config = """{"fields":{"total":{"comparator":"number","tolerance":0.01}}}""";

        Score("json_fields", config, """{"total":10.00}""", """{"total":10.005}""").Score.ShouldBe(1m);
        Score("json_fields", config, """{"total":10.00}""", """{"total":"10.01"}""").Score.ShouldBe(1m);
        Score("json_fields", config, """{"total":10.00}""", """{"total":10.02}""").Score.ShouldBe(0m);
        Score("json_fields", config, """{"total":10.00}""", """{"total":"ten"}""").Score.ShouldBe(0m);
    }

    [Fact]
    public void Number_comparator_tolerance_defaults_to_zero()
        => Score("json_fields", """{"fields":{"total":{"comparator":"number"}}}""", """{"total":3}""", """{"total":3.0001}""").Score.ShouldBe(0m);

    [Fact]
    public void Set_comparator_ignores_order_and_duplicates()
    {
        const string config = """{"fields":{"tags":{"comparator":"set"}}}""";

        Score("json_fields", config, """{"tags":["a","b"]}""", """{"tags":["b","a","a"]}""").Score.ShouldBe(1m);
        Score("json_fields", config, """{"tags":["a","b"]}""", """{"tags":["a"]}""").Score.ShouldBe(0m);
        Score("json_fields", config, """{"tags":["a"]}""", """{"tags":"a"}""").Score.ShouldBe(0m);
    }

    [Fact]
    public void Unparseable_output_fails_with_score_zero_and_flags_the_parse_failure()
    {
        var score = Score("json_fields", "{}", _expected, "Sure! The name is Ana.");

        score.Status.ShouldBe(CaseStatus.Failed);
        score.Score.ShouldBe(0m);
        score.Predicted.ShouldBeNull();
        score.ScoreDetails.GetProperty("parseFailure").GetBoolean().ShouldBeTrue();
        score.ScoreDetails.GetProperty("fields").GetProperty("name").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Output_that_is_json_but_not_an_object_is_a_parse_failure()
        => Score("json_fields", "{}", _expected, "[1,2]").ScoreDetails.GetProperty("parseFailure").GetBoolean().ShouldBeTrue();

    [Fact]
    public void Output_wrapped_in_a_markdown_json_fence_is_accepted()
    {
        Score("json_fields", "{}", _expected, "```json\n{\"name\":\"Ana Silva\",\"email\":\"ana@example.com\"}\n```").Score.ShouldBe(1m);
        Score("json_fields", "{}", _expected, "  ```\n{\"name\":\"Ana Silva\",\"email\":\"ana@example.com\"}```  ").Score.ShouldBe(1m);
    }

    [Fact]
    public void Json_with_prose_around_it_is_still_a_parse_failure()
        => Score("json_fields", "{}", _expected, "Here you go: {\"name\":\"Ana Silva\",\"email\":\"ana@example.com\"}")
            .ScoreDetails.GetProperty("parseFailure").GetBoolean().ShouldBeTrue();

    [Fact]
    public void An_expected_result_that_is_not_an_object_errors_the_case()
    {
        var score = Score("json_fields", "{}", "\"Ana\"", "{}");

        score.Status.ShouldBe(CaseStatus.Errored);
        score.ErrorCode.ShouldBe("invalid_request");
    }

    [Fact]
    public void An_empty_expected_object_scores_one()
        => Score("json_fields", "{}", "{}", "{}").Score.ShouldBe(1m);
}

public sealed class ClassificationEvaluatorTests
{
    private const string _config = """{"labels":["billing","bug","feature"]}""";

    [Fact]
    public void The_trimmed_output_matching_the_expected_label_passes()
    {
        var score = Score("classification", _config, "\"billing\"", " billing\n");

        score.Status.ShouldBe(CaseStatus.Passed);
        score.Score.ShouldBe(1m);
        score.Predicted!.Value.GetString().ShouldBe("billing");
    }

    [Fact]
    public void Another_label_fails_with_that_label_predicted()
    {
        var score = Score("classification", _config, "\"billing\"", "bug");

        score.Status.ShouldBe(CaseStatus.Failed);
        score.Score.ShouldBe(0m);
        score.Predicted!.Value.GetString().ShouldBe("bug");
        score.ScoreDetails.GetProperty("unparseable").GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData("Billing")]
    [InlineData("billing.")]
    [InlineData("The label is billing")]
    [InlineData("")]
    public void Output_matching_no_label_exactly_is_unparseable_and_failed(string output)
    {
        var score = Score("classification", _config, "\"billing\"", output);

        score.Status.ShouldBe(CaseStatus.Failed);
        score.Score.ShouldBe(0m);
        score.Predicted!.Value.GetString().ShouldBe("unparseable");
        score.ScoreDetails.GetProperty("unparseable").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void An_expected_label_that_is_not_a_string_errors_the_case()
        => Score("classification", _config, "{\"label\":\"billing\"}", "billing").Status.ShouldBe(CaseStatus.Errored);
}

public sealed class ReferenceSimilarityEvaluatorTests
{
    [Fact]
    public void The_score_is_the_squad_token_f1_against_the_reference()
    {
        var score = Score("reference_similarity", "{}", "\"Paris\"", "Paris, France capital");

        score.Score.ShouldBe(0.5m);
        score.ScoreDetails.GetProperty("f1").GetDecimal().ShouldBe(0.5m);
        score.Predicted.ShouldBeNull();
    }

    [Fact]
    public void The_default_pass_threshold_is_one_half()
    {
        Score("reference_similarity", "{}", "\"Paris\"", "Paris, France capital").Status.ShouldBe(CaseStatus.Passed);
        Score("reference_similarity", "{}", "\"Paris\"", "London").Status.ShouldBe(CaseStatus.Failed);
        Score("reference_similarity", """{"passThreshold":0.9}""", "\"Paris\"", "Paris, France capital").Status.ShouldBe(CaseStatus.Failed);
    }

    [Fact]
    public void Several_references_score_the_best_match_as_squad_does()
        => Score("reference_similarity", "{}", """["London","the Paris"]""", "Paris").Score.ShouldBe(1m);

    [Fact]
    public void A_reference_that_is_not_text_errors_the_case()
        => Score("reference_similarity", "{}", "{\"a\":1}", "Paris").Status.ShouldBe(CaseStatus.Errored);
}
