using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelSwitch.IntegrationTests.Infrastructure;
using Shouldly;

namespace ModelSwitch.IntegrationTests.Api;

public sealed class ChatCompletionsEndpointTests(GatewayFixture gateway) : IClassFixture<GatewayFixture>
{
    private static JsonObject Chat(string model = GatewayFixture.ChatAlias, string content = "hello there")
        => new()
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    [Theory]
    [InlineData(GatewayFixture.ChatAlias)]
    [InlineData(GatewayFixture.OllamaAlias)]
    [HappyPath("POST /v1/chat/completions")]
    public async Task Non_streaming_completion_returns_an_openai_chat_completion_for_both_protocols(string alias)
    {
        var response = await gateway.Client.PostAsync("/v1/chat/completions", Json(Chat(alias)), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var requestId = response.Headers.GetValues("X-Request-Id").Single();
        Guid.Parse(requestId).Version.ShouldBe(7);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("object").GetString().ShouldBe("chat.completion");
        body.GetProperty("id").GetString().ShouldBe($"chatcmpl-{requestId}");
        body.GetProperty("model").GetString().ShouldBe(alias, "the alias is echoed, never the Provider Model ID");
        var choice = body.GetProperty("choices")[0];
        choice.GetProperty("message").GetProperty("role").GetString().ShouldBe("assistant");
        choice.GetProperty("message").GetProperty("content").GetString()!.ShouldContain("hello there");
        choice.GetProperty("finish_reason").GetString().ShouldBe("stop");
        body.GetProperty("usage").GetProperty("total_tokens").GetInt32().ShouldBeGreaterThan(0);
        body.GetProperty("modelswitch").GetProperty("request_id").GetString().ShouldBe(requestId);
        body.GetProperty("modelswitch").GetProperty("attempts").GetInt32().ShouldBe(1);
        body.ToString().ShouldNotContain("mock-echo");
    }

    [Fact]
    [TriggersError("unsupported_parameter")]
    public async Task Unsupported_parameter_is_a_400_problem_with_errors_and_the_openai_error_member()
    {
        var body = Chat();
        body["tools"] = new JsonArray();
        body["frobnicate"] = 1;

        var response = await gateway.Client.PostAsync("/v1/chat/completions", Json(body), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.Contains("X-Request-Id").ShouldBeTrue();
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("status").GetInt32().ShouldBe(400);
        problem.GetProperty("errorCode").GetString().ShouldBe("unsupported_parameter");
        problem.GetProperty("errors").GetProperty("tools").GetArrayLength().ShouldBe(1);
        problem.GetProperty("errors").GetProperty("frobnicate").GetArrayLength().ShouldBe(1);
        var error = problem.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("unsupported_parameter");
        error.GetProperty("type").GetString().ShouldBe("invalid_request_error");
        error.GetProperty("param").GetString().ShouldBe("tools");
        error.GetProperty("request_id").GetString().ShouldBe(response.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    [TriggersError("invalid_request")]
    public async Task Invalid_field_is_a_400_invalid_request_naming_the_field()
    {
        var body = Chat();
        body["temperature"] = 5;

        var response = await gateway.Client.PostAsync("/v1/chat/completions", Json(body), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_request");
        problem.GetProperty("errors").GetProperty("temperature").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Malformed_json_is_a_400_invalid_request()
    {
        var response = await gateway.Client.PostAsync(
            "/v1/chat/completions", new StringContent("{not json", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_request");
    }

    [Fact]
    [TriggersError("model_not_found")]
    public async Task Unknown_alias_is_a_404_model_not_found()
    {
        var response = await gateway.Client.PostAsync("/v1/chat/completions", Json(Chat("nope")), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("model_not_found");
    }

    [Fact]
    [TriggersError("invalid_api_key")]
    public async Task Missing_key_is_a_401_problem_with_x_request_id()
    {
        var response = await gateway.Anonymous.PostAsync("/v1/chat/completions", Json(Chat()), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.Contains("X-Request-Id").ShouldBeTrue();
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("invalid_api_key");
        problem.GetProperty("error").GetProperty("type").GetString().ShouldBe("authentication_error");
    }

    [Fact]
    public async Task Wrong_key_is_a_401()
    {
        using var client = gateway.Authenticated("msk_00000000_" + new string('A', 43));

        var response = await client.PostAsync("/v1/chat/completions", Json(Chat()), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [TriggersError("rate_limit_exceeded")]
    public async Task Repeated_invalid_keys_from_one_client_are_throttled_with_429_on_that_instance_only()
    {
        // A host of its own: the throttle is per instance, and the shared host's other tests must not see it.
        await using var host = gateway.StartHost();
        using var wrong = host.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "msk_00000000_" + new string('A', 43));
        using var valid = gateway.ClientFor(host);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 20; i++)
        {
            (await wrong.GetAsync("/v1/models", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var throttled = await valid.GetAsync("/v1/models", ct);

        throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeInRange(1, 60);
        var problem = await throttled.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("rate_limit_exceeded");
        (await gateway.Client.GetAsync("/v1/models", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [TriggersError("insufficient_scope")]
    public async Task Key_without_the_inference_scope_is_a_403_insufficient_scope()
    {
        var response = await gateway.ModelsOnly.PostAsync("/v1/chat/completions", Json(Chat()), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("insufficient_scope");
    }

    [Fact]
    [TriggersError("payload_too_large")]
    public async Task Oversized_body_is_a_413_payload_too_large()
    {
        var body = new StringContent(new string('x', 1024 * 1024 + 1), Encoding.UTF8, "application/json");

        var response = await gateway.Client.PostAsync("/v1/chat/completions", body, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.Contains("X-Request-Id").ShouldBeTrue();
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("payload_too_large");
    }

    [Fact]
    [TriggersError("unsupported_media_type")]
    public async Task Wrong_content_type_is_a_415()
    {
        var response = await gateway.Client.PostAsync(
            "/v1/chat/completions", new StringContent(Chat().ToJsonString(), Encoding.UTF8, "text/plain"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("unsupported_media_type");
    }

    [Fact]
    [TriggersError("provider_unavailable")]
    public async Task Provider_failure_is_a_problem_without_provider_text()
    {
        // The Mock Provider's control endpoint scripts a fault for this model; the gateway never forwards X-Mock-* headers.
        using var control = new HttpClient { BaseAddress = gateway.MockAddress };
        var fault = await control.PostAsync(
            "/__scenarios", new StringContent("{\"model\":\"mock-echo\",\"fault\":\"status=503\"}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        fault.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        try
        {
            var response = await gateway.Client.PostAsync("/v1/chat/completions", Json(Chat()), TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            problem.GetProperty("error").GetProperty("code").GetString().ShouldBe("provider_unavailable");
        }
        finally
        {
            await control.DeleteAsync("/__scenarios", TestContext.Current.CancellationToken);
        }
    }
}
