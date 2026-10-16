using ModelSwitch.Inference.Application;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.Usage.Contracts;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

public sealed class AttemptBudgetTests
{
    private sealed class Ledger : IUsageLedger
    {
        public List<Money> TopUps { get; } = [];

        public bool Refuse { get; set; }

        public Task<Result<OpenedRequest>> OpenAsync(OpenInferenceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SettleAsync(SettleInferenceRequest settlement, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result> TopUpAsync(Guid requestId, Money additional, CancellationToken cancellationToken)
        {
            if (Refuse)
            {
                return Task.FromResult<Result>(new Error(ErrorCode.BudgetExceeded, "no"));
            }

            TopUps.Add(additional);
            return Task.FromResult(Result.Success());
        }
    }

    [Fact]
    public async Task Attempts_that_fit_the_hold_cost_no_round_trip()
    {
        var ledger = new Ledger();
        var budget = new AttemptBudget(ledger, Guid.CreateVersion7(), Money.FromUsd(0.2m));

        (await budget.CommitAsync(0.1m, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        (await budget.CommitAsync(0.1m, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        ledger.TopUps.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_fallback_to_a_pricier_candidate_tops_up_only_the_excess()
    {
        var ledger = new Ledger();
        var budget = new AttemptBudget(ledger, Guid.CreateVersion7(), Money.FromUsd(0.2m));

        await budget.CommitAsync(0.1m, CancellationToken.None);
        await budget.CommitAsync(0.35m, CancellationToken.None);

        ledger.TopUps.Single().Amount.ShouldBe(0.25m);
        budget.Held.Amount.ShouldBe(0.45m);
    }

    [Fact]
    public async Task A_refused_top_up_does_not_count_the_attempt_as_committed()
    {
        var ledger = new Ledger { Refuse = true };
        var budget = new AttemptBudget(ledger, Guid.CreateVersion7(), Money.FromUsd(0.1m));

        var refused = await budget.CommitAsync(0.3m, CancellationToken.None);
        refused.Error.Code.ShouldBe(ErrorCode.BudgetExceeded);

        ledger.Refuse = false;
        (await budget.CommitAsync(0.05m, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        ledger.TopUps.ShouldBeEmpty("the cheaper attempt still fits the original hold");
    }
}
