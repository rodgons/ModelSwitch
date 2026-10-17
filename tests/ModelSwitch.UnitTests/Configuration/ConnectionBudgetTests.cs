using System.Globalization;
using System.Text.RegularExpressions;
using Shouldly;

namespace ModelSwitch.UnitTests.Configuration;

/// <summary>
/// Regression (benchmarks/2026-10-17-266c696.md, finding 2): the api's pool (Npgsql's default of 100) was not below PostgreSQL's
/// max_connections (also 100), so under load PostgreSQL refused clients and requests failed with 500. The reference configuration
/// must keep every pool the processes can open below max_connections (docs/deployment.md section 3, "Connection budget").
/// </summary>
public sealed partial class ConnectionBudgetTests
{
    private const int _superuserReserved = 3;

    [Fact]
    public void The_pools_of_the_reference_deployment_stay_under_max_connections()
    {
        var env = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".env.example"))
            .Where(line => line.Contains('=', StringComparison.Ordinal) && !line.StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1]);

        var app = PoolSize(env["MODELSWITCH__ConnectionStrings__Postgres"]);
        var migrator = PoolSize(env["MODELSWITCH__ConnectionStrings__PostgresMigrator"]);
        var retention = PoolSize(env["MODELSWITCH__ConnectionStrings__PostgresRetention"]);
        var maxConnections = int.Parse(env["POSTGRES_MAX_CONNECTIONS"], CultureInfo.InvariantCulture);

        // api: EF Core pool + job enqueuer pool + LISTEN; worker: EF Core pool + jobs pool + retention pool + LISTEN.
        var api = (2 * app) + 1;
        var worker = (2 * app) + retention + 1;
        (api + worker + migrator + _superuserReserved).ShouldBeLessThan(maxConnections);
        File.ReadAllText(Path.Combine(RepositoryRoot(), "deploy", "docker-compose.yml"))
            .ShouldContain("max_connections=${POSTGRES_MAX_CONNECTIONS:-" + env["POSTGRES_MAX_CONNECTIONS"] + "}");
    }

    private static int PoolSize(string connectionString)
    {
        var match = MaximumPoolSize().Match(connectionString);
        match.Success.ShouldBeTrue($"every connection string sets Maximum Pool Size: {connectionString.Split(';')[0]}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"Maximum Pool Size=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MaximumPoolSize();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ModelSwitch.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
