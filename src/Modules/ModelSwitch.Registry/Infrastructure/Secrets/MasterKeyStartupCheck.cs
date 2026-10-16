using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelSwitch.SharedKernel.Hosting;

namespace ModelSwitch.Registry.Infrastructure.Secrets;

/// <summary>
/// Startup validation of the master key (FR-10, ADR-0007). A key that is set must be well formed. A key that is unset is
/// an error only when some Provider references a <c>db:</c> secret. Failures are <see cref="OptionsValidationException"/>,
/// which the hosts map to exit code 78 naming the environment variable.
/// </summary>
internal sealed partial class MasterKeyStartupCheck(
    IConfiguration configuration,
    MasterKeyRing ring,
    IDbContextFactory<RegistryDbContext> contexts,
    ILogger<MasterKeyStartupCheck> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var problems = MasterKeyRing.From(configuration).Problems;
        if (problems.Count > 0)
        {
            throw Failure(problems.ToArray());
        }

        if (ring.HasKey)
        {
            return;
        }

        bool referenced;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            referenced = await db.Providers.AnyAsync(p => p.CredentialReference != null && p.CredentialReference.StartsWith("db:"), cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            // The database is not reachable or not migrated yet. Other startup checks own that failure; a db: credential
            // would still fail closed at resolve time.
            LogSkipped(logger);
            return;
        }

        if (referenced)
        {
            throw Failure(
                $"{RequiredSetting.EnvironmentVariableFor(MasterKeyRing.KeySetting)} is required but not set: a Provider references a db: credential.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static OptionsValidationException Failure(params string[] failures)
        => new(nameof(MasterKeyRing), typeof(MasterKeyRing), failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Master key check skipped: the database could not be queried")]
    private static partial void LogSkipped(ILogger logger);
}
