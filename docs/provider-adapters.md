# Provider Adapters

This document implements FR-02. An Adapter translates the gateway's neutral contract to one Provider's wire protocol, and contains no business rules: no routing, retries, cost or limits.

## 1. Contract (`ModelSwitch.Adapters.Abstractions`)

```csharp
public interface IProviderAdapter
{
    string AdapterType { get; }                       // "openai_compatible" | "ollama"

    Task<CompletionOutcome> CompleteAsync(
        AdapterContext ctx, NeutralChatRequest request, CancellationToken ct);

    IAsyncEnumerable<StreamEvent> StreamAsync(
        AdapterContext ctx, NeutralChatRequest request, CancellationToken ct);

    Task<ConnectivityResult> CheckAsync(AdapterContext ctx, CancellationToken ct);

    Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(AdapterContext ctx, CancellationToken ct); // optional helper for registration UI
}

public sealed record AdapterContext(
    Uri Endpoint, string? Secret, string ProviderModelId, TimeSpan AttemptTimeout);
```

Neutral types:

- `NeutralChatRequest`: messages (role + text), `MaxOutputTokens`, sampling params (all nullable), `Stop`, `Seed`, `ResponseFormat` (`Text | JsonObject | JsonSchema(name, schema, strict)`).
- `CompletionOutcome`: either `Success(text, finishReason, NeutralUsage?)` or `Failure(ProviderFailure)`.
- `StreamEvent`: `ContentDelta(text)`, `Finish(reason)`, `Usage(NeutralUsage)`, `Failed(ProviderFailure)`. The stream always ends with exactly one `Finish` or `Failed`, and `Usage` may come before or after `Finish`.
- `NeutralUsage(int InputTokens, int OutputTokens, int? CachedInputTokens)`. Only *reported* values go here. The Adapter never estimates. `CachedInputTokens` is a subset of `InputTokens`: `null` when the Provider didn't report it, `0` when it reported none. If a Provider reports more cached than input tokens, the Adapter sets `CachedInputTokens = null` (everything is then priced at the full input rate) and increments `modelswitch.provider.invalid_usage`. It doesn't fail the Attempt.
- `ProviderFailure(FailureClass Class, string Code, int? HttpStatus, TimeSpan? RetryAfter, bool RequestSent)`. Classification follows [routing-and-resilience.md §3](routing-and-resilience.md#3-failure-classification).

Rules every Adapter must follow:

1. **Never throws** for Provider or network failures. It returns `Failure` or `Failed`, and throws only for programming errors or `OperationCanceledException` from `ct`.
2. **Honours `ct` immediately**, including mid-stream. Disposing the enumerator aborts the HTTP response.
3. **Sets `RequestSent` correctly.** It is `false` only when the failure happened before any request bytes left the process (connect, DNS, TLS). This is what distinguishes transient from ambiguous.
4. **No retries, no `HttpClient` resilience handlers** (ADR-0004).
5. **Never logs** request or response bodies, and never logs headers that carry credentials.
6. **Sanitizes** Provider error messages: maps them to `Code` and keeps no free text that could contain prompt fragments.
7. **Streams without buffering.** Each Provider chunk becomes a `StreamEvent` as soon as it is parsed.

`HttpClient` instances come from `IHttpClientFactory` with one named client per Provider id, `SocketsHttpHandler.PooledConnectionLifetime = 5 min`, and an outbound destination check (see [security.md §5](security.md#5-outbound-requests-ssrf)).

## 2. OpenAI-compatible Adapter

- `POST {endpoint}/chat/completions`, `Authorization: Bearer <secret>` (header name configurable for Azure-style `api-key`).
- Always sends `stream_options: {include_usage: true}` when streaming. Usage is accepted on any chunk.
- `CachedInputTokens` is read from `usage.prompt_tokens_details.cached_tokens`, falling back to a top-level `usage.cached_tokens` (some Together models use the flat shape).
- Maps `response_format` 1:1. `max_tokens` is sent as `max_completion_tokens`, falling back to `max_tokens` for endpoints that reject the newer field (a per-Provider flag).
- Parses SSE line by line from a `PipeReader`. `data: [DONE]` ends the stream.
- An error object inside a 200 stream maps to `Failed`.
- `CheckAsync`: `GET {endpoint}/models` with a 5 s timeout.

## 3. Ollama Adapter

- `POST {endpoint}/api/chat` with `stream: true|false`, `options: {temperature, top_p, seed, stop, num_predict}`.
- `format: "json"` for `JsonObject`, and `format: <schema>` for `JsonSchema` (Ollama ≥ 0.5).
- Streaming is NDJSON (one JSON object per line). The final object has `done: true` and `prompt_eval_count` / `eval_count`, which map to reported usage. `prompt_eval_cached_count`, when present, maps to `CachedInputTokens`.
- `done_reason` maps to `finish_reason` (`stop`, `length`).
- `CheckAsync`: `GET {endpoint}/api/version`. `ListModelsAsync`: `GET /api/tags`.
- No credential by default. An optional bearer token is supported for Ollama behind a reverse proxy.

## 4. Shared contract test suite (`ModelSwitch.AdapterContractTests`)

An abstract xUnit class `AdapterContract<TAdapter>` holds every scenario. Each Adapter has a subclass that supplies:
- a WireMock.Net server configured with that Provider's recorded wire fixtures (`fixtures/<adapter>/*.json|.sse|.ndjson`), and
- a factory for the Adapter.

Scenarios (each one runs for every Adapter):

| Area | Scenario |
|---|---|
| Success | Non-streaming text, usage reported → `Success` with exact usage |
| | Cached input tokens reported (streaming and non-streaming) → `CachedInputTokens` set; not reported → `null`; more cached than input → `null` |
| | Streaming text → deltas in order, `Finish`, `Usage` |
| | Usage absent → `Usage` null / no `Usage` event (no estimating) |
| | JSON object / JSON schema request is encoded correctly (request body assertion) |
| | `finish_reason=length` mapping |
| Failures | 400, 401, 403, 404, 408, 413, 422, 429 (+`Retry-After` seconds and HTTP-date), 500, 502, 503, 504 → expected class + code |
| | Connection refused → transient, `RequestSent=false` |
| | Response headers never arrive within timeout → ambiguous, `RequestSent=true` |
| | Stream cut mid-way (socket closed) → `Failed` ambiguous after deltas |
| | Malformed JSON body, malformed SSE line → transient `invalid_provider_response` |
| | Error object in a 200 stream → `Failed` |
| Behaviour | Cancellation mid-stream stops reading within 100 ms and aborts the connection |
| | No request/response body appears in captured logs |
| | Secret appears only in the auth header, never in the URL |

A new Adapter is "done" when it passes the whole suite unchanged (FR-02 acceptance).

### Wire fixtures

**OpenAI is the reference Provider** for the OpenAI-compatible Adapter: its fixtures in `fixtures/openai-compatible/` are the canonical contract. Other hosted Providers' deviations (see [research/hosted-providers.md](research/hosted-providers.md)) are added as extra fixtures, never by changing the reference ones.

- **Recorded** from the real API: non-streaming and streaming text, streaming with `stream_options.include_usage`, `finish_reason=length`, `json_object` and `json_schema` responses, 400 `invalid_request_error`, 400 context length exceeded, 401, and 404 unknown model.
- **Synthetic** for everything that can't be provoked on demand (429 rate limit with `Retry-After` as seconds and as an HTTP date, 429 `insufficient_quota`, 5xx, malformed bodies). They are hand-written from OpenAI's documented error body and carry `"_source": "synthetic"`, so nobody mistakes them for recordings. The billing 429 sits next to the rate-limit 429 because the two must be classified differently.
- **Recording** is a manual script, `scripts/record-fixtures`, run with `OPENAI_API_KEY` and **never in CI**. It sends synthetic prompts only, strips `authorization`, `openai-organization`, `openai-project`, `x-request-id`, `cookie` and `set-cookie`, and stamps each fixture with its capture date and model. It is re-run only when the Adapter changes or wire drift is suspected.

## 5. Mock Provider (`tools/ModelSwitch.MockProvider`)

This is a small ASP.NET Core app that implements **both** wire protocols. Tests, load tests and the demo all use it. Its behaviour is set per request through the model id or a control header, so a test can script faults without restarting it:

```
model = "mock-echo"                          → echoes the last user message
model = "mock-fixed?latency=300&tokens=50"   → fixed latency + output size
X-Mock-Fault: status=503;times=2             → fail twice, then succeed (keyed by X-Mock-Scenario id)
X-Mock-Fault: hang                           → never send headers
X-Mock-Fault: cut-after=5                    → send 5 chunks then drop the socket
X-Mock-Fault: no-usage                       → omit usage
X-Mock-Usage: cached=900                     → report 900 of the input tokens as cached
X-Mock-Fault: garbage                        → invalid JSON
```

The gateway never forwards client headers to Providers, so `X-Mock-Fault` works only in direct Adapter tests. Full-stack tests script faults through the mock's `POST /__scenarios` control endpoint instead. It sets the fault for a given mock model id (e.g. `{"model":"mock-a","fault":"status=503;times=2"}`) before the test calls the gateway.
