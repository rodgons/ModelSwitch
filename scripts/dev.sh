#!/usr/bin/env bash
# Development loop (docs/deployment.md section 2, "Development loop"): PostgreSQL and Valkey run in Compose, the gateway runs on
# the host with hot reload.
#   - api        dotnet watch, https://localhost:5081 (the dev certificate: its __Host- cookies need HTTPS, ADR-0011)
#   - worker     dotnet run (restart `make dev` after changing it)
#   - mock       the Mock Provider, http://localhost:8091
#   - web        Vite, http://localhost:5173, proxying /admin, /v1, /health and /ready to the api
# It uses its own database, modelswitch_dev, so the Compose stack's catalog (whose Providers are Compose host names) is untouched.
# The first run seeds the demo catalog against the local mock and prints an API Key. Ctrl-C stops everything.
#
#   ./scripts/dev.sh
#
# Needs: docker compose, the .NET SDK, pnpm, curl, jq, and a trusted dev certificate (dotnet dev-certs https --trust).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
env_file="$root/.env"
dev_database=modelswitch_dev
log() { printf '%s\n' "$*" >&2; }
die() { log "error: $*"; exit 1; }

[ -f "$env_file" ] || die "$env_file not found. Run: make setup"
[ -f "$root/compose.override.yaml" ] || die "compose.override.yaml not found (it publishes PostgreSQL and Valkey). Run: make setup"
command -v dotnet >/dev/null || die "dotnet not found. Install the .NET SDK"
# `dotnet run` starts each app's apphost, which finds the runtime only through DOTNET_ROOT or /usr/local/share/dotnet;
# version-manager installs (asdf, mise, dotnet-install) live elsewhere, so point it at the SDK in use.
if [ -z "${DOTNET_ROOT:-}" ] || [ ! -d "$DOTNET_ROOT/shared/Microsoft.NETCore.App" ]; then
  sdk_dir="$(dotnet --list-sdks | tail -n 1 | sed -E 's/.*\[(.*)\]$/\1/')"
  export DOTNET_ROOT="$(dirname "$sdk_dir")"
fi
dotnet dev-certs https --check --trust >/dev/null || die "no trusted dev certificate. Run: dotnet dev-certs https --trust"

env_value() {
  awk -v key="$1" 'index($0, key "=") == 1 { value = substr($0, length(key) + 2) } END { print value }' "$env_file"
}

migrator_password="$(env_value POSTGRES_MIGRATOR_PASSWORD)"
app_password="$(env_value POSTGRES_APP_PASSWORD)"
retention_password="$(env_value POSTGRES_RETENTION_PASSWORD)"
postgres="Host=localhost;Database=$dev_database;GSS Encryption Mode=Disable"

# The settings the Compose services read from .env, pointed at localhost and the dev database.
export ASPNETCORE_ENVIRONMENT=Development
export MODELSWITCH__ConnectionStrings__PostgresMigrator="$postgres;Username=modelswitch_migrator;Password=$migrator_password;Maximum Pool Size=5"
export MODELSWITCH__ConnectionStrings__Postgres="$postgres;Username=modelswitch_app;Password=$app_password;Maximum Pool Size=40"
export MODELSWITCH__ConnectionStrings__PostgresRetention="$postgres;Username=modelswitch_retention;Password=$retention_password;Maximum Pool Size=5"
export MODELSWITCH__ConnectionStrings__Valkey=localhost:6379
export MODELSWITCH__Security__MasterKey="$(env_value MODELSWITCH__Security__MasterKey)"
export MODELSWITCH__Security__ApiKeyPepper="$(env_value MODELSWITCH__Security__ApiKeyPepper)"
export MODELSWITCH__Bootstrap__OwnerEmail="$(env_value MODELSWITCH__Bootstrap__OwnerEmail)"
export MODELSWITCH__Bootstrap__OwnerPassword="$(env_value MODELSWITCH__Bootstrap__OwnerPassword)"
export MODELSWITCH__Hosting__PublicBaseUrl=http://localhost:5173
# The local mock is on loopback, which outbound calls refuse unless the host is allowed (security.md, SSRF).
export MODELSWITCH__Outbound__AllowedPrivateHosts=localhost
export POSTGRES_APP_PASSWORD="$app_password"
export POSTGRES_RETENTION_PASSWORD="$retention_password"
openai_key="$(env_value OPENAI_API_KEY)"
[ -z "$openai_key" ] || export OPENAI_API_KEY="$openai_key"

cd "$root"
log "Starting PostgreSQL and Valkey …"
docker compose up -d --wait postgres valkey
if ! docker compose exec -T postgres psql -U modelswitch_migrator -d postgres -tAc \
    "SELECT 1 FROM pg_database WHERE datname = '$dev_database'" | grep -q 1; then
  docker compose exec -T postgres createdb -U modelswitch_migrator "$dev_database"
  first_run=true
else
  first_run=false
fi

log "Building and migrating …"
dotnet build ModelSwitch.slnx -v quiet -nologo
dotnet run --no-build --project src/ModelSwitch.Migrator
[ -d web/node_modules ] || (cd web && pnpm install)

# Each process's output is prefixed with its name; Ctrl-C (or any process exiting) stops the rest.
pids=()
run() {
  local name="$1"; shift
  ( "$@" 2>&1 | sed -u "s/^/[$name] /" ) &
  pids+=("$!")
}
stop() {
  trap - INT TERM EXIT
  log "Stopping …"
  pkill -P "$$" 2>/dev/null || true
  for pid in ${pids[@]+"${pids[@]}"}; do pkill -P "$pid" 2>/dev/null || true; done
  wait 2>/dev/null || true
}
trap stop INT TERM EXIT

run mock env ASPNETCORE_URLS=http://localhost:8091 dotnet run --no-build --project tools/ModelSwitch.MockProvider
run worker dotnet run --no-build --project src/ModelSwitch.Worker
run api env ASPNETCORE_URLS=https://localhost:5081 dotnet watch --non-interactive --project src/ModelSwitch.Api
run web pnpm --dir web dev --strictPort

if [ "$first_run" = true ]; then
  key="$(MODELSWITCH_URL=http://localhost:5173 MODELSWITCH_DEMO_MOCK_URL=http://localhost:8091/v1 ./scripts/seed-demo.sh --no-ollama 2>/dev/null)" \
    && log "Seeded the dev catalog. API Key: $key" \
    || log "Seeding failed; run it by hand: MODELSWITCH_URL=http://localhost:5173 MODELSWITCH_DEMO_MOCK_URL=http://localhost:8091/v1 ./scripts/seed-demo.sh --no-ollama"
fi
log "Dashboard: http://localhost:5173 (Owner $MODELSWITCH__Bootstrap__OwnerEmail, password in .env). Ctrl-C stops."

# Returns when any process exits (bash 3.2, macOS's, has no `wait -n`).
while for pid in "${pids[@]}"; do kill -0 "$pid" 2>/dev/null || exit 1; done; do sleep 1; done
