using Microsoft.EntityFrameworkCore;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Usage.Infrastructure;

internal sealed class UsageMigrator(IDbContextFactory<UsageDbContext> contexts) : IModuleMigrator
{
    /// <summary>Months of partitions kept ahead of the current one. The Migrator runs on every deploy and extends this window.</summary>
    public const int PartitionMonthsAhead = 12;

    public string Schema => UsageDbContext.Schema;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS usage", cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);

        // Every run, not only when a migration is pending: this is what keeps future months' partitions in existence
        // until the Phase 4 retention job takes over partition management (data-model.md, "Partitioning").
        await db.Database.ExecuteSqlRawAsync(
            "SELECT usage.ensure_request_partitions(1, {0})",
            [PartitionMonthsAhead],
            cancellationToken);
        return pending;
    }
}
