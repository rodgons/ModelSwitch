using ModelSwitch.Evaluation.Domain.Evaluators;
using Shouldly;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class CaseScoreTests
{
    [Fact]
    public void A_score_is_rounded_to_the_five_decimals_case_result_stores()
        => CaseScore.Scored(2m / 3m, passed: false, null, new { }).Score.ShouldBe(0.66667m);

    [Fact]
    public void A_score_outside_zero_to_one_is_a_bug()
        => Should.Throw<ArgumentOutOfRangeException>(() => CaseScore.Scored(1.5m, passed: true, null, new { }));

    [Fact]
    public void An_errored_case_has_no_score_and_carries_its_error_code()
    {
        var score = CaseScore.Errored(new CaseError("provider_error", "x"));

        score.Status.ShouldBe(CaseStatus.Errored);
        score.Score.ShouldBeNull();
        score.ErrorCode.ShouldBe("provider_error");
    }

    [Fact]
    public void Statuses_have_the_lowercase_wire_names_of_case_result_status()
    {
        CaseStatus.Passed.ToWire().ShouldBe("passed");
        CaseStatus.Failed.ToWire().ShouldBe("failed");
        CaseStatus.Errored.ToWire().ShouldBe("errored");
        CaseStatus.Skipped.ToWire().ShouldBe("skipped");
        CaseStatusExtensions.Parse("skipped").ShouldBe(CaseStatus.Skipped);
    }

    [Fact]
    public void Score_details_are_camel_case_json()
        => CaseScore.Scored(1m, passed: true, null, new { ParseFailure = false }).ScoreDetails.GetRawText().ShouldBe("""{"parseFailure":false}""");
}
