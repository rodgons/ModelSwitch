using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Inference.Contracts;
using ModelSwitch.Inference.Endpoints;
using ModelSwitch.SharedKernel.Errors;
using Shouldly;

namespace ModelSwitch.UnitTests.Inference;

/// <summary>SSE framing and pumping (docs/api.md section 1, "Streaming response").</summary>
public sealed class SseTests
{
    private static readonly Guid _requestId = new("0192f0c8-0000-7000-8000-0000000000a1");
    private static readonly TimeSpan _keepAlive = TimeSpan.FromSeconds(15);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    // ---- framing ---------------------------------------------------------------------------------------------------

    private static string Frame(Action<SseFrames> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var frames = new SseFrames(buffer, _requestId, "balanced", 1760450000);
        write(frames);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static JsonElement Data(string frame)
    {
        frame.ShouldStartWith("data: ");
        frame.ShouldEndWith("\n\n");
        return JsonDocument.Parse(frame["data: ".Length..^2]).RootElement.Clone();
    }

    [Fact]
    public void The_role_chunk_is_the_documented_openai_chunk_with_the_alias_as_model()
        => Frame(f => f.WriteRole()).ShouldBe(
            $"data: {{\"id\":\"chatcmpl-{_requestId}\",\"object\":\"chat.completion.chunk\",\"created\":1760450000,\"model\":\"balanced\"," +
            "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n");

    [Fact]
    public void A_content_chunk_escapes_quotes_newlines_and_keeps_unicode_as_utf8()
    {
        const string text = "say \"hi\"\nthen <b>é漢</b>";

        var frame = Frame(f => f.WriteContent(text));

        frame.Count(c => c == '\n').ShouldBe(2, "only the terminating blank line; the newline in the text is escaped");
        frame.ShouldContain("é漢");
        Data(frame).GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString().ShouldBe(text);
    }

    [Theory]
    [InlineData(FinishReason.Stop, "stop")]
    [InlineData(FinishReason.Length, "length")]
    [InlineData(FinishReason.ContentFilter, "content_filter")]
    public void The_finish_chunk_has_an_empty_delta_and_the_finish_reason(FinishReason reason, string expected)
    {
        var choice = Data(Frame(f => f.WriteFinish(reason))).GetProperty("choices")[0];

        choice.GetProperty("delta").EnumerateObject().ShouldBeEmpty();
        choice.GetProperty("finish_reason").GetString().ShouldBe(expected);
    }

    [Fact]
    public void The_usage_chunk_has_empty_choices_usage_and_the_gateway_metadata()
    {
        var chunk = Data(Frame(f => f.WriteUsage(new NeutralUsage(42, 17, null), "reported", 2)));

        chunk.GetProperty("choices").GetArrayLength().ShouldBe(0);
        chunk.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().ShouldBe(42);
        chunk.GetProperty("usage").GetProperty("completion_tokens").GetInt32().ShouldBe(17);
        chunk.GetProperty("usage").GetProperty("total_tokens").GetInt32().ShouldBe(59);
        chunk.GetProperty("modelswitch").GetProperty("request_id").GetString().ShouldBe(_requestId.ToString());
        chunk.GetProperty("modelswitch").GetProperty("usage_source").GetString().ShouldBe("reported");
        chunk.GetProperty("modelswitch").GetProperty("attempts").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void The_error_event_is_the_openai_error_object_with_the_stream_interrupted_code()
    {
        var error = Data(Frame(f => f.WriteError(new Error(ErrorCode.StreamInterrupted, "The stream was interrupted before it completed.")))).GetProperty("error");

        error.GetProperty("code").GetString().ShouldBe("stream_interrupted");
        error.GetProperty("type").GetString().ShouldBe("provider_error");
        error.GetProperty("message").GetString().ShouldBe("The stream was interrupted before it completed.");
        error.GetProperty("param").ValueKind.ShouldBe(JsonValueKind.Null);
        error.GetProperty("request_id").GetString().ShouldBe(_requestId.ToString());
    }

    [Fact]
    public void Done_and_keep_alive_are_the_literal_lines()
    {
        Frame(f => f.WriteDone()).ShouldBe("data: [DONE]\n\n");
        Frame(f => f.WriteKeepAlive()).ShouldBe(": keep-alive\n\n");
    }

    [Fact]
    public void Frames_written_one_after_another_stay_separate_events()
    {
        var all = Frame(f =>
        {
            f.WriteRole();
            f.WriteContent("a");
            f.WriteContent("b");
        });

        var events = all.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        events.Length.ShouldBe(3);
        events.ShouldAllBe(e => e.StartsWith("data: {", StringComparison.Ordinal));
        events.ToList().ForEach(e => JsonDocument.Parse(e["data: ".Length..]).Dispose());
    }

    // ---- pump ------------------------------------------------------------------------------------------------------

    private static StreamPart.Content Text(string text) => new(text);

    private static StreamPart.Completed Done(bool reported = true)
        => new(FinishReason.Stop, new NeutralUsage(3, 4, null), reported ? "reported" : "estimated", 1);

    private static StreamPart.Interrupted Cut()
        => new(new Error(ErrorCode.StreamInterrupted, "The stream was interrupted before it completed."));

    private sealed class Run
    {
        public MemoryStream Output { get; } = new();

        public bool Disposed { get; set; }

        public string Text => Encoding.UTF8.GetString(Output.ToArray());

        public string[] Events => Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }

    private static StreamingCompletion Completion(Func<CancellationToken, IAsyncEnumerable<StreamPart>> parts)
        => new(_requestId, "balanced", parts, () => ValueTask.CompletedTask);

    private async Task PumpAsync(Run run, StreamingCompletion stream, bool includeUsage = true, CancellationTokenSource? cancel = null)
    {
        var writer = PipeWriter.Create(run.Output, new StreamPipeWriterOptions(leaveOpen: true));
        await new SsePump(_time, _keepAlive).PumpAsync(writer, stream, includeUsage, 1760450000, cancel?.Token ?? TestContext.Current.CancellationToken);
        await writer.CompleteAsync(null);
    }

    private static async IAsyncEnumerable<StreamPart> Parts(Run run, params StreamPart[] parts)
    {
        try
        {
            foreach (var part in parts)
            {
                yield return part;
                await Task.Yield();
            }
        }
        finally
        {
            run.Disposed = true;
        }
    }

    [Fact]
    public async Task A_completion_is_role_content_finish_usage_and_done_in_that_order()
    {
        var run = new Run();

        await PumpAsync(run, Completion(_ => Parts(run, Text("Hel"), Text("lo"), Done())));

        run.Events.Length.ShouldBe(6);
        Data(run.Events[0] + "\n\n").GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString().ShouldBe("assistant");
        Data(run.Events[1] + "\n\n").GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString().ShouldBe("Hel");
        Data(run.Events[2] + "\n\n").GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString().ShouldBe("lo");
        Data(run.Events[3] + "\n\n").GetProperty("choices")[0].GetProperty("finish_reason").GetString().ShouldBe("stop");
        Data(run.Events[4] + "\n\n").GetProperty("usage").GetProperty("total_tokens").GetInt32().ShouldBe(7);
        run.Events[5].ShouldBe("data: [DONE]");
        run.Text.ShouldEndWith("data: [DONE]\n\n");
        run.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_include_usage_there_is_no_usage_chunk_but_still_done()
    {
        var run = new Run();

        await PumpAsync(run, Completion(_ => Parts(run, Text("a"), Done())), includeUsage: false);

        run.Text.ShouldNotContain("\"usage\"");
        run.Text.ShouldEndWith("data: [DONE]\n\n");
    }

    [Fact]
    public async Task An_empty_completion_still_gets_its_role_chunk_before_the_finish()
    {
        var run = new Run();

        await PumpAsync(run, Completion(_ => Parts(run, Done())));

        run.Events[0].ShouldContain("\"role\":\"assistant\"");
        run.Events[1].ShouldContain("\"finish_reason\":\"stop\"");
    }

    [Fact]
    public async Task An_interruption_ends_with_the_error_event_and_never_a_done()
    {
        var run = new Run();

        await PumpAsync(run, Completion(_ => Parts(run, Text("a"), Text("b"), Cut())));

        run.Events.Length.ShouldBe(4);
        Data(run.Events[3] + "\n\n").GetProperty("error").GetProperty("code").GetString().ShouldBe("stream_interrupted");
        run.Text.ShouldNotContain("[DONE]");
        run.Text.ShouldNotContain("finish_reason\":\"stop");
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        public async IAsyncEnumerable<StreamPart> After(Run run, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, params StreamPart[] parts)
        {
            try
            {
                await _open.Task.WaitAsync(ct);
                foreach (var part in parts)
                {
                    yield return part;
                }
            }
            finally
            {
                run.Disposed = true;
            }
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    [Fact]
    public async Task A_keep_alive_comment_is_sent_after_the_interval_of_silence_and_not_before()
    {
        var run = new Run();
        var gate = new Gate();
        var pump = PumpAsync(run, Completion(token => gate.After(run, token, Text("a"), Done())));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        run.Text.ShouldNotContain("keep-alive");

        _time.Advance(TimeSpan.FromSeconds(2));
        (await WaitForAsync(() => run.Text.Contains(": keep-alive\n\n", StringComparison.Ordinal))).ShouldBeTrue("silence of 16 s");

        gate.Open();
        await pump;
        run.Text.ShouldEndWith("data: [DONE]\n\n");
        run.Text.Split(": keep-alive").Length.ShouldBe(2, "exactly one comment for 16 s");
    }

    [Fact]
    public async Task A_write_resets_the_keep_alive_clock()
    {
        var run = new Run();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new Gate();
        var pump = PumpAsync(run, Completion(_ => Slow(run, release, gate)));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(10));
        release.SetResult(); // a chunk at t = 10 s
        (await WaitForAsync(() => run.Text.Contains("\"content\":\"a\"", StringComparison.Ordinal))).ShouldBeTrue();
        _time.Advance(TimeSpan.FromSeconds(10)); // t = 20 s: 10 s since the chunk
        await Task.Delay(50, TestContext.Current.CancellationToken);
        run.Text.ShouldNotContain("keep-alive");

        gate.Open();
        await pump;
    }

    private static async IAsyncEnumerable<StreamPart> Slow(Run run, TaskCompletionSource release, Gate gate)
    {
        try
        {
            await release.Task;
            yield return Text("a");
            await foreach (var part in gate.After(run, CancellationToken.None, Done()))
            {
                yield return part;
            }
        }
        finally
        {
            run.Disposed = true;
        }
    }

    [Fact]
    public async Task A_client_that_stopped_reading_ends_the_pump_and_disposes_the_stream()
    {
        var run = new Run();
        var pipe = new Pipe();
        await pipe.Reader.CompleteAsync(); // nobody reads: the next flush reports the client gone
        var stream = Completion(_ => Parts(run, Text("a"), Text("b"), Done()));

        await new SsePump(_time, _keepAlive).PumpAsync(pipe.Writer, stream, true, 1, TestContext.Current.CancellationToken);

        run.Disposed.ShouldBeTrue("the Provider stream is closed when the client is gone");
    }

    [Fact]
    public async Task Cancelling_the_token_throws_and_disposes_the_stream()
    {
        var run = new Run();
        var gate = new Gate();
        using var cancel = new CancellationTokenSource();
        var pump = PumpAsync(run, Completion(token => gate.After(run, token, Done())), includeUsage: true, cancel);

        await Task.Delay(30, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => pump);
        run.Disposed.ShouldBeTrue("the stream is torn down, which closes the Provider call and settles the request");
    }

    [Fact]
    public async Task A_stream_reads_once()
    {
        var run = new Run();
        var stream = Completion(_ => Parts(run, Done()));
        _ = stream.ReadAsync(TestContext.Current.CancellationToken);

        Should.Throw<InvalidOperationException>(() => stream.ReadAsync(TestContext.Current.CancellationToken));
        await stream.DisposeAsync();
    }
}
