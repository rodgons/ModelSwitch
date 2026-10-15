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

`estimatedInputTokens` is counted with the tokenizer named in the model's `capabilities.tokenizer`, plus 4 tokens per message. A tokenizer this gives a reasonable estimate, not an exact count. When a model declares no tokenizer, the gateway uses `ceil(utf8Bytes / 3)`, which is deliberately pessimistic.

### Ranking

| Strategy | Order |
|---|---|
| `static` | Listed order |
| `rules` | Listed order within the matched rule |
| `cost` | Ascending `worstCase`. Ties are broken by listed order, then by Provider Model id |

Every tie-break is deterministic. Ranking never uses randomness, and in the MVP it never uses latency or load. Latency-aware ranking is post-MVP and would be added as a new strategy, measured first (PRD §14.3).

## 2. Routing Policy definition

Stored as `routing_policy_version.definition`. It is validated against a JSON Schema plus semantic checks: referenced models exist, at least one candidate, rules are reachable, and `maxAttempts ≥ 1`. A failing definition is rejected with `400 validation_failed` (Problem Details, one entry per problem in `errors`).

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
| HTTP 500, 502, 503, 504, 529 | transient | yes | yes |
| Per-Attempt timeout **before** response headers were received | **ambiguous** | no | yes (non-streaming, or streaming before the First Token) |
| Connection reset after the request body was sent | **ambiguous** | no | yes (same conditions) |
| HTTP 400 (invalid request), 404 (unknown model), 413, 422 | permanent | no | **no**. The request itself is the problem. Exception: provider `context_length_exceeded` → fall back to the next model (its window may be larger) |
| HTTP 401, 403 | permanent (provider misconfigured) | no | yes. Counts toward the circuit, because an operator problem should not fail every request |
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
        timeout = min(attemptTimeoutMs, deadline - now)
        outcome = adapter.Execute(candidate, request, timeout, ct)
        record Attempt(outcome)
        circuit(candidate).Report(outcome)
        if outcome.Succeeded: return
        if outcome.Class == permanent and not fallback-eligible: return failure
        if streaming and firstTokenSent: return stream_interrupted
        if outcome.Class == transient and try < maxRetries:
            delay = backoff(try) with full jitter, or RetryAfter
            if now + delay + minAttemptBudget > deadline: break  // go to the next candidate
            await delay (cancellation-aware)
            continue
        break   // next candidate
return the last failure, normalized (provider_unavailable / provider_error / deadline_exceeded)
```

Bounds:

- **Total Attempts** ≤ `maxAttempts` (default 4), across all Candidates. This bounds *retry amplification*: one client call produces at most 4 Provider calls. The failure-injection report measures it.
- **Total time** ≤ Deadline. The Deadline is `min(policy.deadlineMs, X-ModelSwitch-Timeout-Ms)` from request receipt.
- **Cancellation** is checked before every Attempt and every backoff delay, and is passed into the `HttpClient` call.

## 5. Circuit breakers

There is one breaker per Provider Model per instance (ADR-0005). It is built on Polly's circuit breaker state machine and fed manually by `Report(outcome)`.

| Setting (deployment config) | Default |
|---|---|
| Sampling window | 30 s |
| Minimum calls in the window | 10 |
| Failure ratio to open | 0.5 |
| Break duration | 30 s, doubling on each consecutive re-open up to 5 min |
| Half-open | 1 probe Attempt at a time. Other requests treat the breaker as open |

**Counted as failures:** transient, ambiguous, and Provider 401/403.
**Not counted:** permanent request errors (400/404/413/422), client cancellations, and content filters. Counting these would let one tenant's bad requests trip the breaker for everyone.

Every state transition emits a structured log line, a metric (`modelswitch.circuit.transitions`) and a span event.

## 6. Streaming specifics

- An Attempt counts as "streaming started" when the **first content chunk is written to the client**. Up to that moment the stream is treated like a non-streaming call: retries and Fallbacks are allowed, and the client sees nothing.
- Role-only and empty deltas from the Provider are buffered and are not counted as the First Token. Their purpose is to keep the retry window open as long as possible.
- `ttft_ms` is measured from request receipt to the first content chunk written.
- Usage for a streaming Attempt comes from the Provider's final usage chunk (`stream_options.include_usage` for OpenAI-compatible, the final `done` object for Ollama). If the stream breaks before that, input tokens are estimated, output tokens are counted from the forwarded text with the tokenizer, and `usage_source = estimated`.

## 7. Rate and concurrency limits

These are enforced before routing, using Valkey (ADR-0003).

- **Rate Limit**: GCRA (generic cell rate algorithm) per tenant, key `rl:{tenantId}`, one Lua `EVALSHA`, `requests_per_minute` with a burst of `max(1, rpm/10)`.
- **Concurrency Limit**: a sorted set `cc:{tenantId}` of lease ids scored by expiry, which is the Deadline plus 5 s. Acquire is Lua: remove expired leases, check `ZCARD < max`, then `ZADD`. Release is `ZREM` in a `finally` block. A lease from a crashed instance expires by itself.
- Both checks happen in **one** round trip, using a single script.
- On a Valkey error or timeout (budget 50 ms): with `FailureMode=Closed` → `503 limits_unavailable`, and with `Open` → allow and increment `modelswitch.limits.degraded`.
