using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ModelSwitch.Usage.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c> to scaffold migrations. No database is contacted.</summary>
internal sealed class UsageDesignTimeFactory : IDesignTimeDbContextFactory<UsageDbContext>
{
    public UsageDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<UsageDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", npgsql => npgsql.MigrationsHistoryTable(UsageDbContext.MigrationsHistoryTable, UsageDbContext.Schema))
            .Options;
        return new UsageDbContext(options);
    }
}
