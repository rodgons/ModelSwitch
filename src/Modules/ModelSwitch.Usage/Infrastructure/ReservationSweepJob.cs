using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.Usage.Contracts;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>
/// The recurring worker job that recovers abandoned Reservations (usage-and-budgets.md section 3). It drains the backlog in batches,
/// so a long outage is cleared in one run. Sweeping is idempotent, so a lease handed to another worker mid-run is harmless.
/// </summary>
internal sealed class ReservationSweepJob(IReservationSweeper sweeper) : IJobHandler
{
    public const string JobKind = "usage.sweep_reservations";

    /// <summary>The cadence the docs promise: abandoned Reservations are settled within about 30 s of expiring.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private const int _batchSize = 100;

    public string Kind => JobKind;

    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        int swept;
        do
        {
            swept = await sweeper.SweepAbandonedAsync(_batchSize, cancellationToken);
        }
        while (swept == _batchSize);
    }
}
