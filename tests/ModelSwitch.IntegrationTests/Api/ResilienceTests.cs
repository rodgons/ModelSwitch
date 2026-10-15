using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.Registry.Contracts;
using ModelSwitch.Registry.Infrastructure.Health;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// The attempt loop through the real host and the Mock Provider's fault scripting (testing.md section 3): retries, Fallback and the
/// Attempt history each of them leaves.
/// </summary>
public sealed class ResilienceTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private static StringContent Body(string alias)
        => new(
            new JsonObject
            {
                ["model"] = alias,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }),
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

    [Fact]
    public async Task Two_503s_then_ok_succeeds_with_three_attempts_recorded()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=503;times=2");

        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body(GatewayFixture.ResilientAlias), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("modelswitch").GetProperty("attempts").GetInt32().ShouldBe(3);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();

        (await Rows($"select status, attempt_count, had_fallback, selected_provider_model_id = (select id from registry.provider_model where provider_model_id = 'mock-fallback') from usage.inference_request where id = '{requestId}'"))
            .Single().ShouldBe(["succeeded", "3", "True", "True"]);
        (await Rows(
            "select attempt_number, kind, status, error_class, http_status, provider_error_type, error_code, " +
            "(select provider_model_id from registry.provider_model m where m.id = a.provider_model_id) " +
            $"from usage.provider_attempt a where inference_request_id = '{requestId}' order by attempt_number"))
            .ShouldBe(
            [
                ["1", "primary", "failed", "transient", "503", "server_error", "provider_unavailable", "mock-echo"],
                ["2", "retry", "failed", "transient", "503", "server_error", "provider_unavailable", "mock-echo"],
                ["3", "fallback", "succeeded", string.Empty, string.Empty, string.Empty, string.Empty, "mock-fallback"],
            ]);
    }

    [Fact]
    public async Task One_503_is_retried_on_the_same_model_without_a_fallback()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=503;times=1");

        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body(GatewayFixture.ResilientAlias), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        (await Rows($"select kind, status from usage.provider_attempt where inference_request_id = '{requestId}' order by attempt_number"))
            .ShouldBe([["primary", "failed"], ["retry", "succeeded"]]);
        (await Rows($"select had_fallback from usage.inference_request where id = '{requestId}'")).Single().ShouldBe(["False"]);
    }

    [Fact]
    public async Task A_401_is_not_retried_on_the_same_model_but_falls_back()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=401");

        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body(GatewayFixture.ResilientAlias), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        (await Rows($"select kind, status, error_class, http_status from usage.provider_attempt where inference_request_id = '{requestId}' order by attempt_number"))
            .ShouldBe([["primary", "failed", "permanent", "401"], ["fallback", "succeeded", string.Empty, string.Empty]]);
    }

    [Fact]
    public async Task A_bad_request_is_not_retried_or_fallen_back_from()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=400");

        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body(GatewayFixture.ResilientAlias), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        (await Rows($"select kind, status, error_class from usage.provider_attempt where inference_request_id = '{requestId}'"))
            .ShouldBe([["primary", "failed", "permanent"]]);
        (await Rows($"select status, error_code from usage.inference_request where id = '{requestId}'")).Single().ShouldBe(["failed", "provider_error"]);
    }

    [Fact]
    public async Task Provider_health_snapshot_rows_are_written_per_instance()
    {
        await gateway.SetFaultAsync(null, null);
        (await gateway.Client.PostAsync("/v1/chat/completions", Body(GatewayFixture.ResilientAlias), TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var job = gateway.Services.GetServices<IHostedService>().OfType<ProviderHealthSnapshotService>().Single();

        var written = await job.WriteOnceAsync(TestContext.Current.CancellationToken);

        written.ShouldBeGreaterThan(0);
        var rows = await Rows("select instance_id, circuit_state, window_calls > 0, captured_at is not null from registry.provider_health_snapshot");
        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(row => row[0] == Environment.MachineName && row[3] == "True");
        gateway.Services.GetRequiredService<IProviderHealth>().Snapshot().Count.ShouldBe(rows.Count);
    }

    private async Task<List<string[]>> Rows(string sql)
    {
        await using var connection = new NpgsqlConnection(gateway.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await reader.ReadAsync())
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!)]);
        }

        return rows;
    }
}

/// <summary>testing.md section 3, "All candidates failing x20": the circuit opens and later requests get a fast 503 without calling the Provider.</summary>
public sealed class CircuitBreakerTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    [Fact]
    public async Task A_failing_provider_trips_the_circuit_and_later_requests_fail_fast_without_calling_it()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=503");

        HttpStatusCode last = default;
        string? lastRequestId = null;
        int? lastRetryAfter = null;
        for (var i = 0; i < 20; i++)
        {
            using var content = new StringContent(
                new JsonObject
                {
                    ["model"] = GatewayFixture.ChatAlias,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }),
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");
            var response = await gateway.Client.PostAsync("/v1/chat/completions", content, TestContext.Current.CancellationToken);
            last = response.StatusCode;
            lastRequestId = response.Headers.GetValues("X-Request-Id").Single();
            lastRetryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalSeconds : null;
        }

        last.ShouldBe(HttpStatusCode.ServiceUnavailable);
        lastRetryAfter.ShouldNotBeNull("the router knows the remaining break").ShouldBeInRange(1, 30);
        var attempts = await Statuses(lastRequestId!);
        attempts.ShouldBeEmpty("the router excluded the open circuit, so nothing was called");
        var health = gateway.Services.GetRequiredService<IProviderHealth>();
        health.OpenCircuits().Count.ShouldBe(1);

        var requestStatus = await Scalar($"select status || '/' || error_code || '/' || attempt_count || '/' || (policy_version is not null) || '/' || (routing_decision::text like '%circuit_open%') from usage.inference_request where id = '{lastRequestId}'");
        requestStatus.ShouldBe("failed/provider_unavailable/0/true/true");

        await gateway.SetFaultAsync(null, null);
    }

    private async Task<List<string>> Statuses(string requestId)
    {
        await using var connection = new NpgsqlConnection(gateway.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"select status from usage.provider_attempt where inference_request_id = '{requestId}' order by attempt_number", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var statuses = new List<string>();
        while (await reader.ReadAsync())
        {
            statuses.Add(reader.GetString(0));
        }

        return statuses;
    }

    private async Task<string?> Scalar(string sql)
    {
        await using var connection = new NpgsqlConnection(gateway.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync()) as string;
    }
}
