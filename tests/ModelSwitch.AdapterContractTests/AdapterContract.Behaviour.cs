using System.Diagnostics;
using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.Abstractions;
using Shouldly;

namespace ModelSwitch.AdapterContractTests;

public abstract partial class AdapterContract<TAdapter>
{
    // ---- cancellation ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_mid_stream_stops_reading_within_100ms_and_aborts_the_connection()
    {
        await using var raw = new RawStreamServer(Wire.StreamContentType, Wire.EncodeStreamDeltas(["a", "b"]), StreamEnding.Hold);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        await using var enumerator = Adapter.StreamAsync(ContextFor(raw.BaseAddress), ChatRequest(), cts.Token).GetAsyncEnumerator(cts.Token);

        var received = new List<StreamEvent>();
        while (received.Count < 2 && await enumerator.MoveNextAsync())
        {
            received.Add(enumerator.Current);
        }

        received.OfType<StreamEvent.ContentDelta>().Select(d => d.Text).ShouldBe(["a", "b"]);

        var stopwatch = Stopwatch.StartNew();
        var pending = enumerator.MoveNextAsync().AsTask();
        await cts.CancelAsync();
        await ShouldThrowCancellationAsync(async () => await pending, stopwatch);

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(100));
        (await CompletesWithinAsync(raw.ClientDisconnected, TimeSpan.FromSeconds(5))).ShouldBeTrue("the Adapter must abort the HTTP connection on cancellation");
    }

    [Fact]
    public async Task Disposing_the_enumerator_early_aborts_the_connection()
    {
        await using var raw = new RawStreamServer(Wire.StreamContentType, Wire.EncodeStreamDeltas(["a", "b"]), StreamEnding.Hold);
        var enumerator = Adapter.StreamAsync(ContextFor(raw.BaseAddress), ChatRequest(), TestToken).GetAsyncEnumerator(TestToken);

        for (var i = 0; i < 2; i++)
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
        }

        await enumerator.DisposeAsync();

        (await CompletesWithinAsync(raw.ClientDisconnected, TimeSpan.FromSeconds(5))).ShouldBeTrue("disposing the enumerator must abort the HTTP response");
    }

    [Fact]
    public async Task Complete_cancelled_while_waiting_for_the_response_throws_promptly()
    {
        StubHang(Wire.ChatPath, TimeSpan.FromSeconds(2));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        await ShouldThrowCancellationAsync(() => Adapter.CompleteAsync(Context(), ChatRequest(), cts.Token), stopwatch);

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task Stream_cancelled_before_the_headers_arrive_throws_promptly()
    {
        StubHang(Wire.ChatPath, TimeSpan.FromSeconds(2));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        await ShouldThrowCancellationAsync(() => CollectAsync(Adapter.StreamAsync(Context(), ChatRequest(), cts.Token), cts.Token), stopwatch);

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task An_already_cancelled_token_throws_without_a_failure_result()
    {
        StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub("x", FinishReason.Stop, ReportedUsage)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await ShouldThrowCancellationAsync(() => Adapter.CompleteAsync(Context(), ChatRequest(), cts.Token));
    }

    // ---- logging and credentials ----------------------------------------------------------------------------------

    [Fact]
    public async Task No_request_or_response_body_appears_in_captured_logs()
    {
        StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub(CompletionText, FinishReason.Stop, ReportedUsage)));
        await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);

        Server.ResetMappings();
        StubStreamBody(Wire.EncodeStream(new StreamStub([CompletionText], FinishReason.Stop, ReportedUsage)));
        await StreamAllAsync(Context(), ChatRequest());

        Server.ResetMappings();
        StubJson(Wire.ChatPath, 500, Wire.EncodeError("server_error", ProviderErrorText));
        await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);
        await StreamAllAsync(Context(), ChatRequest());

        Server.ResetMappings();
        StubJson(Wire.ChatPath, 200, Wire.EncodeMalformedBody());
        await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);

        var logs = string.Join('\n', CapturedLogs);
        logs.ShouldNotContain("CANARY-PROMPT");
        logs.ShouldNotContain("CANARY-COMPLETION");
        logs.ShouldNotContain(ProviderErrorText);
        logs.ShouldNotContain(Secret);
    }

    [Fact]
    public async Task The_secret_appears_only_in_the_auth_header_never_in_the_url_or_body()
    {
        StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub("x", FinishReason.Stop, ReportedUsage)));
        StubJson(Wire.CheckPath, 200, Wire.EncodeCheckBody());
        StubJson(Wire.ListModelsPath, 200, Wire.EncodeModelList(["m"]));
        await Adapter.CompleteAsync(Context(), ChatRequest(), TestToken);
        await Adapter.CheckAsync(Context(), TestToken);
        await Adapter.ListModelsAsync(Context(), TestToken);

        Server.ResetMappings();
        StubStreamBody(Wire.EncodeStream(new StreamStub(["x"], FinishReason.Stop, ReportedUsage)));
        await StreamAllAsync(Context(), ChatRequest());

        var entries = Server.LogEntries.Select(e => e.RequestMessage!).ToList();
        entries.ShouldNotBeEmpty();
        foreach (var message in entries)
        {
            message.AbsoluteUrl.ShouldNotContain(Secret);
            (message.BodyData?.BodyAsString ?? string.Empty).ShouldNotContain(Secret);
            var carriers = message.Headers!.Where(h => h.Value.Any(v => v.Contains(Secret, StringComparison.Ordinal))).Select(h => h.Key).ToList();
            carriers.ShouldBe([Wire.AuthHeaderName], ignoreOrder: true, customMessage: $"{message.Path}: only the auth header may carry the secret");
        }
    }

    [Fact]
    public async Task Without_a_secret_no_auth_header_is_sent()
    {
        StubJson(Wire.ChatPath, 200, Wire.EncodeCompletion(new CompletionStub("x", FinishReason.Stop, ReportedUsage)));

        await Adapter.CompleteAsync(Context(secret: null), ChatRequest(), TestToken);

        var message = Server.LogEntries.Single().RequestMessage!;
        message.Headers!.Keys.ShouldNotContain(k => string.Equals(k, Wire.AuthHeaderName, StringComparison.OrdinalIgnoreCase));
    }
}
