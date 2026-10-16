using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelSwitch.MockProvider;
using Shouldly;

namespace ModelSwitch.UnitTests.MockProvider;

/// <summary>Drives the Mock Provider over a real loopback socket, in-process.</summary>
public sealed class MockProviderEndpointTests : IAsyncLifetime
{
    private RunningMockProvider _mock = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _mock = await MockProviderApp.StartAsync(TestContext.Current.CancellationToken);
        _http = new HttpClient { BaseAddress = _mock.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _mock.DisposeAsync();
    }

    private static JsonObject Chat(string model, bool stream, string text = "hello brave world") => new()
    {
        ["model"] = model,
        ["stream"] = stream,
        ["stream_options"] = new JsonObject { ["include_usage"] = true },
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }),
    };

    private Task<HttpResponseMessage> PostAsync(string path, JsonObject body, string? fault = null, string? usage = null, string? scenario = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (fault is not null)
        {
            request.Headers.Add("X-Mock-Fault", fault);
        }

        if (usage is not null)
        {
            request.Headers.Add("X-Mock-Usage", usage);
        }

        if (scenario is not null)
        {
            request.Headers.Add("X-Mock-Scenario", scenario);
        }

        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OpenAi_echo_returns_the_last_user_message_with_usage()
    {
        using var response = await PostAsync("/v1/chat/completions", Chat("mock-echo", stream: false));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        json["choices"]![0]!["message"]!["content"]!.GetValue<string>().ShouldBe("hello brave world");
        json["usage"]!["completion_tokens"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public async Task The_judge_model_replies_in_the_llm_judge_format()
    {
        using var response = await PostAsync("/v1/chat/completions", Chat("mock-judge", stream: false));

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        var reply = JsonNode.Parse(json["choices"]![0]!["message"]!["content"]!.GetValue<string>())!;
        reply["score"]!.GetValue<decimal>().ShouldBe(0.75m);
    }

    [Fact]
    public async Task OpenAi_stream_ends_with_usage_and_done()
    {
        using var response = await PostAsync("/v1/chat/completions", Chat("mock-fixed?tokens=3", stream: true));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        body.ShouldContain("\"usage\"");
        body.TrimEnd().ShouldEndWith("data: [DONE]");
    }

    [Fact]
    public async Task Cached_usage_header_is_reported_in_prompt_tokens_details()
    {
        using var response = await PostAsync("/v1/chat/completions", Chat("mock-echo", stream: false), usage: "cached=900");

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        json["usage"]!["prompt_tokens_details"]!["cached_tokens"]!.GetValue<int>().ShouldBe(900);
    }

    [Fact]
    public async Task No_usage_fault_omits_usage()
    {
        using var response = await PostAsync("/v1/chat/completions", Chat("mock-echo", stream: false), fault: "no-usage");

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        json["usage"].ShouldBeNull();
    }

    [Fact]
    public async Task Status_fault_with_times_fails_twice_then_succeeds()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await PostAsync("/v1/chat/completions", Chat("mock-echo", stream: false), fault: "status=503;times=2", scenario: "s1");
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldBe([HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
    }

    [Fact]
    public async Task Status_fault_sends_retry_after_and_error_code()
    {
        using var response = await PostAsync("/v1/chat/completions", Chat("mock-echo", stream: false), fault: "status=429;retry-after=7;error=insufficient_quota");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(7));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        json["error"]!["code"]!.GetValue<string>().ShouldBe("insufficient_quota");
    }

    [Fact]
    public async Task Garbage_fault_returns_invalid_json()
    {
        using var response = await PostAsync("/v1/chat/completions", Chat("mock-echo", stream: false), fault: "garbage");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Should.Throw<System.Text.Json.JsonException>(() => JsonNode.Parse(body));
    }

    [Fact]
    public async Task Hang_fault_never_sends_headers()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = JsonContent.Create(Chat("mock-echo", stream: false)) };
            request.Headers.Add("X-Mock-Fault", "hang");
            await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        });
    }

    [Fact]
    public async Task Cut_after_drops_the_connection_after_n_chunks()
    {
        // Repeated because the original defect was a race between the cut and delivery of the chunks written before it
        // (a reset discards bytes the peer has not read yet), which showed up in roughly one run in ten under load.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var response = await PostAsync("/v1/chat/completions", Chat("mock-fixed?tokens=10", stream: true), fault: "cut-after=3");
            var lines = new List<string>();

            await Should.ThrowAsync<IOException>(async () =>
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
                while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
                {
                    lines.Add(line);
                }
            });

            lines.Count(l => l.Contains("\"content\":\"token", StringComparison.Ordinal) || l.Contains("\"content\":\" token", StringComparison.Ordinal)).ShouldBe(3);
            lines.ShouldNotContain("data: [DONE]");
        }
    }

    [Fact]
    public async Task Scenario_endpoint_sets_a_fault_for_a_model_without_headers()
    {
        using var set = await _http.PostAsJsonAsync("/__scenarios", new { model = "mock-a", fault = "status=503;times=1" }, TestContext.Current.CancellationToken);
        set.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var first = await PostAsync("/v1/chat/completions", Chat("mock-a", stream: false));
        using var second = await PostAsync("/v1/chat/completions", Chat("mock-a", stream: false));

        first.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Scenario_endpoint_rejects_an_invalid_fault()
    {
        using var response = await _http.PostAsJsonAsync("/__scenarios", new { model = "mock-a", fault = "explode" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ollama_chat_streams_ndjson_with_a_final_usage_object()
    {
        var request = new JsonObject
        {
            ["model"] = "mock-fixed?tokens=2",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        };

        using var response = await PostAsync("/api/chat", request, usage: "cached=4");

        var lines = (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(3);
        var last = JsonNode.Parse(lines[^1])!;
        last["done"]!.GetValue<bool>().ShouldBeTrue();
        last["eval_count"]!.GetValue<int>().ShouldBe(2);
        last["prompt_eval_cached_count"]!.GetValue<int>().ShouldBe(4);
    }

    [Fact]
    public async Task Ollama_non_streaming_chat_returns_one_object()
    {
        var request = new JsonObject
        {
            ["model"] = "mock-echo",
            ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "ping pong" }),
        };

        using var response = await PostAsync("/api/chat", request);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        json["message"]!["content"]!.GetValue<string>().ShouldBe("ping pong");
        json["done_reason"]!.GetValue<string>().ShouldBe("stop");
    }

    [Fact]
    public async Task Discovery_endpoints_list_models_for_both_protocols()
    {
        var openAi = JsonNode.Parse(await _http.GetStringAsync("/v1/models", TestContext.Current.CancellationToken))!;
        var ollama = JsonNode.Parse(await _http.GetStringAsync("/api/tags", TestContext.Current.CancellationToken))!;

        openAi["data"]!.AsArray().Count.ShouldBeGreaterThan(0);
        ollama["models"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }
}
