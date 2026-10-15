using System.Buffers;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.Adapters.Ollama;

/// <summary>
/// Translates the neutral contract to Ollama's native API (docs/provider-adapters.md section 3): <c>POST /api/chat</c> (NDJSON when streaming),
/// <c>GET /api/version</c> and <c>GET /api/tags</c>. It holds no business rules and no resilience handlers (ADR-0004), and never logs content (ADR-0010).
/// </summary>
/// <param name="http">A client without resilience handlers, usually from <c>IHttpClientFactory</c>.</param>
/// <param name="logger">Receives status and failure codes only, never content.</param>
/// <param name="time">Resolves an HTTP-date <c>Retry-After</c>.</param>
public sealed partial class OllamaAdapter(HttpClient http, ILogger<OllamaAdapter> logger, TimeProvider time) : IProviderAdapter
{
    public const string TypeName = "ollama";

    private static readonly Meter _meter = new("ModelSwitch.Adapters.Ollama");
    private static readonly Counter<long> _invalidUsage = _meter.CreateCounter<long>("modelswitch.provider.invalid_usage");
    private static readonly TimeSpan _checkTimeout = TimeSpan.FromSeconds(5);

    private const int _maxErrorBodyBytes = 16 * 1024;

    public string AdapterType => TypeName;

    public async Task<CompletionOutcome> CompleteAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(ctx.AttemptTimeout);
        try
        {
            using var message = Build(HttpMethod.Post, ctx, "api/chat", OllamaWire.WriteChatRequest(ctx, request, stream: false));
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Fail(await ClassifyStatusAsync(response, attempt.Token));
            }

            var body = await response.Content.ReadAsByteArrayAsync(attempt.Token);
            if (!OllamaWire.TryParseFrame(new ReadOnlySequence<byte>(body), out var frame))
            {
                return Fail(InvalidResponse());
            }

            if (frame.HasError)
            {
                return Fail(ClassifyEmbeddedError(frame.ErrorCode));
            }

            if (!frame.HasMessage || frame.Content is null)
            {
                return Fail(InvalidResponse());
            }

