using ModelSwitch.Evaluation.Domain.Runs;
using Shouldly;

namespace ModelSwitch.UnitTests.Evaluation;

public sealed class RunExecutionRulesTests
{
    [Theory]
    [InlineData(null, 100.0, 50.0, 50.0, true)]
    [InlineData(1.0, 0.5, 0.2, 0.3, true)]
    [InlineData(1.0, 0.5, 0.2, 0.31, false)]
    [InlineData(1.0, 0.9, 0.0, 0.2, false)]
    [InlineData(0.0, 0.0, 0.0, 0.0, true)]
    public void A_case_starts_only_while_spent_plus_in_flight_plus_its_worst_case_stays_within_the_cap(
        double? cap, double spent, double inFlight, double worstCase, bool starts)
    {
        RunCostCap.AllowsNextCase((decimal?)cap, (decimal)spent, (decimal)inFlight, (decimal)worstCase).ShouldBe(starts);
    }

    [Fact]
    public void Resuming_skips_cases_that_already_have_a_result_and_keeps_upload_order()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();

        var pending = RunResume.Pending(ids, new HashSet<Guid> { ids[1], ids[3] });

        pending.ShouldBe([ids[0], ids[2], ids[4]]);
    }

    [Theory]
    [InlineData("queued", nameof(RunStart.Start))]
    [InlineData("running", nameof(RunStart.Resume))]
    [InlineData("succeeded", nameof(RunStart.Skip))]
    [InlineData("failed", nameof(RunStart.Skip))]
    [InlineData("cancelled", nameof(RunStart.Skip))]
    public void A_claimed_job_starts_a_queued_run_resumes_a_running_one_and_skips_a_finished_one(string status, string expected)
    {
        RunStatusRules.OnClaim(status).ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData("queued", true)]
    [InlineData("running", true)]
    [InlineData("succeeded", false)]
    [InlineData("failed", false)]
    [InlineData("cancelled", false)]
    public void Only_a_queued_or_running_run_can_be_cancelled(string status, bool cancellable)
    {
        RunStatusRules.CanCancel(status).ShouldBe(cancellable);
        RunStatusRules.IsTerminal(status).ShouldBe(!cancellable);
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("cancelled", false)]
    [InlineData("succeeded", false)]
    public void The_worker_starts_another_case_only_while_the_run_is_still_running(string status, bool next)
    {
        RunStatusRules.MayStartCase(status).ShouldBe(next);
    }

    [Fact]
    public void The_run_outcome_is_failed_on_the_cost_cap_cancelled_on_cancellation_and_succeeded_otherwise()
    {
        RunStatusRules.Outcome(costCapReached: true, cancelled: false).ShouldBe(("failed", "run_cost_cap_reached"));
        RunStatusRules.Outcome(costCapReached: false, cancelled: true).ShouldBe(("cancelled", (string?)null));
        RunStatusRules.Outcome(costCapReached: false, cancelled: false).ShouldBe(("succeeded", (string?)null));
    }

    [Theory]
    [InlineData("passed", false)]
    [InlineData("failed", true)]
    [InlineData("errored", true)]
    [InlineData("skipped", false)]
    public void Failed_and_errored_case_results_count_toward_cases_failed(string status, bool counts)
    {
        RunStatusRules.CountsAsFailed(status).ShouldBe(counts);
    }

    [Fact]
    public void Variance_is_reported_only_when_the_same_configuration_has_run_more_than_once()
    {
        RunVariance.Of([0.8m]).ShouldBeNull();
        RunVariance.Of([0.8m, null]).ShouldBeNull();

        var variance = RunVariance.Of([0.8m, 0.6m, 0.7m, null]).ShouldNotBeNull();

        variance.Runs.ShouldBe(3);
        variance.Min.ShouldBe(0.6m);
        variance.Max.ShouldBe(0.8m);
        variance.Mean.ShouldBe(0.7m);
        variance.StdDev.ShouldBe(0.1m, "sample standard deviation of 0.6, 0.7, 0.8");
    }
}
