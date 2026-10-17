# Deployment and Configuration

This document covers FR-10 and NFR-05. The README quick-start is derived from it.

## 1. Compose services

`deploy/docker-compose.yml`, included by the root `compose.yaml` so that `docker compose …` runs from the repository root and reads the root `.env`:

| Service | Image | Profile | Depends on | Health check |
|---|---|---|---|---|
| `postgres` | `postgres:17` | default | — | `pg_isready` |
| `valkey` | `valkey/valkey:8` | default | — | `valkey-cli ping` |
| `migrator` | `modelswitch/migrator` | default | postgres healthy | runs to completion (exit 0) |
| `api` | `modelswitch/api` | default | migrator completed, valkey healthy | `/health` (image `HEALTHCHECK`) |
| `worker` | `modelswitch/worker` | default | migrator completed | none (no HTTP); `restart: unless-stopped` |
| `web` | `modelswitch/web` (static files served by Caddy) | default | — | `wget /` (image `HEALTHCHECK`) |
| `proxy` | `caddy:2.11` with `deploy/Caddyfile` | default | api healthy, web | — |
| `mock-provider` | `modelswitch/mock-provider` | `demo` | — | `/healthz` (image `HEALTHCHECK`) |
| `ollama` | `ollama/ollama:0.40.5` | `ollama` | — | — |
| `otel-lgtm` | `grafana/otel-lgtm:0.35.0` (publishes `127.0.0.1:3000` Grafana and `127.0.0.1:4317/4318` OTLP) | `observability` | — | — |

Only `proxy` publishes a host port (`MODELSWITCH_HTTP_PORT`, default `8080`), apart from the optional `otel-lgtm`, which publishes Grafana and OTLP on `127.0.0.1`. PostgreSQL and Valkey publish ports (on `127.0.0.1`) only in a local `compose.override.yaml`: copy `deploy/compose.override.example.yaml` to the repository root, where it is gitignored and merged automatically. The mock and Ollama are reachable only on the Compose network.

