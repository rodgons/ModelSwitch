using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Adapters.OpenAICompatible;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ModelSwitch.AdapterContractTests.OpenAICompatible;

/// <summary>
/// Replays the fixtures verbatim (recorded, provisional and synthetic) through the Adapter, and covers what the shared suite does not:
/// OpenRouter's quirks, the per-Provider options and the fixtures' own stamps. The shared suite renders fixtures as templates; these
/// tests send them exactly as stored, so a re-recording that changes the wire shape fails here first.
/// </summary>
public sealed class OpenAICompatibleFixtureTests : IAsyncLifetime
{
    private const string _chatPath = "/api/v1/chat/completions";
    private static readonly string[] _allowedSources = ["recorded-openrouter", "provisional", "synthetic"];

    private WireMockServer _server = null!;
    private HttpClient _http = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _server = WireMockServer.Start();
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _server.Dispose();
        _http.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public static TheoryData<string> AllFixtures() => [.. OpenAiFixtures.Names];

    public static TheoryData<string, FailureClass, string> ErrorFixtures() => new()
    {
        { "error-400-invalid-request.json", FailureClass.Permanent, ProviderFailureCodes.BadRequest },
        { "error-400-context-length.json", FailureClass.Permanent, ProviderFailureCodes.ContextLengthExceeded },
        { "error-401.json", FailureClass.Permanent, ProviderFailureCodes.Unauthorized },
        { "error-404-model.json", FailureClass.Permanent, ProviderFailureCodes.NotFound },
        { "synthetic-openai-400.json", FailureClass.Permanent, ProviderFailureCodes.BadRequest },
        { "synthetic-openai-400-context-length.json", FailureClass.Permanent, ProviderFailureCodes.ContextLengthExceeded },
        { "synthetic-openai-401.json", FailureClass.Permanent, ProviderFailureCodes.Unauthorized },
        { "synthetic-openai-404.json", FailureClass.Permanent, ProviderFailureCodes.NotFound },
        { "synthetic-429-rate-limit.json", FailureClass.Transient, ProviderFailureCodes.RateLimited },
        { "synthetic-429-insufficient-quota.json", FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted },
        { "synthetic-402-insufficient-credits.json", FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted },
        { "synthetic-500.json", FailureClass.Transient, ProviderFailureCodes.ServerError },
        { "synthetic-524.json", FailureClass.Ambiguous, ProviderFailureCodes.EdgeTimeout },
    };

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_fixture_carries_an_honest_source_stamp(string name)
    {
        var fixture = OpenAiFixtures.Load(name);

        _allowedSources.ShouldContain(fixture.Source);
        if (name.StartsWith("synthetic-", StringComparison.Ordinal))
        {
            fixture.Source.ShouldBe("synthetic", "a hand-written fixture must never claim to be a recording");
        }
        else
        {
            fixture.Source.ShouldNotBe("synthetic", "fixtures that scripts/record-fixtures produces are recorded or provisional");
        }
    }

