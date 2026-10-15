# Deployment and Configuration

This document covers FR-10 and NFR-05. The README quick-start is derived from it.

## 1. Compose services

`deploy/docker-compose.yml`:

| Service | Image | Profile | Depends on |
|---|---|---|---|
| `postgres` | `postgres:17` | default | — |
| `valkey` | `valkey/valkey:8` | default | — |
| `migrator` | `modelswitch/migrator` | default | postgres healthy |
| `api` | `modelswitch/api` | default | migrator completed, valkey healthy |
| `worker` | `modelswitch/worker` | default | migrator completed |
| `web` | `modelswitch/web` (static files served by Caddy) | default | — |
| `proxy` | `caddy:2` | default | api, web |
| `mock-provider` | `modelswitch/mock-provider` | `demo` | — |
| `ollama` | `ollama/ollama` | `ollama` | — |
| `otel-lgtm` | `grafana/otel-lgtm` | `observability` | — |

Only `proxy` publishes a host port (default `8080`). PostgreSQL and Valkey publish ports only in `docker-compose.override.yml` for local development.

The images are .NET 10 multi-stage builds. Runtime images use `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` and run as a non-root user. `HEALTHCHECK` uses `/health`.

## 2. Quick start (target experience, DoD #1)

```bash
git clone … && cd ModelSwitch
cp .env.example .env
./scripts/generate-secrets.sh          # fills master key, pepper, DB passwords in .env
# set MODELSWITCH__Bootstrap__OwnerEmail / OwnerPassword in .env
docker compose --profile demo up -d
open http://localhost:8080             # sign in as the Owner
./scripts/seed-demo.sh                 # optional: mock provider, models, prices, alias "balanced", tenant + key
curl http://localhost:8080/v1/chat/completions -H "Authorization: Bearer $KEY" -d '{"model":"balanced","messages":[{"role":"user","content":"hi"}]}'
```

The "setup time from a clean checkout" metric (PRD §13) is measured by timing this script on a clean machine.

`./scripts/seed-demo.sh --with-openai` also seeds the reference hosted Provider: an `openai` Provider with `credential_reference = env:OPENAI_API_KEY`, the Provider Models `gpt-5-nano` and `gpt-4o-mini`, and their input, cached-input and output Prices (cached $0.005 and $0.075 per 1M tokens, 0.1× and 0.5× of input) with `effective_from` set to the seed date. The seeded Prices are a snapshot, so check them against OpenAI's pricing page before relying on Estimated Cost. Without the flag, only the mock Provider is seeded and nothing calls a paid API.

## 3. Environment variables

`__` is the .NET configuration section separator. Every variable is validated at startup, and a missing or invalid value exits with code 78 and names the variable.

| Variable | Req | Used by | Default / notes |
|---|---|---|---|
| `MODELSWITCH__ConnectionStrings__Postgres` | ✔ | api, worker | `modelswitch_app` role |
| `MODELSWITCH__ConnectionStrings__PostgresMigrator` | ✔ | migrator | `modelswitch_migrator` role |
| `MODELSWITCH__ConnectionStrings__Valkey` | ✔ | api | |
| `MODELSWITCH__Security__MasterKey` | ✔ if any `db:` credential | api, worker | 32 bytes base64 |
| `MODELSWITCH__Security__ApiKeyPepper` | ✔ | api | ≥32 bytes base64 |
| `MODELSWITCH__Bootstrap__OwnerEmail` / `OwnerPassword` | first run | migrator | ignored once an Administrator exists |
| `MODELSWITCH__Hosting__EndpointGroups` | | api | `inference,admin` |
| `MODELSWITCH__Hosting__PublicBaseUrl` | ✔ | api | used for cookie and CSP settings |
| `MODELSWITCH__Limits__FailureMode` | | api | `Closed` |
| `MODELSWITCH__Limits__MaxRequestBodyBytes` | | api | `1048576` |
| `MODELSWITCH__Outbound__AllowedPrivateHosts` | | api, worker | `""`. Compose sets `ollama,mock-provider` |
| `MODELSWITCH__Retention__RequestDays` | | worker | `30` |
| `MODELSWITCH__Circuit__*` | | api, worker | see routing-and-resilience §5 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | | all | unset → no export |
| `OTEL_SERVICE_NAME` | | all | `modelswitch-api` / `-worker` |
| `MODELSWITCH__Telemetry__TraceUrlTemplate` | | api | e.g. `http://localhost:3000/explore?…{traceId}` |
| Provider env credentials (e.g. `OPENAI_API_KEY`) | | api, worker | referenced as `env:OPENAI_API_KEY` |

## 4. Database initialization

- `migrator` connects as the migrator role, creates the schemas and roles if they are missing, applies every module's migrations in order (`identity, registry, routing, config, usage, evaluation, jobs`), then bootstraps the Owner. It is idempotent: a second run applies nothing and exits 0.
- Development: `dotnet run --project src/ModelSwitch.Migrator`. Adding a migration: `dotnet ef migrations add <Name> --project src/Modules/ModelSwitch.<Module> --context <Module>DbContext`.
- Migrations must be backward-compatible with the previous app version, so they can be applied before api/worker are rolled (expand → migrate → contract).

## 5. Production notes (documented, not automated)

- Run ≥ 2 api replicas behind any L7 load balancer. Affinity is not needed.
- Back up PostgreSQL. Valkey needs no persistence.
- TLS is terminated at the proxy. Caddy can obtain certificates automatically when given a domain.
- Kubernetes manifests are **not** provided (PRD non-goal). Compose is the reference deployment.
