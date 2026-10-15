# API Reference (design)

This is the contract the implementation must meet. Once the code exists, the generated OpenAPI document at `/openapi/v1.json` is authoritative for field-level detail, and this file must be kept consistent with it.

- `/v1/*`: the **inference API**, used by applications with an API Key. It follows the OpenAI Chat Completions format (ADR-0009).
- `/admin/*`: the **admin API**, used by the dashboard with an Administrator session cookie (ADR-0011).
- `/health`, `/ready`: probes, unauthenticated.

## 1. Inference API

### Authentication

```
Authorization: Bearer msk_7f3a9c21_<43 base64url chars>
```

| Situation | Status | `error.code` |
|---|---|---|
| Missing, malformed, unknown, revoked or expired key | 401 | `invalid_api_key` |
| Tenant suspended | 403 | `tenant_suspended` |
| Key lacks the required Scope | 403 | `insufficient_scope` |

The response never reveals *why* a key is invalid, so revoked and unknown keys look the same.

**Scopes (MVP):** `inference` (chat completions + model list) and `models:read` (model list only).

### Request headers (ModelSwitch extensions)

| Header | Purpose |
|---|---|
| `X-ModelSwitch-Task` | Task Type for rule-based routing, `[a-z0-9_-]{1,64}` |
| `X-ModelSwitch-Timeout-Ms` | Requests a Deadline shorter than the policy's. Cannot extend it |
| `X-Client-Request-Id` | Client correlation ID, ≤128 printable ASCII chars, stored as `client_request_id` |
| `traceparent` / `tracestate` | W3C trace context, continued if present |

### Response headers

| Header | Value |
|---|---|
| `X-Request-Id` | The Inference Request ID (UUIDv7). Present on **every** response, including errors |
| `X-ModelSwitch-Attempts` | Number of Attempts made |
| `X-RateLimit-Limit` / `-Remaining` / `-Reset` | Rate Limit state (non-streaming and SSE) |
| `Retry-After` | On 429 and 503 |

### `POST /v1/chat/completions`

**Supported request fields**

| Field | Notes |
|---|---|
| `model` (required) | A Model Alias |
| `messages` (required) | Roles `system`, `developer`, `user`, `assistant`. `content` must be a string or an array of `{type:"text"}` parts |
| `stream` | boolean |
| `stream_options.include_usage` | Always effectively `true`, because the gateway needs usage. Echoed as the client asked |
| `max_tokens` / `max_completion_tokens` | Upper bound for output. If absent, the policy's `defaultMaxOutputTokens` applies (needed for the Reservation) |
| `temperature`, `top_p`, `stop`, `seed`, `presence_penalty`, `frequency_penalty` | Passed through when the Provider Model declares the parameter. Otherwise `400 unsupported_parameter` |
| `response_format` | `{type:"text"}`, `{type:"json_object"}`, `{type:"json_schema", json_schema:{…}}`. Requires the matching Capability |
| `user` | Accepted but **not stored** (it may be personal data) |

**Rejected with `400 unsupported_parameter`:** `tools`, `tool_choice`, `functions`, `n` ≠ 1, `logprobs`, `audio`, `modalities`, `prediction`, image or audio content parts, and any unknown top-level field. Rejecting unknown fields is deliberate (FR-02): it stops a request from silently meaning something different than the client intended. **Phase 1 temporary limits:** `stream: true` is rejected with `400 unsupported_parameter` until SSE lands (Phase 2); `estimated_cost` is absent from `modelswitch` until the cost calculator exists; parameters are not yet checked against Provider Model capabilities; `usage_source` is `reported` or `unavailable`; and `GET /v1/models` lists every configured alias for every Tenant. Unsupported fields and bad values return `unsupported_parameter` or `invalid_request` (not `validation_failed`, which is Admin API only), and `error.param` is the first invalid field. **Tools / function calling are post-MVP.** Supporting them would mean normalizing streaming tool-call deltas in both Adapters, a `tools` Capability with its eligibility rule, and contract-suite coverage. The demo's "connect an application" works with plain chat.

**Limits (configurable, defaults):** body ≤ 1 MiB, ≤ 256 messages, `Content-Type: application/json` only.

