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
    /// <summary>Applies latency, hang and status faults common to both protocols, then hands over to the protocol writer.</summary>
    public static async Task RunAsync(HttpContext http, MockPlan plan, IMockProtocol protocol, TimeProvider time)
    {
        var ct = http.RequestAborted;
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
            // The client went away (or hung up on the hang fault): nothing to answer.
        }
    }

    public static async Task WriteLineAsync(HttpContext http, string text, CancellationToken ct)
    {
        await http.Response.WriteAsync(text, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
