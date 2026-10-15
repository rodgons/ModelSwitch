# Architecture

This document describes how ModelSwitch is built. Its vocabulary comes from [`CONTEXT.md`](../CONTEXT.md), and the reasons behind its choices are in the [ADRs](adr/). The [PRD](../PRD.md) remains the source of truth for *what* is built.

## 1. Technology stack

| Concern | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS), C# 14 | ADR-0001 |
| HTTP | ASP.NET Core Minimal APIs, route groups per module | `TypedResults` everywhere for OpenAPI accuracy |
| OpenAPI | `Microsoft.AspNetCore.OpenApi` (built-in) | Generated document drives the TypeScript client |
| Persistence | PostgreSQL 17, EF Core 10 + Npgsql | One `DbContext` and one schema per module |
| Shared ephemeral state | Valkey 8 (Redis protocol), `StackExchange.Redis` | Rate and Concurrency Limits only (ADR-0003) |
| Resilience primitives | Polly v8 (circuit breaker, backoff) | Driven by our own attempt loop (ADR-0004) |
| Token estimation | `Microsoft.ML.Tokenizers` (o200k / cl100k) | Used only when the Provider does not report usage |
| Telemetry | OpenTelemetry .NET SDK, OTLP exporter | `grafana/otel-lgtm` container as the local backend |
| Logging | `Microsoft.Extensions.Logging` JSON console + OTLP, `Microsoft.Extensions.Compliance.Redaction` | No third-party logger |
| Admin auth | ASP.NET Core Identity (local accounts), cookie auth, antiforgery | ADR-0011 |
| Validation | Hand-written validators that return a normalized error list | No reflection-heavy validation on the hot path. Failures become `400` Problem Details (ADR-0012) |
| Error bodies | RFC 9457 Problem Details (`IProblemDetailsService`, built in) | `/v1/*` adds the OpenAI `error` member (ADR-0012) |
| Frontend | Vue 3 + TypeScript + Vite, Vue Router, Pinia, TanStack Query (Vue), PrimeVue, ECharts (`vue-echarts`) | Client generated with `openapi-typescript` + `openapi-fetch` |
| Tests | xUnit v3, Shouldly, Testcontainers, WireMock.Net, NetArchTest; Vitest + Playwright for the web | See [testing.md](testing.md) |
| Load | k6 against the in-repo Mock Provider | See [testing.md](testing.md) |
| Packaging | Docker multi-stage images, Docker Compose | See [deployment.md](deployment.md) |
| CI | GitHub Actions | See [conventions.md](conventions.md) |

### Dependency justifications

The [constitution](constitution.md) forbids adding a dependency without a written justification in the plan. This table is that record. A dependency that is not listed here, or in the roadmap task that introduces it, must not be added. Framework packages that ship with .NET 10 / ASP.NET Core (`Microsoft.Extensions.*`, `Microsoft.AspNetCore.*`) are part of the stack and are not listed.

