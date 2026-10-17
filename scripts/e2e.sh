#!/usr/bin/env bash
# Runs the dashboard's Playwright smoke and axe suite (web/e2e, docs/testing.md section 1) against the full Compose stack.
# The stack is a Compose project of its own, `modelswitch-e2e`, with its own `.env.e2e`, image names (deploy/compose.e2e.yaml),
# volumes and published port, so it runs next to a stack started from the repository root without touching it.
#
#   ./scripts/e2e.sh [all]       up, seed, test, then down -v (always, also on failure)
#   ./scripts/e2e.sh up          build and start the stack, then seed it (mock Provider, alias "balanced")
#   ./scripts/e2e.sh test [ARG…] run Playwright against the running stack; ARGs go to `playwright test` (e.g. --ui, --debug)
#   ./scripts/e2e.sh down        stop the stack and delete its volumes
#
# Environment: MODELSWITCH_E2E_PORT (default 8081), the proxy's published port. Only the default services and the `demo`
# profile (the Mock Provider) run: no Ollama, no observability. Needs bash, curl, jq, Docker Compose v2, pnpm, and the
# Playwright browser (`cd web && pnpm exec playwright install chromium`).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
env_file="$root/.env.e2e"
port="${MODELSWITCH_E2E_PORT:-8081}"
base_url="http://localhost:$port"

compose() {
  docker compose -p modelswitch-e2e --env-file "$env_file" \
    -f "$root/deploy/docker-compose.yml" -f "$root/deploy/compose.e2e.yaml" --profile demo "$@"
}

value() {
  awk -v key="$1" 'index($0, key "=") == 1 { value = substr($0, length(key) + 2) } END { print value }' "$env_file"
}

set_value() {
  local key="$1" new="$2" tmp
  tmp="$(mktemp "${env_file}.XXXXXX")"
  if grep -q "^$key=" "$env_file"; then
    awk -v key="$key" -v value="$new" 'index($0, key "=") == 1 { print key "=" value; next } { print }' "$env_file" > "$tmp"
  else
    cat "$env_file" > "$tmp"
    printf '%s=%s\n' "$key" "$new" >> "$tmp"
  fi
  cat "$tmp" > "$env_file"
  rm -f "$tmp"
}

prepare_env() {
  "$root/scripts/generate-secrets.sh" "$env_file" >/dev/null
  set_value MODELSWITCH_HTTP_PORT "$port"
  set_value MODELSWITCH__Hosting__PublicBaseUrl "$base_url"
  [ -n "$(value MODELSWITCH__Bootstrap__OwnerEmail)" ] || set_value MODELSWITCH__Bootstrap__OwnerEmail owner@e2e.modelswitch.test
  # The Owner is created on the first migrator run only, so the password is generated once and kept with the volumes.
  [ -n "$(value MODELSWITCH__Bootstrap__OwnerPassword)" ] || set_value MODELSWITCH__Bootstrap__OwnerPassword "$(openssl rand -hex 16)"
}

up() {
  prepare_env
  compose up -d --build
  MODELSWITCH_ENV_FILE="$env_file" MODELSWITCH_URL="$base_url" "$root/scripts/seed-demo.sh" --no-ollama >/dev/null
}

run_tests() {
  [ -f "$env_file" ] || { echo "error: $env_file not found; run ./scripts/e2e.sh up first" >&2; exit 1; }
  cd "$root/web"
  [ -d node_modules ] || pnpm install --frozen-lockfile
  E2E_BASE_URL="$base_url" \
    E2E_OWNER_EMAIL="$(value MODELSWITCH__Bootstrap__OwnerEmail)" \
    E2E_OWNER_PASSWORD="$(value MODELSWITCH__Bootstrap__OwnerPassword)" \
    pnpm exec playwright test "$@"
}

down() {
  compose down -v --remove-orphans
}

command="${1:-all}"
[ $# -eq 0 ] || shift
case "$command" in
  up) up ;;
  test) run_tests "$@" ;;
  down) down ;;
  all)
    trap down EXIT
    up
    status=0
    (run_tests "$@") || status=$?
    if [ "$status" -ne 0 ]; then
      mkdir -p "$root/web/test-results"
      compose logs --no-color > "$root/web/test-results/compose.log" 2>&1 || true
      echo "Compose logs: web/test-results/compose.log" >&2
    fi
    exit "$status"
    ;;
  -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//' ;;
  *) echo "Unknown command: $command (use all, up, test, down)" >&2; exit 64 ;;
esac
