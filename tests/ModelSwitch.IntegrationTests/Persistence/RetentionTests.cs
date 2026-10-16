using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelSwitch.Identity;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Migrator;
using ModelSwitch.SharedKernel.Jobs;
using ModelSwitch.SharedKernel.Retention;
using ModelSwitch.Usage;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Persistence;

/// <summary>data-model.md "Retention" and "Partitioning": the worker's daily retention run against a real database.</summary>
public sealed class RetentionTests(PostgresFixture postgres)
{
    private const string _retentionPassword = "retention-test-password";

    [Fact]
    public async Task The_retention_role_drops_expired_partitions_deletes_expired_rows_keeps_the_rest_and_creates_future_partitions()
    {
        var admin = await MigratedAsync();
        var planted = await PlantAsync(admin);
        var asRetention = new NpgsqlConnectionStringBuilder(admin) { Username = DatabaseRoles.RetentionRole, Password = _retentionPassword }.ConnectionString;

        await RunAsync(admin, new RetentionDataSource(NpgsqlDataSource.Create(asRetention), IsRetentionRole: true));

        (await ScalarAsync(admin, $"select to_regclass('usage.{planted.OldPartition}') is null")).ShouldBe("True");
        (await ScalarAsync(admin, $"select to_regclass('usage.inference_request_{Suffix(DateTimeOffset.UtcNow.AddMonths(12))}') is not null")).ShouldBe("True");
        (await RemainingAsync(admin, "usage.inference_request")).ShouldBe([planted.FreshRequest.ToString()]);
        (await RemainingAsync(admin, "usage.usage_record")).ShouldBe(["fresh"], "old usage records go after 400 days");
        (await RemainingAsync(admin, "usage.usage_hourly", "requested_alias")).ShouldBe(["fresh"]);
        (await RemainingAsync(admin, "identity.audit_record", "action")).ShouldBe(["fresh"]);
        (await RemainingAsync(admin, "jobs.outbox_event", "type")).ShouldBe(["fresh", "unpublished"], ignoreOrder: true);
        (await RemainingAsync(admin, "jobs.processed_event", "consumer")).ShouldBe(["fresh"]);
        (await RemainingAsync(admin, "jobs.job", "kind")).ShouldBe(["fresh", "pending"], ignoreOrder: true);

        // A second run finds nothing more to remove.
        await RunAsync(admin, new RetentionDataSource(NpgsqlDataSource.Create(asRetention), IsRetentionRole: true));
        (await RemainingAsync(admin, "usage.inference_request")).ShouldBe([planted.FreshRequest.ToString()]);
    }

    [Fact]
    public async Task Without_the_retention_role_audit_records_are_kept_and_everything_else_is_still_removed()
    {
        var admin = await MigratedAsync();
        await PlantAsync(admin);

        await RunAsync(admin, new RetentionDataSource(NpgsqlDataSource.Create(admin), IsRetentionRole: false));

        (await RemainingAsync(admin, "identity.audit_record", "action")).ShouldBe(["fresh", "old"], ignoreOrder: true);
        (await RemainingAsync(admin, "usage.usage_hourly", "requested_alias")).ShouldBe(["fresh"]);
    }

    [Fact]
    public async Task The_app_and_retention_roles_may_run_partition_maintenance_but_not_the_ddl_behind_it()
    {
        var admin = await MigratedAsync();

        (await ScalarAsync(admin, $"select has_function_privilege('{DatabaseRoles.RetentionRole}', 'usage.maintain_request_partitions(integer, integer)', 'EXECUTE')")).ShouldBe("True");
        (await ScalarAsync(admin, $"select has_function_privilege('{DatabaseRoles.AppRole}', 'usage.maintain_request_partitions(integer, integer)', 'EXECUTE')")).ShouldBe("True");
        (await ScalarAsync(admin, "select has_function_privilege('public', 'usage.maintain_request_partitions(integer, integer)', 'EXECUTE')")).ShouldBe("False");
        (await ScalarAsync(admin, $"select has_function_privilege('{DatabaseRoles.RetentionRole}', 'usage.ensure_request_partitions(integer, integer)', 'EXECUTE')")).ShouldBe("False");
    }

    private sealed record Planted(Guid FreshRequest, string OldPartition);

    private static string Suffix(DateTimeOffset month) => month.ToString("'y'yyyy'm'MM", CultureInfo.InvariantCulture);

    private async Task<string> MigratedAsync()
    {
        var admin = await postgres.CreateMigratedDatabaseAsync();

        // Roles are cluster-wide: another test may have created the retention role NOLOGIN, so give it a known login here.
        await new DatabaseRoles(NullLogger<DatabaseRoles>.Instance).ApplyAsync(admin, ["identity", "usage", "jobs"], appPassword: "app-secret-with-'quote", _retentionPassword, CancellationToken.None);
        await ExecuteAsync(admin, $"alter role {DatabaseRoles.RetentionRole} login password '{_retentionPassword}'");
        return admin;
    }

