# Routing and Resilience

This document covers how a Model Alias becomes an ordered list of Candidates, and how the attempt loop turns that list into one outcome. It implements FR-04 and FR-05. Terms are defined in [`CONTEXT.md`](../CONTEXT.md).

## 1. Routing pipeline

```
RoutingInput { tenant, alias, taskType?, requiredCapabilities, estimatedInputTokens, maxOutputTokens, snapshot }
      │
      ▼
 resolve alias ──(unknown)──────────────────────────▶ model_not_found
      │ tenant not allowed alias ────────────────────▶ model_not_allowed
      ▼
 load Policy Version (latest in snapshot)
      ▼
 select candidate set
   static: policy.candidates
   rules:  first rule whose `when` matches → rule.candidates, else policy.default
   cost:   policy.candidates (ranking differs)
      ▼
 ELIGIBILITY FILTER  (hard, never overridden)
      ▼
 RANKING             (soft, only orders eligible Candidates)
      ▼
 truncate to policy.maxCandidates
      ▼
 RoutingDecision { policyId, policyVersion, ruleMatched?, ordered[], excluded[{model, reason}] }
```

`IRouter.Decide` is a **pure function** of `(RoutingInput, snapshot, circuitStates)`. It does no I/O, so it can be unit-tested exhaustively with a table of cases (PRD §13: "routing correctness on a known test matrix").

### Eligibility filters, in order

Each exclusion is recorded with its reason code.

| # | Reason | Rule |
|---|---|---|
| 1 | `model_disabled` | Provider Model or its Provider is disabled |
| 2 | `capability_missing:<name>` | The request needs a Capability the model lacks (`streaming`, `jsonObject`, `jsonSchema`, any requested parameter) |
| 3 | `context_window` | `estimatedInputTokens + maxOutputTokens > contextWindow`, or `maxOutputTokens > model.maxOutputTokens` |
| 4 | `provider_not_allowed` | Policy `allowedProviders` is set and does not include the model's Provider |
| 5 | `no_price` | The model has no Price in force. Unpriced models are never eligible, so every request can be costed |
| 6 | `over_request_cost_limit` | The worst-case cost exceeds `min(policy.maxRequestCost, tenant.maxRequestCost)` |
| 7 | `circuit_open` | The circuit is open on this instance. This is the only **dynamic** filter (FR-04 determinism exception) |

The Budget is **not** checked during routing. It is enforced afterwards by the Reservation (see [usage-and-budgets.md](usage-and-budgets.md)), so the result can never depend on a ranking score.

**Outcomes when the eligible list is empty:**
- Every Candidate excluded *only* by `circuit_open` → `503 provider_unavailable` with `Retry-After` = shortest remaining break.
- Otherwise → `422 no_eligible_model`. The response body names the distinct reasons, never model IDs.

### Worst-case cost

```
worstCase(model) = estimatedInputTokens × price.inputPerMillion / 1e6
                 + maxOutputTokens     × price.outputPerMillion / 1e6
```

`estimatedInputTokens` is counted with the tokenizer named in the model's `capabilities.tokenizer`, plus 4 tokens per message. A tokenizer this gives a reasonable estimate, not an exact count. When a model declares no tokenizer, the gateway uses `ceil(utf8Bytes / 3)`, which is deliberately pessimistic. Text is counted in windows of 1024 characters, because byte-pair merging is quadratic in an unbroken run (one 1 MiB word would otherwise cost minutes of CPU); a window edge can change the count by a token.

### Ranking

| Strategy | Order |
|---|---|
| `static` | Listed order |
| `rules` | Listed order within the matched rule |
| `cost` | Ascending `worstCase`. Ties are broken by listed order, then by Provider Model id |

Every tie-break is deterministic. Ranking never uses randomness, and in the MVP it never uses latency or load. Latency-aware ranking is post-MVP and would be added as a new strategy, measured first (PRD §14.3).

## 2. Routing Policy definition

