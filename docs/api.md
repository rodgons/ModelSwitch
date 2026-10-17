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
| 20 invalid keys from one client address within a minute (per instance; IPv6 grouped by /64) | 429 + `Retry-After` | `rate_limit_exceeded` |

The response never reveals *why* a key is invalid, so revoked and unknown keys look the same. While a client address is throttled, no key it presents is looked up, a valid one included, until the window ends.

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
| `template_variable_missing` | — (Case Result `error_code`) | `invalid_request_error` | Template variable missing | No. A Prompt Version variable is absent from the Case `input`; the Case is `errored` |
| `internal_error` | 500 | `api_error` | Internal error | Maybe |
| `validation_failed` | 400 | `invalid_request_error` | Validation failed | No. Admin API only (policy, dataset, limits… bodies) |
| `unauthenticated` | 401 | `authentication_error` | Unauthenticated | No. Admin API only (no session, or wrong email or password; the same answer for an unknown account, a deactivated one and a lockout) |
| `forbidden` | 403 | `permission_error` | Forbidden | No. Admin API only |
| `not_found` | 404 | `invalid_request_error` | Not found | No. Admin API only |
| `conflict` | 409 | `invalid_request_error` | Conflict | No. Admin API only (stale `expectedVersion`, a duplicate name, a run that has already finished, or deleting evaluation content that is still in use or still running) |

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
| GET/PATCH | `/admin/tenants/{tenantId}` | V / Operator. PATCH `{name?, status?}`, status `active` or `suspended`. GET returns the Tenant plus `limits`, `modelAccess` and `budgetPeriod`: the current calendar month (UTC) of the Budget, `{periodStart, limit, reserved, spent, currency}`. `reserved` is held by Reservations of requests in flight and `spent` is settled Estimated Cost; the next Reservation is refused when `spent + reserved` plus its hold would pass `limit`. A month no request has touched reads as zeros against the standing limit; `limit` is null without a Budget |
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
| POST | `/admin/routing-policies/{policyId}/simulate` | **Viewer** (it calls no Provider and writes nothing). The Routing Decision a hypothetical request would get, described below |
| GET/PUT/DELETE | `/admin/aliases/{alias}` | PUT `{policyId, description?}` binds (201 new, 200 rebind) |

**Simulate.** Body `{estimatedInputTokens, maxOutputTokens?, taskType?, requires?, tenantId?, version?}`: `estimatedInputTokens` (0-10 000 000) is required and is used for every Candidate's context-window check and worst-case cost; `maxOutputTokens` (1-10 000 000) defaults to the policy's `defaultMaxOutputTokens`; `taskType` follows `X-ModelSwitch-Task` (`[a-z0-9_-]{1,64}`); `requires` lists at most 32 Capabilities (`streaming`, `jsonObject`, `jsonSchema` or a parameter name); `tenantId` applies that Tenant's `maxRequestCost` (not its alias allow-list, because a policy is simulated, not an alias) and must be inside the caller's Tenant Grant, else 404; `version` defaults to the latest. Bad fields → `400 validation_failed`, one `errors` entry each; an unknown policy or version → 404. The decision comes from the same router a request runs through (`IRouter.Decide`), on this instance's configuration snapshot (catalog, Prices, Tenant limits) and its circuits. 200:

```json
{ "policyId": "…", "policyName": "balanced", "version": 3, "strategy": "cost", "ruleMatched": null,
  "effectiveMaxOutputTokens": 1024, "errorCode": null, "message": null, "retryAfterMs": null, "currency": "USD",
  "candidates": [
    { "providerModelId": "…", "providerId": "…", "providerName": "openai-main", "model": "gpt-4.1-mini",
      "status": "eligible", "rank": 1, "exclusionReason": null, "circuitState": "closed", "worstCaseEstimatedCost": 0.0021 },
    { "providerModelId": "…", "providerName": "ollama-local", "model": "llama3.1:8b",
      "status": "excluded", "rank": null, "exclusionReason": "capability_missing:jsonSchema", "circuitState": "closed", "worstCaseEstimatedCost": 0 } ] }
```

