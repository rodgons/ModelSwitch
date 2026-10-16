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
| `X-ModelSwitch-Task` | Task Type for rule-based routing, `[a-z0-9_-]{1,64}`, otherwise `400 invalid_request`. Recorded as `task_type` |
| `X-ModelSwitch-Timeout-Ms` | Requests a Deadline shorter than the policy's. Cannot extend it. A positive integer, otherwise `400 invalid_request` (`errors` names the header) |
| `X-Client-Request-Id` | Client correlation ID, ≤128 printable ASCII chars, stored as `client_request_id`. Longer or non-printable values are rejected with `400 invalid_request` |
| `traceparent` / `tracestate` | W3C trace context, continued if present |

### Response headers

| Header | Value |
|---|---|
| `X-Request-Id` | The Inference Request ID (UUIDv7). Present on **every** response, including errors |
| `X-ModelSwitch-Attempts` | Number of Attempts made |
| `X-RateLimit-Limit` / `-Remaining` / `-Reset` | Rate Limit state (non-streaming and SSE) |
| `Retry-After` | On 429 and on 503 `provider_unavailable` when the gateway knows it: the shortest remaining circuit break, whether routing found every Candidate's circuit open or the loop found them open when it got there. Whole seconds, at least 1 |

### `POST /v1/chat/completions`

**Supported request fields**

| Field | Notes |
|---|---|
| `model` (required) | A Model Alias |
| `messages` (required) | Roles `system`, `developer`, `user`, `assistant`. `content` must be a string or an array of `{type:"text"}` parts |
| `stream` | boolean. `true` answers with Server-Sent Events (below). A model without the `streaming` Capability is not eligible for it (`422 no_eligible_model`) |
| `stream_options.include_usage` | The gateway always asks the Provider for usage, because it needs it. The client gets the final usage chunk only when it asked (`true`). Only meaningful with `stream: true` |
| `max_tokens` / `max_completion_tokens` | Upper bound for output. If absent, the policy's `defaultMaxOutputTokens` applies (needed for the Reservation) |
| `temperature`, `top_p`, `stop`, `seed`, `presence_penalty`, `frequency_penalty` | Passed through when the Provider Model declares the parameter. Otherwise `400 unsupported_parameter` |
| `response_format` | `{type:"text"}`, `{type:"json_object"}`, `{type:"json_schema", json_schema:{…}}`. Requires the matching Capability |
| `user` | Accepted but **not stored** (it may be personal data) |