**Proxy routes** (`deploy/Caddyfile`): `/v1/*`, `/admin/*`, `/health` and `/ready` go to `api:8080` (responses are flushed immediately, so SSE streams chunk by chunk); everything else goes to `web:8080`. The proxy adds the security headers of [security.md §7](security.md#7-http-hardening). `/openapi/v1.json` is not routed: it is a development artefact.

**Images.** `deploy/docker/dotnet.Dockerfile` is one multi-stage build with a target per process (`api`, `worker`, `migrator`, `mock-provider`): the .NET 10 SDK restores and publishes all four in one shared stage, and every runtime image is `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` (the worker and migrator need the ASP.NET Core shared framework because the modules reference it), running as the non-root `app` user. `deploy/docker/web.Dockerfile` builds the dashboard with Node 24 and pnpm, and serves `dist/` with Caddy (`deploy/docker/web.Caddyfile`: long-lived caching for `/assets/*`, `no-cache` for `index.html`, unknown paths fall back to `index.html` for history-mode routes). The build context is the repository root; `.dockerignore` keeps it to the sources. CI builds every image on each push (no push to a registry).

**Health checks without a shell.** Chiseled images have no shell, curl or wget, so the api and mock images run their own entry point as the probe: `HEALTHCHECK CMD ["dotnet", "ModelSwitch.Api.dll", "--health-check"]`. With that lone argument the program GETs `http://127.0.0.1:<first ASPNETCORE_HTTP_PORTS port>/health` (`/healthz` for the mock) and exits 0 on 2xx, 1 otherwise, without building the host (`SharedKernel.Hosting.HealthProbe`). The worker has no HTTP endpoint and therefore no health check; Compose restarts it if it exits.

**HTTPS and the proxy.** The Administrator session and antiforgery cookies are `Secure` and `__Host-` prefixed (ADR-0011), and ASP.NET Core refuses to issue the antiforgery cookie on a request it sees as plain HTTP. The api therefore honours `X-Forwarded-Proto` and `X-Forwarded-For` from loopback and private-network peers (the Compose network, never the Internet), and the local proxy sends `X-Forwarded-Proto: https` because browsers treat `http://localhost` as a secure context and accept those cookies there (Chrome and Firefox do; curl does too). Any host name other than `localhost` must be served over HTTPS: see section 5.

Npgsql's GSS encryption probe is turned off in the connection strings (`GSS Encryption Mode=Disable`), because the chiseled images have no Kerberos library.

## 2. Quick start (DoD #1)

Needs Docker with Compose v2, plus `curl` and `jq` for the seed script. About 1 GB of images and, with Ollama, 1 GB more for the model; the stack and the model fit a 4 CPU / 4 GB Docker VM.

```bash
git clone … && cd ModelSwitch
cp .env.example .env
./scripts/generate-secrets.sh          # fills the DB passwords, master key and pepper in .env (idempotent)
# set MODELSWITCH__Bootstrap__OwnerEmail / OwnerPassword (12+ characters) in .env
docker compose --profile demo up -d --build      # add `--profile ollama` for the local model
open http://localhost:8080             # sign in as the Owner
KEY=$(./scripts/seed-demo.sh)          # optional: mock (+ Ollama) providers, models, prices, alias "balanced", Tenant "demo" + key
curl http://localhost:8080/v1/chat/completions -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"balanced","messages":[{"role":"user","content":"hi"}]}'
docker compose --profile demo --profile ollama down -v   # remove everything, including the database volume
```

The "setup time from a clean checkout" metric (PRD §13) is measured by timing this script on a clean machine. The first `--build` takes a few minutes (about 3.5 minutes on a 4-CPU VM).

`seed-demo.sh` signs in through the proxy as the Owner from `.env` (or the file in `MODELSWITCH_ENV_FILE`; `scripts/e2e.sh` seeds its separate `modelswitch-e2e` stack that way, [testing.md §1](testing.md#1-projects-and-what-goes-where)) and uses the admin API, so it needs the stack running. It prints the new API Key **alone on stdout** (progress goes to stderr) and can be run again: the Providers, models, Prices, policy and Tenant are reused, and each run creates one more key. It seeds:

- Provider `mock` (`openai_compatible`, `http://mock-provider:8080/v1`) with the Provider Models `mock-echo` (echoes the last user message), `mock-fixed` and `mock-judge`, priced at a few cents per 1M tokens so Estimated Cost is visible.
- When the `ollama` container is running, the pinned local model **`qwen2.5:1.5b-instruct-q4_K_M`** (`ollama pull` inside the container, about 1 GB) as Provider `ollama` (`http://ollama:11434`, 120 s timeout) with Prices of `0`, so its Estimated Cost is $0, which is correct for a local model. It is ≤ 3B, instruct-tuned and follows Ollama's JSON-schema `format` (checked with an extraction schema). Its context window is declared as Ollama's default `num_ctx`, 4096. `MODELSWITCH_DEMO_OLLAMA_MODEL` overrides the tag; `--no-ollama` skips it.
- Routing Policy and alias **`balanced`**: `mock/mock-echo` first, falling back to the Ollama model, or to `mock/mock-fixed` without Ollama (re-running the script after starting Ollama saves a new Policy Version). The policy allows 90 s per Attempt, since CPU inference is slow.
- Tenant **`demo`** allowed every alias, and an API Key named `demo-<UTC timestamp>` with the `inference` and `models:read` Scopes.

To see the Fallback, make `mock-echo` fail through the mock's control endpoint (it is not published, so call it from a throwaway container on the Compose network), then send a request to `balanced`; the response reports `"attempts": 3` and the Requests screen shows the Route Strip:

```bash
docker run --rm --network modelswitch_default curlimages/curl:8.11.1 -s http://mock-provider:8080/__scenarios \
  -H 'Content-Type: application/json' -d '{"model":"mock-echo","fault":"status=503;times=2"}'
```

`./scripts/seed-demo.sh --with-openai` also seeds the reference hosted Provider: an `openai` Provider with `credential_reference = env:OPENAI_API_KEY`, the Provider Models `gpt-5-nano` and `gpt-4o-mini`, and their input, cached-input and output Prices (cached $0.005 and $0.075 per 1M tokens, 0.1× and 0.5× of input) with `effective_from` set to the seed time. Set `OPENAI_API_KEY` in `.env` and recreate `api` and `worker` (`docker compose up -d api worker`) for it to be used. The seeded Prices are a snapshot, so check them against OpenAI's pricing page before relying on Estimated Cost. Without the flag, only the mock and Ollama Providers are seeded and nothing calls a paid API.

An API Key can also be created without the admin API: `docker compose run --rm -T migrator seed --tenant acme` prints one on stdout (section 4).

**The demo (PRD §15) needs no paid account.** The baseline runs on the Mock Provider and Ollama: failures are injected through the mock's `/__scenarios`, the Fallback goes from a mock model to Ollama, usage comes from both, and the Comparison gets real quality numbers from Ollama. An optional extended section uses `--with-openai` for a Comparison against a hosted model.

## 3. Environment variables

`__` is the .NET configuration section separator. Every variable is validated at startup, and a missing or invalid value exits with code 78 and names the variable.

In Compose, `.env` is the only place values are set. `deploy/docker-compose.yml` passes each container only the variables it uses: required ones fail `docker compose` with a message when unset, and optional ones are listed without a value, so they reach the container only when `.env` sets them. `.env.example` writes the connection strings with `${POSTGRES_…_PASSWORD}` references, which Compose expands, so each password appears once; `generate-secrets.sh` generates hex passwords, which need no escaping inside a connection string.

| Variable | Req | Used by | Default / notes |
|---|---|---|---|
| `POSTGRES_MIGRATOR_PASSWORD` | ✔ | postgres container | Password of `modelswitch_migrator`, the bootstrap superuser that owns the schemas |
| `POSTGRES_APP_PASSWORD` | ✔ | migrator | Password the migrator gives `modelswitch_app` when it creates the role |
| `POSTGRES_RETENTION_PASSWORD` | | migrator | Password for `modelswitch_retention` when the migrator creates it. Unset: the role is created `NOLOGIN`. `.env.example` sets it and `generate-secrets.sh` fills it |
| `POSTGRES_DB` | | postgres container | `modelswitch` |
| `MODELSWITCH_HTTP_PORT` | | Compose (`proxy`) | `8080`. The published port. Change `PublicBaseUrl` with it |
| `MODELSWITCH__ConnectionStrings__Postgres` | ✔ | api, worker | `modelswitch_app` role |
| `MODELSWITCH__ConnectionStrings__PostgresMigrator` | ✔ | migrator | `modelswitch_migrator` role |
| `MODELSWITCH__ConnectionStrings__Valkey` | ✔ | api | |
| `MODELSWITCH__ConnectionStrings__PostgresRetention` | | worker | `modelswitch_retention` role (needs `POSTGRES_RETENTION_PASSWORD`). Unset: retention runs as the app role and keeps Audit Records |
| `MODELSWITCH__Security__MasterKey` | ✔ if any `db:` credential | api, worker | 32 bytes base64 |
| `MODELSWITCH__Security__MasterKeyVersion` | | api, worker | `1`. Version id of the master key, stored with each secret. Raise it when rotating |
| `MODELSWITCH__Security__PreviousMasterKey` / `PreviousMasterKeyVersion` | during rotation | api, worker | The old key and its version, so secrets written under it still decrypt until re-encrypted |
| `MODELSWITCH__Security__ApiKeyPepper` | ✔ | api, migrator (`seed` only) | ≥32 bytes base64 |
| `MODELSWITCH__Seed__TenantName` | for `seed` | migrator | Tenant to find or create. Also `seed --tenant <name>` |
| `MODELSWITCH__Seed__KeyName` | | migrator | Name of the new API Key, default `seed`. Also `seed --key-name <name>` |
| `MODELSWITCH__Bootstrap__OwnerEmail` / `OwnerPassword` | first run | migrator | ignored once an Administrator exists |
| `MODELSWITCH__Hosting__EndpointGroups` | | api | `inference,admin` |
| `MODELSWITCH__Hosting__PublicBaseUrl` | ✔ | api | used for cookie and CSP settings |
| `MODELSWITCH__Limits__FailureMode` | | api | `Closed`. `Open` lets requests through when Valkey is down (and leaves Valkey out of `/ready`) |
| `MODELSWITCH__Limits__ValkeyTimeoutMs` | | api | `50`. Budget of one limits call; a slower call counts as Valkey being unavailable |
| `MODELSWITCH__Limits__MaxRequestBodyBytes` | | api | `1048576` |
| `MODELSWITCH__Limits__MaxMessages` | | api | `256` |
| `MODELSWITCH__Streaming__KeepAliveSeconds` | | api | `15`. Silence on an SSE stream after which a `: keep-alive` comment is sent |
| `MODELSWITCH__Outbound__AllowedPrivateHosts` | | api, worker | `""`. Compose sets `ollama,mock-provider` |
| `MODELSWITCH__Retention__RequestDays` | | worker | `30` (1 to 400). Request and Attempt rows, and their monthly partitions, are removed after this many days |
| `MODELSWITCH__Circuit__*` | | api, worker | see routing-and-resilience §5 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | | all | unset → no export |
| `OTEL_SERVICE_NAME` | | all | `modelswitch-api` / `-worker` |
| `MODELSWITCH__Telemetry__TraceSampleRatio` | | api, worker | `1.0`. Parent-based head sampling ratio, 0 to 1 (observability.md section 2) |
| `MODELSWITCH__Telemetry__TraceUrlTemplate` | | api | e.g. `http://localhost:3000/explore?…{traceId}` |
| Provider env credentials (e.g. `OPENAI_API_KEY`) | | api, worker | referenced as `env:OPENAI_API_KEY`. Compose passes `OPENAI_API_KEY` only; add others to the `x-gateway-environment` block |
| `MODELSWITCH_URL`, `MODELSWITCH_DEMO_OLLAMA_MODEL` | | `seed-demo.sh` | The gateway URL (default `http://localhost:$MODELSWITCH_HTTP_PORT`) and the Ollama model tag (default `qwen2.5:1.5b-instruct-q4_K_M`) |

## 4. Database initialization

- `migrator` connects as the migrator role, creates the schemas and roles if they are missing, applies every module's migrations in order (`identity, registry, routing, usage, evaluation, config, jobs`), then bootstraps the Owner. It is idempotent: a second run applies nothing and exits 0.
- Commands: `migrate` (default) and `seed` (in Compose: `docker compose run --rm -T migrator seed --tenant acme`). `seed` migrates, finds or creates the Tenant, creates a new API Key with every Scope, and prints the plaintext key **once, alone on stdout** (logs go to stderr), so `KEY=$(migrator seed --tenant acme)` works. The key cannot be shown again. Run it again for another key. An unknown command exits 64.
- Roles: after the migrations, the migrator creates `modelswitch_app` (when `POSTGRES_APP_PASSWORD` is set) and `modelswitch_retention` (`NOLOGIN` unless `POSTGRES_RETENTION_PASSWORD` is set) if they are missing, then re-applies their grants on every run (idempotent, one transaction). Roles that already exist, for example ones a DBA created, are never altered, only granted privileges. If `modelswitch_app` is missing and no password is given, the migrator logs a warning and skips the grants. `modelswitch_app` gets `SELECT/INSERT/UPDATE/DELETE` on every module table except the EF migrations history, and loses `UPDATE`/`DELETE` on the append-only tables (`audit_record`, `model_price`, `routing_policy_version`, `dataset_version`, `eval_case`, `prompt_version`). `modelswitch_retention` gets `SELECT/DELETE` on the retention-managed tables only (data-model.md "Retention"). Creating roles needs `CREATEROLE`, which the bootstrap `modelswitch_migrator` role has in Compose.
- Development: `dotnet run --project src/ModelSwitch.Migrator`. Adding a migration: `dotnet ef migrations add <Name> --project src/Modules/ModelSwitch.<Module> --context <Module>DbContext --output-dir Infrastructure/Migrations` (after `dotnet tool restore`).
- Migrations must be backward-compatible with the previous app version, so they can be applied before api/worker are rolled (expand → migrate → contract).

## 5. Production notes (documented, not automated)

- Run ≥ 2 api replicas behind any L7 load balancer. Affinity is not needed.
- Back up PostgreSQL. Valkey needs no persistence.
- TLS is terminated at the proxy. Caddy can obtain certificates automatically when given a domain: in `deploy/Caddyfile`, replace `:8080` with the domain and remove `auto_https off`, publish ports 80 and 443 on `proxy` (the `caddy-data` volume keeps the certificates), and set `PublicBaseUrl` to the `https://` URL. Serving the dashboard over plain HTTP on any host other than `localhost` does not work: browsers refuse the `Secure` session cookie there.
- Kubernetes manifests are **not** provided (PRD non-goal). Compose is the reference deployment.
