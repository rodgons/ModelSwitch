using System.Diagnostics;
using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.Abstractions;
using Shouldly;

namespace ModelSwitch.AdapterContractTests;

public abstract partial class AdapterContract<TAdapter>
{
    protected const string ProviderErrorText = "CANARY-PROVIDER-ERROR-TEXT";

    // ---- HTTP status classification (docs/routing-and-resilience.md section 3) -----------------------------------------

    [Theory]
    [MemberData(nameof(ContractCases.HttpStatuses), MemberType = typeof(ContractCases))]
    public async Task Complete_classifies_http_status(int status, FailureClass expectedClass, string expectedCode)
    {
        StubJson(Wire.ChatPath, status, Wire.EncodeError(null, ProviderErrorText));

        var failure = FailureOf(await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken));

        failure.Class.ShouldBe(expectedClass);
        failure.Code.ShouldBe(expectedCode);
        failure.HttpStatus.ShouldBe(status);
        failure.RequestSent.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(ContractCases.HttpStatuses), MemberType = typeof(ContractCases))]
    public async Task Stream_classifies_http_status_as_a_single_failed_event(int status, FailureClass expectedClass, string expectedCode)
    {
        StubJson(Wire.ChatPath, status, Wire.EncodeError(null, ProviderErrorText));

        var events = await StreamAllAsync(Context(), ChatRequest());

        events.Count.ShouldBe(1);
        var failure = FailureOf(events);
        failure.Class.ShouldBe(expectedClass);
        failure.Code.ShouldBe(expectedCode);
        failure.HttpStatus.ShouldBe(status);
        failure.RequestSent.ShouldBeTrue();
    }

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(500)]
    public async Task A_non_json_error_body_is_still_classified_by_status(int status)
    {
        StubJson(Wire.ChatPath, status, "<html><body>CANARY-HTML upstream unavailable</body></html>", "text/html");

        var failure = FailureOf(await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken));

        failure.Class.ShouldBe(FailureClass.Transient);
        failure.Code.ShouldBe(ProviderFailureCodes.ServerError);
        failure.HttpStatus.ShouldBe(status);
    }

    [Theory]
    [MemberData(nameof(ContractCases.BillingErrorCodes), MemberType = typeof(ContractCases))]
    public async Task A_429_with_a_billing_error_code_is_permanent(string providerCode)
    {
        StubJson(Wire.ChatPath, 429, Wire.EncodeError(providerCode, ProviderErrorText));

        var failure = FailureOf(await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken));

        failure.Class.ShouldBe(FailureClass.Permanent);
        failure.Code.ShouldBe(ProviderFailureCodes.QuotaExhausted);
        failure.HttpStatus.ShouldBe(429);
    }

    [Fact]
    public async Task A_429_with_a_rate_limit_error_code_is_transient()
    {
        StubJson(Wire.ChatPath, 429, Wire.EncodeError("rate_limit_exceeded", ProviderErrorText));

        var failure = FailureOf(await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken));

        failure.Class.ShouldBe(FailureClass.Transient);
        failure.Code.ShouldBe(ProviderFailureCodes.RateLimited);
    }

    [Fact]
    public async Task A_429_exposes_retry_after_given_in_seconds()
    {
        StubJson(Wire.ChatPath, 429, Wire.EncodeError(null, ProviderErrorText), configure: r => r.WithHeader("Retry-After", "3"));

        var failure = FailureOf(await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken));

        failure.Class.ShouldBe(FailureClass.Transient);
        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_429_exposes_retry_after_given_as_an_http_date()
    {
        var date = DateTimeOffset.UtcNow.AddSeconds(30).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        StubJson(Wire.ChatPath, 429, Wire.EncodeError(null, ProviderErrorText), configure: r => r.WithHeader("Retry-After", date));

        var failure = FailureOf(await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken));

        failure.RetryAfter.ShouldNotBeNull();
        failure.RetryAfter.Value.ShouldBeInRange(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(31));
    }

    [Fact]
    public async Task A_429_without_retry_after_reports_none()
    {
        StubJson(Wire.ChatPath, 429, Wire.EncodeError(null, ProviderErrorText));

        var failure = FailureOf(await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken));

        failure.RetryAfter.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(ContractCases.Streaming), MemberType = typeof(ContractCases))]
    public async Task Context_length_exceeded_is_permanent_with_its_own_code(bool streaming)
    {
        var body = Wire.EncodeContextLengthExceeded();
        Assert.SkipWhen(body is null, "The Provider has no distinguishable context-length error.");
        StubJson(Wire.ChatPath, 400, body!);

        var failure = await FailureAsync(streaming, Context());

        failure.Class.ShouldBe(FailureClass.Permanent);
        failure.Code.ShouldBe(ProviderFailureCodes.ContextLengthExceeded);
        failure.HttpStatus.ShouldBe(400);
    }

    // ---- network failures -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ContractCases.Streaming), MemberType = typeof(ContractCases))]
    public async Task Connection_refused_is_transient_and_the_request_was_not_sent(bool streaming)
    {
        var ctx = ContextFor(ClosedPortEndpoint());

        var failure = await FailureAsync(streaming, ctx);

        failure.Class.ShouldBe(FailureClass.Transient);
        failure.Code.ShouldBe(ProviderFailureCodes.ConnectFailed);
        failure.RequestSent.ShouldBeFalse();
        failure.HttpStatus.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(ContractCases.Streaming), MemberType = typeof(ContractCases))]
    public async Task Headers_that_never_arrive_within_the_timeout_are_ambiguous(bool streaming)
    {
        Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(Wire.ChatPath).UsingAnyMethod())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(2)));
        var stopwatch = Stopwatch.StartNew();

        var failure = await FailureAsync(streaming, Context(timeout: TimeSpan.FromMilliseconds(300)));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5), "the Attempt timeout must be enforced by the Adapter");
        failure.Class.ShouldBe(FailureClass.Ambiguous);
        failure.Code.ShouldBe(ProviderFailureCodes.Timeout);
        failure.RequestSent.ShouldBeTrue();
        failure.HttpStatus.ShouldBeNull();
    }

    [Fact]
    public async Task A_stream_cut_mid_way_fails_ambiguous_after_the_deltas()
    {
        await using var raw = new RawStreamServer(Wire.StreamContentType, Wire.EncodeStreamDeltas(["a", "b", "c"]), StreamEnding.Cut);

        var events = await StreamAllAsync(ContextFor(raw.BaseAddress), ChatRequest());

        events.OfType<StreamEvent.ContentDelta>().Select(d => d.Text).ShouldBe(["a", "b", "c"]);
        var failure = FailureOf(events);
        failure.Class.ShouldBe(FailureClass.Ambiguous);
        failure.Code.ShouldBe(ProviderFailureCodes.ConnectionReset);
        failure.RequestSent.ShouldBeTrue();
        events.Any(e => e is StreamEvent.Finish).ShouldBeFalse();
    }

    [Fact]
    public async Task A_non_streaming_body_cut_mid_way_fails_ambiguous_connection_reset()
    {
        // The response headers arrived, so the request was sent and may have been billed: ambiguous, never retried on the same model.
        await using var raw = new RawStreamServer("application/json", ["{\"id\":\"cut\",\"choices\":[{\"index\":0,"], StreamEnding.Cut);

        var failure = FailureOf(await Adapter.CompleteAsync(ContextFor(raw.BaseAddress), ChatRequest(), TestToken));

        failure.Class.ShouldBe(FailureClass.Ambiguous);
        failure.Code.ShouldBe(ProviderFailureCodes.ConnectionReset);
        failure.RequestSent.ShouldBeTrue();
    }

    // ---- malformed and embedded errors ----------------------------------------------------------------------------

    [Fact]
    public async Task A_malformed_json_body_is_a_transient_invalid_provider_response()
    {
        StubJson(Wire.ChatPath, 200, Wire.EncodeMalformedBody());

        var failure = FailureOf(await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken));

        failure.Class.ShouldBe(FailureClass.Transient);
        failure.Code.ShouldBe(ProviderFailureCodes.InvalidProviderResponse);
    }

    [Fact]
    public async Task A_malformed_stream_frame_fails_transient_invalid_provider_response()
    {
        StubStreamBody([.. Wire.EncodeStreamDeltas(["a"]), Wire.EncodeMalformedFrame()]);

        var events = await StreamAllAsync(Context(), ChatRequest());

        var failure = FailureOf(events);
        failure.Class.ShouldBe(FailureClass.Transient);
        failure.Code.ShouldBe(ProviderFailureCodes.InvalidProviderResponse);
        events.Any(e => e is StreamEvent.Finish).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(ContractCases.InnerErrors), MemberType = typeof(ContractCases))]
    public async Task An_error_object_in_a_200_response_is_classified_by_its_inner_code(bool streaming, InnerErrorKind kind, FailureClass expectedClass)
    {
        Assert.SkipWhen(kind != InnerErrorKind.Unspecified && !Wire.InnerErrorsAreClassifiable, "The Provider's embedded errors carry no code to classify.");
        if (streaming)
        {
            StubStreamBody([.. Wire.EncodeStreamDeltas(["a"]), Wire.EncodeErrorInStreamFrame(kind, ProviderErrorText)]);
        }
        else
        {
            StubJson(Wire.ChatPath, 200, Wire.EncodeErrorInOkBody(kind, ProviderErrorText));
        }

        var failure = await FailureAsync(streaming, Context());

        failure.Class.ShouldBe(expectedClass);
        if (kind == InnerErrorKind.Quota)
        {
            failure.Code.ShouldBe(ProviderFailureCodes.QuotaExhausted);
        }
        else if (kind == InnerErrorKind.RateLimit)
        {
            failure.Code.ShouldBe(ProviderFailureCodes.RateLimited);
        }
    }

    [Fact]
    public async Task A_failed_stream_event_never_carries_provider_free_text()
    {
        StubJson(Wire.ChatPath, 500, Wire.EncodeError("server_error", ProviderErrorText));

        var failure = FailureOf(await StreamAllAsync(Context(), ChatRequest()));

        System.Text.Json.JsonSerializer.Serialize(failure).ShouldNotContain(ProviderErrorText);
        failure.Code.ShouldNotContain(ProviderErrorText);
    }

    // ---- connectivity check ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_reports_a_classified_failure_for_an_error_status()
    {
        StubJson(Wire.CheckPath, 503, Wire.EncodeError(null, ProviderErrorText));

        var result = await Adapter.CheckAsync(Context(), TestToken);

        result.Reachable.ShouldBeFalse();
        result.Failure.ShouldNotBeNull().Class.ShouldBe(FailureClass.Transient);
    }

    [Fact]
    public async Task Check_reports_connect_failures_with_the_request_not_sent()
    {
        var result = await Adapter.CheckAsync(ContextFor(ClosedPortEndpoint()), TestToken);

        result.Reachable.ShouldBeFalse();
        var failure = result.Failure.ShouldNotBeNull();
        failure.Code.ShouldBe(ProviderFailureCodes.ConnectFailed);
        failure.RequestSent.ShouldBeFalse();
    }
}