Candidates come in rank order (`status: eligible`, `rank` 1 is tried first), then `truncated` ones (eligible but ranked after `maxCandidates`, never tried), then `excluded` ones with their Eligibility reason, in listed order. `circuitState` is this instance's breaker (`closed`, `half_open`, `open`). `worstCaseEstimatedCost` is null only for a model with no Price. When no Candidate is eligible, `errorCode` is the error a real request would get (`no_eligible_model`, `request_cost_limit_exceeded`, or `provider_unavailable` with `retryAfterMs`), still with a 200.

### Requests and usage

Every Administrator may read these (Viewer). Results are narrowed to the caller's Tenant Grant; a `tenantId` filter or a request outside it is **404** `not_found`. Time is `from`/`to` (ISO-8601 UTC, `from` < `to`, at most 400 days). Every bad parameter is reported at once as `400 validation_failed`, one `errors` entry per parameter. Estimated Cost figures are USD (`currency`) and are never a Provider's charge.

| Method | Path | Notes |
|---|---|---|
| GET | `/admin/requests` | Filters: `from`, `to` (no default: pages back through everything retained), `tenantId`, `origin` (`api`, `evaluation`), `alias`, `providerId`, `modelId` (a request matches when any of its Attempts used that Provider / Provider Model), `status` (`running`, `succeeded`, `failed` = failed or abandoned, `cancelled`, `abandoned`), `errorCode` (closed set), `hadFallback` (`true`/`false`). `sort=startedAt` (default) or `durationMs`, `order=desc` (default) or `asc`, `limit` (1-200, default 50), `cursor`. Items carry `selectedModel: {id, providerId, providerName, name}` and their `attempts` (the same shape as in the detail), so a list can draw each request's Route Strip |
| GET | `/admin/requests/{requestId}` | `{request, clientRequestId, apiKeyId, evaluationRunId, policyId, policyVersion, inputBytes, traceId, traceUrl, routingDecision, attempts[], usageRecords[], evaluationCase}`. `evaluationCase` is `{runId, caseId, externalId, role: "candidate"|"judge"}` for a request an Evaluation Run made, else null. `traceUrl` is `Telemetry:TraceUrlTemplate` with `{traceId}` replaced, or null. Attempts and Usage Records name their Provider Model |
| GET | `/admin/usage` | `groupBy=tenant\|provider\|model\|alias` (comma-separated or repeated), `interval=hour\|day\|month` (default `day`; `hour` covers at most 31 days), `from`/`to` (default the last 24 h), filters as above. Rows: `{period, tenantId, providerId, providerName, model, alias, requests, failedRequests, inputTokens, cachedInputTokens, outputTokens, estimatedCost, currency}`; columns not grouped by are null. `status`, `errorCode`, `hadFallback` and `providerId` filters are answered from the raw request rows only (within their 30-day retention), because the rollup does not keep them. `origin` is part of the rollup's key, so it is answered for the whole range |
| GET | `/admin/overview` | `from`/`to` (default the last 24 h), `tenantId`, `origin` (`api`, `evaluation`; default both). `{from, to, stepSeconds, currency, totals, series[{start, figures}], recentFailures[]}`. Figures: `requests, failedRequests, fallbackRequests, successRate, fallbackRate, inputTokens, outputTokens, estimatedCost, latencyP50Ms, latencyP95Ms, ttftP50Ms, ttftP95Ms, exact`. The step is 5 min up to 1 h, 15 min up to 6 h, 1 h up to 7 d, else 1 day. `recentFailures` are the 10 newest failed or abandoned requests in the range |
| GET | `/admin/provider-health` | Per Provider Model in the catalog: `{model, enabled, state, instances[{instanceId, state, windowFailureRate, windowCalls, capturedAt, stale}], recent{attempts, failedAttempts, errorRate, latencyP50Ms, latencyP95Ms}}`. `state` is the worst circuit state among instances that wrote in the last 60 s (`open` > `half_open` > `closed`), or `unknown`. `recent` covers the last 15 minutes of Attempts on every instance. Not Tenant-scoped: Provider Health is global |

