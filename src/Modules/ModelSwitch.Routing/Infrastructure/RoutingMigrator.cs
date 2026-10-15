using Microsoft.EntityFrameworkCore;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Routing.Infrastructure;

internal sealed class RoutingMigrator(IDbContextFactory<RoutingDbContext> contexts) : IModuleMigrator
{
    public string Schema => RoutingDbContext.Schema;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS routing", cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);
        return pending;
    }
}