**Non-streaming response**: the OpenAI `chat.completion` object, plus:

```json
{
  "id": "chatcmpl-0192f0c8-…",
  "object": "chat.completion",
  "created": 1760450000,
  "model": "balanced",
  "choices": [{ "index": 0, "message": { "role": "assistant", "content": "…" }, "finish_reason": "stop" }],
  "usage": { "prompt_tokens": 42, "completion_tokens": 17, "total_tokens": 59 },
  "modelswitch": {
    "request_id": "0192f0c8-…",
    "usage_source": "reported",
    "estimated_cost": { "amount": "0.0000213", "currency": "USD" },
    "attempts": 1
  }
}
```

`model` echoes the **alias**, not the Provider Model ID (FR-02). The `modelswitch` object is the optional metadata field from PRD §8. OpenAI SDKs ignore it.

**Streaming response** (`text/event-stream`):

```
data: {"id":"chatcmpl-…","object":"chat.completion.chunk","model":"balanced","choices":[{"index":0,"delta":{"role":"assistant"}}]}

data: {"id":"chatcmpl-…","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hel"}}]}

data: {"id":"chatcmpl-…","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: {"id":"chatcmpl-…","object":"chat.completion.chunk","choices":[],"usage":{…},"modelswitch":{…}}

data: [DONE]

```

- **Completion signal:** `data: [DONE]` is sent only after a successful completion.
- **Failure before the first chunk:** the gateway may still retry or fall back. If it gives up, it returns an ordinary HTTP error response (not SSE).
- **Failure after the first chunk:** the gateway sends one `data: {"error":{…}}` event and closes the stream **without** `[DONE]`. It never replays a partially delivered stream (FR-05).
- **Keep-alive:** an SSE comment line `: keep-alive` is sent every 15 s while waiting for the Provider.
- **Client disconnect:** cancels the upstream Attempt immediately.

### `GET /v1/models`

Returns the Model Aliases the Tenant may use, in OpenAI list format: `{"object":"list","data":[{"id":"balanced","object":"model","created":…,"owned_by":"modelswitch"}]}`. Provider Model IDs are never included.

### Inference error body

Every error response is an RFC 9457 Problem Details document (`Content-Type: application/problem+json`). On `/v1/*` it also carries the OpenAI `error` object as an extension member, so OpenAI SDKs still parse it (ADR-0012).

```json
{
  "type": "/errors/no_eligible_model",
  "title": "No eligible model",
  "status": 422,
  "detail": "No eligible model can serve this request.",
  "instance": "/v1/chat/completions",
  "errorCode": "no_eligible_model",
  "requestId": "0192f0c8-…",
  "error": {
    "message": "No eligible model can serve this request.",
    "type": "routing_error",
    "code": "no_eligible_model",
    "param": null,
    "request_id": "0192f0c8-…"
  }
}
```

`detail` and `error.message` are safe to show to clients. They never contain Provider response bodies, credentials or prompt content. **Validation errors** (`invalid_request`, `unsupported_parameter`, `context_length_exceeded`, `validation_failed`) always return **400**, and list each invalid field in an `errors` extension (`{"messages[3].content": ["…"]}`).

## 2. Error codes

Every error has one code from this closed set. The `type` groups codes and maps to OpenAI's `type` values where those exist.

