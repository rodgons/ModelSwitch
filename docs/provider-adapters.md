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

- `NeutralChatRequest`: messages (role `System | User | Assistant` + text), `MaxOutputTokens`, sampling params (`Temperature`, `TopP`, `PresencePenalty`, `FrequencyPenalty`, all nullable), `Stop`, `Seed`, `ResponseFormat` (`Text | JsonObject | JsonSchema(name, schema, strict)`). Message and completion text is content (ADR-0010), so the record `ToString()` of `NeutralMessage`, `CompletionOutcome.Success` and `StreamEvent.ContentDelta` redacts it.
- `FinishReason`: `Stop | Length | ContentFilter`.
- `CompletionOutcome`: either `Success(text, finishReason, NeutralUsage?)` or `Failure(ProviderFailure)`.
- `StreamEvent`: `ContentDelta(text)`, `Finish(reason)`, `Usage(NeutralUsage)`, `Failed(ProviderFailure)`. The stream always ends with exactly one `Finish` or `Failed`, and `Usage` may come before or after `Finish`.
- `NeutralUsage(int InputTokens, int OutputTokens, int? CachedInputTokens)`. Only *reported* values go here. The Adapter never estimates. `CachedInputTokens` is a subset of `InputTokens`: `null` when the Provider didn't report it, `0` when it reported none. If a Provider reports more cached than input tokens, the Adapter sets `CachedInputTokens = null` (everything is then priced at the full input rate) and increments `modelswitch.provider.invalid_usage`. It doesn't fail the Attempt.
- `ProviderFailure(FailureClass Class, string Code, int? HttpStatus, TimeSpan? RetryAfter, bool RequestSent)`. Classification follows [routing-and-resilience.md §3](routing-and-resilience.md#3-failure-classification). `FailureClass` is `Transient | Permanent | Ambiguous`. `Code` is one of the sanitized `ProviderFailureCodes`: `connect_failed`, `timeout`, `connection_reset`, `edge_timeout`, `bad_request`, `context_length_exceeded`, `unauthorized`, `forbidden`, `not_found`, `payload_too_large`, `unprocessable`, `client_error` (unlisted 4xx), `request_timeout`, `conflict`, `too_early`, `rate_limited`, `quota_exhausted` (402 and billing 429s), `server_error` (all 5xx except 524) and `invalid_provider_response`. These are Adapter-level codes, separate from the gateway's client-facing error codes in [api.md §2](api.md#2-error-codes).
- `ConnectivityResult(bool Reachable, ProviderFailure? Failure)` and `DiscoveredModel(string Id)` are the results of `CheckAsync` and `ListModelsAsync`.

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
- Always sends `stream_options: {include_usage: true}` when streaming. Usage is accepted on any chunk, including one that also has `choices` (OpenRouter) and repeated on several chunks (Together): the Adapter keeps the latest and emits a single `Usage` event when the stream ends.
- `CachedInputTokens` is read from `usage.prompt_tokens_details.cached_tokens`, falling back to a top-level `usage.cached_tokens` (some Together models use the flat shape).
- Maps `response_format` 1:1. `max_tokens` is sent as `max_completion_tokens`, falling back to `max_tokens` for endpoints that reject the newer field (a per-Provider flag).
- Parses SSE line by line from a `PipeReader` and each chunk with `Utf8JsonReader` (no per-chunk LINQ or DOM). Comment lines such as OpenRouter's `: OPENROUTER PROCESSING` keep-alives are skipped. `data: [DONE]` ends the stream; a stream that ends without `[DONE]` after a `finish_reason` still succeeds, and one that ends before it fails ambiguous.
- An error object inside a 200 response maps to a failure: `Failed` in a stream, `Failure` for a non-streaming body (OpenRouter answers 200 with `{"error":{"code":429,...}}`). It is classified by the inner error's `code` (OpenAI sends a string, OpenRouter a numeric status) and `type` ([routing-and-resilience.md §3](routing-and-resilience.md#3-failure-classification)). A 400 whose message says "maximum context length" is `context_length_exceeded` even without OpenAI's `code`, because OpenRouter sends only the sentence. The message is inspected, never kept.
- A 429 is classified by `error.code`: billing/quota codes are permanent, never retried.
- **Before adding another hosted Provider** ([research/hosted-providers.md](research/hosted-providers.md) §4): Groq needs a separate `jsonSchemaStreaming` Capability (it can't stream JSON-schema output); OpenRouter needs each Provider Model to pin one upstream (`provider.only`, `allow_fallbacks: false`) so an Attempt's Price is known; Together reports context-length errors as 403.
- Per-Provider switches live in `OpenAICompatibleOptions`, passed to the Adapter's constructor along with the `HttpClient` the Registry wires (its outbound handler as primary handler, no resilience handlers, `Timeout = Infinite` because the Adapter enforces `AttemptTimeout`): `AuthHeaderName` (`Authorization` sends `Bearer <secret>`, any other name sends the bare secret), `UseLegacyMaxTokens`, and `PinnedUpstreams` (OpenRouter: sends `provider: { only: [...], allow_fallbacks: false }`).
- OpenRouter documents `max_tokens`; whether it honours `max_completion_tokens` is confirmed when the fixtures are recorded. If it does not, set `UseLegacyMaxTokens` on OpenRouter Providers.
- `CheckAsync`: `GET {endpoint}/models` with a 5 s timeout.

## 3. Ollama Adapter

- `POST {endpoint}/api/chat` with `stream: true|false`, `options: {temperature, top_p, seed, stop, num_predict}`.
- `format: "json"` for `JsonObject`, and `format: <schema>` for `JsonSchema` (Ollama ≥ 0.5).
- Streaming is NDJSON (one JSON object per line). The final object has `done: true` and `prompt_eval_count` / `eval_count`, which map to reported usage. `prompt_eval_cached_count`, when present, maps to `CachedInputTokens`.
- `done_reason` maps to `finish_reason` (`stop`, `length`).
- A usage is reported only when both `prompt_eval_count` and `eval_count` are present (Ollama omits `prompt_eval_count` when the prompt was fully cached; the Adapter then reports no usage rather than a partial one). `prompt_eval_cached_count` larger than `prompt_eval_count` sets `CachedInputTokens = null` and increments `modelswitch.provider.invalid_usage`. A `done_reason` of `content_filter` (never emitted by Ollama itself, but possible behind a proxy) maps to `ContentFilter`; any unknown reason maps to `Stop`.
- Errors: Ollama's own error body is a bare string, `{"error": "..."}`, with no machine code, so classification is by HTTP status. An `error` object with a `code` (what a reverse proxy in front of Ollama may emit) is honoured for billing/quota 429s. Ollama has no distinguishable context-length error (it truncates the prompt), so that suite scenario is skipped. A mid-stream `{"error": ...}` line, or one in a 200 non-streaming body, fails transient (nothing classifiable inside).
- The streaming parser reads NDJSON lines from a `PipeReader` and parses each with `Utf8JsonReader`. Fixtures: `fixtures/ollama/` is hand-authored from Ollama's documented API and marked `"_source": "synthetic"`. Ollama has no recording requirement.
- `CheckAsync`: `GET {endpoint}/api/version`. `ListModelsAsync`: `GET /api/tags`.
- No credential by default. An optional bearer token is supported for Ollama behind a reverse proxy.

## 4. Shared contract test suite (`ModelSwitch.AdapterContractTests`)

An abstract xUnit class `AdapterContract<TAdapter>` holds every scenario (partial files by area: Success, Failures, Behaviour). Each Adapter has a subclass that supplies only:
- a `ProviderWireFormat`: how that Provider's wire format is put on the wire. It encodes completions, stream frames, error bodies, embedded errors and malformed payloads (backed by the recorded fixtures `fixtures/<adapter>/*.json|.sse|.ndjson`), and asserts how `json_object` and `json_schema` requests are encoded, and
- a factory for the Adapter, which receives an `AdapterTestEnvironment` (an `HttpClient` without resilience handlers, a capturing `ILoggerFactory` and a `TimeProvider`).

The suite creates one WireMock.Net server per test and does the stubbing itself. Two scenarios need a raw TCP server instead, because WireMock cannot close a socket mid-body or stall mid-body: the stream cut and the mid-stream cancellation (`RawStreamServer`). `SuiteSelfTest/ReferenceAdapterContractTests` runs the suite against a tiny test-only Adapter to prove the suite itself is sound. The real Adapters' test classes look the same.

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
| | Error object in a 200 stream → `Failed`; in a 200 non-streaming body → `Failure`, classified by the inner code |
| | 429 with a billing/quota `error.code`, and 402 → permanent; 524 → ambiguous; unlisted 5xx → transient; unlisted 4xx → permanent |
| Behaviour | Cancellation mid-stream stops reading within 100 ms and aborts the connection |
| | No request/response body appears in captured logs |
| | Secret appears only in the auth header, never in the URL |

A new Adapter is "done" when it passes the whole suite unchanged (FR-02 acceptance).

### Wire fixtures

**OpenAI is the reference Provider** for the OpenAI-compatible Adapter: its fixtures in `tests/ModelSwitch.AdapterContractTests/fixtures/openai-compatible/` are the canonical contract. They are captured through OpenRouter, with every request pinned to the OpenAI upstream (`provider: { only: ["openai"], allow_fallbacks: false }`), because the project has an OpenRouter key and no OpenAI key ([ADR-0013](adr/0013-wire-fixtures-recorded-through-openrouter.md)). Other hosted Providers' deviations (see [research/hosted-providers.md](research/hosted-providers.md)) are added as extra fixtures, never by changing the reference ones.

Every fixture is stamped with a `_source`: `recorded-openrouter`, `provisional` or `synthetic`. `.json` fixtures are envelopes (`status`, `headers`, `body` or `body_text`) and `.sse` fixtures are raw streams whose leading SSE comment lines carry the stamp. A test fails if a `synthetic-*` file claims to be a recording.

- **Recorded** (`recorded-openrouter`): non-streaming and streaming text, streaming with `stream_options.include_usage`, `finish_reason=length`, `json_object` and `json_schema` responses, 400 invalid request, 400 context length exceeded, 401 and 404 unknown model. Until someone runs the script, these files are **`provisional`**: hand-written from OpenAI's and OpenRouter's documented formats so the Adapter and the suite can be built, and overwritten by the recording.
- **Synthetic** (`synthetic`, files named `synthetic-*`) for everything that can't be provoked on demand: 429 rate limit with `Retry-After`, 429 `insufficient_quota`, 402, 524, 500, a 200 non-streaming body carrying an error object, usage on a chunk that also has `choices`, an error chunk mid-stream, and OpenAI-shaped 400/401/404 bodies. These are hand-written from the documented error bodies. The billing 429 sits next to the rate-limit 429 because the two must be classified differently. The OpenAI-shaped 400/401/404 stay beside the recorded ones because OpenRouter's error bodies differ from OpenAI's.
- **Use.** The shared suite renders each fixture as a template (`OpenAICompatibleWireFormat`), so the captured field set is on the wire and only the scenario's values change. `OpenAICompatibleFixtureTests` also replays every fixture verbatim and reads its expectations out of the fixture, so a re-recording needs no test edits.
- **Recording** is a manual script, `scripts/record-fixtures` (bash, curl and jq), run with `OPENROUTER_API_KEY` and **never in CI**. It targets `https://openrouter.ai/api/v1`, takes `--model` (default `openai/gpt-4.1-nano`) and `--context-model`, sends synthetic prompts only, passes the key on curl's stdin rather than its argument list, strips `authorization`, `openai-organization`, `openai-project`, `x-request-id`, `cookie`, `set-cookie` and other identifying headers, and stamps each fixture with `"_source": "recorded-openrouter"`, its capture date and model. It never touches `synthetic-*` files. Re-run it only when the Adapter changes or wire drift is suspected, then review the diff before committing.

## 5. Mock Provider (`tools/ModelSwitch.MockProvider`)

This is a small ASP.NET Core app that implements **both** wire protocols. Tests, load tests and the demo all use it. Its behaviour is set per request through the model id or a control header, so a test can script faults without restarting it:

```
model = "mock-echo"                          → echoes the last user message
model = "mock-fixed?latency=300&tokens=50"   → fixed latency + output size (any other name, e.g. mock-a, behaves like mock-fixed)
X-Mock-Fault: status=503;times=2             → fail twice, then succeed (keyed by X-Mock-Scenario id, or the model id)
X-Mock-Fault: status=429;retry-after=2;error=insufficient_quota   → Retry-After header and the Provider error code in the body
X-Mock-Fault: hang                           → never send headers
X-Mock-Fault: latency=300                    → delay the response
X-Mock-Fault: cut-after=5                    → send 5 chunks then drop the socket
X-Mock-Fault: no-usage                       → omit usage
X-Mock-Fault: garbage                        → invalid JSON
X-Mock-Fault: inline-error=rate_limit_exceeded → a 200 response carrying an error object
X-Mock-Usage: cached=900                     → report 900 of the input tokens as cached (also input=, output=)
```

Directives combine with `;`. The mock serves `POST /v1/chat/completions` (JSON or SSE), `GET /v1/models`, `POST /api/chat` (JSON or NDJSON), `GET /api/tags` and `GET /api/version`. `max_tokens`, `max_completion_tokens` and `num_predict` truncate the output with `finish_reason: length`. Tests host it in-process with `MockProviderApp.StartAsync()`, which listens on a free loopback port; the executable runs it standalone. There is no Dockerfile yet: it arrives with the compose `demo` profile.

The gateway never forwards client headers to Providers, so `X-Mock-Fault` works only in direct Adapter tests. Full-stack tests script faults through the mock's `POST /__scenarios` control endpoint instead. It sets the fault for a given mock model id (e.g. `{"model":"mock-a","fault":"status=503;times=2"}`) before the test calls the gateway.
