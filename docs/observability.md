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

Sampling: parent-based, with a default ratio of 1.0 in development. Production guidance is 0.1 with all errors kept by tail sampling in the collector. That guidance applies to the operator's collector, not to the gateway itself.

## 3. Metrics

Meter `ModelSwitch`. Allowed labels: `alias`, `provider`, `model` (Provider Model id, bounded because it is admin-configured), `status`, `error_code`, `attempt_kind`, `streaming`. **`tenant` is never a label**, because tenant count is unbounded. Per-tenant analytics come from the DB.

| Instrument | Type | Unit |
|---|---|---|
| `modelswitch.inference.requests` | counter | {request} |
| `modelswitch.inference.duration` | histogram | s |
| `modelswitch.inference.ttft` | histogram | s |
| `modelswitch.inference.gateway_overhead` | histogram | s: total minus time inside Adapter calls and backoff waits (NFR-01) |
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

Built-in ASP.NET Core, HttpClient, Npgsql and runtime metrics are also enabled.

## 4. Logs

- JSON console + OTLP. Every line inside a request has `RequestId`, `TraceId`, `TenantId` (an id, not a name).
- Use `[LoggerMessage]` source-generated methods only, with event ids grouped per module (Identity 1xxx, Registry 2xxx, Routing 3xxx, Inference 4xxx, Usage 5xxx, Evaluation 6xxx, Worker 7xxx).
- Levels: one `Information` line per completed request (status, alias, model, attempts, duration), `Warning` per failed Attempt, and `Error` only for gateway faults, never for Provider errors that were handled.

## 5. Degradation rules

- The OTLP exporter uses a bounded batch queue (2 048 items) and drops when full. Export failures are never surfaced to requests.
- If the telemetry backend is down at startup, startup still succeeds.
- A failure test kills the `otel-lgtm` container during load and asserts no change in inference success rate (PRD Phase 4, "test telemetry degradation").

## 6. Local stack

`docker compose --profile observability up` adds `grafana/otel-lgtm` (Grafana, Tempo, Loki, Prometheus/Mimir in one container) on `http://localhost:3000`. `deploy/grafana/` holds a provisioned "ModelSwitch Gateway" dashboard built on the metrics above.
