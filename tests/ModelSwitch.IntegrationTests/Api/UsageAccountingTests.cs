using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelSwitch.IntegrationTests.Infrastructure;
using ModelSwitch.SharedKernel;
using ModelSwitch.Usage.Contracts;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>Usage Records, price versioning and Budget enforcement through the real api host (testing.md section 2).</summary>
public sealed class UsageAccountingTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>, IAsyncLifetime
{
    private Guid _tenant;

    public async ValueTask InitializeAsync()
    {
        _tenant = Guid.Parse((await Rows("select id::text from identity.tenant where name = 'acme'")).Single()[0]);
        await gateway.SetFaultAsync(null, null);
        await Budgets.SetMonthlyLimitAsync(_tenant, null, CancellationToken.None);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IBudgets Budgets => gateway.Services.GetRequiredService<IBudgets>();

    private static StringContent Body(string alias, string content = "count the words in this prompt please")
        => new(
            new JsonObject
            {
                ["model"] = alias,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

    private async Task<(HttpStatusCode Status, string RequestId)> SendAsync(string alias = GatewayFixture.ChatAlias)
    {
        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body(alias), TestContext.Current.CancellationToken);
        return (response.StatusCode, response.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task A_request_writes_one_usage_record_per_attempt_priced_at_the_price_in_force()
    {
        var (status, requestId) = await SendAsync();
        status.ShouldBe(HttpStatusCode.OK);

        var attempt = (await Rows($"select input_tokens, output_tokens from usage.provider_attempt where inference_request_id = '{requestId}'")).Single();
        var (input, output) = (decimal.Parse(attempt[0], CultureInfo.InvariantCulture), decimal.Parse(attempt[1], CultureInfo.InvariantCulture));
        var record = (await Rows(
            "select usage_source, estimate_method, input_cost, output_cost, total_cost, currency, " +
            "model_price_id = (select p.id from registry.model_price p where p.provider_model_id = r.provider_model_id order by effective_from desc limit 1), " +
            $"tenant_id = '{_tenant}' from usage.usage_record r where inference_request_id = '{requestId}'")).Single();

        var price = (await Rows("select input_per_million, output_per_million from registry.model_price p where p.id = (select model_price_id from usage.usage_record where inference_request_id = '" + requestId + "')")).Single();
        var (inputRate, outputRate) = (decimal.Parse(price[0], CultureInfo.InvariantCulture), decimal.Parse(price[1], CultureInfo.InvariantCulture));
        record[0].ShouldBe("reported");
        record[1].ShouldBeEmpty();
        decimal.Parse(record[2], CultureInfo.InvariantCulture).ShouldBe(input * inputRate / 1_000_000m);
        decimal.Parse(record[3], CultureInfo.InvariantCulture).ShouldBe(output * outputRate / 1_000_000m);
        decimal.Parse(record[4], CultureInfo.InvariantCulture).ShouldBe((input * inputRate + output * outputRate) / 1_000_000m);
        record[5].ShouldBe("USD");
        record[6].ShouldBe("True");
        record[7].ShouldBe("True");

        var request = (await Rows($"select total_estimated_cost, (select estimated_cost from usage.provider_attempt where inference_request_id = '{requestId}') from usage.inference_request where id = '{requestId}'")).Single();
        request[0].ShouldBe(request[1]);
        decimal.Parse(request[0], CultureInfo.InvariantCulture).ShouldBe(decimal.Parse(record[4], CultureInfo.InvariantCulture));
        await AssertAccountingConsistentAsync();
    }

    [Fact]
    public async Task A_completed_request_has_released_its_reservation_and_emitted_one_outbox_event()
    {
        var (_, requestId) = await SendAsync();

        (await Rows($"select status from usage.reservation where inference_request_id = '{requestId}'")).Single()[0].ShouldBe("settled");
        (await Rows($"select type from jobs.outbox_event where payload->>'requestId' = '{requestId}'")).Single()[0].ShouldBe("usage.request_completed");
        await AssertAccountingConsistentAsync();
    }

    [Fact]
    public async Task A_new_price_applies_to_later_requests_and_never_changes_recorded_costs()
    {
        var before = await SendAsync();
        var recordedBefore = await RecordAsync(before.RequestId);

        var modelId = (await Rows("select m.id::text from registry.provider_model m join registry.provider p on p.id = m.provider_id where p.name = 'mock-openai' and m.provider_model_id = 'mock-echo'")).Single()[0];
        await gateway.WriteConfigurationAsync(
            $"insert into registry.model_price values (gen_random_uuid(), '{modelId}', 10, null, 20, 'USD', now() - interval '1 second', now(), null);");

        var after = await SendAsync();
        var recordedAfter = await RecordAsync(after.RequestId);

        // History is immutable: the first record still names the old Price and the old cost.
        (await RecordAsync(before.RequestId)).ShouldBe(recordedBefore);
        recordedAfter.PriceId.ShouldNotBe(recordedBefore.PriceId);
        recordedAfter.Total.ShouldBeGreaterThan(recordedBefore.Total * 5, "the new Price is ten times the old one");
        var tokens = (await Rows($"select input_tokens, output_tokens from usage.provider_attempt where inference_request_id = '{after.RequestId}'")).Single();
        recordedAfter.Total.ShouldBe(((decimal.Parse(tokens[0], CultureInfo.InvariantCulture) * 10m) + (decimal.Parse(tokens[1], CultureInfo.InvariantCulture) * 20m)) / 1_000_000m);
        await AssertAccountingConsistentAsync();
    }

    [Fact]
    public async Task A_request_over_the_budget_is_a_402_that_never_reaches_the_provider_and_is_recorded()
    {
        await Budgets.SetMonthlyLimitAsync(_tenant, Money.FromUsd(0.0000001m), CancellationToken.None);
        var callsBefore = (await gateway.MockStatsAsync()).Started.Values.Sum();

        var response = await gateway.Client.PostAsync("/v1/chat/completions", Body(GatewayFixture.ChatAlias), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        body.GetProperty("error").GetProperty("code").GetString().ShouldBe("budget_exceeded");
        body.GetProperty("error").GetProperty("type").GetString().ShouldBe("budget_error");
        (await gateway.MockStatsAsync()).Started.Values.Sum().ShouldBe(callsBefore);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        (await Rows($"select status, error_code, attempt_count from usage.inference_request where id = '{requestId}'")).Single().ShouldBe(["failed", "budget_exceeded", "0"]);
        (await Rows($"select 1 from usage.reservation where inference_request_id = '{requestId}'")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_streaming_request_over_the_budget_is_also_a_402()
    {
        await Budgets.SetMonthlyLimitAsync(_tenant, Money.FromUsd(0.0000001m), CancellationToken.None);
        var body = new JsonObject
        {
            ["model"] = GatewayFixture.ChatAlias,
            ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        };

        var response = await gateway.Client.PostAsync("/v1/chat/completions", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
    }

    [Fact]
    public async Task Concurrent_requests_at_the_budget_boundary_never_overspend_the_limit()
    {
        // Hold per request is fixed (same body, same policy). Find it, then allow room for 5.5 holds.
        var probe = await SendAsync();
        var hold = decimal.Parse((await Rows($"select amount::text from usage.reservation where inference_request_id = '{probe.RequestId}'")).Single()[0], CultureInfo.InvariantCulture);
        var spentSoFar = (await Budgets.GetCurrentAsync(_tenant, CancellationToken.None))!.Spent.Amount;
        var limit = spentSoFar + (hold * 5.5m);
        await Budgets.SetMonthlyLimitAsync(_tenant, Money.FromUsd(limit), CancellationToken.None);

        // Latency keeps every request in flight at once, so each one competes for the room with its Reservation held.
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "latency=800");
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SendAsync()));

        results.Count(r => r.Status == HttpStatusCode.OK).ShouldBe(5);
        results.Count(r => r.Status == HttpStatusCode.PaymentRequired).ShouldBe(15);
        var status = (await Budgets.GetCurrentAsync(_tenant, CancellationToken.None))!;
        status.Reserved.Amount.ShouldBe(0m);
        (status.Spent.Amount + status.Reserved.Amount).ShouldBeLessThanOrEqualTo(limit);
        await AssertAccountingConsistentAsync();
    }

    [Fact]
    public async Task A_provider_that_reports_no_usage_is_estimated_and_says_so()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "no-usage");

        var (status, requestId) = await SendAsync();

        status.ShouldBe(HttpStatusCode.OK);
        var record = (await Rows($"select usage_source, estimate_method, total_cost > 0 from usage.usage_record where inference_request_id = '{requestId}'")).Single();
        record[0].ShouldBe("estimated");
        record[1].ShouldBe("tokenizer:o200k_base");
        record[2].ShouldBe("True");
        (await Rows($"select usage_status from usage.inference_request where id = '{requestId}'")).Single()[0].ShouldBe("estimated");
    }

    // testing.md section 3: budget_period totals equal the sum of the Usage Records, and nothing is left reserved.
    private async Task AssertAccountingConsistentAsync()
    {
        var totals = (await Rows(
            $"select (select coalesce(sum(spent_amount),0) from usage.budget_period where tenant_id = '{_tenant}')::text, " +
            $"(select coalesce(sum(total_cost),0) from usage.usage_record where tenant_id = '{_tenant}')::text, " +
            $"(select coalesce(sum(reserved_amount),0) from usage.budget_period where tenant_id = '{_tenant}')::text, " +
            $"(select coalesce(sum(amount),0) from usage.reservation where tenant_id = '{_tenant}' and status = 'held')::text")).Single();
        decimal.Parse(totals[0], CultureInfo.InvariantCulture).ShouldBe(decimal.Parse(totals[1], CultureInfo.InvariantCulture));
        decimal.Parse(totals[2], CultureInfo.InvariantCulture).ShouldBe(decimal.Parse(totals[3], CultureInfo.InvariantCulture));
    }

    private async Task<(string PriceId, decimal Total)> RecordAsync(string requestId)
    {
        var row = (await Rows($"select model_price_id::text, total_cost::text from usage.usage_record where inference_request_id = '{requestId}'")).Single();
        return (row[0], decimal.Parse(row[1], CultureInfo.InvariantCulture));
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
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!)]);
        }

        return rows;
    }
}