            var usage = OllamaWire.ToUsage(frame, out var invalidCached);
            CountInvalidUsage(invalidCached);
            return new CompletionOutcome.Success(frame.Content, OllamaWire.ToFinishReason(frame.DoneReason), usage);
        }
        catch (Exception ex) when (TryMap(ex, ct, out var failure))
        {
            return Fail(failure);
        }
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        AdapterContext ctx, NeutralChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        ProviderFailure? startFailure = null;
        using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            attempt.CancelAfter(ctx.AttemptTimeout);
            try
            {
                using var message = Build(HttpMethod.Post, ctx, "api/chat", OllamaWire.WriteChatRequest(ctx, request, stream: true));
                response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                if (!response.IsSuccessStatusCode)
                {
                    startFailure = await ClassifyStatusAsync(response, attempt.Token);
                }
            }
            catch (Exception ex) when (TryMap(ex, ct, out var failure))
            {
                startFailure = failure;
            }
        }

        if (startFailure is not null)
        {
            response?.Dispose();
            LogFailure(startFailure.Code);
            yield return new StreamEvent.Failed(startFailure);
            yield break;
        }

        PipeReader? pipe = null;
        try
        {
            pipe = PipeReader.Create(await response!.Content.ReadAsStreamAsync(ct));
            while (true)
            {
                ReadResult result = default;
                ProviderFailure? readFailure = null;
                try
                {
                    result = await pipe.ReadAsync(ct);
                }
                catch (Exception ex) when (TryMap(ex, ct, out var failure))
                {
                    readFailure = failure;
                }

                if (readFailure is not null)
                {
                    LogFailure(readFailure.Code);
                    yield return new StreamEvent.Failed(readFailure);
                    yield break;
                }

                var buffer = result.Buffer;
                while (TryTakeLine(ref buffer, result.IsCompleted, out var line))
                {
                    if (line.IsEmpty)
                    {
                        continue;
                    }

                    if (!OllamaWire.TryParseFrame(line, out var frame))
                    {
                        pipe.AdvanceTo(buffer.End);
                        yield return Failed(InvalidResponse());
                        yield break;
                    }

                    if (frame.HasError)
                    {
                        pipe.AdvanceTo(buffer.End);
                        yield return Failed(ClassifyEmbeddedError(frame.ErrorCode));
                        yield break;
                    }

                    if (!string.IsNullOrEmpty(frame.Content))
                    {
                        yield return new StreamEvent.ContentDelta(frame.Content);
                    }

                    if (frame.Done)
                    {
                        pipe.AdvanceTo(buffer.End);
                        yield return new StreamEvent.Finish(OllamaWire.ToFinishReason(frame.DoneReason));
                        if (OllamaWire.ToUsage(frame, out var invalidCached) is { } usage)
                        {
                            yield return new StreamEvent.Usage(usage);
                        }

                        CountInvalidUsage(invalidCached);
                        yield break;
                    }
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    // The body ended without a `done: true` object: the response was cut.
                    yield return Failed(new ProviderFailure(FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset, null, null, true));
                    yield break;
                }
            }
        }
        finally
        {
            if (pipe is not null)
            {
                await pipe.CompleteAsync();
            }

            response!.Dispose();
        }
    }

    public async Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_checkTimeout);
        try
        {
            using var message = Build(HttpMethod.Get, ctx, "api/version", null);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            return response.IsSuccessStatusCode
                ? ConnectivityResult.Ok
                : ConnectivityResult.Unreachable(await ClassifyStatusAsync(response, attempt.Token));
        }
        catch (Exception ex) when (TryMap(ex, ct, out var failure))
        {
            return ConnectivityResult.Unreachable(failure);
        }
    }

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(ctx.AttemptTimeout);
        try
        {
            using var message = Build(HttpMethod.Get, ctx, "api/tags", null);
            using var response = await http.SendAsync(message, attempt.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogFailure(ClassifyStatusCode((int)response.StatusCode, null).Code);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: attempt.Token);
            var models = new List<DiscoveredModel>();
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("models", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    var id = TryGetString(entry, "model");
                    if (string.IsNullOrEmpty(id))
                    {
                        id = TryGetString(entry, "name");
                    }

                    if (!string.IsNullOrEmpty(id))
                    {
                        models.Add(new DiscoveredModel(id));
                    }
                }
            }

            return models;
        }
        catch (JsonException)
        {
            LogFailure(ProviderFailureCodes.InvalidProviderResponse);
            return [];
        }
        catch (Exception ex) when (TryMap(ex, ct, out var failure))
        {
            LogFailure(failure.Code);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ollama call failed with code {Code}")]
    private partial void LogFailure(string code);

    private static string? TryGetString(JsonElement entry, string property) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HttpRequestMessage Build(HttpMethod method, AdapterContext ctx, string path, byte[]? body)
    {
        var message = new HttpRequestMessage(method, new Uri($"{ctx.Endpoint.AbsoluteUri.TrimEnd('/')}/{path}"));
        if (ctx.Secret is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Secret);
        }

        if (body is not null)
        {
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return message;
    }

    private CompletionOutcome.Failure Fail(ProviderFailure failure)
    {
        LogFailure(failure.Code);
        return new CompletionOutcome.Failure(failure);
    }

    private StreamEvent.Failed Failed(ProviderFailure failure)
    {
        LogFailure(failure.Code);
        return new StreamEvent.Failed(failure);
    }

    private static void CountInvalidUsage(bool invalid)
    {
        if (invalid)
        {
            _invalidUsage.Add(1);
        }
    }

    /// <summary>Takes the next NDJSON line. At end of input a final line without a terminator still counts.</summary>
    private static bool TryTakeLine(ref ReadOnlySequence<byte> buffer, bool completed, out ReadOnlySequence<byte> line)
    {
        var position = buffer.PositionOf((byte)'\n');
        if (position is { } end)
        {
            line = buffer.Slice(0, end);
            buffer = buffer.Slice(buffer.GetPosition(1, end));
            return true;
        }

        if (completed && !buffer.IsEmpty)
        {
            line = buffer;
            buffer = buffer.Slice(buffer.End);
            return true;
        }

        line = default;
        return false;
    }

    private async Task<ProviderFailure> ClassifyStatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? providerCode = null;
        try
        {
            var body = await ReadBoundedAsync(response, ct);
            if (OllamaWire.TryParseFrame(new ReadOnlySequence<byte>(body), out var frame))
            {
                providerCode = frame.ErrorCode;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // The body only refines the classification; the status alone is enough.
        }

        var failure = ClassifyStatusCode((int)response.StatusCode, providerCode) with { RetryAfter = RetryAfter(response) };
        return failure;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[_maxErrorBodyBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return buffer.AsSpan(0, read).ToArray();
    }

    private static ProviderFailure ClassifyStatusCode(int status, string? providerCode)
    {
        var (cls, code) = status switch
        {
            402 => (FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted),
            429 when IsBillingCode(providerCode) => (FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted),
            429 => (FailureClass.Transient, ProviderFailureCodes.RateLimited),
            400 when providerCode == "context_length_exceeded" => (FailureClass.Permanent, ProviderFailureCodes.ContextLengthExceeded),
            400 => (FailureClass.Permanent, ProviderFailureCodes.BadRequest),
            401 => (FailureClass.Permanent, ProviderFailureCodes.Unauthorized),
            403 => (FailureClass.Permanent, ProviderFailureCodes.Forbidden),
            404 => (FailureClass.Permanent, ProviderFailureCodes.NotFound),
            408 => (FailureClass.Transient, ProviderFailureCodes.RequestTimeout),
            409 => (FailureClass.Transient, ProviderFailureCodes.Conflict),
            413 => (FailureClass.Permanent, ProviderFailureCodes.PayloadTooLarge),
            422 => (FailureClass.Permanent, ProviderFailureCodes.Unprocessable),
            425 => (FailureClass.Transient, ProviderFailureCodes.TooEarly),
            524 => (FailureClass.Ambiguous, ProviderFailureCodes.EdgeTimeout),
            >= 500 => (FailureClass.Transient, ProviderFailureCodes.ServerError),
            _ => (FailureClass.Permanent, ProviderFailureCodes.ClientError),
        };
        return new ProviderFailure(cls, code, status, null, true);
    }

    /// <summary>An error object inside a 200 response, classified by its code when it has one. With nothing usable inside: transient.</summary>
    private static ProviderFailure ClassifyEmbeddedError(string? providerCode) => providerCode switch
    {
        _ when IsBillingCode(providerCode) => new ProviderFailure(FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted, 200, null, true),
        "rate_limit_exceeded" => new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.RateLimited, 200, null, true),
        _ => new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 200, null, true),
    };

    private static bool IsBillingCode(string? code) =>
        code is "insufficient_quota" or "credit_balance_exhausted" or "organization_spend_limit_exceeded";

    private static ProviderFailure InvalidResponse() =>
        new(FailureClass.Transient, ProviderFailureCodes.InvalidProviderResponse, 200, null, true);

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>Maps network exceptions to a failure. The caller's own cancellation is never mapped: it propagates.</summary>
    private static bool TryMap(Exception ex, CancellationToken callerToken, out ProviderFailure failure)
    {
        failure = null!;
        switch (ex)
        {
            case OperationCanceledException when callerToken.IsCancellationRequested:
                return false;
            case OperationCanceledException:
                failure = new ProviderFailure(FailureClass.Ambiguous, ProviderFailureCodes.Timeout, null, null, true);
                return true;
            case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError }:
                failure = new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ConnectFailed, null, null, false);
                return true;
            case HttpRequestException or IOException:
                failure = new ProviderFailure(FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset, null, null, true);
                return true;
            default:
                return false;
        }
    }
}
