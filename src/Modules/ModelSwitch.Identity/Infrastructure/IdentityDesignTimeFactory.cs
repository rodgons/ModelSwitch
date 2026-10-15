using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ModelSwitch.Identity.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c> to scaffold migrations. No database is contacted.</summary>
internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", npgsql => npgsql.MigrationsHistoryTable(IdentityDbContext.MigrationsHistoryTable, IdentityDbContext.Schema))
            .Options;
        return new IdentityDbContext(options);
    }
}