| Dependency | Why it is needed | Alternative rejected |
|---|---|---|
| EF Core 10 + Npgsql | Mandated by the constitution (PostgreSQL via EF Core). Npgsql is the PostgreSQL driver | Dapper: we would hand-write migrations and change tracking |
| `Microsoft.EntityFrameworkCore.Design` + `dotnet-ef` (local tool, `.config/dotnet-tools.json`) | Dev-only: scaffolds the EF Core migrations that the Migrator applies (`dotnet ef migrations add`). `Design` is referenced with `PrivateAssets=all`, so it never ships. Central transitive pinning keeps `Microsoft.EntityFrameworkCore*` on one patch version across Npgsql and Design | Hand-written migration SQL: no model snapshot, so drift goes unnoticed |
| ASP.NET Core Identity | Password hashing, lockout and cookie sessions for Administrators (ADR-0011) | Hand-rolled auth: security-sensitive code we should not own |
| `StackExchange.Redis` | Client for Valkey, which holds the shared Rate and Concurrency Limit counters (ADR-0003) | None. It is the standard .NET Redis-protocol client |
| Polly v8 | Circuit-breaker state machine and backoff-with-jitter maths (ADR-0004, ADR-0005) | Writing our own breaker: more code to test for no gain |
| `Microsoft.ML.Tokenizers` | Estimates input tokens for Reservations when the Provider does not report usage | A chars/4 heuristic: underestimates and lets Budgets be exceeded |
| OpenTelemetry .NET SDK + OTLP exporter | Traces, metrics and logs in a vendor-neutral format (FR-08, NFR-05) | Vendor SDKs: tie the product to one backend |
| xUnit v3 (+ `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`) | Mandated by the constitution. The two extra packages let IDE test explorers discover the tests; CI runs on Microsoft.Testing.Platform | — |
| `Microsoft.Extensions.TimeProvider.Testing` | `FakeTimeProvider` for deterministic time in unit tests (framework package, listed for the test project) | Hand-written fake clocks |
| Shouldly | Assertion messages that name the failing expression, which makes CI failures readable | Plain `Assert`: weaker messages. FluentAssertions: commercial licence |
| Testcontainers | Real PostgreSQL and Valkey in integration tests, so tests exercise real SQL and Lua | In-memory fakes: hide provider-specific behaviour |
| WireMock.Net | Scriptable HTTP Provider fixtures for the Adapter contract suite (tests never call a real Provider) | Hand-written `HttpMessageHandler` stubs: cannot test real sockets or timeouts |
| NetArchTest | Enforces the module-boundary and endpoint rules in §3 as tests | Code review only: boundaries erode silently |
| Vue Router, Pinia | Routing and session/UI state for the Vue 3 dashboard | — (official Vue libraries) |
| TanStack Query (Vue) | Server-state caching, refetching and the loading/error/empty states the UX rules require | Hand-written fetch state in Pinia: duplicated, stale data |
| PrimeVue (unstyled) | Accessible tables, forms and dialogs (focus trapping, ARIA) | Building accessible widgets ourselves |
| ECharts (`vue-echarts`) | Time-series and scatter charts in the dashboard and comparisons | Chart.js: weaker large time-series support |
| `openapi-typescript` + `openapi-fetch` | Generates the TypeScript client from the OpenAPI document, so response shapes are never hand-written | Hand-written DTOs: drift from the server |
| Vitest, Playwright, `axe-core` | Web unit tests, E2E smoke tests, accessibility checks | — |
| Web build tooling: Vite + `@vitejs/plugin-vue`, TypeScript, `vue-tsc`, ESLint (`@eslint/js`, `eslint-plugin-vue`, `@vue/eslint-config-typescript`, `@vue/eslint-config-prettier`, `eslint-config-prettier`), Prettier, `@vue/test-utils`, jsdom, `jiti`, `@types/node` | Dev-only: build, typecheck, lint and format the dashboard, and run component tests in a DOM ([conventions.md](conventions.md#typescript--vue)). `jiti` loads the TypeScript ESLint config. TypeScript stays on 6.0.x until typescript-eslint supports 7 | — (the standard Vue 3 toolchain) |
| k6 | Load tests that produce the gateway-overhead benchmark (NFR-01, DoD #10) | — |
| Caddy | One browser origin for api + web (ADR-0011), plus TLS termination | nginx: more configuration for automatic TLS |

## 2. Process topology

```
                 ┌────────────────────── docker compose ──────────────────────┐
  client app ──▶ │  proxy (Caddy) ──▶ api (N≥1)  ──▶ Providers (OpenAI-compat,│
  browser    ──▶ │     │  /v1/*,/admin/*,/health        Ollama, mock-provider)  │
                 │     └─ /  ──▶ web (static Vue build)                        │
                 │                api ─┬─▶ postgres                            │
                 │                     ├─▶ valkey                              │
                 │                     └─▶ otel-lgtm (OTLP)                    │
                 │             worker ──┴─▶ postgres, Providers, otel-lgtm     │
                 │           migrator (one-shot, runs before api/worker)       │
                 └─────────────────────────────────────────────────────────────┘
```

- **api** is stateless apart from in-memory caches (configuration snapshot, verified API keys, circuit breakers), so it can be scaled horizontally. Compose runs one replica by default. The multi-instance tests run two.
- **worker** runs the outbox relay, usage aggregation, Reservation sweeping, Evaluation Run execution, Provider Health snapshots and retention jobs. More than one replica is safe because all work is claimed with `SKIP LOCKED`.
- **migrator** applies every module's EF Core migrations in a fixed order and exits. api and worker never migrate on startup.
- **proxy** gives the browser one origin (ADR-0011) and terminates TLS when it is configured.

## 3. Solution layout

```
ModelSwitch.slnx                 # XML solution format (.NET 10 default)
compose.yaml                     # includes deploy/docker-compose.yml with the root .env
Directory.Build.props            # net10.0, nullable, warnings-as-errors, analyzers
Directory.Packages.props         # central package management
src/
  ModelSwitch.Api/               # host: endpoint wiring, auth handlers, middleware
  ModelSwitch.Worker/            # host: BackgroundService registrations
  ModelSwitch.Migrator/          # host: applies migrations, seeds first Owner
  ModelSwitch.SharedKernel/      # ids (UUIDv7), Money, Clock, Result, error codes, outbox primitives
  Modules/
    ModelSwitch.Identity/        # tenants, API keys, administrators, roles, audit
    ModelSwitch.Registry/        # providers, provider models, prices, secrets, health
    ModelSwitch.Routing/         # aliases, policies, versions, eligibility, ranking
    ModelSwitch.Inference/       # validation, attempt loop, streaming, normalization
    ModelSwitch.Usage/           # requests, attempts, usage records, budgets, rollups
    ModelSwitch.Evaluation/      # datasets, prompts, evaluators, runs, comparisons
    ModelSwitch.Admin/           # dashboard query endpoints (read models), no own tables
  Adapters/
    ModelSwitch.Adapters.Abstractions/   # IProviderAdapter + neutral contract types
    ModelSwitch.Adapters.OpenAICompatible/
    ModelSwitch.Adapters.Ollama/
tools/
  ModelSwitch.MockProvider/      # scriptable fake Provider for tests, load and demos
web/                             # Vue 3 dashboard
tests/
  ModelSwitch.UnitTests/
  ModelSwitch.ArchitectureTests/
  ModelSwitch.AdapterContractTests/
  ModelSwitch.IntegrationTests/
  ModelSwitch.FailureInjectionTests/
load/                            # k6 scripts + benchmark report template
deploy/                          # compose files, Caddyfile, otel config
docs/
```

### Module structure

Every module project has the same internal layout:

```
ModelSwitch.<Module>/
  Contracts/        # public: interfaces, DTOs, events other modules may use
  Domain/           # entities, value objects, pure logic (internal)
  Application/      # application services: use cases, validation, orchestration (internal)
  Infrastructure/   # DbContext, EF configurations, migrations, external I/O (internal)
  Endpoints/        # route group registration + thin handlers that call Application (internal, exposed via one extension)
  <Module>Module.cs # public static AddXModule(IServiceCollection) / MapXEndpoints(...)
```

Only `Contracts/` and the `*Module.cs` entry point are `public`. Everything else is `internal`, and tests get access through `InternalsVisibleTo`. NetArchTest enforces:

1. No module references another module's non-`Contracts` namespaces.
2. Adapters reference only `Adapters.Abstractions` and `SharedKernel`.
3. `Domain/` has no dependency on EF Core, ASP.NET Core or `HttpClient`.
4. `Endpoints/` and the Api host have no dependency on `Infrastructure/`, any `DbContext` or EF Core.

**Endpoints are thin (constitution).** An endpoint handler binds the HTTP input, calls one application service (`Application/` in its own module, or another module's `Contracts/` interface), and maps the `Result<T>` to an HTTP result. It never touches the database and contains no business rules: no branching on domain state, no calculations, no authorization decisions beyond the declarative policy on the route group. The same applies to other HTTP-pipeline code: the API Key authentication handler only calls `IApiKeyAuthenticator`, and middleware only does transport concerns (size limits, headers, correlation). Admin read-model endpoints call each module's query service (`IUsageQueries`, …), not a `DbContext`.

## 4. Module responsibilities and seams

| Module | Owns (schema) | Exposes (Contracts) | Depends on |
|---|---|---|---|
| Identity | `identity` | `IApiKeyAuthenticator`, `ITenantDirectory`, `IAuditWriter`, current-principal accessors | — |
| Registry | `registry` | `IProviderCatalog` (snapshot of providers, models, capabilities, current prices), `IAdapterFactory`, `IProviderHealth` | Adapters.Abstractions |
| Routing | `routing` | `IRouter.Decide(RoutingInput) → RoutingDecision`, `IPolicyStore` | Registry, Identity (tenant model access) |
| Inference | — (writes through Usage) | `IInferenceService` (used by the HTTP endpoint and by Evaluation) | Routing, Registry, Usage, Identity, Adapters.Abstractions (runs Attempts through `IProviderAdapter`) |
| Usage | `usage` | `IUsageLedger` (open/settle requests, Reservations), `IUsageQueries` | Registry (prices) |
| Evaluation | `evaluation` | — (endpoints and worker jobs only) | Inference, Usage |
| Admin | — | — | Contracts/queries of all modules |

`IInferenceService` is the deep seam of the system. Evaluation calls it in-process with the same Tenant, routing, limits and accounting as an external client, so evaluation results reflect what production traffic would get.

## 5. Inference request lifecycle

```
HTTP POST /v1/chat/completions
 1. RequestId = UUIDv7; start Activity; set X-Request-Id response header
 2. Size/content-type guard (Kestrel limit + middleware)        → 413 / 415
 3. Authenticate API Key (cached ≤30 s)                        → 401
 4. Check Scope and Tenant status                              → 403
 5. Parse + validate body (unsupported params rejected)        → 400 (Problem Details)
 6. Acquire Rate Limit token and Concurrency lease (Valkey)    → 429
 7. Routing: resolve alias → Policy Version → eligibility → ranking
    → RoutingDecision (ordered Candidates)                     → 404 / 403 / 422 / 503 (see api.md §2)
 8. Reserve Budget for the worst-case cost of the first Candidate,
    and INSERT inference_request(status=running) in one tx     → 402 budget_exceeded
 9. AttemptExecutor loop (Deadline-bound, cancellation-aware):
      for each Candidate in order:
        skip if circuit open
        attempt (with retries for transient errors, backoff+jitter,
                 Retry-After honoured if it fits the Deadline)
        stream: forward chunks; once the First Token is sent, no more retries or Fallbacks
 10. Normalize response / error; write response
 11. Settle in one tx: update inference_request, insert attempts + usage records,
     release Reservation, adjust budget spent, insert outbox event
 12. Release Concurrency lease; record metrics; end Activity
```

If the client disconnects, `HttpContext.RequestAborted` cancels the attempt loop. The in-flight Attempt is then recorded as `cancelled`, or as `ambiguous` if the request had already been sent to the Provider. Step 11 still runs, using a fresh `CancellationToken` with a short timeout.

If the process dies between steps 8 and 11, the Reservation and the `running` request are left behind. The worker's **Reservation sweeper** finds requests whose Deadline plus a grace period has passed. It marks them `abandoned`, records an `unavailable` Usage Record charged at the reserved amount (worst-case, possibly billed), and settles the budget.

Details of steps 6–9 are in [routing-and-resilience.md](routing-and-resilience.md), and steps 8 and 11 are in [usage-and-budgets.md](usage-and-budgets.md).

## 6. Configuration

There are two kinds of configuration with different lifecycles.

| Kind | Where | Changes require |
|---|---|---|
| **Deployment configuration**: connection strings, master key, API key pepper, OTLP endpoint, limits failure mode, listen URLs, allowed private network hosts | Environment variables (`MODELSWITCH__Section__Key`), validated at startup with `ValidateOnStart` | Restart |
| **Runtime configuration**: Providers, Provider Models, Prices, Routing Policies, Tenants, limits, Budgets | PostgreSQL, edited through the admin API | Nothing; picked up live |

**Runtime configuration propagation.** Each api instance holds an immutable `ConfigurationSnapshot` (catalog + policies + tenant limits). Every admin write increments `config.config_version` and issues `NOTIFY config_changed`. Each instance `LISTEN`s and also polls the version every 5 seconds as a fallback. When the version changes, the instance builds a new snapshot and swaps it in atomically. In-flight requests finish on the snapshot they started with. Their Routing Decision records the Policy Version it used, so a request never mixes two versions.

**Startup validation.** A missing or invalid mandatory setting makes the process exit with code 78 (`EX_CONFIG`) and log one line per problem, naming the environment variable to set (FR-10).

## 7. Cross-cutting conventions

- **Identifiers**: UUIDv7 (`Guid.CreateVersion7()`), generated by the application. They are time-ordered, which keeps B-tree indexes compact.
- **Time**: `TimeProvider` is injected everywhere. Timestamps are stored as `timestamptz` in UTC, and column names end in `_at`.
- **Money**: `decimal` in C#, `numeric(20,10)` in PostgreSQL, always paired with an ISO-4217 currency (USD only in the MVP).
- **Errors**: a closed set of error codes in `SharedKernel.Errors` (see [api.md](api.md#2-error-codes)). Modules return `Result<T>` for expected failures and throw only for bugs. One shared writer turns every error into Problem Details, and validation errors are always 400 (ADR-0012).
- **Hot-path allocation**: the streaming path uses `System.IO.Pipelines` / `Utf8JsonReader` and no per-chunk LINQ.