Stored as `routing_policy_version.definition`. It is validated against a JSON Schema (`src/Modules/ModelSwitch.Routing/Domain/PolicyDefinition.schema.json`, embedded; the validator enforces it directly, so no JSON Schema library is added, and a unit test keeps the two in sync) plus semantic checks: referenced models exist, at least one candidate, rules are reachable, and `maxAttempts ≥ 1`, no duplicate Candidates, `attemptTimeoutMs ≤ deadlineMs`, `backoff.baseMs ≤ maxMs`. A rule is unreachable when an earlier rule matches every request it would match (its `when` conditions are all implied by this rule's). `when` needs at least one of `taskType` / `requires`; a rule matches when all present conditions hold (`requires`: the request needs every listed Capability). A failing definition is rejected with `400 validation_failed` (Problem Details, one entry per problem in `errors`).

```json
{
  "strategy": "rules",
  "candidates": ["openai-main/gpt-4.1-mini", "ollama-local/llama3.1:8b"],
  "rules": [
    { "when": { "taskType": ["extraction", "classification"] },
      "candidates": ["openai-main/gpt-4.1-mini", "openai-main/gpt-4.1"] },
    { "when": { "requires": ["jsonSchema"] },
      "candidates": ["openai-main/gpt-4.1"] }
  ],
  "default": ["ollama-local/llama3.1:8b", "openai-main/gpt-4.1-mini"],
  "allowedProviders": null,
  "maxRequestCost": "0.05",
  "defaultMaxOutputTokens": 1024,
  "maxCandidates": 3,
  "execution": {
    "deadlineMs": 60000,
    "attemptTimeoutMs": 30000,
    "maxAttempts": 4,
    "maxRetriesPerCandidate": 1,
    "backoff": { "baseMs": 200, "maxMs": 2000, "jitter": "full" }
  }
}
```

Candidates are written as `<providerName>/<providerModelId>` for readability. When the version is saved they are resolved to Provider Model ids, and the stored version keeps both.

## 3. Failure classification

Each Adapter maps every failure to a `ProviderFailure { Class, Code, HttpStatus?, RetryAfter? }`. The Adapter contract tests ([provider-adapters.md](provider-adapters.md)) cover the full mapping.

| Situation | Class | Retry same model | Fall back |
|---|---|---|---|
| Connect failure, DNS failure, TLS handshake failure (nothing was sent) | transient | yes | yes |
| HTTP 408, 409 (conflict/lock), 425 | transient | yes | yes |
| HTTP 429 | transient | only if `Retry-After` ≤ remaining Deadline − attemptTimeout, and ≤ 5 s | yes |
| HTTP 429 whose `error.code` is a billing/quota code (`insufficient_quota`, `credit_balance_exhausted`, `organization_spend_limit_exceeded`), and HTTP 402 | permanent (provider misconfigured) | no | yes. Counts toward the circuit, like 401/403 |
| HTTP 500, 502, 503, 504, 529, and any other unlisted 5xx | transient | yes | yes |
| HTTP 524 (edge timeout after the request was sent) | **ambiguous** | no | yes (same conditions as other ambiguous outcomes) |
| Per-Attempt timeout **before** response headers were received | **ambiguous** | no | yes (non-streaming, or streaming before the First Token) |
| Connection reset after the request body was sent | **ambiguous** | no | yes (same conditions) |
| HTTP 400 (invalid request), 404 (unknown model), 413, 422 | permanent | no | **no**. The request itself is the problem. Exception: provider `context_length_exceeded` → fall back to the next model (its window may be larger) |
| HTTP 401, 403 | permanent (provider misconfigured) | no | yes. Counts toward the circuit, because an operator problem should not fail every request |
| Any other unlisted 4xx (e.g. 424, 498, 499) | permanent | no | no |
| An error object in a 200 body, streaming or not | classified by the inner error's `code`/status like the rows above. With nothing usable inside: transient | | |
| Malformed or unparseable success body | transient | yes | yes |
| Content filter / refusal reported by the Provider | not a failure | — | — (returned as `finish_reason: content_filter`) |
| Client cancellation | cancelled | no | no |
| Deadline reached | deadline | no | no |

Retrying an **ambiguous** outcome on the *same* model is avoided because the first call may have succeeded and been billed. Falling back to another model is allowed, and the ambiguous Attempt is charged at its worst-case cost (see [usage-and-budgets.md §4](usage-and-budgets.md#4-ambiguous-outcomes)).

## 4. Attempt loop (`AttemptExecutor`)

```
attempts = 0
for candidate in decision.ordered:
    if circuit(candidate).IsOpen: record skipped_circuit_open; continue
    for try in 0..policy.maxRetriesPerCandidate:
        if attempts == maxAttempts or now >= deadline or cancelled: stop
        attempts++
        timeout = min(attemptTimeoutMs, deadline - now, provider.requestTimeout)
        outcome = adapter.Execute(candidate, request, timeout, ct)
        record Attempt(outcome)
        circuit(candidate).Report(outcome)
        if outcome.Succeeded: return
        if outcome.Class == permanent and not fallback-eligible: return failure
        if streaming and firstTokenSent: return stream_interrupted
        if outcome.Class == transient and try < maxRetries:
            delay = RetryAfter if present, else backoff(try) with full jitter
            if RetryAfter present: it must be ≤ 5 s and ≤ (deadline - now) - attemptTimeout
            else: now + delay + minAttemptBudget (500 ms) must be ≤ deadline
            if it does not fit: break  // go to the next candidate
            await delay (cancellation-aware)
            continue
        break   // next candidate
return the last failure, normalized (provider_unavailable / provider_error / deadline_exceeded)
```

A per-Attempt timeout (`timeout`, or an edge `524`) is a Provider failure: its Attempt is `ambiguous` with `provider_error_type = timeout` and `error_code = provider_unavailable`. `deadline_exceeded` is returned to the client only when the request Deadline itself was reached (checked before every Attempt and after the last one); stopping on `maxAttempts` or running out of Candidates returns the last Provider failure.

Bounds:

- **Total Attempts** ≤ `maxAttempts` (default 4), across all Candidates. This bounds *retry amplification*: one client call produces at most 4 Provider calls. The failure-injection report measures it.
- **Total time** ≤ Deadline. The Deadline is `min(policy.deadlineMs, X-ModelSwitch-Timeout-Ms)` from request receipt.
- **Skipped Attempts.** A Candidate whose circuit is open is recorded as a `skipped_circuit_open` Attempt (it consumes an `attempt_number` but not `maxAttempts`). When nothing was called because every circuit was open the request fails `503 provider_unavailable` with a `Retry-After` of the shortest remaining break. Normally routing already caught this (`circuit_open` is an eligibility filter), so the skip path serves the race where a circuit opens between routing and the loop. The loop does not read the database: each Candidate carries the Provider Model's call target (endpoint, Adapter type, Credential Reference, timeout, tokenizer) from the same configuration snapshot the Routing Decision was made on.
- **Backoff maths** is hand-written (`RetryPolicy.Backoff`): `Polly.Core` has no public backoff API, only its retry strategy, which the attempt loop must not use (ADR-0004). Full jitter is a uniform sample from zero to `min(maxMs, baseMs * 2^try)`, taken from an injectable source so tests are deterministic.
- **Cancellation** is checked before every Attempt and every backoff delay, and is passed into the `HttpClient` call.

## 5. Circuit breakers

There is one breaker per Provider Model per instance (ADR-0005), behind `IProviderHealth` (Registry). It is built on Polly's circuit breaker state machine. The breaker gates the call itself (`IProviderHealth.ExecuteAsync`): an open circuit rejects without calling the Provider, and a half-open one admits a single probe, which Polly guarantees only for calls that run through it. Polly does not expose its sampling window, so the breaker also keeps a small window of outcomes for the health snapshot.

| Setting (deployment config) | Default |
|---|---|
| Sampling window | 30 s |
| Minimum calls in the window | 10 |
| Failure ratio to open | 0.5 |
| Break duration | 30 s, doubling on each consecutive re-open up to 5 min |
| Half-open | 1 probe Attempt at a time. Other requests treat the breaker as open |

The settings are deployment configuration: `Circuit:SamplingWindowSeconds`, `Circuit:MinimumCalls`, `Circuit:FailureRatio`, `Circuit:BreakSeconds`, `Circuit:MaxBreakSeconds`, `Circuit:SnapshotIntervalSeconds`, `Circuit:InstanceId` (env: `MODELSWITCH__Circuit__*`). Invalid values fall back to the defaults.

**Counted as failures:** transient, ambiguous, and Provider 401/403 and billing/quota failures (402, billing 429).
**Not counted:** permanent request errors (400/404/413/422), client cancellations, and content filters. Counting these would let one tenant's bad requests trip the breaker for everyone.

Every state transition emits a structured log line, a metric (`modelswitch.circuit.transitions`) and a span event.

**Health snapshot.** Each api instance upserts `registry.provider_health_snapshot` every 15 s (`ProviderHealthSnapshotService`, a hosted service in the host that owns the breakers). It cannot run in the worker: the circuits live in the api process's memory. Rows are keyed by `(instance_id, provider_model_id)`; `instance_id` defaults to the machine (container) name.

## 6. Streaming specifics

- **First Token.** An Attempt counts as "streaming started" when the **first content chunk is written to the client**. Up to that moment the stream is treated like a non-streaming call: the same loop, the same classification, retries and Fallbacks allowed, and the client sees nothing (no headers, no keep-alive: a failure here is an ordinary HTTP error). The gateway reads the Provider stream until the first non-empty content delta, or until the end of an empty completion (`Finish`), and only then answers `200 text/event-stream`.
- Role-only and empty deltas from the Provider are buffered and are not counted as the First Token, and a `Usage` event that arrives early is kept. Their purpose is to keep the retry window open as long as possible.
- The wait for the First Token (connecting, response headers and the first content) is bounded by the Attempt timeout (the smallest of the policy's `attemptTimeoutMs`, the Provider's request timeout and the time left to the Deadline); a Provider that exceeds it is an `ambiguous` `timeout`, like a non-streaming one. The Deadline bounds time to the First Token only: once a stream has started it runs until the Provider finishes or the client leaves.
- **After the First Token** nothing is retried and there is no Fallback. A Provider failure becomes the `stream_interrupted` event (api.md). A Provider that goes quiet for more than `min(attemptTimeoutMs, provider request timeout)` between events counts as failed (`timeout`). The Attempt is recorded `ambiguous` when the failure is (a cut connection), otherwise `failed`, with `error_code = stream_interrupted`; the circuit breaker is told about the failure (the call that opened the stream already counted as a success).
- **Keep-alives** are SSE comments (`: keep-alive`) written whenever the stream has been quiet for `Streaming:KeepAliveSeconds` (15 s), from the First Token on.
- **Client disconnect** (`RequestAborted`) cancels the token the Adapter's stream was started with, which aborts the Provider connection. The in-flight Attempt is recorded `ambiguous` (`provider_error_type = client_cancelled`) with estimated usage, and the request `cancelled`. The same happens when the response can no longer be written to. Settlement uses its own short-timeout token (architecture.md section 5), and for a finished stream runs after the last event has been handed to the client.
- `ttft_ms` is measured from request receipt to the first content chunk written (on the request) and from the Attempt's start to it (on the Attempt).
- Usage for a streaming Attempt comes from the Provider's final usage chunk (`stream_options.include_usage` for OpenAI-compatible, the final `done` object for Ollama). If the Provider sends none, or the stream breaks or is abandoned before it, input tokens are estimated, output tokens are counted from the forwarded text with the tokenizer (kept in memory only for that, never logged or stored), and `usage_source = estimated`.

## 7. Rate and concurrency limits

These are enforced before routing, using Valkey (ADR-0003). They apply to Request Origin `api` only. Evaluation traffic is throttled by the run's `concurrency` instead ([evaluation.md §5](evaluation.md#5-execution)).

- **Rate Limit**: GCRA (generic cell rate algorithm) per tenant, key `rl:{tenantId}`, one Lua `EVALSHA`, `requests_per_minute` with a burst of `max(1, rpm/10)`.
- **Concurrency Limit**: a sorted set `cc:{tenantId}` of lease ids scored by expiry, which is the Deadline plus 5 s. Acquire is Lua: remove expired leases, check `ZCARD < max`, then `ZADD`. Release is `ZREM` in a `finally` block. A lease from a crashed instance expires by itself.
- Both checks happen in **one** round trip, using a single script.
- On a Valkey error or timeout (budget 50 ms, `Limits:ValkeyTimeoutMs`): with `FailureMode=Closed` → `503 limits_unavailable` with `Retry-After: 1`, and with `Open` → allow and increment `modelswitch.limits.degraded`.

Implementation notes (`Inference`: `ILimiter` in `Contracts/`, `Limiter` decides, `ValkeyLimitStore` runs the script):

- **Atomicity.** The script computes both decisions first and writes only when both pass, so a request refused for concurrency does not spend a Rate Limit token. The rate check comes first: a rate-limited request is refused as `rate_limit_exceeded` without looking at leases.
- **Server time.** The script reads `TIME` from Valkey, so instances with skewed clocks agree. The key `rl:{tenantId}` holds the GCRA theoretical arrival time and expires on its own once the bucket is full.
- **Lease size.** The lease expiry is the Deadline of the alias's Routing Policy (shortened by `X-ModelSwitch-Timeout-Ms`, never extended) plus 5 s. An unknown alias gets the default Deadline; routing rejects it next, which releases the lease.
- **Release.** `Dispose` of the lease runs `ZREM` in the endpoint's `finally`, so it also happens on abort, on any error after step 6, and, for a stream, when the stream ends. A failed release is only logged: the lease expires by itself.
- **Unlimited Tenants.** A Tenant with neither a Rate Limit nor a Concurrency Limit (no `identity.tenant_limits` row, or both columns null) costs no Valkey call, so a Valkey outage does not reach it even when failing closed.
- **Headers.** A Tenant with a Rate Limit gets `X-RateLimit-Limit`, `-Remaining` and `-Reset` (whole seconds until the full burst is back) on success, streaming included. A 429 carries `Retry-After` only. A Concurrency 429 says `Retry-After: 1`, because the gateway cannot know when another request of the Tenant will end.
