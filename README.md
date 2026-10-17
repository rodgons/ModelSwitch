# ModelSwitch

A self-hosted AI gateway: one OpenAI-compatible API in front of multiple LLM Providers, with routing, Fallbacks, circuit breakers, per-Tenant limits and Budgets, cost tracking with explicit provenance, request tracing, and an evaluation lab for comparing models.

> **Status: MVP complete (Phases 0–6).** Every item of the PRD's Definition of Done is checked, with links to its evidence, in [docs/roadmap.md](docs/roadmap.md#definition-of-done-evidence).

## Architecture

A .NET 10 modular monolith deployed as three processes, with PostgreSQL as the only durable store and queue and Valkey for Rate and Concurrency Limits.

```
client app ──▶ proxy (Caddy) ──▶ api ──▶ Providers (OpenAI-compatible, Ollama, mock)
browser    ──▶   │                │
                 └──▶ web (Vue)   ├──▶ PostgreSQL ◀── worker (outbox, rollups, sweeper, evaluation runs, retention)
                                  └──▶ Valkey          migrator (one-shot, before api and worker)
```

- **api** serves `/v1/*` (inference) and `/admin/*` (dashboard API). It is stateless apart from in-memory caches, so it scales horizontally.
- **worker** runs background jobs claimed with `SKIP LOCKED`, so any number of replicas is safe.
- **migrator** applies every module's migrations and creates the least-privilege database roles.
- The modules (Identity, Registry, Routing, Inference, Usage, Evaluation, Admin) talk only through each other's `Contracts/` namespace; architecture tests enforce it.

A request is authenticated, limited, routed (alias → Routing Policy Version → eligible Candidates in order), charged a Budget Reservation for its worst case, and run through a Deadline-bound Attempt loop with retries, Fallbacks and per-instance circuit breakers. Every Attempt is recorded with its own Usage Record and Estimated Cost. Details: [docs/architecture.md](docs/architecture.md) and [docs/routing-and-resilience.md](docs/routing-and-resilience.md).

## Deploy

Needs Docker with Compose v2.

```bash
cp .env.example .env
./scripts/generate-secrets.sh          # master key, API-key pepper, database passwords
# set MODELSWITCH__Bootstrap__OwnerEmail and MODELSWITCH__Bootstrap__OwnerPassword in .env
docker compose --profile demo up -d --build
KEY=$(./scripts/seed-demo.sh)          # mock Provider, alias "balanced", Tenant "demo", a new API Key
open http://localhost:8080             # dashboard: sign in as the Owner
```

Add `--profile ollama` to run a small local model as the Fallback. Environment variables, images, health checks, TLS and production notes are in [docs/deployment.md](docs/deployment.md).

## API usage

The inference API is OpenAI-compatible, so existing SDKs work by changing the base URL:

```bash
curl http://localhost:8080/v1/chat/completions \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"balanced","messages":[{"role":"user","content":"hi"}]}'
```

```python
from openai import OpenAI
client = OpenAI(base_url="http://localhost:8080/v1", api_key=KEY)
client.chat.completions.create(model="balanced", messages=[{"role": "user", "content": "hi"}])
```

- `model` is an alias, bound to a Routing Policy by an Administrator. `GET /v1/models` lists the aliases the key's Tenant may use.
- `"stream": true` returns SSE chunks ending with `data: [DONE]`.
- Every response carries `X-Request-Id`, which finds the request, its Attempts and its trace in the dashboard. `X-ModelSwitch-Attempts` says how many Attempts it took.
- Errors are Problem Details with an OpenAI-style `error` member, from a closed set of codes.

Reference: [docs/api.md](docs/api.md). The admin API is described in the same document; the dashboard is its main client.

**Demo:** [docs/demo.md](docs/demo.md) walks through deploy → connect an app → inject a Provider failure → watch the Fallback → inspect usage → run a Comparison.

## Testing

```bash
dotnet test ModelSwitch.slnx          # needs Docker (Testcontainers)
cd web && pnpm install && pnpm test   # Vitest
```

| Layer | What it proves |
|---|---|
| Unit tests | Every business rule: eligibility, ranking, cost, budgets, failure classification, circuit transitions, evaluation metrics |
| Architecture tests | Module boundaries, thin endpoints, no resilience handlers on Adapter clients, no content in log calls |
| Adapter contract tests | Both Adapters (OpenAI-compatible, Ollama) against recorded wire fixtures |
| Integration tests | Real PostgreSQL and Valkey; a happy path for every endpoint and a trigger for every error code (both enforced by gate tests), tenant isolation, multi-instance limits |
| Failure-injection tests | Provider 503/429/401/hang/bad JSON/stream cut, circuit opening, PostgreSQL and Valkey down, worker killed mid-evaluation, telemetry backend down, each with bounded attempts and consistent accounting |
| Playwright + axe | Dashboard smoke and accessibility, nightly |
| k6 | Gateway overhead and throughput |

CI runs format checks, the build with analyzers as errors, all tests, image builds, gitleaks and CodeQL. Strategy and catalogues: [docs/testing.md](docs/testing.md). Measured results: [docs/benchmarks/](docs/benchmarks/).