    private static async Task<Planted> PlantAsync(string admin)
    {
        var now = DateTimeOffset.UtcNow;
        var oldMonth = now.AddMonths(-3);
        var fresh = Guid.CreateVersion7();
        await ExecuteAsync(admin, "select usage.ensure_request_partitions(4, 0)");
        await ExecuteAsync(
            admin,
            $$"""
            insert into usage.inference_request (id, started_at, tenant_id, origin, api_key_id, requested_alias, streaming, status, attempt_count, had_fallback, input_bytes, usage_status, trace_id) values
              (gen_random_uuid(), '{{Iso(oldMonth)}}', gen_random_uuid(), 'api', gen_random_uuid(), 'old', false, 'succeeded', 1, false, 1, 'reported', ''),
              (gen_random_uuid(), '{{Iso(now.AddDays(-31))}}', gen_random_uuid(), 'api', gen_random_uuid(), 'boundary', false, 'succeeded', 1, false, 1, 'reported', ''),
              ('{{fresh}}', '{{Iso(now.AddDays(-1))}}', gen_random_uuid(), 'api', gen_random_uuid(), 'fresh', false, 'succeeded', 1, false, 1, 'reported', '');
            insert into usage.provider_attempt (id, inference_request_id, attempt_number, provider_id, provider_model_id, kind, status, started_at, completed_at, duration_ms) values
              (gen_random_uuid(), gen_random_uuid(), 1, gen_random_uuid(), gen_random_uuid(), 'primary', 'succeeded', '{{Iso(now.AddDays(-31))}}', '{{Iso(now.AddDays(-31))}}', 1);
            insert into usage.usage_record (id, inference_request_id, tenant_id, usage_source, input_cost, output_cost, total_cost, currency, recorded_at, estimate_method) values
              (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'reported', 0, 0, 0, 'USD', '{{Iso(now.AddDays(-401))}}', 'old'),
              (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'reported', 0, 0, 0, 'USD', '{{Iso(now.AddDays(-399))}}', 'fresh');
            insert into usage.usage_hourly (tenant_id, provider_model_id, requested_alias, hour, requests, failed_requests, fallback_requests, attempts, input_tokens, cached_input_tokens, output_tokens, estimated_cost, duration_ms_sum, latency_histogram, ttft_histogram) values
              (gen_random_uuid(), gen_random_uuid(), 'old', '{{Iso(now.AddDays(-401))}}', 1, 0, 0, 1, 0, 0, 0, 0, 0, '{}', '{}'),
              (gen_random_uuid(), gen_random_uuid(), 'fresh', '{{Iso(now.AddDays(-1))}}', 1, 0, 0, 1, 0, 0, 0, 0, 0, '{}', '{}');
            insert into identity.audit_record (id, occurred_at, action, target_type, details) values
              (gen_random_uuid(), '{{Iso(now.AddDays(-401))}}', 'old', 'test', '{}'),
              (gen_random_uuid(), '{{Iso(now.AddDays(-1))}}', 'fresh', 'test', '{}');
            insert into jobs.outbox_event (id, type, payload, occurred_at, published_at, attempts) values
              (gen_random_uuid(), 'old', '{}', '{{Iso(now.AddDays(-9))}}', '{{Iso(now.AddDays(-8))}}', 0),
              (gen_random_uuid(), 'unpublished', '{}', '{{Iso(now.AddDays(-30))}}', null, 0),
              (gen_random_uuid(), 'fresh', '{}', '{{Iso(now.AddDays(-1))}}', '{{Iso(now.AddDays(-1))}}', 0);
            insert into jobs.processed_event (consumer, event_id, processed_at) values
              ('old', gen_random_uuid(), '{{Iso(now.AddDays(-8))}}'), ('fresh', gen_random_uuid(), '{{Iso(now.AddDays(-1))}}');
            insert into jobs.job (id, kind, payload, status, attempts, max_attempts, run_after, created_at, updated_at) values
              (gen_random_uuid(), 'old', '{}', 'succeeded', 1, 5, '{{Iso(now.AddDays(-20))}}', '{{Iso(now.AddDays(-20))}}', '{{Iso(now.AddDays(-15))}}'),
              (gen_random_uuid(), 'pending', '{}', 'pending', 0, 5, '{{Iso(now.AddDays(-20))}}', '{{Iso(now.AddDays(-20))}}', '{{Iso(now.AddDays(-20))}}'),
              (gen_random_uuid(), 'fresh', '{}', 'dead', 5, 5, '{{Iso(now.AddDays(-2))}}', '{{Iso(now.AddDays(-2))}}', '{{Iso(now.AddDays(-1))}}');
            """);
        return new Planted(fresh, $"inference_request_{Suffix(oldMonth)}");
    }

    private static async Task RunAsync(string admin, RetentionDataSource source)
    {
        await using var services = ModuleServices.ForJobs(admin, configure: s =>
        {
            s.AddUsageModule();
            s.AddIdentityModule();
            s.AddRetention(_ => source);
        });
        var handler = services.GetServices<IJobHandler>().Single(h => h.Kind == "retention.run");
        await handler.HandleAsync(new JobContext(Guid.CreateVersion7(), "retention.run", "{}", 1, 5), CancellationToken.None);
    }

    private static string Iso(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static async Task<List<string>> RemainingAsync(string connectionString, string table, string column = "id::text")
    {
        // usage_record keeps the planted label in estimate_method.
        var selected = table == "usage.usage_record" ? "estimate_method" : column;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"select {selected} from {table}", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }
}
