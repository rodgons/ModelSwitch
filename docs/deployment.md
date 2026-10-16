# Deployment and Configuration

This document covers FR-10 and NFR-05. The README quick-start is derived from it.

## 1. Compose services

`deploy/docker-compose.yml`, included by the root `compose.yaml` so that `docker compose …` runs from the repository root and reads the root `.env`:

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

Only `proxy` publishes a host port (default `8080`). PostgreSQL and Valkey publish ports (on `127.0.0.1`) only in a local `compose.override.yaml`: copy `deploy/compose.override.example.yaml` to the repository root, where it is gitignored and merged automatically.

The images are .NET 10 multi-stage builds. Runtime images use `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` and run as a non-root user. `HEALTHCHECK` uses `/health`.

## 2. Quick start (target experience, DoD #1)

```bash
git clone … && cd ModelSwitch
cp .env.example .env
./scripts/generate-secrets.sh          # fills master key, pepper, DB passwords in .env
# set MODELSWITCH__Bootstrap__OwnerEmail / OwnerPassword in .env
docker compose --profile demo up -d
open http://localhost:8080             # sign in as the Owner
./scripts/seed-demo.sh                 # optional: mock + Ollama providers, models, prices, alias "balanced", tenant + key
curl http://localhost:8080/v1/chat/completions -H "Authorization: Bearer $KEY" -d '{"model":"balanced","messages":[{"role":"user","content":"hi"}]}'
```

The "setup time from a clean checkout" metric (PRD §13) is measured by timing this script on a clean machine.

`./scripts/seed-demo.sh --with-openai` also seeds the reference hosted Provider: an `openai` Provider with `credential_reference = env:OPENAI_API_KEY`, the Provider Models `gpt-5-nano` and `gpt-4o-mini`, and their input, cached-input and output Prices (cached $0.005 and $0.075 per 1M tokens, 0.1× and 0.5× of input) with `effective_from` set to the seed date. The seeded Prices are a snapshot, so check them against OpenAI's pricing page before relying on Estimated Cost. Without the flag, only the mock and Ollama Providers are seeded and nothing calls a paid API.

**The demo (PRD §15) needs no paid account.** The baseline runs on the Mock Provider and Ollama: failures are injected through the mock's `/__scenarios`, the Fallback goes from a mock model to Ollama, usage comes from both, and the Comparison gets real quality numbers from Ollama. The seed pulls one **≤ 3B instruct model that supports Ollama's JSON-schema `format`**, pinned by exact tag (the tag is chosen and verified when `seed-demo.sh` is written). Its Prices are `0`, so its Estimated Cost is $0, which is correct for a local model. The reviewer downloads about 2 GB and runs it on CPU. An optional extended section uses `--with-openai` for a Comparison against a hosted model.

## 3. Environment variables

`__` is the .NET configuration section separator. Every variable is validated at startup, and a missing or invalid value exits with code 78 and names the variable.

