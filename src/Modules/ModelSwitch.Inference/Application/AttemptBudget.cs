using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Inference.Application;

/// <summary>
/// Tracks one request's Reservation while its Attempts run. Each Attempt commits its worst case; once the commitments pass what is
/// held, the ledger raises the hold by the difference before the Attempt starts (usage-and-budgets.md section 3). The hold normally
/// covers the first two Attempts, so most requests never top up.
/// </summary>
internal sealed class AttemptBudget(IUsageLedger ledger, Guid requestId, Money held)
{
    private Money _held = held;
    private Money _committed;

    /// <summary>The amount currently held, including top-ups.</summary>
    public Money Held => _held;

    /// <summary>Fails with <c>budget_exceeded</c> when the Budget cannot cover the Attempt.</summary>
    public async Task<Result> CommitAsync(decimal worstCase, CancellationToken cancellationToken)
    {
        var cost = Money.FromUsd(worstCase).Round();
        var committed = _committed + cost;
        if (committed > _held)
        {
            var topUp = await ledger.TopUpAsync(requestId, committed - _held, cancellationToken);
            if (topUp.IsFailure)
            {
                return topUp;
            }

            _held = committed;
        }

        _committed = committed;
        return Result.Success();
    }
}