| Code | HTTP | Type | Retry by client? |
|---|---|---|---|
| `invalid_request` | 400 | `invalid_request_error` | No |
| `unsupported_parameter` | 400 | `invalid_request_error` | No |
| `context_length_exceeded` | 400 | `invalid_request_error` | No |
| `invalid_api_key` | 401 | `authentication_error` | No |
| `budget_exceeded` | 402 | `budget_error` | No (until the period resets) |
| `request_cost_limit_exceeded` | 402 | `budget_error` | No |
| `insufficient_scope` | 403 | `permission_error` | No |
| `tenant_suspended` | 403 | `permission_error` | No |
| `model_not_allowed` | 403 | `permission_error` | No |
| `model_not_found` | 404 | `invalid_request_error` | No |
| `payload_too_large` | 413 | `invalid_request_error` | No |
| `unsupported_media_type` | 415 | `invalid_request_error` | No |
| `no_eligible_model` | 422 | `routing_error` | No (capability or cost constraints cannot be met) |
| `rate_limit_exceeded` | 429 | `rate_limit_error` | Yes, after `Retry-After` |
| `concurrency_limit_exceeded` | 429 | `rate_limit_error` | Yes, after `Retry-After` |
| `provider_error` | 502 | `provider_error` | Maybe |
| `provider_unavailable` | 503 | `provider_error` | Yes (all eligible circuits open, or Fallbacks exhausted) |
| `limits_unavailable` | 503 | `api_error` | Yes (Valkey down, fail-closed) |
| `deadline_exceeded` | 504 | `timeout_error` | Maybe |
| `stream_interrupted` | — (SSE event) | `provider_error` | Maybe, but the output may be partial |
| `run_cost_cap_reached` | — (Evaluation Run `error`) | `budget_error` | No. The run's `maxEstimatedCost` would be exceeded; remaining Cases are `skipped` |
| `internal_error` | 500 | `api_error` | Maybe |
| `validation_failed` | 400 | `invalid_request_error` | No. Admin API only (policy, dataset, limits… bodies) |
| `forbidden` | 403 | `permission_error` | No. Admin API only |
| `not_found` | 404 | `invalid_request_error` | No. Admin API only |
| `conflict` | 409 | `invalid_request_error` | No. Admin API only (stale `expectedVersion`) |

`budget_exceeded` uses **402, not 429**, because OpenAI SDKs automatically retry 429, and retrying cannot help when the budget is exhausted.

## 3. Admin API

All `/admin/*` endpoints need an Administrator session. State-changing methods also need the `X-XSRF-TOKEN` antiforgery header. Errors use the same RFC 9457 `application/problem+json` body as the inference API, without the `error` member. Validation errors return `400 validation_failed` with an `errors` extension.

Authorization uses **Role** and **Tenant Grant** together:

| Role | Can |
|---|---|
| Viewer | Read everything inside their Tenant Grant |
| Operator | Viewer + manage Tenants, keys, limits, Budgets, Datasets, Evaluation Runs inside their Tenant Grant |
| Owner | Everything, including Providers, Provider Models, Prices, Routing Policies, Administrators. Always has an all-Tenants grant |

A resource outside the caller's Tenant Grant returns **404**, not 403, so its existence is not revealed.

Lists use cursor pagination, `?limit=50&cursor=<opaque>`, and return `{ items, nextCursor }`. Time filters are `from`/`to` in ISO-8601 UTC.

### Session

| Method | Path | Notes |
|---|---|---|
| POST | `/admin/session` | `{email, password}` → sets cookie. Locked out after 5 failures in 15 min |
| DELETE | `/admin/session` | Sign out |
| GET | `/admin/session` | Current Administrator, Role, Tenant Grant, antiforgery token |

### Tenants and keys

| Method | Path | Role |
|---|---|---|
| GET/POST | `/admin/tenants` | V / O |
| GET/PATCH | `/admin/tenants/{tenantId}` | V / O. Status: `active`, `suspended` |
| PUT | `/admin/tenants/{tenantId}/limits` | O. `{requestsPerMinute, maxConcurrent, maxRequestCost, budget:{amount, period:"month"}}` |
| PUT | `/admin/tenants/{tenantId}/model-access` | O. Allowed Model Aliases (`["*"]` = all) |
| GET/POST | `/admin/tenants/{tenantId}/api-keys` | V / O. POST returns the raw key **once** |
| DELETE | `/admin/tenants/{tenantId}/api-keys/{keyId}` | O. Revokes the key (soft, sets `revoked_at`) |

### Providers, models, prices (Owner)

| Method | Path | Notes |
|---|---|---|
| GET/POST | `/admin/providers` | `{name, adapterType: "openai_compatible"\|"ollama", endpoint, credential?: {secret}\|{envVar}}` |
| GET/PATCH | `/admin/providers/{id}` | The secret is write-only. Responses show `credential: {kind, configured: true}` |
| POST | `/admin/providers/{id}/connectivity-check` | Calls the Adapter's `CheckAsync` and returns latency + normalized error |
| GET/POST | `/admin/providers/{id}/models` | `{providerModelId, displayName, capabilities:{…}, enabled}` |
| PATCH | `/admin/models/{modelId}` | Capabilities, enabled |
| GET/POST | `/admin/models/{modelId}/prices` | POST creates a new Price version (`effectiveFrom` ≥ now). Existing Prices are immutable |

