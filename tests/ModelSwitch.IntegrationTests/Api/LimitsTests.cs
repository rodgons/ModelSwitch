using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelSwitch.Api;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;
using StackExchange.Redis;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// Rate and Concurrency Limits through the real host and a real Valkey (testing.md sections 2 and 4): enforcement, the release on
/// every exit path, expiry of a lease nobody released, and two api instances sharing the counters (FR-07).
/// </summary>
public sealed class LimitsTests(GatewayFixture gateway, PostgresFixture postgres, ValkeyFixture valkey) : IClassFixture<GatewayFixture>
{
    private static StringContent Body(string alias = GatewayFixture.ChatAlias, bool stream = false)
        => new(
            new JsonObject
            {
                ["model"] = alias,
                ["stream"] = stream,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }),
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

    private static HttpClient As(WebApplicationFactory<ApiHost> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string alias = GatewayFixture.ChatAlias)
        => client.PostAsync("/v1/chat/completions", Body(alias), TestContext.Current.CancellationToken);

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task A_tenant_without_limits_is_never_limited_and_gets_no_rate_headers()
    {
        var (_, key) = await gateway.SeedTenantAsync("unlimited");
        using var client = gateway.Authenticated(key);

        for (var i = 0; i < 5; i++)
        {
            var response = await PostAsync(client);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.Contains("X-RateLimit-Limit").ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_rate_limit_admits_the_burst_then_refuses_with_429_and_retry_after()
    {
        // 20 per minute: one token every 3 s, burst max(1, 20 / 10) = 2.
        var (_, key) = await gateway.SeedTenantAsync("rate", requestsPerMinute: 20);
        using var client = gateway.Authenticated(key);

        var first = await PostAsync(client);
        var second = await PostAsync(client);
        var third = await PostAsync(client);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Headers.GetValues("X-RateLimit-Limit").Single().ShouldBe("20");
        first.Headers.GetValues("X-RateLimit-Remaining").Single().ShouldBe("1");
        int.Parse(first.Headers.GetValues("X-RateLimit-Reset").Single(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.GetValues("X-RateLimit-Remaining").Single().ShouldBe("0");
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        third.Headers.Contains("X-Request-Id").ShouldBeTrue();
        int.Parse(third.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThanOrEqualTo(1);
        (await ErrorCodeAsync(third)).ShouldBe("rate_limit_exceeded");
    }

    [Fact]
    public async Task The_rate_limit_refills_with_time()
    {
        // 24 per minute: one token every 2.5 s, burst 2.
        var (_, key) = await gateway.SeedTenantAsync("refill", requestsPerMinute: 24);
        using var client = gateway.Authenticated(key);
        (await PostAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var refused = await PostAsync(client);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        await Task.Delay(TimeSpan.FromSeconds(double.Parse(refused.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture)) + TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        (await PostAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_concurrency_limit_refuses_the_request_over_the_limit_and_frees_the_slot_when_a_request_ends()
    {
        var (_, key) = await gateway.SeedTenantAsync("concurrency", maxConcurrent: 3);
        using var client = gateway.Authenticated(key);
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "latency=1500");
        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostAsync(client)));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(3);
            var refused = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
            refused.Count.ShouldBe(3);
            foreach (var response in refused)
            {
                (await ErrorCodeAsync(response)).ShouldBe("concurrency_limit_exceeded");
                response.Headers.Contains("Retry-After").ShouldBeTrue();
            }
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }

        (await PostAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_refusal_by_the_concurrency_limit_does_not_spend_a_rate_limit_token()
    {
        // One slot, 20 per minute (burst 2, a token every 3 s). The held request takes one token; if the three refusals also took one
        // each, the bucket would be empty afterwards and the final request would be refused for rate.
        var (_, key) = await gateway.SeedTenantAsync("atomic", requestsPerMinute: 20, maxConcurrent: 1);
        using var client = gateway.Authenticated(key);
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "latency=1500");
        try
        {
            var held = PostAsync(client);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            for (var i = 0; i < 3; i++)
            {
                (await PostAsync(client)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            }

            (await held).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }

        (await PostAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_request_that_fails_after_the_limits_step_still_releases_its_lease()
    {
        var (_, key) = await gateway.SeedTenantAsync("release-on-failure", maxConcurrent: 1);
        using var client = gateway.Authenticated(key);

        for (var i = 0; i < 3; i++)
        {
            (await PostAsync(client, "no-such-alias")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await PostAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_stream_holds_its_lease_until_it_ends()
    {
        var (_, key) = await gateway.SeedTenantAsync("stream-lease", maxConcurrent: 1);
        using var client = gateway.Authenticated(key);
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "chunk-delay=300");
        try
        {
            using var streaming = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Body(stream: true) },
                HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken);
            streaming.StatusCode.ShouldBe(HttpStatusCode.OK);

            var during = await PostAsync(client);
            during.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await ErrorCodeAsync(during)).ShouldBe("concurrency_limit_exceeded");

            (await streaming.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("[DONE]");
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }

        await EventuallyOkAsync(client);
    }

    [Fact]
    public async Task A_client_that_aborts_releases_its_lease()
    {
        var (_, key) = await gateway.SeedTenantAsync("abort", maxConcurrent: 1);
        using var client = gateway.Authenticated(key);
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "latency=5000");
        try
        {
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            abort.CancelAfter(500);
            await Should.ThrowAsync<OperationCanceledException>(async () => await client.PostAsync("/v1/chat/completions", Body(), abort.Token));
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }

        await EventuallyOkAsync(client);
    }

    [Fact]
    public async Task A_lease_nobody_released_expires_by_itself()
    {
        // What a crashed instance leaves behind: a lease in the Tenant's set with an expiry in the future.
        var (tenantId, key) = await gateway.SeedTenantAsync("crashed-host", maxConcurrent: 1);
        using var client = gateway.Authenticated(key);
        await using var connection = await ConnectionMultiplexer.ConnectAsync(valkey.ConnectionString);
        var database = connection.GetDatabase();
        var serverTime = (string[])database.Execute("TIME")!;
        var nowMs = (long.Parse(serverTime[0], System.Globalization.CultureInfo.InvariantCulture) * 1000) + (long.Parse(serverTime[1], System.Globalization.CultureInfo.InvariantCulture) / 1000);
        await database.SortedSetAddAsync($"cc:{{{tenantId:D}}}", Guid.CreateVersion7().ToString("D"), nowMs + 2_000);

        (await PostAsync(client)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        await EventuallyOkAsync(client, TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task Two_hosts_sharing_one_valkey_admit_exactly_the_concurrency_limit()
    {
        const int limit = 5;
        var (_, key) = await gateway.SeedTenantAsync("two-hosts-concurrency", maxConcurrent: limit);
        await using var second = gateway.StartHost();
        using var onFirst = gateway.Authenticated(key);
        using var onSecond = As(second, key);
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "latency=2000");
        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 2 * limit).Select(i => PostAsync(i % 2 == 0 ? onFirst : onSecond)));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(limit);
            responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).ShouldBe(limit);
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }
    }

    [Fact]
    public async Task Two_hosts_sharing_one_valkey_admit_exactly_the_rate_limit_burst()
    {
        // 50 per minute: burst 5, one more token only every 1.2 s.
        const int burst = 5;
        var (_, key) = await gateway.SeedTenantAsync("two-hosts-rate", requestsPerMinute: 50);
        await using var second = gateway.StartHost();
        using var onFirst = gateway.Authenticated(key);
        using var onSecond = As(second, key);

        var responses = await Task.WhenAll(Enumerable.Range(0, 2 * burst).Select(i => PostAsync(i % 2 == 0 ? onFirst : onSecond)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(burst);
        responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests).ShouldBe(burst);
    }

    [Fact]
    public async Task Ready_reports_valkey_when_the_limits_fail_closed()
    {
        var response = await gateway.Anonymous.GetAsync("/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("checks").GetProperty("valkey").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task Ready_leaves_valkey_out_when_the_limits_fail_open()
    {
        var open = new GatewayFixture(postgres, valkey);
        open.Settings["Limits:FailureMode"] = "Open";
        await open.InitializeAsync();
        try
        {
            var response = await open.Anonymous.GetAsync("/ready", TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            body.GetProperty("checks").TryGetProperty("valkey", out _).ShouldBeFalse();
        }
        finally
        {
            await open.DisposeAsync();
        }
    }

    // The slot is freed when the server notices the disconnect or finishes the stream, which is shortly after the client sees it.
    private static async Task EventuallyOkAsync(HttpClient client, TimeSpan? within = null)
    {
        var deadline = Environment.TickCount64 + (long)(within ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        HttpStatusCode last;
        do
        {
            last = (await PostAsync(client)).StatusCode;
            if (last == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        while (Environment.TickCount64 < deadline);

        last.ShouldBe(HttpStatusCode.OK);
    }
}