**Rejected with `400 unsupported_parameter`:** `tools`, `tool_choice`, `functions`, `n` ≠ 1, `logprobs`, `audio`, `modalities`, `prediction`, image or audio content parts, and any unknown top-level field. Rejecting unknown fields is deliberate (FR-02): it stops a request from silently meaning something different than the client intended. **Phase 1 temporary limits:** `estimated_cost` is absent from `modelswitch` until the cost calculator exists; parameters are not yet checked against Provider Model capabilities; and `usage_source` is `reported` or `unavailable`. Unsupported fields and bad values return `unsupported_parameter` or `invalid_request` (not `validation_failed`, which is Admin API only), and `error.param` is the first invalid field. **Tools / function calling are post-MVP.** Supporting them would mean normalizing streaming tool-call deltas in both Adapters, a `tools` Capability with its eligibility rule, and contract-suite coverage. The demo's "connect an application" works with plain chat.

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
data: {"id":"chatcmpl-…","object":"chat.completion.chunk","created":1760450000,"model":"balanced","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

data: {"id":"chatcmpl-…","object":"chat.completion.chunk","created":1760450000,"model":"balanced","choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}]}

data: {"id":"chatcmpl-…","object":"chat.completion.chunk","created":1760450000,"model":"balanced","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: {"id":"chatcmpl-…","object":"chat.completion.chunk","created":1760450000,"model":"balanced","choices":[],"usage":{…},"modelswitch":{"request_id":"…","usage_source":"reported","attempts":1}}

data: [DONE]

```

Every chunk carries the same `id` (`chatcmpl-<request id>`) and the alias as `model`. The `role` chunk goes out together with the First Token, never before it. The usage chunk (`usage_source` is `reported`, or `estimated` when the Provider sent no usage) is sent only when the client set `stream_options.include_usage`.

- **Completion signal:** `data: [DONE]` is sent only after a successful completion.
- **Failure before the First Token** (the first content chunk): the gateway still retries or falls back, the client has seen nothing, and if it gives up it returns an ordinary HTTP error response (not SSE). Routing, Rate Limit and validation errors are ordinary responses too. Consequently the gateway sends no bytes at all until the First Token has arrived from a Provider.
- **Failure after the First Token:** the gateway sends one `data: {"error":{…}}` event (the same `error` object as Problem Details, `code` `stream_interrupted`) and closes the stream **without** `[DONE]`. It never replays a partially delivered stream and never falls back (FR-05).
- **Keep-alive:** an SSE comment line `: keep-alive` is sent whenever the stream has been quiet for 15 s (`Streaming:KeepAliveSeconds`), from the First Token on. A Provider that stays silent for longer than the policy's `attemptTimeoutMs` ends the stream with `stream_interrupted`.
- **Client disconnect:** cancels the upstream Attempt immediately. The request is recorded `cancelled` and its Attempt `ambiguous` (the Provider was generating and may bill for it) with estimated usage.

### `GET /v1/models`

Returns the Model Aliases the Tenant may use (from the configuration snapshot, filtered by the Tenant's model access), sorted by name, in OpenAI list format: `{"object":"list","data":[{"id":"balanced","object":"model","created":…,"owned_by":"modelswitch"}]}`. Provider Model IDs are never included.

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

Every error has one code from this closed set. The `type` groups codes and maps to OpenAI's `type` values where those exist. The `Title` is the Problem Details `title` for that code (`ErrorCode.Title`; a test keeps this table and the code in step).

| Code | HTTP | Type | Title | Retry by client? |
|---|---|---|---|---|
| `invalid_request` | 400 | `invalid_request_error` | Invalid request | No |
| `unsupported_parameter` | 400 | `invalid_request_error` | Unsupported parameter | No |
| `context_length_exceeded` | 400 | `invalid_request_error` | Context length exceeded | No |
| `invalid_api_key` | 401 | `authentication_error` | Invalid api key | No |
| `budget_exceeded` | 402 | `budget_error` | Budget exceeded | No (until the period resets) |
| `request_cost_limit_exceeded` | 402 | `budget_error` | Request cost limit exceeded | No |
| `insufficient_scope` | 403 | `permission_error` | Insufficient scope | No |
| `tenant_suspended` | 403 | `permission_error` | Tenant suspended | No |
| `model_not_allowed` | 403 | `permission_error` | Model not allowed | No |
| `model_not_found` | 404 | `invalid_request_error` | Model not found | No |
| `payload_too_large` | 413 | `invalid_request_error` | Payload too large | No |
| `unsupported_media_type` | 415 | `invalid_request_error` | Unsupported media type | No |
| `no_eligible_model` | 422 | `routing_error` | No eligible model | No (capability or cost constraints cannot be met) |
| `rate_limit_exceeded` | 429 | `rate_limit_error` | Rate limit exceeded | Yes, after `Retry-After` |
| `concurrency_limit_exceeded` | 429 | `rate_limit_error` | Concurrency limit exceeded | Yes, after `Retry-After` |
| `provider_error` | 502 | `provider_error` | Provider error | Maybe |
| `provider_unavailable` | 503 | `provider_error` | Provider unavailable | Yes (all eligible circuits open, or Fallbacks exhausted) |
| `limits_unavailable` | 503 | `api_error` | Limits unavailable | Yes (Valkey down, fail-closed) |
| `deadline_exceeded` | 504 | `timeout_error` | Deadline exceeded | Maybe |
| `stream_interrupted` | — (SSE event) | `provider_error` | Stream interrupted | Maybe, but the output may be partial |
| `run_cost_cap_reached` | — (Evaluation Run `error`) | `budget_error` | Run cost cap reached | No. The run's `maxEstimatedCost` would be exceeded; remaining Cases are `skipped` |
| `internal_error` | 500 | `api_error` | Internal error | Maybe |
| `validation_failed` | 400 | `invalid_request_error` | Validation failed | No. Admin API only (policy, dataset, limits… bodies) |
| `unauthenticated` | 401 | `authentication_error` | Unauthenticated | No. Admin API only (no session, or wrong email or password; the same answer for an unknown account, a deactivated one and a lockout) |
| `forbidden` | 403 | `permission_error` | Forbidden | No. Admin API only |
| `not_found` | 404 | `invalid_request_error` | Not found | No. Admin API only |
| `conflict` | 409 | `invalid_request_error` | Conflict | No. Admin API only (stale `expectedVersion`) |

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
| GET | `/admin/antiforgery-token` | Anonymous. The token the sign-in POST must carry (header `X-XSRF-TOKEN`) |

### Tenants and keys

| Method | Path | Role |
|---|---|---|
| GET/POST | `/admin/tenants` | V / Operator. GET lists only Tenants in the caller's grant, newest first. POST `{name}` → 201 and needs an all-Tenants grant (an explicit grant cannot contain a Tenant that does not exist yet → 403 `forbidden`); duplicate name → 409 |
| GET/PATCH | `/admin/tenants/{tenantId}` | V / Operator. PATCH `{name?, status?}`, status `active` or `suspended`. GET returns the Tenant plus `limits` and `modelAccess` |
| PUT | `/admin/tenants/{tenantId}/limits` | Operator. `{requestsPerMinute, maxConcurrent, maxRequestCost, budget:{amount, period:"month"}}`. PUT replaces everything: an absent or null limit means unlimited, an absent or null `budget` removes the Budget. Limits are integers 1-10 000 000; amounts are non-negative USD with at most 10 decimals. The Budget is stored through `IBudgets` (`usage.budget`) in the same transaction |
| PUT | `/admin/tenants/{tenantId}/model-access` | Operator. `{allowedAliases: [...]}`: Model Alias names (`[a-z0-9][a-z0-9._-]{0,63}`) or `["*"]` alone for all. Never empty (to block a Tenant, suspend it). Alias existence is not checked, so an alias can be granted before it is bound |
| GET/POST | `/admin/tenants/{tenantId}/api-keys` | V / Operator. POST `{name, scopes?, expiresAt?}` (scopes default `["inference"]`) → 201 with the view plus `key`, the raw key **once**, with `Cache-Control: no-store` |
| DELETE | `/admin/tenants/{tenantId}/api-keys/{keyId}` | Operator. Revokes the key (soft, sets `revoked_at`) → 204, idempotent. Takes effect at once on the instance that handled it and within 30 s on the others |

Every write above is audited and bumps the configuration version in the same transaction. Operators (inside their grant) write; Owners hold every grant. Key views show `keyPrefix` as `msk_` + the 8-character prefix, never the hash.

### Providers, models, prices (Owner to write, Viewer to read)

The catalog holds no Tenant data and no secret, so every Administrator may read it. Every write is audited and bumps the config version in the same transaction. Bodies are validated by hand: any problem is `400 validation_failed` with one `errors` entry per field; a duplicate name, Provider model id or Price instant is `409 conflict`.

| Method | Path | Notes |
|---|---|---|
| GET/POST | `/admin/providers` | `{name, adapterType: "openai_compatible"\|"ollama", endpoint, requestTimeoutMs?, enabled?, credential?: {secret}\|{envVar}}`. The `name` is `[a-z0-9][a-z0-9._-]{0,62}` (Policy Candidates are `name/modelId`). The `endpoint` is checked by the outbound destination policy on save (`errors.endpoint`). POST returns 201 |
| GET/PATCH | `/admin/providers/{id}` | PATCH `{endpoint?, requestTimeoutMs?, enabled?, credential?}`. The name and Adapter type are fixed. The secret is write-only: responses show `credential: {kind: none\|env\|db, configured, updatedAt}`. A `{secret}` replaces the stored one (audit `provider.secret_replaced`, no secret in it); an `{envVar}` (not `MODELSWITCH__*`) switches to `env:NAME` and deletes any stored secret |
| POST | `/admin/providers/{id}/connectivity-check` | Calls the Adapter's `CheckAsync` with the Provider's credential and returns `{reachable, latencyMs, failure: {class, code, httpStatus}\|null}`. Unreachable is a 200, not an error |
| GET/POST | `/admin/providers/{id}/models` | `{providerModelId, displayName?, capabilities:{…}, enabled?}`. Capabilities are checked key by key (unknown keys rejected; `tokenizer` is `o200k_base` or `cl100k_base`; `maxOutputTokens` ≤ `contextWindow`). POST returns 201 |
| PATCH | `/admin/models/{modelId}` | `{displayName?, capabilities?, enabled?}`. `providerModelId` is fixed |
| GET/POST | `/admin/models/{modelId}/prices` | GET lists newest first and flags the Price `inForce`. POST `{inputPerMillion, cachedInputPerMillion?, outputPerMillion, currency?: "USD", effectiveFrom?}` appends a new Price version (`effectiveFrom` defaults to now and must be ≥ now). Existing Prices are immutable. Only `USD`, values 0 to 9 999 999 999 with ≤ 10 decimals |

Not in the MVP: model discovery through the Adapter's `ListModelsAsync` (the Provider Model is entered by hand).

### Routing (Owner to write, Viewer to read)

| Method | Path | Notes |
|---|---|---|
| GET | `/admin/routing-policies` | Latest version of each policy, with the `aliases` pointing at it |
| POST | `/admin/routing-policies` | `{name, definition}`. Creates the policy at version 1 (201) |
| PUT | `/admin/routing-policies/{policyId}` | `{definition, expectedVersion}`. Validates (→ 400 `validation_failed`, errors keyed `definition.<path>`) and creates version n+1 (FR-11), which is immediately the version aliases resolve to; to roll back, save the old definition again. A stale `expectedVersion` → 409 |
| GET | `/admin/routing-policies/{policyId}/versions[/{version}]` | History, newest first. Versions are immutable, with `resolvedCandidates` fixed when saved |
| POST | `/admin/routing-policies/validate` | Dry-run validation, writes nothing. 200 `{valid, resolvedCandidates}` or 400 |
| GET/PUT/DELETE | `/admin/aliases/{alias}` | PUT `{policyId, description?}` binds (201 new, 200 rebind) |

`POST /admin/routing-policies/{policyId}/simulate` is not served yet; the dashboard's Routing screen leaves the simulate panel out until it is.

### Requests and usage

Every Administrator may read these (Viewer). Results are narrowed to the caller's Tenant Grant; a `tenantId` filter or a request outside it is **404** `not_found`. Time is `from`/`to` (ISO-8601 UTC, `from` < `to`, at most 400 days). Every bad parameter is reported at once as `400 validation_failed`, one `errors` entry per parameter. Estimated Cost figures are USD (`currency`) and are never a Provider's charge.

| Method | Path | Notes |
|---|---|---|
| GET | `/admin/requests` | Filters: `from`, `to` (no default: pages back through everything retained), `tenantId`, `origin` (`api`, `evaluation`), `alias`, `providerId`, `modelId` (a request matches when any of its Attempts used that Provider / Provider Model), `status` (`running`, `succeeded`, `failed` = failed or abandoned, `cancelled`, `abandoned`), `errorCode` (closed set), `hadFallback` (`true`/`false`). `sort=startedAt` (default) or `durationMs`, `order=desc` (default) or `asc`, `limit` (1-200, default 50), `cursor`. Items carry `selectedModel: {id, providerId, providerName, name}` and their `attempts` (the same shape as in the detail), so a list can draw each request's Route Strip |
| GET | `/admin/requests/{requestId}` | `{request, clientRequestId, apiKeyId, evaluationRunId, policyId, policyVersion, inputBytes, traceId, traceUrl, routingDecision, attempts[], usageRecords[]}`. `traceUrl` is `Telemetry:TraceUrlTemplate` with `{traceId}` replaced, or null. Attempts and Usage Records name their Provider Model |
| GET | `/admin/usage` | `groupBy=tenant\|provider\|model\|alias` (comma-separated or repeated), `interval=hour\|day\|month` (default `day`; `hour` covers at most 31 days), `from`/`to` (default the last 24 h), filters as above. Rows: `{period, tenantId, providerId, providerName, model, alias, requests, failedRequests, inputTokens, cachedInputTokens, outputTokens, estimatedCost, currency}`; columns not grouped by are null. `status`, `errorCode`, `hadFallback` and `providerId` filters are answered from the raw request rows only (within their 30-day retention), because the rollup does not keep them. `origin` is part of the rollup's key, so it is answered for the whole range |
| GET | `/admin/overview` | `from`/`to` (default the last 24 h), `tenantId`, `origin` (`api`, `evaluation`; default both). `{from, to, stepSeconds, currency, totals, series[{start, figures}], recentFailures[]}`. Figures: `requests, failedRequests, fallbackRequests, successRate, fallbackRate, inputTokens, outputTokens, estimatedCost, latencyP50Ms, latencyP95Ms, ttftP50Ms, ttftP95Ms, exact`. The step is 5 min up to 1 h, 15 min up to 6 h, 1 h up to 7 d, else 1 day. `recentFailures` are the 10 newest failed or abandoned requests in the range |
| GET | `/admin/provider-health` | Per Provider Model in the catalog: `{model, enabled, state, instances[{instanceId, state, windowFailureRate, windowCalls, capturedAt, stale}], recent{attempts, failedAttempts, errorRate, latencyP50Ms, latencyP95Ms}}`. `state` is the worst circuit state among instances that wrote in the last 60 s (`open` > `half_open` > `closed`), or `unknown`. `recent` covers the last 15 minutes of Attempts on every instance. Not Tenant-scoped: Provider Health is global |

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

| Method | Path | Notes |
|---|---|---|
| GET/POST/PATCH | `/admin/administrators[/{id}]` | POST `{email, password (≥ 12), role, allTenants?, tenantIds?}`; PATCH `{role?, allTenants?, tenantIds?, active?, password?}`. 409 when the last active Owner would be demoted or deactivated |
| GET | `/admin/audit-records` | Filters `tenantId`, `action`, `from`, `to`; `limit` (1-200), `cursor`. Newest first |

## 4. Health

| Path | Meaning | Checks |
|---|---|---|
| `GET /health` | The process is alive | None beyond the process responding. Always 200 unless shutting down |
| `GET /ready` | This instance should receive traffic | PostgreSQL reachable, configuration snapshot loaded, Valkey reachable (only when `Limits:FailureMode=Closed`) |

Provider status is **not** part of readiness. A Provider outage affects every instance equally, so taking instances out of rotation would not help; the gateway answers with `provider_unavailable` instead. `/ready` reports Provider Health in its body for information only (PRD §8). The body is JSON and never contains secrets.
