using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ModelSwitch.Evaluation.Infrastructure;

/// <summary>Used only by <c>dotnet ef</c> to scaffold migrations. No database is contacted.</summary>
internal sealed class EvaluationDesignTimeFactory : IDesignTimeDbContextFactory<EvaluationDbContext>
{
    public EvaluationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EvaluationDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time", npgsql => npgsql.MigrationsHistoryTable(EvaluationDbContext.MigrationsHistoryTable, EvaluationDbContext.Schema))
            .Options;
        return new EvaluationDbContext(options);
    }
}
