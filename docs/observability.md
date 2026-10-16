# Observability

This document covers FR-08 (telemetry side), NFR-02 ("observability failures must not cascade") and DoD #11.

Two separate systems are involved:

| | Usage Records + request tables | OpenTelemetry |
|---|---|---|
| Purpose | Authoritative history, dashboard, accounting | Operations, debugging, alerting |
| Durability | Transactional | Best-effort, may drop |
| Cardinality | Anything (it's a DB) | Bounded labels only |
| Retention | [data-model.md](data-model.md#retention) | Backend's choice |

The dashboard reads **only** PostgreSQL, so it works when no telemetry backend is configured.

## 1. Correlation

- `X-Request-Id` = `inference_request.id` = span attribute `modelswitch.request_id`.
- `trace_id` is stored on `inference_request` and on every log line through the logging scope. The request detail page links to the trace when `MODELSWITCH__Telemetry__TraceUrlTemplate` is set (e.g. a Grafana Tempo URL).
- An incoming W3C `traceparent` is continued, so client traces join gateway traces.

## 2. Traces

```
POST /v1/chat/completions                    (ASP.NET Core server span)
 ├─ modelswitch.auth                         (cache hit/miss attr)
 ├─ modelswitch.limits                       (Valkey; result attr)
 ├─ modelswitch.route                        (policy id/version, candidates count, chosen)
 ├─ modelswitch.reserve                      (DB)
 ├─ modelswitch.attempt  [n]                 (provider, model, kind, outcome, error_class)
 │    └─ HTTP POST <provider>                (HttpClient span; URL path only, no query)
 └─ modelswitch.settle                       (DB)
```

Every span comes from the one `ActivitySource` named `ModelSwitch` (`SharedKernel.Telemetry.GatewayTelemetry`). `modelswitch.auth` sets `modelswitch.auth.cache_hit` and `modelswitch.result` (never the key or its prefix); `modelswitch.attempt` sets `modelswitch.provider_id`, `gen_ai.response.model` (the Provider Model id), `modelswitch.attempt.kind`, `modelswitch.attempt.number`, `modelswitch.outcome`, `modelswitch.error_class` and `modelswitch.error_code`. A streaming Attempt's span ends at the First Token; what follows is in the request's duration and the Attempt record. The server span carries `modelswitch.request_id` and, once authenticated, `modelswitch.tenant_id`.

Sampling: parent-based, with a default ratio of 1.0 in development (`MODELSWITCH__Telemetry__TraceSampleRatio`, 0 to 1). Production guidance is 0.1 with all errors kept by tail sampling in the collector. That guidance applies to the operator's collector, not to the gateway itself.

## 3. Metrics

Meter `ModelSwitch` (the circuit and Adapter instruments use `ModelSwitch.Circuit` and `ModelSwitch.Adapters.*`, which the hosts also enable). Allowed labels: `alias`, `provider`, `model` (Provider Model id, bounded because it is admin-configured), `status`, `error_code`, `attempt_kind`, `streaming`. **`tenant` is never a label**, because tenant count is unbounded. Per-tenant analytics come from the DB.

| Instrument | Type | Unit |
|---|---|---|
| `modelswitch.inference.requests` | counter | {request} |
| `modelswitch.inference.duration` | histogram | s |
| `modelswitch.inference.ttft` | histogram | s |
| `modelswitch.inference.gateway_overhead` | histogram | s: total minus time inside Adapter calls and backoff waits (NFR-01), measured as the request's duration minus the window from the first Attempt's start to the last Attempt's end |
| `modelswitch.attempts` | counter | {attempt} |
| `modelswitch.fallbacks` | counter | {fallback} |
| `modelswitch.tokens` | counter | {token}, label `direction=input\|cached_input\|output`, `source` (`cached_input` is a subset of `input`) |
| `modelswitch.provider.invalid_usage` | counter | label `field`; reported usage the Adapter discarded as inconsistent |
| `modelswitch.estimated_cost` | counter | USD |
| `modelswitch.circuit.state` | observable gauge | 0 closed, 1 half-open, 2 open |
| `modelswitch.circuit.transitions` | counter | |
| `modelswitch.limits.rejections` | counter | label `kind=rate\|concurrency\|budget\|request_cost` |
| `modelswitch.limits.degraded` | counter | |
| `modelswitch.settlement.failures` | counter | |
| `modelswitch.outbox.lag` | observable gauge | s |
| `modelswitch.jobs.queue_depth` | observable gauge | by `kind` |

Built-in ASP.NET Core, HttpClient, Npgsql and runtime metrics are also enabled. `provider` and `model` carry ids, not names. `modelswitch.tokens` and `modelswitch.estimated_cost` are recorded by the Usage ledger after a settlement commits, so a rolled-back settlement counts nothing. `modelswitch.outbox.lag` and `modelswitch.jobs.queue_depth` are sampled by the worker every 15 s (`JobsMetricsSampler`), so an exporter callback never queries the database.

## 4. Logs

- JSON console + OTLP. Every line inside a request has `RequestId`, `TraceId`, `TenantId` (an id, not a name).
- `RequestIdMiddleware` opens a scope with `RequestId`; the inference endpoints add `TenantId` to it once the API Key is authenticated (`RequestId.SetTenant`). `TraceId` and `SpanId` come from activity tracking.
- `[SecretData]` and `[ContentData]` (`SharedKernel.Telemetry`) classify `[LoggerMessage]` parameters; the hosts register an erasing redactor for both, and for anything else classified.
- Use `[LoggerMessage]` source-generated methods only, with event ids grouped per module (Identity 1xxx, Registry 2xxx, Routing 3xxx, Inference 4xxx, Usage 5xxx, Evaluation 6xxx, Worker 7xxx).
- Levels: one `Information` line per completed request (status, alias, model, attempts, duration), `Warning` per failed Attempt, and `Error` only for gateway faults, never for Provider errors that were handled.

## 5. Degradation rules

- The SDK is wired by `ModelSwitch.Telemetry` (`AddModelSwitchTelemetry`), referenced only by the api and worker hosts. Without `OTEL_EXPORTER_OTLP_ENDPOINT` nothing is exported. With it, `UseOtlpExporter` sends all three signals.
- The OTLP exporter uses a bounded batch queue (2 048 items, the SDK default) and drops when full. Export failures are never surfaced to requests.
- If the telemetry backend is down at startup, startup still succeeds.
- `TelemetryCanaryTests` (integration) runs a streaming and a non-streaming request with a canary prompt through a Provider whose credential is a canary secret, exports every span, metric and log line in memory, and asserts neither canary nor the API Key appears (DoD #11).
- Failure tests (PRD Phase 4, "test telemetry degradation", `TelemetryDegradationScenarios`) point the api at an in-process OTLP/HTTP backend: one stops it during load (as a killed `otel-lgtm` container would) and asserts no change in inference success rate; the other accepts exports and never answers, and asserts that startup succeeds and request latency stays far below the exporter's 10 s timeout. A fake backend is used instead of the `otel-lgtm` image to keep CI from pulling a ~1 GB image, and because a hanging backend is a harsher case than a dead one.

## 6. Local stack

`docker compose --profile observability up` adds `grafana/otel-lgtm` (Grafana, Tempo, Loki, Prometheus in one container, pinned by tag) on `http://localhost:3000`, with OTLP published on loopback (`4317` gRPC, `4318` HTTP) for an api or worker run outside Compose. `deploy/grafana/` holds the provisioned "ModelSwitch Gateway" dashboard (folder *ModelSwitch*): request rate and status, errors by code, latency, TTFT and gateway-overhead percentiles, Attempts by kind and status, Fallbacks, circuit state, tokens, Estimated Cost by alias, limit rejections, and the worker's outbox lag, job queue and settlement failures.

In Prometheus the instruments appear with OTLP's naming translation: dots become underscores, counters gain `_total` and units become suffixes, e.g. `modelswitch_inference_requests_total`, `modelswitch_inference_gateway_overhead_seconds_bucket`, `modelswitch_estimated_cost_USD_total`, `modelswitch_outbox_lag_seconds`. The latency histograms advise bucket bounds in seconds (1 ms to 120 s, `InferenceMetrics.SecondsBuckets`), because the SDK's default bounds are sized for milliseconds.
