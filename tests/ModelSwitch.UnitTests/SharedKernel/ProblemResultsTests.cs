using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelSwitch.Inference.Application;
using ModelSwitch.Inference.Endpoints;
using ModelSwitch.SharedKernel;
using ModelSwitch.SharedKernel.Errors;
using ModelSwitch.SharedKernel.Http;
using Shouldly;

namespace ModelSwitch.UnitTests.SharedKernel;

public sealed class ProblemResultsTests
{
    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonElement> WriteAsync(DefaultHttpContext context, Error error)
    {
        await ProblemResults.From(context, error).ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body).RootElement.Clone();
    }

    [Fact]
    public async Task Inference_errors_are_problem_json_with_the_openai_error_member()
    {
        var context = Context("/v1/chat/completions");
        await new RequestIdMiddleware(_ => Task.CompletedTask, new FixedIds(), NullLogger<RequestIdMiddleware>.Instance).InvokeAsync(context);

        var body = await WriteAsync(context, new Error(ErrorCode.NoEligibleModel, "No eligible model can serve this request."));

        context.Response.StatusCode.ShouldBe(422);
        context.Response.ContentType.ShouldStartWith("application/problem+json");
        body.GetProperty("type").GetString().ShouldBe("/errors/no_eligible_model");
        body.GetProperty("title").GetString().ShouldBe("No eligible model");
        body.GetProperty("instance").GetString().ShouldBe("/v1/chat/completions");
        body.GetProperty("errorCode").GetString().ShouldBe("no_eligible_model");
        body.GetProperty("requestId").GetString().ShouldBe(FixedIds.Id.ToString());
        var error = body.GetProperty("error");
        error.GetProperty("message").GetString().ShouldBe("No eligible model can serve this request.");
        error.GetProperty("type").GetString().ShouldBe("routing_error");
        error.GetProperty("code").GetString().ShouldBe("no_eligible_model");
        error.GetProperty("param").ValueKind.ShouldBe(JsonValueKind.Null);
        error.GetProperty("request_id").GetString().ShouldBe(FixedIds.Id.ToString());
        context.Response.Headers[RequestId.HeaderName].ToString().ShouldBe(FixedIds.Id.ToString());
    }

    [Theory]
    [InlineData(0.2, "1")]
    [InlineData(1.0, "1")]
    [InlineData(29.4, "30")]
    public async Task A_retry_after_is_emitted_in_whole_seconds_rounded_up(double seconds, string expected)
    {
        var context = Context("/v1/chat/completions");

        await WriteAsync(context, new Error(ErrorCode.ProviderUnavailable, "Unavailable.") { RetryAfter = TimeSpan.FromSeconds(seconds) });

        context.Response.StatusCode.ShouldBe(503);
        context.Response.Headers.RetryAfter.ToString().ShouldBe(expected);
    }

    [Fact]
    public async Task No_retry_after_header_without_a_known_retry_after()
    {
        var context = Context("/v1/chat/completions");

        await WriteAsync(context, new Error(ErrorCode.ProviderUnavailable, "Unavailable."));

        context.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
    }

    [Fact]
    public async Task Admin_errors_have_no_openai_error_member()
    {
        var body = await WriteAsync(Context("/admin/tenants"), new Error(ErrorCode.NotFound, "Not found."));

        body.TryGetProperty("error", out _).ShouldBeFalse();
        body.GetProperty("errorCode").GetString().ShouldBe("not_found");
    }

    [Fact]
    public async Task Field_errors_become_the_errors_extension_and_the_first_field_is_param()
    {
        var error = Error.Validation("bad", new Dictionary<string, string[]> { ["messages[3].content"] = ["is required"] });

        var body = await WriteAsync(Context("/v1/chat/completions"), error);

        body.GetProperty("errors").GetProperty("messages[3].content")[0].GetString().ShouldBe("is required");
        body.GetProperty("error").GetProperty("param").GetString().ShouldBe("messages[3].content");
    }

    [Theory]
    [InlineData("invalid_request", 400)]
    [InlineData("invalid_api_key", 401)]
    [InlineData("budget_exceeded", 402)]
    [InlineData("payload_too_large", 413)]
    [InlineData("unsupported_media_type", 415)]
    [InlineData("rate_limit_exceeded", 429)]
    [InlineData("provider_unavailable", 503)]
    [InlineData("deadline_exceeded", 504)]
    [InlineData("internal_error", 500)]
    public async Task Status_comes_from_the_error_code(string code, int status)
    {
        ErrorCode.TryParse(code, out var parsed).ShouldBeTrue();
        var context = Context("/v1/models");

        await WriteAsync(context, new Error(parsed, "x"));

        context.Response.StatusCode.ShouldBe(status);
    }

    [Fact]
    public void Unhandled_exceptions_map_to_internal_error_without_their_text()
    {
        var error = HttpPipelineExtensions.Map(new InvalidOperationException("prompt: secret"));

        error.Code.ShouldBe(ErrorCode.InternalError);
        error.Message.ShouldNotContain("secret");
    }

    [Fact]
    public void Oversized_bodies_map_to_413_and_other_bad_requests_to_400()
    {
        HttpPipelineExtensions.Map(new BadHttpRequestException("x", StatusCodes.Status413PayloadTooLarge)).Code.ShouldBe(ErrorCode.PayloadTooLarge);
        HttpPipelineExtensions.Map(new BadHttpRequestException("x", StatusCodes.Status400BadRequest)).Code.ShouldBe(ErrorCode.InvalidRequest);
    }

    [Fact]
    public async Task Request_id_middleware_sets_a_uuid_v7_header_and_item()
    {
        var context = Context("/health");
        var ids = new GuidGenerator7();

        await new RequestIdMiddleware(_ => Task.CompletedTask, ids, NullLogger<RequestIdMiddleware>.Instance).InvokeAsync(context);

        var header = Guid.Parse(context.Response.Headers[RequestId.HeaderName].ToString());
        header.Version.ShouldBe(7);
        RequestId.Get(context).ShouldBe(header);
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("APPLICATION/JSON", true)]
    [InlineData("text/plain", false)]
    [InlineData("application/x-www-form-urlencoded", false)]
    [InlineData(null, false)]
    public async Task Guard_rejects_other_content_types_with_415(string? contentType, bool allowed)
    {
        var context = PostChat(contentType, null);
        var called = false;

        await new InferenceRequestGuardMiddleware(_ => { called = true; return Task.CompletedTask; }, new InferenceOptions()).InvokeAsync(context);

        called.ShouldBe(allowed);
        if (!allowed)
        {
            context.Response.StatusCode.ShouldBe(415);
        }
    }

    [Fact]
    public async Task Guard_rejects_a_declared_length_over_the_limit_with_413()
    {
        var context = PostChat("application/json", 11);

        await new InferenceRequestGuardMiddleware(_ => Task.CompletedTask, new InferenceOptions { MaxRequestBodyBytes = 10 }).InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(413);
    }

    [Fact]
    public async Task Guard_caps_the_kestrel_body_size_for_chunked_bodies()
    {
        var context = PostChat("application/json", null);
        var feature = new FakeBodySizeFeature();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(feature);

        await new InferenceRequestGuardMiddleware(_ => Task.CompletedTask, new InferenceOptions { MaxRequestBodyBytes = 10 }).InvokeAsync(context);

        feature.MaxRequestBodySize.ShouldBe(10);
    }

    [Fact]
    public async Task Guard_ignores_other_routes()
    {
        var context = Context("/v1/models");
        context.Request.Method = "GET";
        var called = false;

        await new InferenceRequestGuardMiddleware(_ => { called = true; return Task.CompletedTask; }, new InferenceOptions()).InvokeAsync(context);

        called.ShouldBeTrue();
    }

    private static DefaultHttpContext PostChat(string? contentType, long? length)
    {
        var context = Context("/v1/chat/completions");
        context.Request.Method = "POST";
        context.Request.ContentType = contentType;
        context.Request.ContentLength = length;
        return context;
    }

    private sealed class FakeBodySizeFeature : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class FixedIds : IGuidGenerator
    {
        public static readonly Guid Id = Guid.Parse("0192f0c8-0000-7000-8000-000000000001");

        public Guid NewGuid() => Id;
    }

    private sealed class GuidGenerator7 : IGuidGenerator
    {
        public Guid NewGuid() => Guid.CreateVersion7();
    }
}
