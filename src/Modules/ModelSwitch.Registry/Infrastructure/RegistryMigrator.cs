using Microsoft.EntityFrameworkCore;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Registry.Infrastructure;

internal sealed class RegistryMigrator(IDbContextFactory<RegistryDbContext> contexts) : IModuleMigrator
{
    public string Schema => RegistryDbContext.Schema;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS registry", cancellationToken);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);
        return pending;
    }
}
