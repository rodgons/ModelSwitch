using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelSwitch.Adapters.Abstractions;

namespace ModelSwitch.AdapterContractTests.SuiteSelfTest;

/// <summary>
/// A deliberately small OpenAI-style Adapter that exists only to prove the contract suite is runnable and self-consistent
/// (see <see cref="ReferenceAdapterContractTests"/>). It is not the OpenAI-compatible Adapter and shares no code with it.
/// </summary>
public sealed partial class ReferenceAdapter(HttpClient http, ILogger<ReferenceAdapter> logger, TimeProvider time) : IProviderAdapter
{
    public string AdapterType => "reference";

    public async Task<CompletionOutcome> CompleteAsync(AdapterContext ctx, NeutralChatRequest request, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(ctx.AttemptTimeout);
        try
        {
            using var message = Build(HttpMethod.Post, ctx, "chat/completions", ToBody(ctx, request, stream: false));
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new CompletionOutcome.Failure(await ClassifyStatusAsync(response, attempt.Token));
            }

            var text = await response.Content.ReadAsStringAsync(attempt.Token);
            if (!TryParse(text, out var body))
            {
                return new CompletionOutcome.Failure(Invalid());
            }

            if (body["error"] is JsonObject error)
            {
                return new CompletionOutcome.Failure(ClassifyInner(error));
            }

            var choice = (body["choices"] as JsonArray)?.FirstOrDefault();
            var content = choice?["message"]?["content"]?.GetValue<string>();
            if (content is null)
            {
                return new CompletionOutcome.Failure(Invalid());
            }

            return new CompletionOutcome.Success(content, ParseFinish(choice?["finish_reason"]?.GetValue<string>()), ParseUsage(body["usage"]));
        }
        catch (Exception ex) when (TryMap(ex, ct, out var failure))
        {
            return new CompletionOutcome.Failure(failure);
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
                using var message = Build(HttpMethod.Post, ctx, "chat/completions", ToBody(ctx, request, stream: true));
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

        using (response)
        {
            if (startFailure is not null)
            {
                yield return new StreamEvent.Failed(startFailure);
                yield break;
            }

            using var reader = new StreamReader(await response!.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            var finished = false;
            while (true)
            {
                var (line, readFailure) = await ReadLineAsync(reader, ct);
                if (readFailure is not null)
                {
                    yield return new StreamEvent.Failed(readFailure);
                    yield break;
                }

                if (line is null)
                {
                    if (!finished)
                    {
                        yield return new StreamEvent.Failed(new ProviderFailure(FailureClass.Ambiguous, ProviderFailureCodes.ConnectionReset, null, null, true));
                    }

                    yield break;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    if (!finished)
                    {
                        yield return new StreamEvent.Failed(Invalid());
                    }

                    yield break;
                }

                if (!TryParse(data, out var chunk))
                {
                    yield return new StreamEvent.Failed(Invalid());
                    yield break;
                }

                if (chunk["error"] is JsonObject error)
                {
                    yield return new StreamEvent.Failed(ClassifyInner(error));
                    yield break;
                }

                var choice = (chunk["choices"] as JsonArray)?.FirstOrDefault();
                if (choice?["delta"]?["content"]?.GetValue<string>() is { Length: > 0 } text)
                {
                    yield return new StreamEvent.ContentDelta(text);
                }

                if (choice?["finish_reason"]?.GetValue<string>() is { } reason)
                {
                    finished = true;
                    yield return new StreamEvent.Finish(ParseFinish(reason));
                }

                if (ParseUsage(chunk["usage"]) is { } usage)
                {
                    yield return new StreamEvent.Usage(usage);
                }
            }
        }
    }

    public async Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var message = Build(HttpMethod.Get, ctx, "models", null);
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
            using var message = Build(HttpMethod.Get, ctx, "models", null);
            using var response = await http.SendAsync(message, attempt.Token);
            if (!response.IsSuccessStatusCode || !TryParse(await response.Content.ReadAsStringAsync(attempt.Token), out var body))
            {
                return [];
            }

            return [.. (body["data"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new DiscoveredModel(m["id"]!.GetValue<string>()))];
        }
        catch (Exception ex) when (TryMap(ex, ct, out _))
        {
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Provider answered with status {Status}")]
    private partial void LogStatus(int status);

    private static HttpRequestMessage Build(HttpMethod method, AdapterContext ctx, string path, JsonObject? body)
    {
        var message = new HttpRequestMessage(method, new Uri($"{ctx.Endpoint.AbsoluteUri.TrimEnd('/')}/{path}"));
        if (ctx.Secret is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.Secret);
        }

        if (body is not null)
        {
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return message;
    }

    private static JsonObject ToBody(AdapterContext ctx, NeutralChatRequest request, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = ctx.ProviderModelId,
            ["messages"] = new JsonArray(request.Messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role.ToString().ToLowerInvariant(), ["content"] = m.Text }).ToArray()),
            ["stream"] = stream,
        };
        if (stream)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        body["max_completion_tokens"] = request.MaxOutputTokens;
        body["temperature"] = request.Temperature;
        body["top_p"] = request.TopP;
        body["presence_penalty"] = request.PresencePenalty;
        body["frequency_penalty"] = request.FrequencyPenalty;
        body["seed"] = request.Seed;
        if (request.Stop is { Count: > 0 } stop)
        {
            body["stop"] = new JsonArray(stop.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        }

        switch (request.ResponseFormat)
        {
            case ResponseFormat.JsonObject:
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                break;
            case ResponseFormat.JsonSchema schema:
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = schema.Name, ["strict"] = schema.Strict, ["schema"] = JsonNode.Parse(schema.Schema.GetRawText()) },
                };
                break;
        }

        return body;
    }

    private async Task<ProviderFailure> ClassifyStatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? providerCode = null;
        try
        {
            if (TryParse(await response.Content.ReadAsStringAsync(ct), out var body))
            {
                providerCode = body["error"]?["code"]?.GetValue<string>();
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // The body is only used to refine the classification.
        }

        LogStatus(status);

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
        return new ProviderFailure(cls, code, status, RetryAfter(response), true);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - time.GetUtcNow() : null;
    }

    private static ProviderFailure ClassifyInner(JsonObject error)
    {
        var code = error["error"]?["code"]?.GetValue<string>() ?? error["code"]?.GetValue<string>();
        return code switch
        {
            _ when IsBillingCode(code) => new ProviderFailure(FailureClass.Permanent, ProviderFailureCodes.QuotaExhausted, 200, null, true),
            "rate_limit_exceeded" => new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.RateLimited, 200, null, true),
            _ => new ProviderFailure(FailureClass.Transient, ProviderFailureCodes.ServerError, 200, null, true),
        };
    }

    private static bool IsBillingCode(string? code) =>
        code is "insufficient_quota" or "credit_balance_exhausted" or "organization_spend_limit_exceeded";

    private static ProviderFailure Invalid() =>
        new(FailureClass.Transient, ProviderFailureCodes.InvalidProviderResponse, 200, null, true);

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

    private static async Task<(string? Line, ProviderFailure? Failure)> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return (await reader.ReadLineAsync(ct), null);
        }
        catch (Exception ex) when (TryMap(ex, ct, out var failure))
        {
            return (null, failure);
        }
    }

    private static FinishReason ParseFinish(string? reason) => reason switch
    {
        "length" => FinishReason.Length,
        "content_filter" => FinishReason.ContentFilter,
        _ => FinishReason.Stop,
    };

    private static NeutralUsage? ParseUsage(JsonNode? usage)
    {
        if (usage is not JsonObject node || node["prompt_tokens"] is null)
        {
            return null;
        }

        var input = node["prompt_tokens"]!.GetValue<int>();
        var output = node["completion_tokens"]?.GetValue<int>() ?? 0;
        var cached = node["prompt_tokens_details"]?["cached_tokens"]?.GetValue<int>() ?? node["cached_tokens"]?.GetValue<int>();
        return new NeutralUsage(input, output, cached > input ? null : cached);
    }

    private static bool TryParse(string text, out JsonObject body)
    {
        try
        {
            body = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            return body.Count > 0 || text.Trim() == "{}";
        }
        catch (JsonException)
        {
            body = [];
            return false;
        }
    }
}
