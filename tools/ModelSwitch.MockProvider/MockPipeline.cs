using Microsoft.AspNetCore.Connections.Features;

namespace ModelSwitch.MockProvider;

/// <summary>One wire protocol's way of writing the planned response.</summary>
internal interface IMockProtocol
{
    Task WriteStatusErrorAsync(HttpContext http, FaultSpec fault, CancellationToken ct);

    Task WriteCompletionAsync(HttpContext http, MockPlan plan, CancellationToken ct);

    Task WriteStreamAsync(HttpContext http, MockPlan plan, CancellationToken ct);
}

internal static class MockPipeline
{
    private static readonly TimeSpan _clientHangupGrace = TimeSpan.FromSeconds(5);

    /// <summary>Applies latency, hang and status faults common to both protocols, then hands over to the protocol writer.</summary>
    public static async Task RunAsync(HttpContext http, MockPlan plan, IMockProtocol protocol, TimeProvider time)
    {
        var ct = http.RequestAborted;
        var store = http.RequestServices.GetRequiredService<ScenarioStore>();
        store.NoteStarted(plan.ModelId);
        try
        {
            if (plan.Fault is { Hang: true })
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, time, ct);
                return;
            }

            if (plan.Latency > TimeSpan.Zero)
            {
                await Task.Delay(plan.Latency, time, ct);
            }

            if (plan.Fault is { Status: not null } statusFault)
            {
                await protocol.WriteStatusErrorAsync(http, statusFault, ct);
                return;
            }

            if (plan.Stream)
            {
                await protocol.WriteStreamAsync(http, plan, ct);
            }
            else
            {
                await protocol.WriteCompletionAsync(http, plan, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away (or hung up on the hang fault): nothing to answer. Tests assert on the count.
            store.NoteAborted(plan.ModelId);
        }
    }

    public static async Task WriteLineAsync(HttpContext http, string text, CancellationToken ct)
    {
        await http.Response.WriteAsync(text, ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Drops the connection mid-response (the <c>cut-after</c> fault). Aborting right away would close the socket with a
    /// reset while the chunks written so far may still be queued in Kestrel's output pipe or unread by the peer, so they
    /// raced with the reset and sometimes never reached the client (even the response headers could be lost). Completing
    /// the transport's output pipe instead makes the socket send loop drain everything queued and then shut the sending
    /// side down (a FIN): the peer reads the chunks and then sees the stream end without the chunked terminator, which is
    /// what a Provider dying mid-stream looks like. The abort afterwards waits for the client to hang up and is only a
    /// backstop for one that never does.
    /// </summary>
    public static async Task CutConnectionAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Features.Get<IConnectionTransportFeature>()?.Transport is { } transport)
        {
            await transport.Output.CompleteAsync();
            try
            {
                await Task.Delay(_clientHangupGrace, ct);
            }
            catch (OperationCanceledException)
            {
                // The client hung up, which is exactly the end this fault simulates.
            }
        }

        http.Abort();
    }
}