### Routing (Owner to write, Viewer to read)

| Method | Path | Notes |
|---|---|---|
| GET | `/admin/routing-policies` | Latest version of each policy |
| POST | `/admin/routing-policies` | Creates the policy at version 1 |
| PUT | `/admin/routing-policies/{policyId}` | Validates (→ 400 `validation_failed`) and creates version n+1 (FR-11). Body includes `expectedVersion` for optimistic concurrency → 409 |
| GET | `/admin/routing-policies/{policyId}/versions[/{version}]` | History |
| POST | `/admin/routing-policies/validate` | Dry-run validation |
| POST | `/admin/routing-policies/{policyId}/simulate` | `{tenantId, taskType, requiredCapabilities, estimatedInputTokens}` → the RoutingDecision that would result |
| GET/PUT/DELETE | `/admin/aliases/{alias}` | Binds an alias to a policy |

### Requests and usage

| Method | Path | Notes |
|---|---|---|
| GET | `/admin/requests` | Filters: `from`, `to`, `tenantId`, `origin` (`api`, `evaluation`), `alias`, `providerId`, `modelId`, `status`, `errorCode`, `hadFallback`. Sort by `startedAt` (default desc) or `durationMs` |
| GET | `/admin/requests/{requestId}` | Request + RoutingDecision + Attempts timeline + Usage Records |
| GET | `/admin/usage` | `groupBy=tenant\|provider\|model\|alias` (multi), `interval=hour\|day\|month`, filters as above. Returns tokens, Estimated Cost, request and error counts |
| GET | `/admin/overview` | Dashboard tiles + time series: volume, error rate, p50/p95 latency, p50/p95 TTFT, tokens, cost, fallback rate |
| GET | `/admin/provider-health` | Per Provider Model: aggregate + per-instance circuit state, recent error rate, latency |

### Evaluation

| Method | Path | Notes |
|---|---|---|
| GET/POST | `/admin/datasets` | Scoped to a Tenant |
| POST | `/admin/datasets/{id}/versions` | Upload JSONL (≤ 10 MiB, ≤ 5 000 Cases) → new immutable version. Invalid lines → 400 `validation_failed` |
| GET | `/admin/datasets/{id}/versions/{v}/cases` | Paginated |
| GET/POST | `/admin/prompts`, `/admin/prompts/{id}/versions` | Prompt Versions |
| POST | `/admin/evaluations` | Creates a **Comparison** with one Evaluation Run per Run Configuration. Returns 202 + ids |
| GET | `/admin/evaluations` | Comparisons and runs, filterable by dataset and status |
| GET | `/admin/evaluations/{runId}` | Status, progress, aggregates, failure summary |
| GET | `/admin/evaluations/{runId}/results` | Case Results, paginated |
| POST | `/admin/evaluations/{runId}/cancel` | |
| GET | `/admin/evaluations/{runId}/export?format=csv\|jsonl` | Streams the export |
| GET | `/admin/comparisons/{id}` | Side-by-side aggregates |

### Administrators and audit (Owner)

| Method | Path |
|---|---|
| GET/POST/PATCH | `/admin/administrators[/{id}]` |
| GET | `/admin/audit-records` |

## 4. Health

| Path | Meaning | Checks |
|---|---|---|
| `GET /health` | The process is alive | None beyond the process responding. Always 200 unless shutting down |
| `GET /ready` | This instance should receive traffic | PostgreSQL reachable, configuration snapshot loaded, Valkey reachable (only when `Limits:FailureMode=Closed`) |

Provider status is **not** part of readiness. A Provider outage affects every instance equally, so taking instances out of rotation would not help; the gateway answers with `provider_unavailable` instead. `/ready` reports Provider Health in its body for information only (PRD §8). The body is JSON and never contains secrets.
