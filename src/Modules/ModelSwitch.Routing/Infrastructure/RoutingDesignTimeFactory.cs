using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ModelSwitch.Routing.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c> to scaffold migrations. No database is contacted.</summary>
internal sealed class RoutingDesignTimeFactory : IDesignTimeDbContextFactory<RoutingDbContext>
{
    public RoutingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RoutingDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", npgsql => npgsql.MigrationsHistoryTable(RoutingDbContext.MigrationsHistoryTable, RoutingDbContext.Schema))
            .Options;
        return new RoutingDbContext(options);
    }
}
