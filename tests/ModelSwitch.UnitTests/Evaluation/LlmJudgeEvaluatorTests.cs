using ModelSwitch.Evaluation.Domain.Evaluators;
using Shouldly;
using static ModelSwitch.UnitTests.Evaluation.EvaluatorTestSupport;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class LlmJudgeEvaluatorTests
{
    private const string _config = """{"judgeAlias":"judge","rubric":"Score 1 if the answer names the capital correctly."}""";

    private static IJudgedEvaluator Judge(string config = _config) => (IJudgedEvaluator)Create("llm_judge", config);

    private static CaseContext Context(string expected = "\"Paris\"")
        => new(Json("""{"question":"Capital of France?"}"""), Json(expected), "It is Paris.");

    [Fact]
    public void The_judge_alias_comes_from_the_config()
        => Judge().JudgeAlias.ShouldBe("judge");

    [Fact]
    public void Judge_messages_carry_the_reply_format_rubric_input_expected_and_candidate_output()
    {
        var messages = Judge().BuildJudgeMessages(Context());

        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe("system");
        messages[0].Content.ShouldContain("\"score\"");
        messages[1].Role.ShouldBe("user");
        messages[1].Content.ShouldContain("Score 1 if the answer names the capital correctly.");
        messages[1].Content.ShouldContain("""{"question":"Capital of France?"}""");
        messages[1].Content.ShouldContain("Paris");
        messages[1].Content.ShouldContain("It is Paris.");
    }

    [Fact]
    public void Judge_messages_omit_the_expected_section_when_the_case_has_none()
    {
        var messages = Judge().BuildJudgeMessages(Context("null"));

        messages[1].Content.ShouldNotContain("Expected result");
    }

    [Fact]
    public void A_well_formed_reply_is_the_score_and_the_reason_is_kept_in_the_details()
    {
        var score = Judge().ScoreJudgeReply(Context(), """{"score": 0.8, "reason": "Correct but verbose."}""");

        score.Status.ShouldBe(CaseStatus.Passed);
        score.Score.ShouldBe(0.8m);
        score.ScoreDetails.GetProperty("reason").GetString().ShouldBe("Correct but verbose.");
        score.ScoreDetails.GetProperty("modelJudged").GetBoolean().ShouldBeTrue();
        score.ScoreDetails.GetProperty("judgeAlias").GetString().ShouldBe("judge");
    }

    [Fact]
    public void The_default_pass_threshold_is_one_half_and_is_configurable()
    {
        Judge().ScoreJudgeReply(Context(), """{"score":0.5}""").Status.ShouldBe(CaseStatus.Passed);
        Judge().ScoreJudgeReply(Context(), """{"score":0.49}""").Status.ShouldBe(CaseStatus.Failed);
        Judge("""{"judgeAlias":"j","rubric":"r","passThreshold":0.9}""").ScoreJudgeReply(Context(), """{"score":0.8}""").Status.ShouldBe(CaseStatus.Failed);
    }

    [Fact]
    public void A_reply_in_a_json_fence_is_accepted()
        => Judge().ScoreJudgeReply(Context(), "```json\n{\"score\":1,\"reason\":\"ok\"}\n```").Score.ShouldBe(1m);

    [Theory]
    [InlineData("I'd give it 0.8")]
    [InlineData("""{"reason":"no score"}""")]
    [InlineData("""{"score":"0.8"}""")]
    [InlineData("""{"score":1.2}""")]
    [InlineData("""{"score":-0.1}""")]
    [InlineData("""{"score":0.5,"reason":3}""")]
    [InlineData("[0.5]")]
    public void An_unparseable_judge_reply_errors_the_case_with_no_score(string reply)
    {
        var score = Judge().ScoreJudgeReply(Context(), reply);

        score.Status.ShouldBe(CaseStatus.Errored);
        score.Score.ShouldBeNull();
        score.ErrorCode.ShouldBe("provider_error");
        score.ScoreDetails.GetProperty("modelJudged").GetBoolean().ShouldBeTrue();
    }
}