### Evaluation

Datasets and Prompts belong to one Tenant. Viewers read and Operators write inside their Tenant Grant; a Dataset or Prompt of a Tenant outside it is **404** `not_found`, like one that does not exist. Lists are newest first (`limit` 1-200, default 50, `cursor`) and take an optional `tenantId` filter (outside the grant → 404); without it they show every Tenant in the grant. Creating a Dataset or Prompt, each new version of either, and deleting one writes an Audit Record (`dataset.created`, `dataset.versioned`, `dataset.deleted`, `prompt.created`, `prompt.versioned`, `prompt.deleted`) with names, numbers and the content hash, never Case or template content. Names are trimmed, 1-128 characters, no control characters, unique per Tenant (duplicate → 409 `conflict`); an unknown `tenantId` → 404.

| Method | Path | Notes |
|---|---|---|
| GET/POST | `/admin/datasets` | POST `{tenantId, name, taskKind: "extraction"\|"classification"\|"free_text"}` → 201 `DatasetView {id, tenantId, name, taskKind, latestVersion, createdAt}` (`latestVersion` is 0 until the first upload) |
| GET | `/admin/datasets/{datasetId}` | `DatasetView` |
| DELETE | `/admin/datasets/{datasetId}` | Operator. Deletes the Dataset with every Dataset Version and Case → 204. 409 `conflict` while a Comparison uses it (delete those first). Audit `dataset.deleted` (name, task kind, version and Case counts). See evaluation.md section 8 |
| GET | `/admin/datasets/{datasetId}/versions` | Dataset Versions, newest first: `DatasetVersionView {datasetId, version, caseCount, contentSha256, scoringSchema, createdAt, createdByAdminId}` |
| POST | `/admin/datasets/{datasetId}/versions` | Upload JSONL as the **raw request body** with `Content-Type: application/x-ndjson` (also `application/jsonl`, `application/x-jsonlines`; any other → 415 `unsupported_media_type`). The size and Case limits answer differently: a body over 10 MiB is 413 `payload_too_large` (not a validation error), while no Cases or more than 5 000 is 400 `validation_failed` (key `body`). → 201 `DatasetVersionView`, the next version number (concurrent uploads each get their own). Invalid upload → 400 `validation_failed`, see below |
| GET | `/admin/datasets/{datasetId}/versions/{version}` | `DatasetVersionView` |
| GET | `/admin/datasets/{datasetId}/versions/{version}/cases` | Cases in upload order, paginated: `CaseView {id, externalId, ordinal, input, expected, metadata}` (`externalId` is the JSONL `id`, `ordinal` counts from 0) |
| GET/POST | `/admin/prompts` | POST `{tenantId, name}` → 201 `PromptView {id, tenantId, name, latestVersion, createdAt}` |
| GET | `/admin/prompts/{promptId}` | `PromptView` |
| DELETE | `/admin/prompts/{promptId}` | Operator. Deletes the Prompt with every Prompt Version → 204. 409 `conflict` while a Comparison has a run built from it. Audit `prompt.deleted` (name, version count) |
| GET/POST | `/admin/prompts/{promptId}/versions` | GET newest first. POST `{messages: [{role, content}], responseFormat?}` appends the next immutable Prompt Version → 201 `PromptVersionView {promptId, version, template: {messages, responseFormat}, createdAt, createdByAdminId}` |
| GET | `/admin/prompts/{promptId}/versions/{version}` | `PromptVersionView` |

