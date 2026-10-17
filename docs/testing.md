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

**Implementation status.** `ModelSwitch.FailureInjectionTests` holds every scenario in the table. It references `ModelSwitch.IntegrationTests` to reuse `GatewayFixture` and `PostgresFixture` (re-declaring the assembly fixture, which xunit only honours on the entry assembly). Each scenario class has its own fixture, so circuits and fault scripts do not leak, and installs a Routing Policy with small timeouts. A scenario that stops a dependency gets a container of its own: the Valkey scenarios use `ValkeyFixture.StartDedicatedAsync`, and "PostgreSQL stopped mid-load" uses `PostgresFixture.StartDedicatedAsync` (a fixed host port, so connection strings survive the restart). "Worker killed during evaluation" runs the real `ModelSwitch.Worker` executable and kills its process, so the job is neither released nor heartbeated; a new in-process worker host re-claims it. Waits that the product measures in minutes (a job lease, a Reservation's expiry) are fast-forwarded by moving the timestamp into the past, and the sweeper is then called through `IReservationSweeper`. Classes carry `[Slow]` (trait `Category=slow`). They run in the normal `dotnet test` of the `dotnet` CI job; filter with `--filter-trait "Category=slow"` to run or split them out. Provider calls are counted by the Mock Provider. Every scenario records its amplification with `AmplificationReport.Measure`, which asserts it against the scenario's stated bound, and at the end of the run the rows are written to `retry-amplification.json` and `retry-amplification.md` in `TestResults/` under the test output directory, or in `MODELSWITCH_AMPLIFICATION_REPORT_DIR` when that is set. The measured numbers are in [benchmarks/retry-amplification.md](benchmarks/retry-amplification.md).

**Retry amplification** is measured as `provider_calls / client_requests` per scenario and reported in [benchmarks/retry-amplification.md](benchmarks/retry-amplification.md) for the benchmark report (PRD §13).

## 4. Security tests (PRD §10.4)

These are covered in the catalogue above, plus:
- Log/trace canary test ([security.md §6](security.md#6-logging-and-telemetry-hygiene)).
- SSRF: endpoints `http://169.254.169.254`, `http://127.0.0.1`, a hostname resolving to 10.x, and a DNS-rebinding hostname (stub resolver) → rejected on save and at connect.
- Admin: Viewer → POST is 403. Missing antiforgery token → 400. Operator outside their grant → 404.
- Rate-limit abuse: parallel bursts across two hosts → not exceeded. Concurrency leases from a crashed host expire.

### Evidence (Phase 6 security pass, DoD #5, #11, #13)

Each security item of §2 and §4 and the test that proves it. `IT` is `ModelSwitch.IntegrationTests`, `UT` is `ModelSwitch.UnitTests`.

| Item | Test(s) |
|---|---|
| Invalid key → 401 | IT `ChatCompletionsEndpointTests.Wrong_key_is_a_401`, `.Missing_key_is_a_401_problem_with_x_request_id`; IT `ApiKeyAuthenticationTests.Altered_key_is_invalid`; UT `ApiKeyAuthenticatorTests` (unknown prefix, wrong secret, malformed) |
| Revoked key → 401 | IT `TenantAdminEndpointTests.A_revoked_key_gets_401_on_v1_at_once_and_revoking_twice_audits_once`; UT `ApiKeyAuthenticatorTests.Revoked_key_is_invalid` |
| Expired key → 401 | IT `AdminCrossCuttingTests.A_key_that_reaches_its_expiry_gets_401_on_v1_even_while_it_is_cached` (fake clock); IT `ApiKeyAuthenticationTests.Expired_key_is_invalid`; UT `ApiKeyAuthenticatorTests.Expired_key_is_invalid`, `.Key_expiring_exactly_now_is_invalid`, `.Cached_key_is_not_served_past_its_own_expiry` |
| Revocation within the 30 s cap (fake clock) | IT `AdminCrossCuttingTests.A_revoked_key_stops_at_once_on_the_revoking_host_and_within_the_cache_bound_on_the_other`; IT `ApiKeyAuthenticationTests.Revoked_key_is_invalid_once_the_cache_has_expired`; UT `ApiKeyAuthenticatorTests.Revocation_takes_effect_no_later_than_30_seconds_after_the_key_was_cached` |
| Missing scope → 403 | IT `ChatCompletionsEndpointTests.Key_without_the_inference_scope_is_a_403_insufficient_scope`; UT `ApiKeyAuthenticationHandlerTests.Forbidden_is_insufficient_scope_403` |
| Suspended tenant → 403 | IT `TenantAdminEndpointTests.A_suspended_tenant_gets_403_on_v1_and_is_served_again_when_reactivated`; IT `ApiKeyAuthenticationTests.Suspended_tenant_is_tenant_suspended`; UT `ApiKeyAuthenticatorTests.Suspended_tenant_is_tenant_suspended_not_invalid_key` |
| Repeated invalid keys throttled (security.md §2) | IT `ChatCompletionsEndpointTests.Repeated_invalid_keys_from_one_client_are_throttled_with_429_on_that_instance_only`; UT `ApiKeyFailureThrottleTests`, `ApiKeyAuthenticationHandlerTests.A_client_over_the_invalid_key_limit_gets_429_with_retry_after_and_the_key_is_not_checked` |
| Tenant isolation matrix (foreign id → 404) | IT `TenantIsolationTests.Every_tenant_scoped_route_is_in_the_isolation_matrix`, `.An_operator_granted_only_tenant_a_gets_404_for_every_tenant_scoped_route_of_tenant_b`, `.A_viewer_granted_only_tenant_a_gets_404_for_the_read_routes_of_tenant_b`, `.A_foreign_child_id_under_the_callers_own_tenant_is_404_for_an_operator_and_for_the_owner` |
| `/v1` never shows another tenant's aliases or usage | IT `TenantIsolationTests.Models_lists_only_the_aliases_the_tenants_model_access_allows`, `.An_alias_only_another_tenant_may_use_is_403_model_not_allowed_and_leaks_nothing`, `.Responses_carry_only_the_callers_own_request_ids_limits_and_usage` |
| Log/trace canary (DoD #11) | IT `TelemetryCanaryTests.No_span_metric_or_log_line_contains_the_prompt_or_a_secret`; IT `RequestRecordsTests.Prompt_and_completion_content_is_never_persisted`; UT `LogRedactionTests.Secret_and_content_parameters_are_erased_and_ordinary_ones_kept` |
| SSRF `http://169.254.169.254`, `http://127.0.0.x`, `localhost`, `10.x` literal → rejected on save | IT `CatalogAdminEndpointTests.A_provider_endpoint_that_reaches_the_gateways_own_network_is_rejected_on_create_and_on_patch`; UT `OutboundDestinationPolicyTests.Non_public_ip_literals_are_rejected`, `IpAddressClassifierTests` |
| SSRF hostname resolving to 10.x → rejected on save and at connect | UT `OutboundDestinationPolicyTests.A_hostname_resolving_to_10_x_is_refused_on_save_and_at_connect_time`, `.Hostname_resolving_to_a_private_address_is_rejected` |
| SSRF DNS rebinding (stub resolver) → accepted on save, refused at connect | UT `OutboundDestinationPolicyTests.A_rebinding_host_accepted_on_save_is_refused_by_the_real_handler_at_connect_time`, `.Dns_rebinding_is_caught_at_connect_time`, `.Connect_callback_refuses_a_rebound_host_and_never_opens_a_socket` |
| No redirects or proxies on Adapter clients | UT `OutboundDestinationPolicyTests.Handler_disables_redirects_and_proxies` |
| Viewer → POST is 403 | IT `AdminSecurityTests.A_viewer_or_operator_posting_to_an_owner_endpoint_is_403_forbidden`; IT `TenantAdminEndpointTests.A_viewer_can_read_but_every_write_is_403` |
| Missing antiforgery token → 400 | IT `AdminSecurityTests.A_missing_antiforgery_token_on_sign_in_is_a_400_validation_problem`, `.A_missing_antiforgery_token_on_an_unsafe_request_with_a_valid_session_is_a_400`, `.A_garbage_antiforgery_token_is_rejected_and_a_token_from_another_session_does_not_transfer` |
| Operator outside their grant → 404 | IT `AdminSecurityTests.An_operator_outside_their_grant_gets_404_and_inside_it_gets_200` |
| Rate-limit abuse: parallel bursts across two hosts | IT `LimitsTests.Two_hosts_sharing_one_valkey_admit_exactly_the_rate_limit_burst`, `.Two_hosts_sharing_one_valkey_admit_exactly_the_concurrency_limit` |
| Leases from a crashed host expire | IT `LimitsTests.A_lease_nobody_released_expires_by_itself` |
| Provider secrets encrypted at rest, bound to their Provider | IT `ProviderSecretTests`; UT `SecretCipherTests` |
| Secrets in the repository | `gitleaks` job in `ci.yml` (full history). Local run on 2026-10-16, gitleaks 8.30.1: `gitleaks git` over the whole history and `gitleaks dir` over the tree, no leaks, no allowlist needed |
| Static analysis | `codeql.yml` (C# + JS/TS, `security-extended`). Local run on 2026-10-16, CodeQL 2.27.2 (C# extracted with `build-mode: none`): 0 results for C# and for JS/TS. The broader `security-and-quality` suite reports only maintainability notes (no security query fires). .NET analyzers: `AnalysisLevel` `latest-recommended`, `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild` in `Directory.Build.props` |

## 5. Load tests and benchmark report

How to run: [load/README.md](../load/README.md).

- `load/scenarios/overhead.js` (k6): constant arrival rate in steps of 50, 100, 200 and 400 RPS for 5 min each (`RATES`, `STEP_DURATION`; `30s` steps make a smoke run), non-streaming and streaming variants (`STREAM=false|true|both`), against the alias `balanced` of `scripts/seed-demo.sh`. Every step is a separate k6 scenario, so the summary reports each one.
- `load/run.sh` runs the whole measurement: it waits until no `modelswitch-e2e` container runs, starts the stack as its own Compose project `modelswitch-load` (Release images, profiles `demo` and `observability`, overlay `load/compose.load.yaml` that exports telemetry to `otel-lgtm` every 5 s), seeds a Tenant with no limits, makes the Mock Provider answer `mock-echo` after a fixed 200 ms (`POST /__scenarios` `latency=200`), warms up, runs one k6 step per (variant, rate), collects after each step, and tears the stack down.
- Primary result: `modelswitch.inference.gateway_overhead` p50/p95/p99 (target p95 < 100 ms, NFR-01) from the Prometheus inside `otel-lgtm` (`histogram_quantile` over the step, interpolated within the 1–250 ms buckets, plus the exact share ≤ 100 ms), the same overhead recomputed exactly per request from `inference_request` and `provider_attempt`, and k6 end-to-end latency minus the mock latency as a cross-check.
- Also captured: throughput, error rate, dropped iterations, api working set (RSS), GC pause time and collections, CPU, thread-pool queue, Npgsql pool usage against its maximum and operation latency (Npgsql exports no pool wait-time instrument), Valkey server-side time per command (`INFO commandstats`), outbox lag, and a `docker stats` sample per step.
- `docs/benchmarks/<date>-<git-sha>.md` uses the template in `load/REPORT_TEMPLATE.md`: hardware, OS, Docker resources, image digests, dataset size, concurrency, duration, raw numbers, **limitations**. Results are only published when they were actually measured (DoD #10). Raw output stays in the gitignored `load/results/`.
- `.github/workflows/load.yml` runs a short smoke (manual and nightly) on a GitHub runner and uploads the output as an artifact; runner numbers are not a benchmark and are never committed.

## 6. Coverage expectations

Coverage percentage is not a gate. Instead, CI fails if any of these are missing tests: each row of the failure-classification table, each eligibility reason code, each error code in [api.md §2](api.md#2-error-codes) (there is a test that triggers it), and each Evaluator kind.

| Gate | Test |
|---|---|
| Failure-classification rows | UT `FailureClassificationTableTests.Every_documented_row_is_covered` (parses [routing-and-resilience.md §3](routing-and-resilience.md#3-failure-classification)), `.Every_adapter_failure_code_has_a_row`, `.Each_row_of_the_table_is_retried_fallen_back_from_and_counted_as_documented` |
| Eligibility reason codes | UT `RouterTests.Every_reason_code_has_an_exclusion_case` over `RouterTests.Each_reason_code_excludes_the_candidate_and_is_recorded` |
| Error codes | IT `ErrorCodeGateTests.Every_error_code_is_triggered_by_a_tagged_test`: each code in the closed set needs an integration test tagged `[TriggersError("<code>")]` that produces and asserts it. UT `ErrorCodeTests` keeps the set equal to the api.md table |
| Evaluator kinds | UT `EvaluatorRegistryTests.Every_evaluator_kind_has_a_scoring_test_class` (kind `json_fields` needs `JsonFieldsEvaluatorTests`) |
| Endpoints | IT `EndpointHappyPathGateTests.Every_route_has_a_happy_path_test` |

**Endpoint gate:** a test in `ModelSwitch.IntegrationTests` lists every route from the app's `EndpointDataSource` and fails if one has no test tagged `[HappyPath("<METHOD> <route>")]`.

**Business rules** cannot be found automatically, so the reviewer checks them: a PR that adds or changes a rule in `Domain/` or `Application/` without a unit test is not mergeable.