    [Theory]
    [MemberData(nameof(ErrorFixtures))]
    public async Task Error_fixtures_are_classified_by_status_and_code_through_both_paths(string name, FailureClass expectedClass, string expectedCode)
    {
        var fixture = Serve(name);

        var complete = ((CompletionOutcome.Failure)await Adapter().CompleteAsync(Context(), ChatReq(), Token)).Error;
        var streamed = (await Collect(Adapter().StreamAsync(Context(), ChatReq(), Token))).ShouldHaveSingleItem().ShouldBeOfType<StreamEvent.Failed>().Error;

        foreach (var failure in new[] { complete, streamed })
        {
            failure.Class.ShouldBe(expectedClass);
            failure.Code.ShouldBe(expectedCode);
            failure.HttpStatus.ShouldBe(fixture.Status);
            failure.RequestSent.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_429_fixture_exposes_its_retry_after()
    {
        Serve("synthetic-429-rate-limit.json");

        var failure = ((CompletionOutcome.Failure)await Adapter().CompleteAsync(Context(), ChatReq(), Token)).Error;

        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
    }

    // Expectations are read out of the fixture itself, so the same tests hold for the provisional text and for a re-recording.

    [Theory]
    [InlineData("completion-text.json", FinishReason.Stop)]
    [InlineData("completion-length.json", FinishReason.Length)]
    [InlineData("completion-json-object.json", FinishReason.Stop)]
    [InlineData("completion-json-schema.json", FinishReason.Stop)]
    public async Task Completion_fixtures_are_parsed(string name, FinishReason finish)
    {
        var body = Serve(name).BodyNode;
        var usage = body["usage"]!;

        var success = (CompletionOutcome.Success)await Adapter().CompleteAsync(Context(), ChatReq(), Token);

        success.Text.ShouldBe(body["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        success.FinishReason.ShouldBe(finish);
        success.Usage.ShouldBe(new NeutralUsage(
            usage["prompt_tokens"]!.GetValue<int>(),
            usage["completion_tokens"]!.GetValue<int>(),
            usage["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>()));
    }

    [Theory]
    [InlineData("stream-text.sse", FinishReason.Stop)]
    [InlineData("stream-length.sse", FinishReason.Length)]
    public async Task Stream_fixtures_are_parsed(string name, FinishReason finish)
    {
        Serve(name);
        var chunks = OpenAiFixtures.Chunks(name);
        var expectedText = string.Concat(chunks
            .Where(c => c["choices"]!.AsArray().Count > 0)
            .Select(c => c["choices"]![0]!["delta"]?["content"]?.GetValue<string>()));
        var usage = chunks.Last(c => c["usage"] is not null)["usage"]!;

        var events = await Collect(Adapter().StreamAsync(Context(), ChatReq(), Token));

        string.Concat(events.OfType<StreamEvent.ContentDelta>().Select(d => d.Text)).ShouldBe(expectedText);
        events.OfType<StreamEvent.Finish>().Single().Reason.ShouldBe(finish);
        events.OfType<StreamEvent.Usage>().Single().Value.ShouldBe(new NeutralUsage(
            usage["prompt_tokens"]!.GetValue<int>(),
            usage["completion_tokens"]!.GetValue<int>(),
            usage["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>()));
        events.Last().ShouldBeOfType<StreamEvent.Usage>();
    }

    // ---- OpenRouter quirks (research/hosted-providers.md section 4) -------------------------------------------------

    [Fact]
    public async Task A_200_non_streaming_body_that_is_an_error_object_fails_classified_by_its_numeric_code()
    {
        Serve("synthetic-200-error-body.json");

        var failure = ((CompletionOutcome.Failure)await Adapter().CompleteAsync(Context(), ChatReq(), Token)).Error;

        failure.Class.ShouldBe(FailureClass.Transient);
        failure.Code.ShouldBe(ProviderFailureCodes.RateLimited);
        failure.HttpStatus.ShouldBe(200);
    }

    [Fact]
    public async Task Usage_on_a_chunk_that_also_has_choices_is_reported_once_even_when_repeated()
    {
        Serve("synthetic-stream-usage-with-choices.sse");

        var events = await Collect(Adapter().StreamAsync(Context(), ChatReq(), Token));

        events.OfType<StreamEvent.Usage>().ShouldHaveSingleItem().Value.ShouldBe(new NeutralUsage(24, 2, 0));
        events.OfType<StreamEvent.Finish>().ShouldHaveSingleItem().Reason.ShouldBe(FinishReason.Stop);
    }

    [Fact]
    public async Task An_error_chunk_after_the_first_token_fails_the_stream_after_the_deltas()
    {
        Serve("synthetic-stream-midstream-error.sse");

        var events = await Collect(Adapter().StreamAsync(Context(), ChatReq(), Token));

        events.OfType<StreamEvent.ContentDelta>().Select(d => d.Text).ShouldBe(["Par"]);
        var failure = events.Last().ShouldBeOfType<StreamEvent.Failed>().Error;
        failure.Class.ShouldBe(FailureClass.Transient);
        failure.Code.ShouldBe(ProviderFailureCodes.ServerError);
        events.OfType<StreamEvent.Finish>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_numeric_inner_code_of_402_is_a_permanent_quota_failure()
    {
        _server.Given(Request.Create().WithPath(_chatPath)).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "application/json").WithBody("""{"error":{"message":"Insufficient credits","code":402}}"""));

        var failure = ((CompletionOutcome.Failure)await Adapter().CompleteAsync(Context(), ChatReq(), Token)).Error;

        failure.Class.ShouldBe(FailureClass.Permanent);
        failure.Code.ShouldBe(ProviderFailureCodes.QuotaExhausted);
    }

    [Fact]
    public async Task Together_style_flat_cached_tokens_are_read()
    {
        _server.Given(Request.Create().WithPath(_chatPath)).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "application/json").WithBody(
                """{"choices":[{"message":{"content":"x"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":5,"cached_tokens":64}}"""));

        var success = (CompletionOutcome.Success)await Adapter().CompleteAsync(Context(), ChatReq(), Token);

        success.Usage.ShouldBe(new NeutralUsage(100, 5, 64));
    }

    // ---- per-Provider options -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Max_output_tokens_is_sent_as_max_completion_tokens_by_default()
    {
        Serve("completion-text.json");

        await Adapter().CompleteAsync(Context(), ChatReq(), Token);

        var body = LastBody();
        body["max_completion_tokens"]!.GetValue<int>().ShouldBe(64);
        body["max_tokens"].ShouldBeNull();
        body["provider"].ShouldBeNull("no upstream is pinned unless asked");
    }

    [Fact]
    public async Task The_legacy_flag_sends_max_tokens_instead()
    {
        Serve("completion-text.json");

        await Adapter(new OpenAICompatibleOptions { UseLegacyMaxTokens = true }).CompleteAsync(Context(), ChatReq(), Token);

        var body = LastBody();
        body["max_tokens"]!.GetValue<int>().ShouldBe(64);
        body["max_completion_tokens"].ShouldBeNull();
    }

    [Fact]
    public async Task Pinned_upstreams_are_sent_as_provider_only_without_fallbacks()
    {
        Serve("completion-text.json");

        await Adapter(new OpenAICompatibleOptions { PinnedUpstreams = ["openai"] }).CompleteAsync(Context(), ChatReq(), Token);

        var provider = LastBody()["provider"]!;
        provider["only"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["openai"]);
        provider["allow_fallbacks"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task A_custom_auth_header_carries_the_bare_secret()
    {
        Serve("completion-text.json");

        await Adapter(new OpenAICompatibleOptions { AuthHeaderName = "api-key" }).CompleteAsync(Context(), ChatReq(), Token);

        var headers = _server.LogEntries.Single().RequestMessage!.Headers!;
        headers["api-key"].ShouldBe(["secret-value"]);
        headers.Keys.ShouldNotContain(k => string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Streaming_requests_ask_for_usage()
    {
        Serve("stream-text.sse");

        await Collect(Adapter().StreamAsync(Context(), ChatReq(), Token));

        var options = LastBody()["stream_options"]!;
        options["include_usage"]!.GetValue<bool>().ShouldBeTrue();
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static AdapterContext Context(Uri? endpoint = null, TimeSpan? timeout = null) =>
        new(endpoint ?? new Uri("http://127.0.0.1/api/v1"), "secret-value", "openai/gpt-4.1-nano", timeout ?? TimeSpan.FromSeconds(10));

    private static NeutralChatRequest ChatReq() =>
        new([new NeutralMessage(MessageRole.User, "synthetic prompt")]) { MaxOutputTokens = 64 };

    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var item in stream.WithCancellation(Token))
        {
            events.Add(item);
        }

        return events;
    }

    private OpenAICompatibleAdapter Adapter(OpenAICompatibleOptions? options = null) =>
        new(_http, TimeProvider.System, NullLogger<OpenAICompatibleAdapter>.Instance, options);

    private AdapterContext Context() => Context(new Uri($"http://127.0.0.1:{_server.Port}/api/v1"));

    private OpenAiFixture Serve(string name)
    {
        var fixture = OpenAiFixtures.Load(name);
        var response = Response.Create().WithStatusCode(fixture.Status).WithBody(fixture.Body);
        foreach (var (key, value) in fixture.Headers)
        {
            response = response.WithHeader(key, value);
        }

        _server.Given(Request.Create().WithPath(_chatPath)).RespondWith(response);
        return fixture;
    }

    private JsonNode LastBody() =>
        JsonNode.Parse(_server.LogEntries.Last(e => e.RequestMessage?.Path == _chatPath).RequestMessage!.BodyData!.BodyAsString!)!;
}
