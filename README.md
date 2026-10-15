# ModelSwitch

A self-hosted AI gateway: one OpenAI-compatible API in front of multiple LLM Providers, with routing, Fallbacks, circuit breakers, per-Tenant limits and Budgets, cost tracking with explicit provenance, request tracing, and an evaluation lab for comparing models.

> **Status: design phase.** No code yet. The design documents below define what will be built and how.

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
| [docs/evaluation.md](docs/evaluation.md) | Datasets, prompts, evaluators, runs, reproducibility, export |
| [docs/frontend.md](docs/frontend.md) | Dashboard stack, screens, UX rules |
| [docs/design-system.md](docs/design-system.md) | Dashboard design system: violet palette, type, spacing, buttons, links, Route Strip, responsive primitives. Rendered in [design-system-specimen.html](docs/design-system-specimen.html) |
| [docs/testing.md](docs/testing.md) | Test layers, integration/failure/security catalogues, load tests, benchmark report |
| [docs/deployment.md](docs/deployment.md) | Compose services, quick start, environment variables, migrations |
| [docs/conventions.md](docs/conventions.md) | Coding standards and CI |
| [docs/roadmap.md](docs/roadmap.md) | Phased, issue-sized implementation plan |
| [docs/open-questions.md](docs/open-questions.md) | Assumptions to confirm and undecided questions |
| [docs/adr/](docs/adr/) | Architecture Decision Records |

## Stack at a glance

.NET 10 (ASP.NET Core Minimal APIs, EF Core) · PostgreSQL 17 · Valkey 8 · OpenTelemetry · Vue 3 + TypeScript · Docker Compose

## Quick start

Not available yet. The target experience is described in [docs/deployment.md §2](docs/deployment.md#2-quick-start-target-experience-dod-1).
