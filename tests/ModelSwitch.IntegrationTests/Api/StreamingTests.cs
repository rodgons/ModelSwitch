using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelSwitch.IntegrationTests.Infrastructure;
using Npgsql;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

/// <summary>
/// SSE streaming end to end through the real host and the Mock Provider (testing.md sections 2 and 3): framing, the first-token
/// rule, the mid-stream error event, keep-alives and client disconnects.
/// </summary>
public sealed class StreamingTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private const string _eightWords = "one two three four five six seven eight";

    private static StringContent Body(string alias, string content, bool? includeUsage = true)
    {
        var body = new JsonObject
        {
            ["model"] = alias,
            ["stream"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };
        if (includeUsage is { } include)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = include };
        }

        return new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private Task<HttpResponseMessage> StartAsync(string alias, string content, bool? includeUsage = true, CancellationToken cancellationToken = default)
        => gateway.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Body(alias, content, includeUsage) },
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

    /// <summary>The raw events: <c>data: …</c> payloads and <c>: comment</c> lines, in order.</summary>
    private static async Task<List<string>> ReadAllAsync(HttpResponseMessage response)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), Encoding.UTF8);
        var events = new List<string>();
        while (await ReadEventAsync(reader, TestContext.Current.CancellationToken) is { } next)
        {
            events.Add(next);
        }

        return events;
    }

    private static async Task<string?> ReadEventAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return text.Length == 0 ? null : text.ToString();
            }

            if (line.Length == 0)
            {
                if (text.Length > 0)
                {
                    return text.ToString();
                }

                continue;
            }

            text.Append(text.Length == 0 ? string.Empty : "\n").Append(line);
        }
    }

    private static JsonElement Chunk(string rawEvent)
    {
        rawEvent.ShouldStartWith("data: ");
        return JsonDocument.Parse(rawEvent["data: ".Length..]).RootElement.Clone();
    }

    private static string ContentOf(IEnumerable<string> events)
        => string.Concat(events
            .Where(e => e.StartsWith("data: {", StringComparison.Ordinal))
            .Select(Chunk)
            .Where(c => c.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].GetProperty("delta").TryGetProperty("content", out _))
            .Select(c => c.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString()));

    [Theory]
    [InlineData(GatewayFixture.ChatAlias)]
    [InlineData(GatewayFixture.OllamaAlias)]
    [HappyPath("POST /v1/chat/completions")]
    public async Task A_stream_is_role_content_finish_usage_and_done_for_both_protocols(string alias)
    {
        using var response = await StartAsync(alias, _eightWords, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        var events = await ReadAllAsync(response);

        events.Last().ShouldBe("data: [DONE]");
        var chunks = events[..^1].Select(Chunk).ToList();
        chunks.ShouldAllBe(c => c.GetProperty("object").GetString() == "chat.completion.chunk" && c.GetProperty("model").GetString() == alias && c.GetProperty("id").GetString() == $"chatcmpl-{requestId}");
        chunks[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString().ShouldBe("assistant");
        ContentOf(events).ShouldBe(_eightWords);
        var finish = chunks[^2].GetProperty("choices")[0];
        finish.GetProperty("finish_reason").GetString().ShouldBe("stop");
        var usage = chunks[^1];
        usage.GetProperty("choices").GetArrayLength().ShouldBe(0);
        usage.GetProperty("usage").GetProperty("total_tokens").GetInt32().ShouldBeGreaterThan(0);
        usage.GetProperty("modelswitch").GetProperty("request_id").GetString().ShouldBe(requestId);
        usage.GetProperty("modelswitch").GetProperty("usage_source").GetString().ShouldBe("reported");
        events.ShouldNotContain(e => e.Contains("mock-echo", StringComparison.Ordinal), "the Provider Model ID never leaks");

        (await Row($"select status, streaming, ttft_ms is not null, usage_status, attempt_count, (select status from usage.provider_attempt where inference_request_id = r.id), (select ttft_ms is not null from usage.provider_attempt where inference_request_id = r.id) from usage.inference_request r where id = '{requestId}'"))
            .ShouldBe(["succeeded", "True", "True", "reported", "1", "succeeded", "True"]);
    }

    [Fact]
    public async Task Without_include_usage_the_client_gets_no_usage_chunk_but_the_request_is_still_costed()
    {
        using var response = await StartAsync(GatewayFixture.ChatAlias, _eightWords, includeUsage: null, TestContext.Current.CancellationToken);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();

        var events = await ReadAllAsync(response);

        events.Last().ShouldBe("data: [DONE]");
        events.ShouldNotContain(e => e.Contains("\"usage\"", StringComparison.Ordinal));
        (await Row($"select usage_status, total_input_tokens > 0, total_output_tokens > 0 from usage.inference_request where id = '{requestId}'")).ShouldBe(["reported", "True", "True"]);
    }

    [Fact]
    [TriggersError("stream_interrupted")]
    public async Task A_stream_cut_after_five_chunks_ends_with_the_error_event_no_done_and_no_replay()
    {
        await gateway.SetFaultAsync(null, null); // zero the Mock Provider's counters
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "cut-after=5");
        try
        {
            // The resilient alias has a Fallback: it must stay unused, because the First Token has gone out.
            using var response = await StartAsync(GatewayFixture.ResilientAlias, _eightWords, cancellationToken: TestContext.Current.CancellationToken);
            var requestId = response.Headers.GetValues("X-Request-Id").Single();

            var events = await ReadAllAsync(response);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            events.ShouldNotContain("data: [DONE]");
            events.Count(e => e.StartsWith("data: {", StringComparison.Ordinal) && e.Contains("\"content\"", StringComparison.Ordinal)).ShouldBe(5);
            ContentOf(events).ShouldBe("one two three four five");
            var error = Chunk(events.Last()).GetProperty("error");
            error.GetProperty("code").GetString().ShouldBe("stream_interrupted");
            error.GetProperty("type").GetString().ShouldBe("provider_error");
            error.GetProperty("request_id").GetString().ShouldBe(requestId);

            (await Row($"select status, error_code, attempt_count, had_fallback, usage_status from usage.inference_request where id = '{requestId}'"))
                .ShouldBe(["failed", "stream_interrupted", "1", "False", "estimated"]);
            (await Row($"select kind, status, error_class, error_code, output_tokens > 0 from usage.provider_attempt where inference_request_id = '{requestId}'"))
                .ShouldBe(["primary", "ambiguous", "ambiguous", "stream_interrupted", "True"]);
            (await gateway.MockStatsAsync()).Started[GatewayFixture.PrimaryModel].ShouldBe(1, "the stream was not replayed");
            (await gateway.MockStatsAsync()).Started.ContainsKey(GatewayFixture.FallbackModel).ShouldBeFalse("and no Fallback was tried");
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }
    }

    [Fact]
    public async Task A_503_before_the_first_token_falls_back_and_the_stream_still_succeeds()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=503");
        try
        {
            using var response = await StartAsync(GatewayFixture.ResilientAlias, _eightWords, cancellationToken: TestContext.Current.CancellationToken);
            var requestId = response.Headers.GetValues("X-Request-Id").Single();

            var events = await ReadAllAsync(response);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            events.Last().ShouldBe("data: [DONE]");
            ContentOf(events).ShouldStartWith("token");
            Chunk(events[^2]).GetProperty("modelswitch").GetProperty("attempts").GetInt32().ShouldBe(3);
            (await Rows($"select kind, status from usage.provider_attempt where inference_request_id = '{requestId}' order by attempt_number"))
                .ShouldBe([["primary", "failed"], ["retry", "failed"], ["fallback", "succeeded"]]);
            (await Row($"select status, had_fallback from usage.inference_request where id = '{requestId}'")).ShouldBe(["succeeded", "True"]);
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }
    }

    [Fact]
    [TriggersError("provider_error")]
    public async Task When_every_candidate_fails_before_the_first_token_the_answer_is_an_ordinary_error_not_an_event_stream()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "status=400");
        try
        {
            using var response = await StartAsync(GatewayFixture.ChatAlias, _eightWords, cancellationToken: TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("provider_error");
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }
    }

    [Fact]
    public async Task A_quiet_stream_gets_keep_alive_comments_between_chunks()
    {
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "chunk-delay=1300");
        try
        {
            using var response = await StartAsync(GatewayFixture.ChatAlias, "slow answer", cancellationToken: TestContext.Current.CancellationToken);

            var events = await ReadAllAsync(response);

            events.ShouldContain(": keep-alive");
            events.Last().ShouldBe("data: [DONE]");
            ContentOf(events).ShouldBe("slow answer");
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }
    }

    [Fact]
    public async Task A_client_that_disconnects_mid_stream_closes_the_upstream_and_the_attempt_is_recorded_cancelled()
    {
        await gateway.SetFaultAsync(null, null);
        await gateway.SetFaultAsync(GatewayFixture.PrimaryModel, "chunk-delay=300");
        try
        {
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var words = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"w{i}"));
            using var response = await StartAsync(GatewayFixture.ChatAlias, words, cancellationToken: abort.Token);
            var requestId = response.Headers.GetValues("X-Request-Id").Single();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(abort.Token), Encoding.UTF8);
            (await ReadEventAsync(reader, abort.Token))!.ShouldContain("assistant");
            (await ReadEventAsync(reader, abort.Token))!.ShouldContain("\"content\"");

            // Hanging up: the client stops reading and drops the connection.
            await abort.CancelAsync();
            reader.Dispose();
            response.Dispose();

            (await EventuallyAsync(async () => (await gateway.MockStatsAsync()).Aborted.GetValueOrDefault(GatewayFixture.PrimaryModel) >= 1))
                .ShouldBeTrue("the Mock Provider saw the gateway hang up: the upstream call was closed");
            (await EventuallyAsync(async () => (await Row($"select status from usage.inference_request where id = '{requestId}'"))[0] == "cancelled"))
                .ShouldBeTrue("the request is settled cancelled, on a fresh token, though the client is gone");
            (await Row($"select status, provider_error_type, output_tokens > 0 from usage.provider_attempt where inference_request_id = '{requestId}'"))
                .ShouldBe(["ambiguous", "client_cancelled", "True"]);
            (await Row($"select attempt_count, usage_status from usage.inference_request where id = '{requestId}'")).ShouldBe(["1", "estimated"]);
        }
        finally
        {
            await gateway.SetFaultAsync(null, null);
        }
    }

    [Fact]
    public async Task Streaming_content_is_never_persisted()
    {
        const string marker = "zebra-unique-stream-marker-4412";
        using var response = await StartAsync(GatewayFixture.ChatAlias, marker, cancellationToken: TestContext.Current.CancellationToken);
        (await ReadAllAsync(response)).Last().ShouldBe("data: [DONE]");

        var dump = await Rows("select t::text from usage.inference_request t union all select t::text from usage.provider_attempt t");

        dump.ShouldAllBe(row => !row[0].Contains(marker, StringComparison.Ordinal));
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private async Task<string[]> Row(string sql) => (await Rows(sql)).FirstOrDefault() ?? [];

    private async Task<List<string[]>> Rows(string sql)
    {
        await using var connection = new NpgsqlConnection(gateway.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!)]);
        }

        return rows;
    }
}
