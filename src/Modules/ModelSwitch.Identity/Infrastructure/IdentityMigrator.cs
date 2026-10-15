using Microsoft.EntityFrameworkCore;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Identity.Infrastructure;

internal sealed class IdentityMigrator(IDbContextFactory<IdentityDbContext> contexts) : IModuleMigrator
{
    public string Schema => IdentityDbContext.Schema;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS identity", cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);
        return pending;
    }
}
