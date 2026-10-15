using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ModelSwitch.Registry.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c> to scaffold migrations. No database is contacted.</summary>
internal sealed class RegistryDesignTimeFactory : IDesignTimeDbContextFactory<RegistryDbContext>
{
    public RegistryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RegistryDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", npgsql => npgsql.MigrationsHistoryTable(RegistryDbContext.MigrationsHistoryTable, RegistryDbContext.Schema))
            .Options;
        return new RegistryDbContext(options);
    }
}