| Variable | Req | Used by | Default / notes |
|---|---|---|---|
| `POSTGRES_MIGRATOR_PASSWORD` | ✔ | postgres container | Password of `modelswitch_migrator`, the bootstrap superuser that owns the schemas |
| `POSTGRES_APP_PASSWORD` | ✔ | migrator | Password the migrator gives `modelswitch_app` when it creates the role |
| `POSTGRES_RETENTION_PASSWORD` | | migrator | Password for `modelswitch_retention` when the migrator creates it. Unset: the role is created `NOLOGIN` |
| `POSTGRES_DB` | | postgres container | `modelswitch` |
| `MODELSWITCH__ConnectionStrings__Postgres` | ✔ | api, worker | `modelswitch_app` role |
| `MODELSWITCH__ConnectionStrings__PostgresMigrator` | ✔ | migrator | `modelswitch_migrator` role |
| `MODELSWITCH__ConnectionStrings__Valkey` | ✔ | api | |
| `MODELSWITCH__Security__MasterKey` | ✔ if any `db:` credential | api, worker | 32 bytes base64 |
| `MODELSWITCH__Security__MasterKeyVersion` | | api, worker | `1`. Version id of the master key, stored with each secret. Raise it when rotating |
| `MODELSWITCH__Security__PreviousMasterKey` / `PreviousMasterKeyVersion` | during rotation | api, worker | The old key and its version, so secrets written under it still decrypt until re-encrypted |
| `MODELSWITCH__Security__ApiKeyPepper` | ✔ | api, migrator (`seed` only) | ≥32 bytes base64 |
| `MODELSWITCH__Seed__TenantName` | for `seed` | migrator | Tenant to find or create. Also `seed --tenant <name>` |
| `MODELSWITCH__Seed__KeyName` | | migrator | Name of the new API Key, default `seed`. Also `seed --key-name <name>` |
| `MODELSWITCH__Bootstrap__OwnerEmail` / `OwnerPassword` | first run | migrator | ignored once an Administrator exists |
| `MODELSWITCH__Hosting__EndpointGroups` | | api | `inference,admin` |
| `MODELSWITCH__Hosting__PublicBaseUrl` | ✔ | api | used for cookie and CSP settings |
| `MODELSWITCH__Limits__FailureMode` | | api | `Closed` |
| `MODELSWITCH__Limits__MaxRequestBodyBytes` | | api | `1048576` |
| `MODELSWITCH__Limits__MaxMessages` | | api | `256` |
| `MODELSWITCH__Streaming__KeepAliveSeconds` | | api | `15`. Silence on an SSE stream after which a `: keep-alive` comment is sent |
| `MODELSWITCH__Outbound__AllowedPrivateHosts` | | api, worker | `""`. Compose sets `ollama,mock-provider` |
| `MODELSWITCH__Retention__RequestDays` | | worker | `30` |
| `MODELSWITCH__Circuit__*` | | api, worker | see routing-and-resilience §5 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | | all | unset → no export |
| `OTEL_SERVICE_NAME` | | all | `modelswitch-api` / `-worker` |
| `MODELSWITCH__Telemetry__TraceUrlTemplate` | | api | e.g. `http://localhost:3000/explore?…{traceId}` |
| Provider env credentials (e.g. `OPENAI_API_KEY`) | | api, worker | referenced as `env:OPENAI_API_KEY` |

## 4. Database initialization

- `migrator` connects as the migrator role, creates the schemas and roles if they are missing, applies every module's migrations in order (`identity, registry, routing, config, usage, evaluation, jobs`), then bootstraps the Owner. It is idempotent: a second run applies nothing and exits 0.
- Commands: `migrate` (default) and `seed`. `seed` migrates, finds or creates the Tenant, creates a new API Key with every Scope, and prints the plaintext key **once, alone on stdout** (logs go to stderr), so `KEY=$(migrator seed --tenant acme)` works. The key cannot be shown again. Run it again for another key. An unknown command exits 64.
- Roles: after the migrations, the migrator creates `modelswitch_app` (when `POSTGRES_APP_PASSWORD` is set) and `modelswitch_retention` (`NOLOGIN` unless `POSTGRES_RETENTION_PASSWORD` is set) if they are missing, then re-applies their grants on every run (idempotent, one transaction). Roles that already exist, for example ones a DBA created, are never altered, only granted privileges. If `modelswitch_app` is missing and no password is given, the migrator logs a warning and skips the grants. `modelswitch_app` gets `SELECT/INSERT/UPDATE/DELETE` on every module table except the EF migrations history, and loses `UPDATE`/`DELETE` on `audit_record` and `model_price`. `modelswitch_retention` gets `SELECT/DELETE` on the retention-managed tables only (data-model.md "Retention"). Creating roles needs `CREATEROLE`, which the bootstrap `modelswitch_migrator` role has in Compose.
- Development: `dotnet run --project src/ModelSwitch.Migrator`. Adding a migration: `dotnet ef migrations add <Name> --project src/Modules/ModelSwitch.<Module> --context <Module>DbContext --output-dir Infrastructure/Migrations` (after `dotnet tool restore`).
- Migrations must be backward-compatible with the previous app version, so they can be applied before api/worker are rolled (expand → migrate → contract).

## 5. Production notes (documented, not automated)

- Run ≥ 2 api replicas behind any L7 load balancer. Affinity is not needed.
- Back up PostgreSQL. Valkey needs no persistence.
- TLS is terminated at the proxy. Caddy can obtain certificates automatically when given a domain.
- Kubernetes manifests are **not** provided (PRD non-goal). Compose is the reference deployment.
