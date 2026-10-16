# Testing Strategy

This document implements PRD §10 and DoD #2, #4, #5, #10, #13 and #14.

## 1. Projects and what goes where

| Project | Scope | Infra | Runs in CI |
|---|---|---|---|
| `ModelSwitch.UnitTests` | Pure domain logic: routing eligibility and ranking, cost calculator, failure classification, circuit transitions (with `FakeTimeProvider`), budget decisions, usage normalization, evaluation metrics, API-key parsing and verification, config validation | none | every push |
| `ModelSwitch.ArchitectureTests` | Module boundary rules (NetArchTest), "endpoints never reference `Infrastructure`/EF Core", "no `AddStandardResilienceHandler` on Adapter clients", "no `ILogger` call takes a `[ContentData]` value" | none | every push |
| `ModelSwitch.AdapterContractTests` | Shared suite × each Adapter ([provider-adapters.md §4](provider-adapters.md#4-shared-contract-test-suite-modelswitchadaptercontracttests)) | WireMock.Net | every push |
| `ModelSwitch.IntegrationTests` | `WebApplicationFactory` + real PostgreSQL + Valkey via Testcontainers + Mock Provider in-process | Docker | every push |
| `ModelSwitch.FailureInjectionTests` | Fault scenarios through the full stack, including container kills | Docker | every push (tagged `slow`, may run in parallel job) |
| `web` (Vitest) | Components, composables, formatters (units, currency, "estimated" labels) | none | every push |
| `web` (Playwright) | Smoke E2E: sign in, create tenant + key, run a request, see it in explorer | compose | main + nightly |
| `load/` (k6) | Gateway overhead, throughput | compose (Release images) | manual + nightly, results committed for releases |

Rules from the [constitution](constitution.md), which apply to every task:

- **Every business rule has a unit test.** A business rule is any decision in `Domain/` or `Application/`: eligibility, ranking, cost, budget, limit, classification, validation, evaluation metric. The test is named after the rule.
- **Every endpoint has at least one happy-path integration test** in `ModelSwitch.IntegrationTests`, covering `/v1/*`, `/admin/*`, `/health` and `/ready`.
- **Validation errors return 400 Problem Details.** Each validator has an integration test asserting the status, `Content-Type: application/problem+json` and the `errors` extension.
- **Every bug fix comes with a regression test** that fails before the fix and passes after it, at the lowest layer that reproduces the bug.

Test runner: xUnit v3 on Microsoft.Testing.Platform (`global.json` `test.runner`), run with `dotnet test ModelSwitch.slnx`. `tests/Directory.Build.props` passes `--ignore-exit-code 8`, so a test project that has no tests yet does not fail the run; real failures still do.

Other rules: no test calls a real external Provider. Testcontainers are shared per collection (one Postgres per test assembly, with a fresh database per test class via template DB clone) to keep the suite fast. Time is controlled with `FakeTimeProvider`, never `Thread.Sleep`.

## 2. Integration test catalogue (minimum)

- Migrations: apply from empty → apply again (no-op) → the schema matches the EF model snapshot (FR-10).
- Auth: invalid, revoked and expired keys → 401. Missing scope → 403. Suspended tenant → 403. Revocation is effective within the documented 30 s cap (using a fake clock).
- Tenant isolation matrix: every tenant-scoped admin endpoint × foreign id → 404, and `/v1` never shows another tenant's aliases or usage.
- Endpoint happy paths: one test per route in [api.md](api.md) (see §6 for the gate).
- Inference: non-streaming and streaming happy paths. `[DONE]` is emitted. Unsupported params → 400 Problem Details whose `error.code` an OpenAI SDK can read. Size limit → 413.
- Admin validation: an invalid body for each write endpoint → 400 `validation_failed` Problem Details.
- Request records: one `inference_request`, N `provider_attempt`, N `usage_record`, all correlated by `X-Request-Id`.
- Pricing preservation: record a request → add a new Price → the old Usage Record's cost and `model_price_id` are unchanged.
- Budgets: concurrent requests at the boundary never push `spent + reserved` past the limit by more than one request's estimation error.
- **Multi-instance limits:** two `WebApplicationFactory` hosts share one Valkey. They fire `2 × limit` requests concurrently and assert that exactly `limit` succeed (FR-07), once for the Concurrency Limit and once for the Rate Limit burst (`LimitsTests`).
- Config propagation: update a Routing Policy through the admin API → the next request on the *other* host uses the new version within 1 s (via NOTIFY).
- Background recovery: kill the worker mid-evaluation (dispose the host) → a new worker resumes → no duplicate Case Results. An abandoned Reservation is swept.
- Streaming cancellation: the client aborts mid-stream → the upstream connection is closed (assert on the mock), the Attempt is recorded `cancelled` or `ambiguous`, and the Concurrency lease is released.

## 3. Failure-injection scenarios (PRD §10.3)

Each one asserts **bounded attempts**, **a recorded outcome** and **consistent accounting** (`budget_period` totals equal the sum of Usage Records).

| Scenario | Expected |
|---|---|
| Provider 503 ×2 then OK | 3 Attempts or a Fallback, success, all recorded |
| Provider 429 with `Retry-After: 1` | Waits, then succeeds within the Deadline |
| Provider 429 with `Retry-After: 120` | No wait. Falls back |
| Provider hangs (no headers) | Ambiguous, no same-model retry, Fallback, worst-case charge |
| Provider 401 | No retry, Fallback, circuit counts it |
| Invalid JSON from the Provider | Transient, retried |
| Stream cut after 5 chunks | `stream_interrupted` event, no `[DONE]`, no replay |
| All candidates failing ×20 | Circuit opens. Further requests get 503 fast, without calling the Provider |
| PostgreSQL stopped mid-load | Requests fail cleanly (503/500 with request id), no hang. After restart, the sweeper reconciles |
| Valkey stopped (fail-closed) | 503 `limits_unavailable`, `/ready` = 503 |
| Valkey stopped (fail-open) | Requests succeed, degraded metric increments |
| Worker killed during evaluation | Resumed, no duplicates |
| OTLP backend killed | No change in success rate. Also: a backend that hangs neither blocks startup nor slows requests (in-process fake backend, observability.md section 5) |

**Implementation status.** `ModelSwitch.FailureInjectionTests` holds the first eight scenarios (503 x2, both 429 cases, hang, 401, invalid JSON, stream cut, circuit opens), the two Valkey scenarios and the OTLP backend scenarios. It references `ModelSwitch.IntegrationTests` to reuse `GatewayFixture` and `PostgresFixture` (re-declaring the assembly fixture, which xunit only honours on the entry assembly). Each scenario class has its own fixture, so circuits and fault scripts do not leak, and installs a Routing Policy with small timeouts. Classes carry `[Slow]` (trait `Category=slow`). They run in the normal `dotnet test` of the `dotnet` CI job; filter with `--filter-trait "Category=slow"` to run or split them out. Assertions on `usage_record` and `budget_period` are marked `TODO(Phase 3)` because those tables do not exist yet. Provider calls are counted by the Mock Provider, and `AmplificationReport` collects each scenario's ratio (test output now, benchmark report in Phase 6).

**Retry amplification** is measured as `provider_calls / client_requests` per scenario and reported in the benchmark report (PRD §13).

## 4. Security tests (PRD §10.4)

These are covered in the catalogue above, plus:
- Log/trace canary test ([security.md §6](security.md#6-logging-and-telemetry-hygiene)).
- SSRF: endpoints `http://169.254.169.254`, `http://127.0.0.1`, a hostname resolving to 10.x, and a DNS-rebinding hostname (stub resolver) → rejected on save and at connect.
- Admin: Viewer → POST is 403. Missing antiforgery token → 400. Operator outside their grant → 404.
- Rate-limit abuse: parallel bursts across two hosts → not exceeded. Concurrency leases from a crashed host expire.

## 5. Load tests and benchmark report

- `load/scenarios/overhead.js`: the Mock Provider with a fixed 200 ms latency. Constant arrival rate in steps of 50, 100, 200 and 400 RPS for 5 min each. Non-streaming and streaming variants.
- Primary result: `modelswitch.inference.gateway_overhead` p50/p95/p99 (target p95 < 100 ms, NFR-01), together with k6 end-to-end latency minus the mock latency as a cross-check.
- Also captured: throughput, error rate, process RSS, GC pauses, Npgsql pool wait time, Valkey latency, outbox lag.
- `docs/benchmarks/<date>-<git-sha>.md` uses the template in `load/REPORT_TEMPLATE.md`: hardware, OS, Docker resources, image digests, dataset size, concurrency, duration, raw numbers, **limitations**. Results are only published when they were actually measured (DoD #10).

## 6. Coverage expectations

Coverage percentage is not a gate. Instead, CI fails if any of these are missing tests: each row of the failure-classification table, each eligibility reason code, each error code in [api.md §2](api.md#2-error-codes) (there is a test that triggers it), and each Evaluator kind.

**Endpoint gate:** a test in `ModelSwitch.IntegrationTests` lists every route from the app's `EndpointDataSource` and fails if one has no test tagged `[HappyPath("<METHOD> <route>")]`.

**Business rules** cannot be found automatically, so the reviewer checks them: a PR that adds or changes a rule in `Domain/` or `Application/` without a unit test is not mergeable.
