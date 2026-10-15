using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelSwitch.AdapterContractTests.Support;
using ModelSwitch.Adapters.Abstractions;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ModelSwitch.AdapterContractTests;

/// <summary>
/// The shared Adapter contract suite (docs/provider-adapters.md section 4). Every scenario lives in this class (split across partial files
/// by area). A concrete Adapter's test class derives from it and supplies only an Adapter factory and a <see cref="ProviderWireFormat"/>
/// describing how that Provider's wire format is stubbed. A new Adapter is "done" when it passes the whole suite unchanged (FR-02).
/// xUnit creates one instance per test, so every test gets its own WireMock server and its own captured logs.
/// </summary>
/// <typeparam name="TAdapter">The Adapter under test.</typeparam>
public abstract partial class AdapterContract<TAdapter> : IAsyncLifetime
    where TAdapter : IProviderAdapter
{
    protected const string Secret = "sk-contract-secret-9f3a1c";
    protected const string ModelId = "contract-model";

    private static readonly TimeSpan _defaultAttemptTimeout = TimeSpan.FromSeconds(10);

    private HttpClient _http = null!;
    private CapturingLoggerProvider _logs = null!;
    private ILoggerFactory _loggerFactory = null!;

    /// <summary>Creates the Adapter under test. It must use <see cref="AdapterTestEnvironment.Http"/> and no resilience handlers (ADR-0004).</summary>
    protected abstract TAdapter CreateAdapter(AdapterTestEnvironment environment);

    /// <summary>How this Provider's wire format is stubbed (backed by its recorded fixtures).</summary>
    protected abstract ProviderWireFormat Wire { get; }

    protected TAdapter Adapter { get; private set; } = default!;

    protected WireMockServer Server { get; private set; } = null!;

    protected IReadOnlyList<string> CapturedLogs => _logs.Entries;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Server = WireMockServer.Start();
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _logs = new CapturingLoggerProvider();
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs));
        Adapter = CreateAdapter(new AdapterTestEnvironment(_http, _loggerFactory, TimeProvider.System));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Server.Stop();
        Server.Dispose();
        _http.Dispose();
        _loggerFactory.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void Adapter_declares_a_type_name()
    {
        Adapter.AdapterType.ShouldNotBeNullOrWhiteSpace();
    }

    // ---- context and request helpers -------------------------------------------------------------------------------

    protected AdapterContext Context(TimeSpan? timeout = null, string? secret = Secret) =>
        new(new Uri($"http://127.0.0.1:{Server.Port}{Wire.EndpointPath}"), secret, ModelId, timeout ?? _defaultAttemptTimeout);

    protected AdapterContext ContextFor(Uri baseAddress, TimeSpan? timeout = null) =>
        new(new Uri(baseAddress, Wire.EndpointPath), Secret, ModelId, timeout ?? _defaultAttemptTimeout);

    protected static NeutralChatRequest ChatRequest(ResponseFormat? format = null) =>
        new([new NeutralMessage(MessageRole.System, "You are a test."), new NeutralMessage(MessageRole.User, "CANARY-PROMPT-text")])
        {
            MaxOutputTokens = 64,
            Temperature = 0.2,
            ResponseFormat = format ?? new ResponseFormat.Text(),
        };

    // ---- stubbing helpers ------------------------------------------------------------------------------------------

    protected void StubJson(string path, int status, string body, string contentType = "application/json", Action<IResponseBuilder>? configure = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body);
        configure?.Invoke(response);
        Server.Given(Request.Create().WithPath(path).UsingAnyMethod()).RespondWith(response);
    }

    protected void StubStreamBody(IEnumerable<string> frames) =>
        StubJson(Wire.ChatPath, 200, string.Concat(frames), Wire.StreamContentType);

    protected void StubHang(string path, TimeSpan delay) =>
        Server.Given(Request.Create().WithPath(path).UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Wire.EncodeCheckBody()).WithDelay(delay));

    protected static Uri ClosedPortEndpoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }

    // ---- observation helpers ---------------------------------------------------------------------------------------

    protected static async Task<List<StreamEvent>> CollectAsync(IAsyncEnumerable<StreamEvent> stream, CancellationToken ct)
    {
        var events = new List<StreamEvent>();
        await foreach (var item in stream.WithCancellation(ct))
        {
            events.Add(item);
        }

        return events;
    }

    protected Task<List<StreamEvent>> StreamAllAsync(AdapterContext ctx, NeutralChatRequest request) =>
        CollectAsync(Adapter.StreamAsync(ctx, request, TestToken), TestToken);

    /// <summary>Asserts the stream contract: exactly one <see cref="StreamEvent.Finish"/> or <see cref="StreamEvent.Failed"/>, last (a Usage may follow Finish).</summary>
    protected static void ShouldEndWithExactlyOneTerminalEvent(IReadOnlyList<StreamEvent> events)
    {
        events.ShouldNotBeEmpty();
        events.Count(e => e is StreamEvent.Finish or StreamEvent.Failed).ShouldBe(1);
        var terminal = events.Select((e, i) => (e, i)).Single(x => x.e is StreamEvent.Finish or StreamEvent.Failed);
        if (terminal.e is StreamEvent.Failed)
        {
            terminal.i.ShouldBe(events.Count - 1, "nothing may follow Failed");
        }
        else
        {
            events.Skip(terminal.i + 1).ShouldAllBe(e => e is StreamEvent.Usage, "only Usage may follow Finish");
        }
    }

    protected static ProviderFailure FailureOf(CompletionOutcome outcome) =>
        outcome.ShouldBeOfType<CompletionOutcome.Failure>().Error;

    protected static ProviderFailure FailureOf(IReadOnlyList<StreamEvent> events)
    {
        ShouldEndWithExactlyOneTerminalEvent(events);
        return events[^1].ShouldBeOfType<StreamEvent.Failed>().Error;
    }

    /// <summary>The failure an Adapter reports for the current stubs, through the non-streaming or the streaming path.</summary>
    protected async Task<ProviderFailure> FailureAsync(bool streaming, AdapterContext ctx)
    {
        if (!streaming)
        {
            return FailureOf(await Adapter.CompleteAsync(ctx, ChatRequest(), TestToken));
        }

        return FailureOf(await StreamAllAsync(ctx, ChatRequest()));
    }

    protected JsonNode LastChatRequestBody()
    {
        var entry = Server.LogEntries.Last(e => e.RequestMessage?.Path == Wire.ChatPath);
        return JsonNode.Parse(entry.RequestMessage!.BodyData!.BodyAsString!)!;
    }

    protected static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    protected static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout, TestToken));
        return finished == task;
    }

    protected static async Task<Stopwatch> ShouldThrowCancellationAsync(Func<Task> action, Stopwatch? stopwatch = null)
    {
        stopwatch ??= Stopwatch.StartNew();
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return stopwatch;
        }

        throw new Shouldly.ShouldAssertException("Expected an OperationCanceledException but the call completed.");
    }
}
