using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelSwitch.Identity.Contracts;
using ModelSwitch.SharedKernel.Hosting;
using ModelSwitch.SharedKernel.Persistence;

namespace ModelSwitch.Migrator;

internal sealed partial class MigratorRunner(
    IEnumerable<IModuleMigrator> migrators,
    IServiceProvider services,
    IConfiguration configuration,
    DatabaseRoles roles,
    ILogger<MigratorRunner> logger)
{
    /// <summary>Applies every module's migrations in registration order. Idempotent: a second run applies nothing.</summary>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken)
    {
        var schemas = new List<string>();
        foreach (var migrator in migrators)
        {
            var applied = await migrator.MigrateAsync(cancellationToken);
            LogModuleMigrated(logger, migrator.Schema, applied.Count);
            schemas.Add(migrator.Schema);
        }

        // After every module's tables exist: roles and grants (data-model.md "Database roles").
        await roles.ApplyAsync(
            configuration["ConnectionStrings:PostgresMigrator"]!,
            schemas,
            configuration["POSTGRES_APP_PASSWORD"],
            configuration["POSTGRES_RETENTION_PASSWORD"],
            cancellationToken);

        return await BootstrapOwnerAsync(cancellationToken);
    }

    // The first Owner exists only if no Administrator does (security.md section 3). The password is never logged.
    private async Task<int> BootstrapOwnerAsync(CancellationToken cancellationToken)
    {
        var bootstrapper = (IOwnerBootstrapper?)services.GetService(typeof(IOwnerBootstrapper));
        if (bootstrapper is null)
        {
            return 0;
        }

        var email = configuration["Bootstrap:OwnerEmail"];
        var result = await bootstrapper.BootstrapAsync(email, configuration["Bootstrap:OwnerPassword"], cancellationToken);
        if (result.IsFailure)
        {
            foreach (var (setting, messages) in result.Error.FieldErrors ?? new Dictionary<string, string[]>())
            {
                var problem = string.Join(' ', messages);
                LogOwnerSettingInvalid(logger, setting, problem);
            }

            return StartupExit.ConfigurationError;
        }

        switch (result.Value)
        {
            case BootstrapOutcome.Created:
                var created = email!.Trim();
                LogOwnerCreated(logger, created);
                break;
            case BootstrapOutcome.NotConfigured:
                LogOwnerNotConfigured(logger);
                break;
            default:
                LogOwnerExists(logger);
                break;
        }

        return 0;
    }

    /// <summary>
    /// Migrates, then creates (or finds) the Tenant and a new API Key. The plaintext key is the only thing written
    /// to <paramref name="stdout"/>, once, on its own line. Everything else goes to the log (stderr).
    /// </summary>
    public async Task<int> SeedAsync(TextWriter stdout, CancellationToken cancellationToken)
    {
        await MigrateAsync(cancellationToken);

        var seeder = (IIdentitySeeder)services.GetService(typeof(IIdentitySeeder))!;
        var seeded = await seeder.SeedAsync(
            configuration["Seed:TenantName"]!.Trim(),
            (configuration["Seed:KeyName"] ?? "seed").Trim(),
            cancellationToken);

        LogSeeded(logger, seeded.TenantId, seeded.TenantCreated, seeded.KeyPrefix);
        await stdout.WriteLineAsync(seeded.PlaintextKey);
        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Schema {Schema}: applied {Count} migration(s).")]
    private static partial void LogModuleMigrated(ILogger logger, string schema, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "No Administrator existed, so the Owner {Email} was created from the bootstrap settings. The bootstrap variables can be removed now.")]
    private static partial void LogOwnerCreated(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Information, Message = "An Administrator already exists: the Owner bootstrap settings were ignored.")]
    private static partial void LogOwnerExists(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No Administrator exists and MODELSWITCH__Bootstrap__OwnerEmail / OwnerPassword are not set, so nobody can sign in to the dashboard yet.")]
    private static partial void LogOwnerNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "configuration error: {Setting}: {Problem}")]
    private static partial void LogOwnerSettingInvalid(ILogger logger, string setting, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded API key {KeyPrefix} for tenant {TenantId} (tenant created: {TenantCreated}). The key is printed once on stdout and cannot be shown again.")]
    private static partial void LogSeeded(ILogger logger, Guid tenantId, bool tenantCreated, string keyPrefix);
}