**Measured** ([docs/benchmarks/2026-10-17-ec9c440.md](docs/benchmarks/2026-10-17-ec9c440.md), one api replica on a 4-CPU / 3.8 GiB Docker VM, Mock Provider answering in 200 ms): gateway overhead p95 is 3.4 ms at 50 RPS, 4.4 ms at 100 RPS and 13.6 ms at 200 RPS non-streaming (8.7, 11.2 and 48.9 ms streaming), all with 0 errors, so the NFR-01 target of p95 < 100 ms holds up to 200 RPS. At 400 RPS that machine saturates at about 340 RPS (274 streaming) and queues instead of failing. Retry amplification for each failure scenario is in [docs/benchmarks/retry-amplification.md](docs/benchmarks/retry-amplification.md).

## Limitations

- **No prompt or completion content is stored or logged** ([ADR-0010](docs/adr/0010-no-prompt-or-completion-content-retained.md)). A bad completion is diagnosed by reproducing it, not by looking it up. Evaluation Datasets and their outputs are the deliberate exception, and are deleted with their Dataset or Comparison.
- **Costs are estimates.** They come from the Prices an Administrator enters and the tokens the Provider reports (or the gateway estimates, which is labelled). They are not a Provider's bill.
- **Circuit breakers are per api instance** ([ADR-0005](docs/adr/0005-circuit-breakers-are-per-instance.md)): with N replicas, a failing model can take up to N times the failures to be cut off everywhere.
- **No exactly-once Provider calls.** An Attempt whose outcome is unknown (a hang, a cut connection) is recorded as ambiguous, is not retried on the same model, and is charged at its worst case.
- **Once a stream has sent its first token, there are no retries or Fallbacks.** A cut stream ends with a `stream_interrupted` event.
- **Tenant isolation is enforced in the application** and proven by an isolation test matrix; PostgreSQL row-level security is deferred ([security.md §3](docs/security.md)).
- **The invalid-key throttle is per instance**, and it trusts `X-Forwarded-For` only from loopback and private networks.
- **Text chat only.** No embeddings, images, audio or tools-as-agents; no semantic caching; no SSO. Compose is the reference deployment; there are no Kubernetes manifests.
- **TLS** is the proxy's job. The bundled local setup is plain HTTP on `localhost`, which browsers accept for Secure cookies; any other hostname needs TLS ([deployment.md §5](docs/deployment.md#5-production-notes-documented-not-automated)).
- **The outbox relay publishes one event per transaction, in sequence.** Above about 200 RPS sustained it falls behind (measured in the benchmark report), so usage rollups lag; the raw Usage Records are written with each request and are always current.
- Evaluation scores from LLMs are not deterministic even at temperature 0. The Comparison view shows run-to-run variance once a configuration has run twice.

## Documentation

| Document | What it answers |
|---|---|
| [PRD.md](PRD.md) | What we're building and why: requirements, scope, definition of done |
| [docs/constitution.md](docs/constitution.md) | Non-negotiable project rules. Overrides every other doc |
| [CONTEXT.md](CONTEXT.md) | The domain vocabulary. Use these words in code, docs and UI |
| [docs/architecture.md](docs/architecture.md) | Stack, processes, solution layout, module seams, request lifecycle, configuration |
| [docs/api.md](docs/api.md) | Inference API, admin API, error codes, health probes |
| [docs/data-model.md](docs/data-model.md) | PostgreSQL schema per module, indexes, retention, DB roles |
| [docs/routing-and-resilience.md](docs/routing-and-resilience.md) | Eligibility, ranking, policy format, failure classification, attempt loop, circuit breakers, limits |
| [docs/usage-and-budgets.md](docs/usage-and-budgets.md) | Usage Records, cost calculation, Reservations, ambiguous outcomes, aggregation |
| [docs/provider-adapters.md](docs/provider-adapters.md) | Adapter contract, the two Adapters, contract test suite, Mock Provider |
| [docs/security.md](docs/security.md) | Threat model, API keys, admin sessions, tenant isolation, SSRF, log hygiene |
| [docs/observability.md](docs/observability.md) | Traces, metrics, logs, correlation, degradation rules |
| [docs/evaluation.md](docs/evaluation.md) | Datasets, prompts, evaluators, runs, reproducibility, export, deletion |
| [docs/frontend.md](docs/frontend.md) | Dashboard stack, screens, UX rules |
| [docs/design-system.md](docs/design-system.md) | Dashboard design system. Rendered in [design-system-specimen.html](docs/design-system-specimen.html) |
| [docs/testing.md](docs/testing.md) | Test layers, integration/failure/security catalogues, load tests, benchmark report |
| [docs/deployment.md](docs/deployment.md) | Compose services, quick start, environment variables, migrations |
| [docs/demo.md](docs/demo.md) | The end-to-end demo script |
| [docs/conventions.md](docs/conventions.md) | Coding standards and CI |
| [docs/roadmap.md](docs/roadmap.md) | Phased implementation plan and the Definition of Done evidence |
| [docs/adr/](docs/adr/) | Architecture Decision Records |

## Stack

.NET 10 (ASP.NET Core Minimal APIs, EF Core) · PostgreSQL 17 · Valkey 8 · OpenTelemetry · Vue 3 + TypeScript · Docker Compose

## License

[Apache License 2.0](LICENSE). It covers the whole repository, documentation included. See [NOTICE](NOTICE).
