# Implementation Roadmap

The PRD §12 phases are broken into issue-sized tasks. Each task should be one PR of roughly a day or two. Exit criteria point back to FR, NFR and DoD items.

Every task follows the [constitution](constitution.md): unit tests for each business rule it adds, a happy-path integration test for each endpoint it adds, 400 Problem Details for validation errors, a regression test for each bug fixed, and commits in the form `type(scope): description [FR-xx]`. A task that adds a dependency first records its justification in [architecture.md](architecture.md#dependency-justifications).

> **Deviation from PRD §12:** a *minimal* Tenant + API Key (no admin UI, created by the migrator seed) moves into Phase 1, because every request record carries a `tenant_id` and FR-01 isolation has to shape the schema from day one. The full identity work stays in Phase 3.

## Phase 0: Repository skeleton (½ week)

- [x] `git init`, `.gitignore`, `.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`, `global.json` (SDK 10.0.x)
- [x] Empty solution with all projects from [architecture.md §3](architecture.md#3-solution-layout) and the reference graph
- [x] Architecture tests for module boundaries and thin endpoints (they pass vacuously at first)
- [x] CI workflow: format, build, test, gitleaks
- [x] `web/` Vite + Vue + TS scaffold with lint/typecheck/test in CI
- [x] `deploy/docker-compose.yml` with postgres + valkey, `.env.example`

**Exit:** `dotnet build` and `pnpm build` are green in CI; `docker compose up postgres valkey` works.

## Phase 1: Foundation (≈2 weeks)

- [x] SharedKernel: ids, `Result`, error codes, Money, `TimeProvider` wiring
- [x] Migrator host + `identity` (tenant, api_key), `registry` (provider, provider_model, model_price) migrations; seed command
- [x] API key authentication handler (HMAC, prefix lookup, cache) + unit tests
- [x] Adapters.Abstractions neutral contract
- [x] Mock Provider (both protocols, fault scripting)
- [x] Adapter contract test suite
- [x] OpenAI-compatible Adapter (non-streaming + streaming) passing the suite, with fixtures recorded from OpenAI through OpenRouter ([provider-adapters.md §4](provider-adapters.md#wire-fixtures), [ADR-0013](adr/0013-wire-fixtures-recorded-through-openrouter.md)). The "recorded" fixtures are stamped `provisional` until someone runs `OPENROUTER_API_KEY=... scripts/record-fixtures`
- [x] Ollama Adapter passing the suite
- [x] Registry: env-based credential resolution, outbound destination policy
- [x] `POST /v1/chat/completions` non-streaming with a **static alias → single model** stub, request validation, Problem Details error body with the OpenAI `error` member (ADR-0012), `X-Request-Id`
- [x] `GET /v1/models`, `/health`, `/ready`
- [x] Integration test infrastructure (Testcontainers, WebApplicationFactory, in-process mock) + the endpoint happy-path gate ([testing.md §6](testing.md#6-coverage-expectations))

**Exit (PRD deliverable):** the gateway can invoke either Provider. DoD #2 is met.

## Phase 2: Routing and resilience (≈2 weeks)

- [x] `routing` schema: policies, versions, aliases; definition JSON Schema + semantic validation
- [x] `IRouter` pure implementation: eligibility filters + three ranking strategies + table-driven tests
- [x] Tokenizer-based input estimation and worst-case cost
- [x] `AttemptExecutor`: deadlines, retries with backoff+jitter, Retry-After, Fallback, cancellation
- [x] Failure classification in both Adapters (contract-tested)
- [x] Per-instance circuit breakers + health snapshot job
- [x] SSE streaming end-to-end: first-token rule, keep-alives, mid-stream error event, disconnect cancellation
- [x] `usage` schema: `inference_request`, `provider_attempt` (+ partitions); request/attempt recording
- [x] Runtime configuration snapshot + NOTIFY/poll refresh
- [x] Failure-injection test project with the first scenarios (503 retry, hang, stream cut, circuit opens)

**Exit:** a resilient gateway with inspectable execution history. FR-03, FR-04 and FR-05 are covered and DoD #3 and #4 are met.

## Phase 3: Identity and usage (≈2 weeks)

- [x] Administrator accounts, cookie sessions, antiforgery, Roles + Tenant Grants, Owner bootstrap
- [x] Audit records
- [x] Admin endpoints: tenants, keys (create-once display, revoke), limits, model access, providers, models, prices, policies, aliases
- [x] Encrypted `db:` provider secrets (AES-GCM) + write-only API (`IProviderSecretStore` service; the HTTP endpoint ships with the admin providers endpoints above)
- [x] Valkey rate + concurrency limits (one Lua script), failure modes, multi-instance test
- [x] Usage Records, CostCalculator, Price versioning, Usage Source
- [x] Budget Reservations: reserve/top-up/settle, sweeper job, outbox *(the sweeper is the recurring `usage.sweep_reservations` job, every 30 s; the outbox relay is ready and the `usage.request_completed` consumer is the Phase 4 rollup)*
- [x] Jobs infrastructure in the worker (claim, lease, retry, dead-letter), recurring jobs and the outbox relay (`SharedKernel/Jobs`)
- [x] Tenant-isolation test matrix *(admin routes are derived from `EndpointDataSource` and gated; the Phase 4 usage and request endpoints must add their rows)*

**Exit:** a multi-tenant gateway with usage controls. FR-01, FR-06 and FR-07 are covered and DoD #5 and #7 are met.

## Phase 4: Observability (≈2 weeks)

- [x] OpenTelemetry traces/metrics/logs per [observability.md](observability.md); redaction; canary leak test
- [x] Usage rollup consumer + `usage_hourly`
- [x] Admin query endpoints: overview, requests (filters, cursor pagination), request detail, usage, provider health *(the isolation matrix has rows for the request detail and for the `tenantId` filter of requests, usage and overview)*
- [x] Dashboard design tokens (StyleX) + layout primitives per [design-system.md](design-system.md)
- [x] Dashboard: app shell, sign-in, Overview, Request Explorer, Request detail, Providers, Routing, Tenants *(the Routing simulate panel waits for its endpoint; the Budget gauge shows spent, not reserved)*
- [x] Grafana provisioning in `deploy/grafana`
- [x] Telemetry degradation test
- [x] Retention job + partition management

**Exit:** an operationally observable product. FR-08 is covered and DoD #6, #8 and #11 are met.

## Phase 5: Evaluation (≈2 weeks)

- [x] Carried over from Phase 4: `origin` in the `usage_hourly` key, so long-range Overview and usage figures can separate evaluation traffic from API traffic *(`status`, `errorCode`, `hadFallback` and `providerId` usage filters still read only the 30 days of raw rows)*
- [x] Carried over from Phase 4: `POST /admin/routing-policies/{policyId}/simulate` and the Routing screen's simulate panel; the reserved amount of the current Budget period on the Tenant detail *(its gauge shows spent and reserved against the limit)*

- [x] `evaluation` schema; dataset JSONL upload + versioning; prompts + versions
- [x] Evaluators: exact_match, json_fields, classification (P/R/F1), reference_similarity, llm_judge
- [x] Run creation (frozen configuration), worker execution with resume, cancellation
- [x] Aggregates from Case Results; Comparisons; export CSV/JSONL
- [x] Dashboard: datasets, run builder, comparison view, charts
- [x] Sample datasets in `samples/evaluations/` (extraction + classification, synthetic, no personal data)

**Exit:** a repeatable evaluation workflow. FR-09 and FR-11 are covered and DoD #9 is met.

## Phase 6: Hardening and release (≈1–2 weeks)

- [x] Complete the failure-injection catalogue; retry-amplification numbers *(PostgreSQL stopped mid-load and worker killed mid-evaluation added; both exposed bugs, fixed with regression tests)*
- [x] k6 load runs; benchmark report with real numbers (DoD #10) *(the first run found a per-Tenant `budget_period` row lock and an oversized connection pool; fixed and re-measured)*
- [x] Security test pass ([testing.md §4](testing.md#4-security-tests-prd-104)); `gitleaks` + CodeQL clean *(adds the invalid-key throttle security.md promised, and coverage gates for error codes, eligibility reasons, failure classification rows and Evaluator kinds)*
- [x] Data-retention review; README (architecture, deploy, API usage, testing, limitations) (DoD #12) *(adds deletion of Datasets, Prompts and Comparisons, and retention for expired sessions and finished Reservations)*
- [x] Demo script: deploy → connect app → inject Provider failure → watch Fallback → inspect usage → run a Comparison (PRD §15)
- [x] Playwright smoke + axe in nightly CI

Follow-ups found during Phase 6, not part of the MVP: the outbox relay publishes one event per transaction and falls behind above about 200 RPS sustained (benchmark report); a single Evaluation Run cannot be deleted on its own, only its Comparison.

**Exit:** all 14 DoD items are checked, each with a link to its evidence.

## Definition of Done evidence

| # | PRD §11 item | Evidence |
|---|---|---|
| 1 | Deploy the stack from documented instructions | [deployment.md §2](deployment.md#2-quick-start-dod-1); [demo.md](demo.md), whose verification run is recorded at its end; `scripts/e2e.sh` deploys the stack from scratch on every nightly run ([nightly.yml](../.github/workflows/nightly.yml)); the `images` job in [ci.yml](../.github/workflows/ci.yml) builds every image |
| 2 | Two Adapters pass the shared contract tests | [AdapterContractTests](../tests/ModelSwitch.AdapterContractTests): `OpenAICompatible/` and `Ollama/` against the shared suite |
| 3 | Normalized non-streaming and streaming inference | [ChatCompletionsEndpointTests](../tests/ModelSwitch.IntegrationTests/Api/ChatCompletionsEndpointTests.cs), [StreamingTests](../tests/ModelSwitch.IntegrationTests/Api/StreamingTests.cs) |
| 4 | Routing, retries, Fallbacks and circuit breakers have automated tests | [RouterTests](../tests/ModelSwitch.UnitTests/Routing/RouterTests.cs), [AttemptExecutorTests](../tests/ModelSwitch.UnitTests/Inference/AttemptExecutorTests.cs), [ResilienceTests](../tests/ModelSwitch.IntegrationTests/Api/ResilienceTests.cs), [FailureInjectionTests](../tests/ModelSwitch.FailureInjectionTests) |
| 5 | Tenant isolation and API-key revocation verified | [TenantIsolationTests](../tests/ModelSwitch.IntegrationTests/Api/TenantIsolationTests.cs), revocation within the 30 s cap in [AdminCrossCuttingTests](../tests/ModelSwitch.IntegrationTests/Api/AdminCrossCuttingTests.cs); full list in [testing.md §4](testing.md#4-security-tests-prd-104) |
| 6 | Request history and Attempts persisted and inspectable | [RequestRecordsTests](../tests/ModelSwitch.IntegrationTests/Api/RequestRecordsTests.cs); the request explorer in [smoke.spec.ts](../web/e2e/smoke.spec.ts) |
| 7 | Token usage and cost recorded with provenance | [UsageAccountingTests](../tests/ModelSwitch.IntegrationTests/Api/UsageAccountingTests.cs); price preservation in [CatalogAdminEndpointTests](../tests/ModelSwitch.IntegrationTests/Api/CatalogAdminEndpointTests.cs) |
| 8 | Dashboard shows request, cost, latency and failure information | [smoke.spec.ts](../web/e2e/smoke.spec.ts) asserts the request detail's duration and Estimated Cost and the Overview's cost, success, latency and recent failure; [DashboardEndpointTests](../tests/ModelSwitch.IntegrationTests/Api/DashboardEndpointTests.cs) asserts the figures behind them; every screen passes axe in [screens.spec.ts](../web/e2e/screens.spec.ts) |
| 9 | Evaluation Runs compare models on the same versioned Dataset | [EvaluationRunTests](../tests/ModelSwitch.IntegrationTests/Api/EvaluationRunTests.cs), [evaluation.spec.ts](../web/e2e/evaluation.spec.ts), [demo.md §6](demo.md#6-run-a-comparison) |
| 10 | A benchmark report with actual, reproducible measurements | [benchmarks/2026-10-17-ec9c440.md](benchmarks/2026-10-17-ec9c440.md) (and the earlier [266c696](benchmarks/2026-10-17-266c696.md) run), produced by [load/run.sh](../load/run.sh) |
| 11 | Logs and traces expose no API keys or content by default | [TelemetryCanaryTests](../tests/ModelSwitch.IntegrationTests/Api/TelemetryCanaryTests.cs), [LogRedactionTests](../tests/ModelSwitch.UnitTests/Telemetry/LogRedactionTests.cs), [ADR-0010](adr/0010-no-prompt-or-completion-content-retained.md) |
| 12 | The README covers architecture, deployment, API usage, testing and limitations | [README.md](../README.md) |
| 13 | CI runs formatting checks, automated tests and static analysis | [ci.yml](../.github/workflows/ci.yml) (format, analyzers as errors, tests, gitleaks), [codeql.yml](../.github/workflows/codeql.yml), [nightly.yml](../.github/workflows/nightly.yml) |
| 14 | Failure-injection tests show bounded retries and controlled degradation | [FailureInjectionTests](../tests/ModelSwitch.FailureInjectionTests), [testing.md §3](testing.md#3-failure-injection-scenarios-prd-103), [benchmarks/retry-amplification.md](benchmarks/retry-amplification.md) |
