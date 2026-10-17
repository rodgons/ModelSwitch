using ModelSwitch.Evaluation.Domain;
using Shouldly;

namespace ModelSwitch.UnitTests.Evaluation;

/// <summary>evaluation.md section 8: what an Operator may delete, and when.</summary>
public sealed class DeletionRulesTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(7, false)]
    public void A_dataset_or_prompt_can_be_deleted_only_once_no_comparison_uses_it(int comparisonsUsingIt, bool deletable)
        => DeletionRules.CanDeleteSource(comparisonsUsingIt).ShouldBe(deletable);

    [Theory]
    [InlineData("succeeded", true, true)]
    [InlineData("failed", true, true)]
    [InlineData("cancelled", true, true)]
    [InlineData("queued", false, false)]
    [InlineData("running", false, false)]
    [InlineData("cancelled", false, false)]
    public void A_run_lets_its_comparison_be_deleted_only_once_it_has_finished_and_stored_its_outcome(string status, bool hasCompletedAt, bool deletable)
        => DeletionRules.CanDeleteComparison([new RunState(status, hasCompletedAt)]).ShouldBe(deletable);

    [Fact]
    public void One_unfinished_run_blocks_deleting_its_comparison()
        => DeletionRules.CanDeleteComparison([new RunState("succeeded", true), new RunState("running", false)]).ShouldBeFalse();

    [Fact]
    public void A_comparison_without_runs_can_be_deleted()
        => DeletionRules.CanDeleteComparison([]).ShouldBeTrue();
}
