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

- [ ] `routing` schema: policies, versions, aliases; definition JSON Schema + semantic validation
- [ ] `IRouter` pure implementation: eligibility filters + three ranking strategies + table-driven tests
- [ ] Tokenizer-based input estimation and worst-case cost
- [ ] `AttemptExecutor`: deadlines, retries with backoff+jitter, Retry-After, Fallback, cancellation
- [ ] Failure classification in both Adapters (contract-tested)
- [ ] Per-instance circuit breakers + health snapshot job
- [ ] SSE streaming end-to-end: first-token rule, keep-alives, mid-stream error event, disconnect cancellation
- [ ] `usage` schema: `inference_request`, `provider_attempt` (+ partitions); request/attempt recording
- [ ] Runtime configuration snapshot + NOTIFY/poll refresh
- [ ] Failure-injection test project with the first scenarios (503 retry, hang, stream cut, circuit opens)

**Exit:** a resilient gateway with inspectable execution history. FR-03, FR-04 and FR-05 are covered and DoD #3 and #4 are met.

## Phase 3: Identity and usage (≈2 weeks)

- [ ] Administrator accounts, cookie sessions, antiforgery, Roles + Tenant Grants, Owner bootstrap
- [ ] Audit records
- [ ] Admin endpoints: tenants, keys (create-once display, revoke), limits, model access, providers, models, prices, policies, aliases
- [ ] Encrypted `db:` provider secrets (AES-GCM) + write-only API
- [ ] Valkey rate + concurrency limits (one Lua script), failure modes, multi-instance test
- [ ] Usage Records, CostCalculator, Price versioning, Usage Source
- [ ] Budget Reservations: reserve/top-up/settle, sweeper job, outbox
- [ ] Jobs infrastructure in the worker (claim, lease, retry, dead-letter)
- [ ] Tenant-isolation test matrix

**Exit:** a multi-tenant gateway with usage controls. FR-01, FR-06 and FR-07 are covered and DoD #5 and #7 are met.

## Phase 4: Observability (≈2 weeks)

- [ ] OpenTelemetry traces/metrics/logs per [observability.md](observability.md); redaction; canary leak test
- [ ] Usage rollup consumer + `usage_hourly`
- [ ] Admin query endpoints: overview, requests (filters, cursor pagination), request detail, usage, provider health
- [ ] Dashboard design tokens (StyleX) + layout primitives per [design-system.md](design-system.md)
- [ ] Dashboard: app shell, sign-in, Overview, Request Explorer, Request detail, Providers, Routing, Tenants
- [ ] Grafana provisioning in `deploy/grafana`
- [ ] Telemetry degradation test
- [ ] Retention job + partition management

**Exit:** an operationally observable product. FR-08 is covered and DoD #6, #8 and #11 are met.

## Phase 5: Evaluation (≈2 weeks)

- [ ] `evaluation` schema; dataset JSONL upload + versioning; prompts + versions
- [ ] Evaluators: exact_match, json_fields, classification (P/R/F1), reference_similarity, llm_judge
- [ ] Run creation (frozen configuration), worker execution with resume, cancellation
- [ ] Aggregates from Case Results; Comparisons; export CSV/JSONL
- [ ] Dashboard: datasets, run builder, comparison view, charts
- [ ] Sample datasets in `samples/evaluations/` (extraction + classification, synthetic, no personal data)

**Exit:** a repeatable evaluation workflow. FR-09 and FR-11 are covered and DoD #9 is met.

## Phase 6: Hardening and release (≈1–2 weeks)

- [ ] Complete the failure-injection catalogue; retry-amplification numbers
- [ ] k6 load runs; benchmark report with real numbers (DoD #10)
- [ ] Security test pass ([testing.md §4](testing.md#4-security-tests-prd-104)); `gitleaks` + CodeQL clean
- [ ] Data-retention review; README (architecture, deploy, API usage, testing, limitations) (DoD #12)
- [ ] Demo script: deploy → connect app → inject Provider failure → watch Fallback → inspect usage → run a Comparison (PRD §15)
- [ ] Playwright smoke + axe in nightly CI

**Exit:** all 14 DoD items are checked, each with a link to its evidence.
