using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelSwitch.Adapters.Abstractions;
using ModelSwitch.Adapters.OpenAICompatible.Wire;

namespace ModelSwitch.Adapters.OpenAICompatible;

/// <summary>
/// The OpenAI-compatible Adapter (docs/provider-adapters.md section 2): <c>POST {endpoint}/chat/completions</c>, non-streaming and SSE.
/// It contains no business rules (ADR-0004): no retries, no resilience handlers on <paramref name="http"/>, no routing or cost.
/// The caller supplies the <see cref="HttpClient"/> (the Registry's outbound handler as primary handler, an infinite
/// <see cref="HttpClient.Timeout"/>: the Adapter enforces <see cref="AdapterContext.AttemptTimeout"/> itself) and the per-Provider
/// <see cref="OpenAICompatibleOptions"/>. Prompt and completion text is never logged (ADR-0010).
/// </summary>
public sealed partial class OpenAICompatibleAdapter(
    HttpClient http,
    TimeProvider time,
    ILogger<OpenAICompatibleAdapter> logger,
    OpenAICompatibleOptions? options = null) : IProviderAdapter
{
    public const string TypeName = "openai_compatible";

    private const int _maxErrorBodyBytes = 64 * 1024;

    private static readonly TimeSpan _checkTimeout = TimeSpan.FromSeconds(5);

    private static readonly Meter _meter = new("ModelSwitch.Adapters.OpenAICompatible");

    private static readonly Counter<long> _invalidUsage = _meter.CreateCounter<long>(
        "modelswitch.provider.invalid_usage",
        description: "Responses whose usage reported more cached input tokens than input tokens.");

    private readonly OpenAICompatibleOptions _options = options ?? new OpenAICompatibleOptions();

    public string AdapterType => TypeName;

    public async Task<CompletionOutcome> CompleteAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(ctx.AttemptTimeout);
        try
        {
            using var message = Build(HttpMethod.Post, ctx, "chat/completions", ChatRequestWriter.Write(ctx, request, stream: false, _options));
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new CompletionOutcome.Failure(await ClassifyStatusAsync(response, attempt.Token));
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(attempt.Token);
            var parser = new ChatPayloadParser();
            if (!parser.Parse(bytes))
            {
                return Fail(FailureClassifier.InvalidResponse());
            }

            if (parser.HasError)
            {
                // OpenRouter answers 200 with an error object in place of a completion.
                return Fail(FailureClassifier.FromInnerError(parser));
            }

            if (!parser.HasChoice || (parser.Content is null && parser.FinishReason is null))
            {
                return Fail(FailureClassifier.InvalidResponse());
            }

            CountInconsistentUsage(parser);
            return new CompletionOutcome.Success(parser.Content ?? string.Empty, ParseFinish(parser.FinishReason), parser.Usage);
        }
        catch (Exception ex) when (FailureClassifier.TryMap(ex, ct, out var failure))
        {
            return Fail(failure);
        }
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        AdapterContext ctx, NeutralChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var (response, startFailure) = await SendStreamRequestAsync(ctx, request, ct);
        using (response)
        {
            if (startFailure is not null)
            {
                yield return new StreamEvent.Failed(startFailure);
                yield break;
            }

            var pipe = PipeReader.Create(await response!.Content.ReadAsStreamAsync(ct));
            try
            {
                var sse = new SseReader(pipe);
                var parser = new ChatPayloadParser();
                var finished = false;
                NeutralUsage? usage = null;

                while (true)
                {
                    var (kind, readFailure) = await NextAsync(sse, ct);
                    if (readFailure is not null)
                    {
                        LogFailed(readFailure.Code, readFailure.HttpStatus);
                        yield return new StreamEvent.Failed(readFailure);
                        yield break;
                    }

                    if (kind == SseKind.End)
                    {
                        if (!finished)
                        {
                            yield return new StreamEvent.Failed(FailureClassifier.ConnectionReset());
                        }
                        else if (usage is not null)
                        {
                            yield return new StreamEvent.Usage(usage);
                        }

                        yield break;
                    }

                    if (kind == SseKind.Done)
                    {
                        if (!finished)
                        {
                            yield return new StreamEvent.Failed(FailureClassifier.InvalidResponse());
                        }
                        else if (usage is not null)
                        {
                            yield return new StreamEvent.Usage(usage);
                        }

                        yield break;
                    }

                    if (!parser.Parse(sse.Payload))
                    {
                        LogFailed(ProviderFailureCodes.InvalidProviderResponse, 200);
                        yield return new StreamEvent.Failed(FailureClassifier.InvalidResponse());
                        yield break;
                    }

                    if (parser.HasError)
                    {
                        var failure = FailureClassifier.FromInnerError(parser);
                        LogFailed(failure.Code, failure.HttpStatus);
                        yield return new StreamEvent.Failed(failure);
                        yield break;
                    }

                    if (!string.IsNullOrEmpty(parser.Content))
                    {
                        yield return new StreamEvent.ContentDelta(parser.Content);
                    }

                    if (parser.FinishReason is { } reason && !finished)
                    {
                        finished = true;
                        yield return new StreamEvent.Finish(ParseFinish(reason));
                    }

                    if (parser.Usage is { } reported)
                    {
                        // Usage can ride on any chunk, on several, or on one that also has choices (OpenRouter, Together).
                        // Keep the latest and report it once, when the stream ends.
                        CountInconsistentUsage(parser);
                        usage = reported;
                    }
                }
            }
            finally
            {
                await pipe.CompleteAsync();
            }
        }
    }

    public async Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_checkTimeout);
        try
        {
            using var message = Build(HttpMethod.Get, ctx, "models", null);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            if (response.IsSuccessStatusCode)
            {
                return ConnectivityResult.Ok;
            }

            return ConnectivityResult.Unreachable(await ClassifyStatusAsync(response, attempt.Token));
        }
        catch (Exception ex) when (FailureClassifier.TryMap(ex, ct, out var failure))
        {
            LogFailed(failure.Code, failure.HttpStatus);
            return ConnectivityResult.Unreachable(failure);
        }
    }

    public async Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(ctx.AttemptTimeout);
        try
        {
            using var message = Build(HttpMethod.Get, ctx, "models", null);
            using var response = await http.SendAsync(message, attempt.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed("list_models_failed", (int)response.StatusCode);
                return [];
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(attempt.Token);
            return ReadModelIds(bytes);
        }
        catch (Exception ex) when (FailureClassifier.TryMap(ex, ct, out var failure))
        {
            LogFailed(failure.Code, failure.HttpStatus);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Provider answered with status {Status}")]
    private partial void LogStatus(int status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Provider call failed: code {Code}, status {Status}")]
    private partial void LogFailed(string code, int? status);

    private static List<DiscoveredModel> ReadModelIds(byte[] bytes)
    {
        var models = new List<DiscoveredModel>();
        try
        {
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return models;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals("data"u8))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    continue;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    string? id = null;
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("id"u8))
                        {
                            reader.Read();
                            id = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                            reader.Skip();
                        }
                        else
                        {
                            reader.Read();
                            reader.Skip();
                        }
                    }

                    if (!string.IsNullOrEmpty(id))
                    {
                        models.Add(new DiscoveredModel(id));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A malformed listing is just an empty listing: the helper is optional.
        }

        return models;
    }

    private async Task<(HttpResponseMessage? Response, ProviderFailure? Failure)> SendStreamRequestAsync(
        AdapterContext ctx, NeutralChatRequest request, CancellationToken ct)
    {
        // The Attempt timeout covers connecting and waiting for the response headers; the body then streams under the caller's token.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(ctx.AttemptTimeout);
        HttpResponseMessage? response = null;
        try
        {
            using var message = Build(HttpMethod.Post, ctx, "chat/completions", ChatRequestWriter.Write(ctx, request, stream: true, _options));
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            if (response.IsSuccessStatusCode)
            {
                return (response, null);
            }

            var failure = await ClassifyStatusAsync(response, attempt.Token);
            response.Dispose();
            return (null, failure);
        }
        catch (Exception ex) when (FailureClassifier.TryMap(ex, ct, out var failure))
        {
            response?.Dispose();
            return (null, failure);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    private static async ValueTask<(SseKind Kind, ProviderFailure? Failure)> NextAsync(SseReader sse, CancellationToken ct)
    {
        try
        {
            return (await sse.ReadAsync(ct), null);
        }
        catch (Exception ex) when (FailureClassifier.TryMap(ex, ct, out var failure))
        {
            return (SseKind.End, failure);
        }
    }

    private HttpRequestMessage Build(HttpMethod method, AdapterContext ctx, string path, HttpContent? content)
    {
        var message = new HttpRequestMessage(method, new Uri($"{ctx.Endpoint.AbsoluteUri.TrimEnd('/')}/{path}"));
        if (ctx.Secret is not null)
        {
            if (string.Equals(_options.AuthHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Secret);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(_options.AuthHeaderName, ctx.Secret);
            }
        }

        message.Content = content;
        return message;
    }

    private async Task<ProviderFailure> ClassifyStatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? providerCode = null;
        string? providerType = null;
        var contextLength = false;
        try
        {
            var body = await ReadCappedAsync(response, ct);
            var parser = new ChatPayloadParser();
            if (parser.Parse(body) && parser.HasError)
            {
                // OpenAI sends a string code, OpenRouter a numeric one; the status already says what the number would.
                providerCode = parser.ErrorCode;
                providerType = parser.ErrorType;
                contextLength = parser.ErrorMentionsContextLength;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // The body only refines the classification; the status alone is enough.
        }

        LogStatus(status);
        var failure = FailureClassifier.FromStatus(status, providerCode, providerType, contextLength, FailureClassifier.RetryAfter(response.Headers.RetryAfter, time));
        LogFailed(failure.Code, failure.HttpStatus);
        return failure;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[_maxErrorBodyBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return buffer[..length];
    }

    private CompletionOutcome.Failure Fail(ProviderFailure failure)
    {
        LogFailed(failure.Code, failure.HttpStatus);
        return new CompletionOutcome.Failure(failure);
    }

    private static void CountInconsistentUsage(ChatPayloadParser parser)
    {
        if (parser.UsageInconsistent)
        {
            _invalidUsage.Add(1);
        }
    }

    private static FinishReason ParseFinish(string? reason) => reason switch
    {
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        _ => FinishReason.Stop,
    };
}
