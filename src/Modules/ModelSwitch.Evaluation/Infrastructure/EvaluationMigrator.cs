using Microsoft.EntityFrameworkCore;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Evaluation.Infrastructure;

internal sealed class EvaluationMigrator(IDbContextFactory<EvaluationDbContext> contexts) : IModuleMigrator
{
    public string Schema => EvaluationDbContext.Schema;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS evaluation", cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);
        return pending;
    }
}