**Dataset upload validation** (evaluation.md section 1). UTF-8 (a BOM is tolerated), one JSON object per line, `\n` or `\r\n`; blank lines are ignored. Each Case has exactly `id` (non-empty string ≤ 256 chars, unique in the upload), `input` (object), `expected` and optionally `metadata` (object); any other field, a repeated field name or nesting deeper than 32 is an error. `expected` is a non-empty object for `extraction`, a non-empty label string without surrounding whitespace (≤ 256 chars) for `classification`, a non-empty string for `free_text`. One bad line rejects the whole upload. `errors` keys are `lines[N]` (the line as a whole) or `lines[N].<field>`, where **N is the 1-based line number in the file**, plus `body` for the upload as a whole (no Cases, more than 5 000, not UTF-8, or "only the first 100 invalid lines are listed"). Messages never quote Case content.

**`contentSha256`** is the lower-case hex SHA-256 of the UTF-8 canonical Cases: each Case as canonical JSON (members sorted by name in UTF-16 code-unit order as in RFC 8785, no whitespace, strings re-encoded from their decoded value with only `"`, `\` and control characters escaped, numbers kept as written, `metadata` omitted when absent), in upload order, joined by `\n` with no trailing newline. Equal content gives an equal hash whatever its key order, spacing, escapes, line endings or blank lines. **`scoringSchema`** is `{"taskKind":"classification","labels":[…]}` (distinct expected labels), `{"taskKind":"extraction","fields":[…]}` (every expected field name) or `{"taskKind":"free_text"}`, lists sorted ordinally.

**Prompt template validation** (evaluation.md section 2). 1 to 256 `messages`, each with `role` `system`, `user` or `assistant` and non-blank `content` (`{{var}}` placeholders are checked when a run renders a Case, not here). `responseFormat`, when given, is `{type:"text"}`, `{type:"json_object"}` or `{type:"json_schema", schema:{…}, name?, strict?}` (`name` `[A-Za-z0-9_-]{1,64}`); other fields are errors. Errors are keyed `messages`, `messages[i].role`, `messages[i].content` (0-based `i`), `responseFormat` or `responseFormat.<field>`. The stored `responseFormat` is in canonical JSON form.

| Method | Path | Notes |
|---|---|---|
#### Evaluation Runs and Comparisons

Viewers read and Operators write inside their Tenant Grant; a run or Comparison of a Tenant outside it is **404** `not_found`. Creating a Comparison (directly or with "Run again") writes an Audit Record `evaluation.created` (target the Comparison: name, Dataset and version, run ids, `configSha256`s, `maxEstimatedCost`, `rerunOf`); cancelling writes `evaluation.cancelled` (target the run); deleting writes `evaluation.deleted` (target the Comparison: name, Dataset and version, run ids, Case Result count). Money is USD Estimated Cost, never a Provider's charge. Times are ISO-8601 UTC.

| Method | Path | Role | Notes |
|---|---|---|---|
| POST | `/admin/evaluations` | Operator | `CreateEvaluationRequest` → **202** `{comparisonId, runIds[]}` (runs in configuration order). Creates one Comparison, one run per configuration (`queued`, `casesTotal` = the version's Case count) and one `evaluation.run` job per run, in one transaction |
| POST | `/admin/evaluations/estimate` | Operator | Same body and validation, writes nothing → 200 `EvaluationEstimateView` |
| GET | `/admin/evaluations` | Viewer | Comparisons newest first, each with its runs: `{items: ComparisonView[], nextCursor}`. Filters `tenantId`, `datasetId`, `status` (a Comparison with at least one run in that status), `limit`, `cursor` |
| GET | `/admin/evaluations/{runId}` | Viewer | `RunDetailView` |
| GET | `/admin/evaluations/{runId}/results` | Viewer | Case Results in upload order: `{items: CaseResultView[], nextCursor}`. Filters `status` (`passed`, `failed`, `errored`, `skipped`), `externalId` (the Case's dataset `id`, exact match: the Case drill-down reads one Case per run with it), `limit`, `cursor` |
| POST | `/admin/evaluations/{runId}/cancel` | Operator | → 200 `{id, status: "cancelled"}`; 409 `conflict` when the run has already finished |
| POST | `/admin/evaluations/{runId}/rerun` | Operator | "Run again": a new Comparison named `<name> (run again)` with one run whose Run Configuration is a copy of the frozen one (same Policy Versions, so the same `configSha256`) and the same `maxEstimatedCost` → **202** `{comparisonId, runIds}` |
| POST | `/admin/evaluations/{runId}/aggregates/recompute` | Operator | Recomputes a finished run's aggregates from its stored Case Results, stores and returns them (the aggregates object); 409 `conflict` while queued or running |
| GET | `/admin/evaluations/{runId}/export?format=csv\|jsonl` | Viewer | Streams the export (evaluation.md section 7); `format` is required, anything else → 400 `validation_failed` (`errors.format`). `Content-Disposition: attachment; filename=evaluation-run-<runId>.<format>` |
| GET | `/admin/comparisons/{comparisonId}` | Viewer | `ComparisonDetailView` |
| DELETE | `/admin/comparisons/{comparisonId}` | Operator | Deletes the Comparison, its Evaluation Runs and their Case Results (outputs and the copied Prompt template included) → 204. 409 `conflict` while a run is queued or running, or cancelled but still finishing its in-flight Cases (`completedAt` null): cancel it and wait. Inference Requests the runs made keep their metadata until request retention removes them (evaluation.md section 8) |

**`CreateEvaluationRequest`**: `{tenantId, datasetId, datasetVersion, name, configurations: [1..6 × {target: {alias} | {providerModelId}, prompt: {id, version}, params?: {temperature? 0-2, topP? 0-1, seed? int64, maxOutputTokens? ≥ 1}, evaluator: {kind, version: "1", config?}}], concurrency? 1-16 (default 4), maxEstimatedCost? (USD, ≥ 0, ≤ 10 decimals)}`. Order of checks: a `tenantId` outside the grant or unknown → 404; then every problem at once → 400 `validation_failed`. Shape keys: `tenantId`, `datasetId`, `datasetVersion`, `name` (the Dataset name rule), `concurrency`, `maxEstimatedCost`, `configurations`, `configurations[i]`, `configurations[i].target` (exactly one of the two), `.target.alias` (`[a-z0-9][a-z0-9._-]{0,63}`), `.target.providerModelId`, `.prompt`, `.prompt.id`, `.prompt.version`, `.params.<name>`, `.evaluator`, and the Evaluator's own keys under `configurations[i].evaluator.` (`kind`, `version`, `config.labels[1]`, …). Reference keys: `datasetId` (not the Tenant's), `datasetVersion` (no such version), `.prompt.id` (not the Tenant's), `.prompt.version`, `.target.alias` (no such Model Alias), `.target.providerModelId` (not an enabled Provider Model), `.evaluator.config.judgeAlias` (no such alias), `.evaluator.kind` (does not fit the Dataset's task kind), `.evaluator.config.labels` (classification labels miss an expected label of the version), `.evaluator.config.fields.<name>` (`json_fields` field the version's expected results do not have).

**`EvaluationEstimateView`**: `{currency: "USD", total, configurations: [{index, target (label), cases, estimatedCases, candidateWorstCase, judgeWorstCase, worstCase, maxPerCaseWorstCase|null, exceedsMaxEstimatedCost: bool|null, failures: {<errorCode>: count}}]}`. A Case's worst case is the Budget Reservation its candidate call would hold (`IInferenceService.EstimateWorstCaseAsync`), plus its judge call's for `llm_judge`; a Case whose prompt does not render (`template_variable_missing`) or that routing refuses (`model_not_allowed`, `request_cost_limit_exceeded`, …) costs 0 and is counted in `failures`.

**`TargetView`**: `{label, alias, policyId, policyVersion, providerModelId, providerModel}` (`label` is `alias@vN` or `provider/model`; the other side is null).

**`RunSummaryView`** (inside `ComparisonView.runs`): `{id, comparisonId, status, target: TargetView, prompt: {id, version}, evaluator: {kind, version}, configSha256, casesTotal, casesDone, casesFailed, quality: {metric, value}|null, maxEstimatedCost, error, createdAt, startedAt, completedAt}`. `casesDone` counts stored Case Results of any status; `casesFailed` counts failed plus errored. **`ComparisonView`**: `{id, tenantId, datasetId, datasetVersion, name, createdAt, createdByAdminId, runs: RunSummaryView[]}`.

**`RunDetailView`**: `{id, comparisonId, comparisonName, tenantId, status, progress: {total, done, failed}, configuration (the frozen Run Configuration, below), configSha256, maxEstimatedCost, reproducibility: {datasetId, datasetVersion, contentSha256, prompt: {id, version}, target: TargetView, params, evaluator: {kind, version, config, judge?: {alias, policyId, policyVersion}}, configSha256, gatewayVersion, startedAt, completedAt}, aggregates|null, aggregatesLive, failures: {byStatus: {<status>: n}, byErrorCode: {<code>: n}}, error, createdAt, startedAt, completedAt}`. `aggregates` are the stored ones of a finished run; for a queued or running run they are computed for the answer from the Case Results stored so far (`aggregatesLive: true`). Their shape is `RunAggregates` (evaluation.md section 3): `{evaluatorKind, casesTotal, scored, passed, failed, errored, skipped, erroredRate, quality: {metric, value}, classification?|jsonFields?|llmJudge?, latency: {p50Ms, p95Ms}, tokens: {candidateInput, candidateOutput, judgeInput, judgeOutput}, cost: {candidate, judge, total, perCaseCandidate}, currency}`.

**Run Configuration** (`run_configuration`): `{dataset: {id, version, contentSha256, taskKind}, target: {alias, policyId, policyVersion} | {providerModelId, providerModel}, prompt: {id, version, template}, params: {temperature?, topP?, seed?, maxOutputTokens?}, evaluator: {kind, version, config, judge?}, concurrency, gatewayVersion}`. `configSha256` is the SHA-256 of its canonical JSON (the `contentSha256` canonicalization) without `concurrency` and `gatewayVersion`.

**`CaseResultView`**: `{id, caseId, externalId, ordinal, status, score, predicted, outputText, scoreDetails, inputTokens, outputTokens, estimatedCost, judgeInputTokens, judgeOutputTokens, judgeEstimatedCost, latencyMs, errorCode, inferenceRequestId, judgeInferenceRequestId, createdAt}`. Token and cost fields without `judge` are the candidate call's (a failed call keeps its request id and its recorded Estimated Cost); `outputText` is the candidate output, stored by design for evaluation (ADR-0010).

**`ComparisonDetailView`**: `{id, tenantId, datasetId, datasetVersion, name, createdAt, currency, runs: [{runId, status, target: TargetView, prompt, evaluator, params, configSha256, casesTotal, casesDone, quality|null, erroredRate, latencyP50Ms, latencyP95Ms, candidateCostPerCase, candidateCost, judgeCost, totalCost, modelJudged, aggregatesLive, error, variance: {runs, min, max, mean, stdDev}|null}]}`. Configurations line up on `candidateCostPerCase`; `judgeCost` is its own column. `variance` is over the Tenant's succeeded runs with the same `configSha256` (sample standard deviation of the quality value), shown from two runs.

`GET /admin/requests/{requestId}` also carries `evaluationCase: {runId, caseId, externalId, role: "candidate"|"judge"}` (or null) for a request an Evaluation Run made, through `Evaluation.Contracts.IEvaluationCaseLinks`.

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
