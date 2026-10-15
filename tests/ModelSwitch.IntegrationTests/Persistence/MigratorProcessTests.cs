using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

/// <summary>Runs the real Migrator executable: command parsing, exit code 78 and what reaches stdout (deployment.md section 4).</summary>
public sealed partial class MigratorProcessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Missing_connection_string_exits_78_naming_the_variable()
    {
        var run = await RunAsync(["migrate"], []);

        run.ExitCode.ShouldBe(78);
        run.Stderr.ShouldContain("MODELSWITCH__ConnectionStrings__PostgresMigrator");
        run.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task Seed_without_a_pepper_or_tenant_exits_78_naming_both_variables()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        var run = await RunAsync(["seed"], Env(connectionString, pepper: null));

        run.ExitCode.ShouldBe(78);
        run.Stderr.ShouldContain("MODELSWITCH__Security__ApiKeyPepper");
        run.Stderr.ShouldContain("MODELSWITCH__Seed__TenantName");
    }

    [Fact]
    public async Task Seed_with_a_short_pepper_exits_78()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        var run = await RunAsync(["seed", "--tenant", "acme"], Env(connectionString, pepper: Convert.ToBase64String(new byte[8])));

        run.ExitCode.ShouldBe(78);
        run.Stderr.ShouldContain("MODELSWITCH__Security__ApiKeyPepper");
    }

    [Fact]
    public async Task Migrate_is_idempotent_and_exits_0()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var env = Env(connectionString, pepper: null);

        var first = await RunAsync([], env);
        var second = await RunAsync(["migrate"], env);

        first.ExitCode.ShouldBe(0);
        first.Stdout.ShouldBeEmpty();
        second.ExitCode.ShouldBe(0);
        second.Stderr.ShouldContain("applied 0 migration(s)");
    }

    [Fact]
    public async Task Seed_prints_the_plaintext_key_exactly_once_on_stdout_and_nowhere_else()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        var run = await RunAsync(["seed", "--tenant", "acme", "--key-name", "ci"], Env(connectionString, ModuleServices.Pepper));

        run.ExitCode.ShouldBe(0);
        var key = run.Stdout.Trim();
        KeyPattern().IsMatch(key).ShouldBeTrue($"stdout was: {run.Stdout}");
        run.Stdout.Trim().Split('\n').Length.ShouldBe(1);
        run.Stderr.ShouldNotContain(key);
        run.Stderr.ShouldContain(key.Substring(4, 8), Case.Sensitive);
    }

    [Fact]
    public async Task Seed_reads_the_tenant_name_from_the_environment()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var env = Env(connectionString, ModuleServices.Pepper);
        env["MODELSWITCH__Seed__TenantName"] = "from-env";

        var run = await RunAsync(["seed"], env);

        run.ExitCode.ShouldBe(0);
        KeyPattern().IsMatch(run.Stdout.Trim()).ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_command_exits_64()
    {
        var run = await RunAsync(["explode"], []);

        run.ExitCode.ShouldBe(64);
    }

    private static Dictionary<string, string> Env(string connectionString, string? pepper)
    {
        var env = new Dictionary<string, string> { ["MODELSWITCH__ConnectionStrings__PostgresMigrator"] = connectionString };
        if (pepper is not null)
        {
            env["MODELSWITCH__Security__ApiKeyPepper"] = pepper;
        }

        return env;
    }

    private static async Task<ProcessResult> RunAsync(string[] arguments, Dictionary<string, string> environment)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "ModelSwitch.Migrator.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MODELSWITCH__", StringComparison.Ordinal)).ToList())
        {
            start.Environment.Remove(key);
        }

        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    [GeneratedRegex("^msk_[0-9a-hjkmnp-tv-z]{8}_[A-Za-z0-9_-]{43}$")]
    private static partial Regex KeyPattern();

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
