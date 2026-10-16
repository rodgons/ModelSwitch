using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.SharedKernel.Configuration;
using ModelSwitch.SharedKernel.Errors;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

public sealed class ProviderSecretTests(PostgresFixture postgres)
{
    private const string _providerId = "0192f0c8-0000-7000-8000-0000000000a1";
    private const string _canary = "sk-CANARY-7f3a9c0d51e2";

    private static readonly string _keyOne = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string _keyTwo = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)(i * 3)).ToArray());

    private sealed class CaptureProvider : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception) + exception);

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Stored_secret_resolves_through_the_credential_resolver_and_is_encrypted_at_rest()
    {
        var connectionString = await SeedAsync();
        var capture = new CaptureProvider();
        await using var services = ModuleServices.ForRegistry(connectionString, _keyOne, b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        var store = services.GetRequiredService<IProviderSecretStore>();
        var versionBefore = await ReadVersionAsync(connectionString);

        var status = await store.SetAsync(Guid.Parse(_providerId), _canary, TestContext.Current.CancellationToken);

        status.IsSuccess.ShouldBeTrue();
        status.Value.Kind.ShouldBe("db");
        status.Value.Configured.ShouldBeTrue();
        (await ReadVersionAsync(connectionString)).ShouldBe(versionBefore + 1);

        var reference = await ReferenceAsync(connectionString);
        reference.ShouldStartWith("db:");
        var resolved = await services.GetRequiredService<ICredentialResolver>().ResolveAsync(reference, TestContext.Current.CancellationToken);
        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value!.Reveal().ShouldBe(_canary);

        var ciphertext = (byte[])(await ScalarAsync(connectionString, "select ciphertext from registry.provider_secret"))!;
        Encoding.UTF8.GetString(ciphertext).ShouldNotContain(_canary);
        ciphertext.ShouldNotBe(Encoding.UTF8.GetBytes(_canary));
        capture.Lines.ShouldNotBeEmpty();
        capture.Lines.ShouldAllBe(l => !l.Contains(_canary, StringComparison.Ordinal));
        resolved.Value.ToString().ShouldNotContain(_canary);
    }

    [Fact]
    public async Task Replacing_a_secret_leaves_one_row_and_clearing_removes_it()
    {
        var connectionString = await SeedAsync();
        await using var services = ModuleServices.ForRegistry(connectionString, _keyOne);
        var store = services.GetRequiredService<IProviderSecretStore>();
        var id = Guid.Parse(_providerId);

        (await store.SetAsync(id, "first", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await store.SetAsync(id, "second", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await ScalarAsync(connectionString, "select count(*) from registry.provider_secret")).ShouldBe(1L);
        var reference = await ReferenceAsync(connectionString);
        (await services.GetRequiredService<ICredentialResolver>().ResolveAsync(reference, TestContext.Current.CancellationToken)).Value!.Reveal().ShouldBe("second");

        var cleared = await store.ClearAsync(id, TestContext.Current.CancellationToken);

        cleared.Value.ShouldBe(new ProviderSecretStatus("none", false, null));
        (await ScalarAsync(connectionString, "select count(*) from registry.provider_secret")).ShouldBe(0L);
        (await store.GetStatusAsync(id, TestContext.Current.CancellationToken)).Value.Configured.ShouldBeFalse();
    }

    [Fact]
    public async Task Unknown_provider_and_empty_secret_are_typed_errors_and_missing_key_is_refused()
    {
        var connectionString = await SeedAsync();
        await using var services = ModuleServices.ForRegistry(connectionString, _keyOne);
        var store = services.GetRequiredService<IProviderSecretStore>();

        (await store.SetAsync(Guid.CreateVersion7(), "x", TestContext.Current.CancellationToken)).Error.Code.ShouldBe(ErrorCode.NotFound);
        (await store.SetAsync(Guid.Parse(_providerId), " ", TestContext.Current.CancellationToken)).Error.Code.ShouldBe(ErrorCode.ValidationFailed);

        await using var keyless = ModuleServices.ForRegistry(connectionString, masterKey: null);
        (await keyless.GetRequiredService<IProviderSecretStore>().SetAsync(Guid.Parse(_providerId), "x", TestContext.Current.CancellationToken)).Error.Code.ShouldBe(ErrorCode.InternalError);
    }

    [Fact]
    public async Task Master_key_rotation_keeps_secrets_readable_and_reencrypts_them()
    {
        var connectionString = await SeedAsync();
        await using (var before = ModuleServices.ForRegistry(connectionString, _keyOne))
        {
            (await before.GetRequiredService<IProviderSecretStore>().SetAsync(Guid.Parse(_providerId), _canary, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        }

        var reference = await ReferenceAsync(connectionString);
        await using var after = ModuleServices.ForRegistry(connectionString, _keyTwo, extra: new()
        {
            ["Security:MasterKeyVersion"] = "2",
            ["Security:PreviousMasterKey"] = _keyOne,
            ["Security:PreviousMasterKeyVersion"] = "1",
        });

        (await after.GetRequiredService<ICredentialResolver>().ResolveAsync(reference, TestContext.Current.CancellationToken)).Value!.Reveal().ShouldBe(_canary);
        (await after.GetRequiredService<IProviderSecretStore>().ReencryptAllAsync(TestContext.Current.CancellationToken)).Value.ShouldBe(1);
        (await ScalarAsync(connectionString, "select key_version from registry.provider_secret")).ShouldBe((short)2);

        await using var onlyNew = ModuleServices.ForRegistry(connectionString, _keyTwo, extra: new() { ["Security:MasterKeyVersion"] = "2" });
        (await onlyNew.GetRequiredService<ICredentialResolver>().ResolveAsync(reference, TestContext.Current.CancellationToken)).Value!.Reveal().ShouldBe(_canary);
    }

    [Fact]
    public async Task A_wrong_master_key_fails_resolution()
    {
        var connectionString = await SeedAsync();
        await using (var writer = ModuleServices.ForRegistry(connectionString, _keyOne))
        {
            await writer.GetRequiredService<IProviderSecretStore>().SetAsync(Guid.Parse(_providerId), _canary, TestContext.Current.CancellationToken);
        }

        var reference = await ReferenceAsync(connectionString);
        await using var wrong = ModuleServices.ForRegistry(connectionString, _keyTwo);

        var result = await wrong.GetRequiredService<ICredentialResolver>().ResolveAsync(reference, TestContext.Current.CancellationToken);

        result.Error.Code.ShouldBe(ErrorCode.ProviderUnavailable);
    }

    private static async Task<string> ReferenceAsync(string connectionString)
        => (string)(await ScalarAsync(connectionString, $"select credential_reference from registry.provider where id = '{_providerId}'"))!;

    private async Task<string> SeedAsync()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        await ScalarAsync(connectionString, $"insert into registry.provider values ('{_providerId}', 'openai-main', 'openai_compatible', 'https://api.example.test/v1', null, 30000, true, now(), now())");
        return connectionString;
    }

    private static async Task<long> ReadVersionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await ConfigVersion.ReadAsync(connection, CancellationToken.None);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
