using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ModelSwitch.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>testing.md section 2, "Request records" (usage_record rows arrive with Phase 3).</summary>
public sealed class RequestRecordsTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private static StringContent Body(string alias, string content = "secret prompt text")
        => new(
            new JsonObject
            {
                ["model"] = alias,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

    [Fact]
    public async Task A_completion_records_one_request_and_one_attempt_correlated_by_request_id()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Body(GatewayFixture.ChatAlias) };
        message.Headers.Add("X-Client-Request-Id", "client-abc");

        var response = await gateway.Client.SendAsync(message, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var requestId = Guid.Parse(response.Headers.GetValues("X-Request-Id").Single());
        var request = (await Rows(
            $"select status, origin, api_key_id is not null, client_request_id, requested_alias, attempt_count, had_fallback, selected_provider_model_id is not null, " +
            $"usage_status, total_input_tokens > 0, total_output_tokens > 0, completed_at is not null, input_bytes > 0, error_code from usage.inference_request where id = '{requestId}'")).Single();
        request.ShouldBe(["succeeded", "api", "True", "client-abc", "balanced", "1", "False", "True", "reported", "True", "True", "True", "True", ""]);

        var attempts = await Rows(
            $"select attempt_number, kind, status, error_class, input_tokens > 0, provider_model_id = (select selected_provider_model_id from usage.inference_request where id = '{requestId}') " +
            $"from usage.provider_attempt where inference_request_id = '{requestId}' order by attempt_number");
        attempts.Count.ShouldBe(1);
        attempts[0].ShouldBe(["1", "primary", "succeeded", "", "True", "True"]);
    }

    [Fact]
    public async Task A_failed_request_is_recorded_with_its_error_code_and_no_attempts()
    {
        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body("no-such-alias"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        (await Rows($"select status, error_code, attempt_count, usage_status from usage.inference_request where id = '{requestId}'")).Single()
            .ShouldBe(["failed", "model_not_found", "0", "reported"]);
        (await Rows($"select 1 from usage.provider_attempt where inference_request_id = '{requestId}'")).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_invalid_client_request_id_is_a_400_and_nothing_is_recorded()
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Body(GatewayFixture.ChatAlias) };
        message.Headers.Add("X-Client-Request-Id", new string('x', 129));

        var response = await gateway.Client.SendAsync(message, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        (await Rows($"select 1 from usage.inference_request where id = '{requestId}'")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Prompt_and_completion_content_is_never_persisted()
    {
        const string marker = "zebra-unique-marker-9137";
        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body(GatewayFixture.ChatAlias, marker), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var dump = await Rows("select t::text from usage.inference_request t union all select t::text from usage.provider_attempt t");

        dump.ShouldNotBeEmpty();
        dump.ShouldAllBe(row => !row[0].Contains(marker, StringComparison.Ordinal));
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
