#!/usr/bin/env bash
# Seeds the demo catalog through the admin API, via the proxy (docs/deployment.md section 2):
#   - Provider "mock" (the Mock Provider, `demo` profile) with mock-echo and mock-fixed, priced so Estimated Cost is visible;
#   - Provider "ollama" with one small local model, pulled into the `ollama` container, when the `ollama` profile is running;
#   - Routing Policy "balanced" and alias "balanced": mock-echo first, falling back to the Ollama model (or, without Ollama,
#     to mock-fixed). docs/deployment.md section 2 shows how to make mock-echo fail through the mock's /__scenarios;
#   - Tenant "demo" (or MODELSWITCH_DEMO_TENANT) allowed every alias, and a new API Key with the inference and models:read Scopes.
# The plaintext key is printed once, alone on stdout, so `KEY=$(./scripts/seed-demo.sh)` works; progress goes to stderr.
# Safe to run again: existing Providers, models, Prices, the policy and the Tenant are reused, and a new key is created.
#
#   ./scripts/seed-demo.sh [--with-openai] [--with-openrouter] [--no-ollama]
#
#   --with-openai  also seeds the hosted reference Provider "openai" (env:OPENAI_API_KEY; set OPENAI_API_KEY in .env and
#                  recreate api and worker). Without it nothing calls a paid API.
#   --with-openrouter  also seeds the hosted Provider "openrouter" with openai/gpt-4.1-nano, and alias "openrouter" (that model,
#                  falling back to the mock). The key is OPENROUTER_API_KEY from the environment or .env, stored encrypted in
#                  the catalog, so the containers need no restart. Calls a paid API.
#   --no-ollama    skip Ollama even when its container runs.
#
# Environment: MODELSWITCH_URL (default http://localhost:$MODELSWITCH_HTTP_PORT, or :8080), MODELSWITCH_DEMO_MOCK_URL (the mock's base URL,
# default http://mock-provider:8080/v1; scripts/dev.sh runs it on localhost), MODELSWITCH_DEMO_OLLAMA_MODEL
# (default below), MODELSWITCH_ENV_FILE (default .env, read for the port and the Owner credentials: MODELSWITCH__Bootstrap__OwnerEmail
# / OwnerPassword), MODELSWITCH_DEMO_TENANT (the Tenant's name, default demo; load/run.sh seeds several).
# Needs: bash, curl, jq, and docker compose (for Ollama only).
set -euo pipefail

# The demo's local model: ≤ 3B, instruct-tuned, supports Ollama's JSON-schema `format` (verified with an extraction schema).
# Pinned by exact tag. About 1 GB to download and ~1.2 GB of RAM, so it fits next to the stack in a 4 GB Docker VM.
default_ollama_model="qwen2.5:1.5b-instruct-q4_K_M"

with_openai=false
with_openrouter=false
use_ollama=auto
for argument in "$@"; do
  case "$argument" in
    --with-openai) with_openai=true ;;
    --with-openrouter) with_openrouter=true ;;
    --no-ollama) use_ollama=false ;;
    -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $argument (use --with-openai, --with-openrouter, --no-ollama)" >&2; exit 64 ;;
  esac
done

root="$(cd "$(dirname "$0")/.." && pwd)"
env_file="${MODELSWITCH_ENV_FILE:-$root/.env}"
log() { printf '%s\n' "$*" >&2; }
die() { log "error: $*"; exit 1; }

for tool in curl jq; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is required"
done
[ -f "$env_file" ] || die "$env_file not found. Run: cp .env.example .env && ./scripts/generate-secrets.sh"

env_value() {
  awk -v key="$1" 'index($0, key "=") == 1 { value = substr($0, length(key) + 2) } END { print value }' "$env_file"
}

port="$(env_value MODELSWITCH_HTTP_PORT)"
base_url="${MODELSWITCH_URL:-http://localhost:${port:-8080}}"
owner_email="$(env_value MODELSWITCH__Bootstrap__OwnerEmail)"
owner_password="$(env_value MODELSWITCH__Bootstrap__OwnerPassword)"
ollama_model="${MODELSWITCH_DEMO_OLLAMA_MODEL:-$default_ollama_model}"
tenant_name="${MODELSWITCH_DEMO_TENANT:-demo}"
[ -n "$owner_email" ] && [ -n "$owner_password" ] || die "set MODELSWITCH__Bootstrap__OwnerEmail and OwnerPassword in .env"
openrouter_key="${OPENROUTER_API_KEY:-$(env_value OPENROUTER_API_KEY)}"
[ "$with_openrouter" = false ] || [ -n "$openrouter_key" ] || die "--with-openrouter needs OPENROUTER_API_KEY (in .env or the environment)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
jar="$work/cookies"
xsrf=""

# api METHOD PATH [JSON]: prints the response body; fails with the Problem Details on a non-2xx status.
api() {
  local method="$1" path="$2" body="${3:-}" status
  local args=(-sS -o "$work/body" -w '%{http_code}' -X "$method" -b "$jar" -c "$jar" -H "X-XSRF-TOKEN: $xsrf")
  if [ -n "$body" ]; then
    args+=(-H 'Content-Type: application/json' --data-binary "$body")
  fi
  status="$(curl "${args[@]}" "$base_url$path")" || die "$method $path: cannot reach $base_url"
  case "$status" in
    2??) cat "$work/body" ;;
    *) log "$method $path answered $status:"; cat "$work/body" >&2; log ""; return 1 ;;
  esac
}

log "Waiting for $base_url/ready …"
for _ in $(seq 1 60); do
  if curl -fsS -o /dev/null "$base_url/ready" 2>/dev/null; then break; fi
  sleep 2
done
curl -fsS -o /dev/null "$base_url/ready" || die "the gateway is not ready at $base_url (docker compose ps)"

# Sign in as the Owner (ADR-0011): the anonymous antiforgery token, then the session, which returns a session-bound token.
xsrf="$(api GET /admin/antiforgery-token | jq -r .token)"
xsrf="$(api POST /admin/session "$(jq -n --arg email "$owner_email" --arg password "$owner_password" '{email: $email, password: $password}')" \
  | jq -r .antiforgeryToken)" || die "sign-in failed for $owner_email"
log "Signed in as $owner_email"

# provider NAME ADAPTER ENDPOINT TIMEOUT_MS [CREDENTIAL_JSON] → Provider id
provider() {
  local name="$1" adapter="$2" endpoint="$3" timeout="$4" credential="${5:-null}" id
  id="$(api GET '/admin/providers?limit=200' | jq -r --arg name "$name" '.items[] | select(.name == $name) | .id')"
  if [ -z "$id" ]; then
    id="$(api POST /admin/providers "$(jq -n --arg name "$name" --arg adapter "$adapter" --arg endpoint "$endpoint" \
      --argjson timeout "$timeout" --argjson credential "$credential" \
      '{name: $name, adapterType: $adapter, endpoint: $endpoint, requestTimeoutMs: $timeout}
       + (if $credential == null then {} else {credential: $credential} end)')" | jq -r .id)"
    log "Created Provider $name"
  else
    log "Provider $name exists"
  fi
  printf '%s' "$id"
}

# model PROVIDER_ID MODEL_ID CAPABILITIES_JSON INPUT CACHED_INPUT OUTPUT → Provider Model id (with a Price in force)
model() {
  local provider_id="$1" model_id="$2" capabilities="$3" input="$4" cached="$5" output="$6" id
  id="$(api GET "/admin/providers/$provider_id/models?limit=200" | jq -r --arg model "$model_id" '.items[] | select(.providerModelId == $model) | .id')"
  if [ -z "$id" ]; then
    id="$(api POST "/admin/providers/$provider_id/models" "$(jq -n --arg model "$model_id" --argjson capabilities "$capabilities" \
      '{providerModelId: $model, capabilities: $capabilities}')" | jq -r .id)"
    log "  created model $model_id"
  fi
  if [ "$(api GET "/admin/models/$id/prices" | jq '[.items[]] | length')" = "0" ]; then
    api POST "/admin/models/$id/prices" "$(jq -n --arg input "$input" --arg cached "$cached" --arg output "$output" \
      '{inputPerMillion: ($input | tonumber), cachedInputPerMillion: ($cached | tonumber), outputPerMillion: ($output | tonumber), currency: "USD"}')" >/dev/null
    log "  priced $model_id: \$$input in / \$$cached cached / \$$output out per 1M tokens"
  fi
  printf '%s' "$id"
}

capabilities() { # STREAMING JSON_OBJECT JSON_SCHEMA CONTEXT MAX_OUTPUT [TOKENIZER]
  jq -n --argjson streaming "$1" --argjson jsonObject "$2" --argjson jsonSchema "$3" --argjson context "$4" --argjson output "$5" \
    --arg tokenizer "${6:-}" \
    '{streaming: $streaming, jsonObject: $jsonObject, jsonSchema: $jsonSchema, contextWindow: $context, maxOutputTokens: $output,
      parameters: ["temperature", "top_p", "seed", "stop"]} + (if $tokenizer == "" then {} else {tokenizer: $tokenizer} end)'
}

# --- Mock Provider (compose profile `demo`) ---
mock="$(provider mock openai_compatible "${MODELSWITCH_DEMO_MOCK_URL:-http://mock-provider:8080/v1}" 30000)"
model "$mock" mock-echo "$(capabilities true false false 32768 4096 o200k_base)" 0.15 0.075 0.60 >/dev/null
model "$mock" mock-fixed "$(capabilities true false false 32768 4096 o200k_base)" 0.10 0.05 0.40 >/dev/null
model "$mock" mock-judge "$(capabilities true true true 32768 4096 o200k_base)" 0.10 0.05 0.40 >/dev/null
fallback="mock/mock-fixed"

# --- Ollama (compose profile `ollama`) ---
if [ "$use_ollama" = auto ]; then
  if command -v docker >/dev/null 2>&1 && [ -n "$(cd "$root" && docker compose ps --status running -q ollama 2>/dev/null)" ]; then
    use_ollama=true
  else
    use_ollama=false
    log "Ollama is not running (start it with: docker compose --profile ollama up -d ollama); the Fallback stays on the mock."
  fi
fi
if [ "$use_ollama" = true ]; then
  log "Pulling $ollama_model into the ollama container (about 1 GB the first time) …"
  (cd "$root" && docker compose exec -T ollama ollama pull "$ollama_model" >&2) || die "ollama pull $ollama_model failed"
  # Local CPU inference is slow, so the Provider timeout is generous. The context window is Ollama's default num_ctx.
  ollama="$(provider ollama ollama http://ollama:11434 120000)"
  model "$ollama" "$ollama_model" "$(capabilities true true true 4096 1024)" 0 0 0 >/dev/null
  fallback="ollama/$ollama_model"
fi

# --- Hosted reference Provider (opt-in; calls a paid API) ---
if [ "$with_openai" = true ]; then
  # Prices are a snapshot taken when this script was written: check https://openai.com/api/pricing before trusting Estimated Cost.
  openai="$(provider openai openai_compatible https://api.openai.com/v1 60000 '{"envVar": "OPENAI_API_KEY"}')"
  model "$openai" gpt-5-nano "$(capabilities true true true 400000 128000 o200k_base)" 0.05 0.005 0.40 >/dev/null
  model "$openai" gpt-4o-mini "$(capabilities true true true 128000 16384 o200k_base)" 0.15 0.075 0.60 >/dev/null
fi

# policy NAME DEFINITION_JSON → Routing Policy id; a changed candidate list is saved as a new version
policy() {
  local name="$1" definition="$2" current id
  current="$(api GET '/admin/routing-policies?limit=200' | jq -c --arg name "$name" '.items[] | select(.name == $name)')"
  if [ -z "$current" ]; then
    id="$(api POST /admin/routing-policies "$(jq -n --arg name "$name" --argjson definition "$definition" '{name: $name, definition: $definition}')" | jq -r .policyId)"
    log "Created Routing Policy $name: $(jq -r '.candidates | join(" → ")' <<<"$definition")"
  else
    id="$(jq -r .id <<<"$current")"
    if [ "$(jq -c '.definition.candidates' <<<"$current")" != "$(jq -c .candidates <<<"$definition")" ]; then
      api PUT "/admin/routing-policies/$id" "$(jq -n --argjson definition "$definition" --argjson version "$(jq .version <<<"$current")" \
        '{definition: $definition, expectedVersion: $version}')" >/dev/null
      log "Routing Policy $name is now $(jq -r '.candidates | join(" → ")' <<<"$definition") (new version)"
    else
      log "Routing Policy $name is up to date"
    fi
  fi
  printf '%s' "$id"
}

# --- Hosted Provider through OpenRouter (opt-in; calls a paid API) ---
if [ "$with_openrouter" = true ]; then
  openrouter="$(provider openrouter openai_compatible https://openrouter.ai/api/v1 60000 "$(jq -n --arg secret "$openrouter_key" '{secret: $secret}')")"
  # OpenAI's gpt-4.1-nano Prices when this was written: check https://openrouter.ai/openai/gpt-4.1-nano before trusting Estimated Cost.
  model "$openrouter" openai/gpt-4.1-nano "$(capabilities true true true 1047576 32768 o200k_base)" 0.10 0.025 0.40 >/dev/null
  openrouter_policy="$(policy openrouter "$(jq -n '{
    strategy: "static",
    candidates: ["openrouter/openai/gpt-4.1-nano", "mock/mock-fixed"],
    defaultMaxOutputTokens: 512,
    execution: {deadlineMs: 60000, attemptTimeoutMs: 45000, maxAttempts: 3, maxRetriesPerCandidate: 1}
  }')")"
  api PUT /admin/aliases/openrouter "$(jq -n --arg id "$openrouter_policy" '{policyId: $id, description: "openai/gpt-4.1-nano through OpenRouter, falling back to the mock"}')" >/dev/null
  log "Alias openrouter → policy openrouter"
fi

# --- Routing Policy and alias "balanced": mock-echo, then the Fallback ---
definition="$(jq -n --arg fallback "$fallback" '{
  strategy: "static",
  candidates: ["mock/mock-echo", $fallback],
  defaultMaxOutputTokens: 256,
  execution: {deadlineMs: 120000, attemptTimeoutMs: 90000, maxAttempts: 4, maxRetriesPerCandidate: 1}
}')"
policy_id="$(policy balanced "$definition")"
api PUT /admin/aliases/balanced "$(jq -n --arg id "$policy_id" '{policyId: $id, description: "Demo: mock-echo with a Fallback"}')" >/dev/null
log "Alias balanced → policy balanced"

# --- Tenant (default "demo") and a new API Key ---
tenant_id="$(api GET '/admin/tenants?limit=200' | jq -r --arg name "$tenant_name" '.items[] | select(.name == $name) | .id')"
if [ -z "$tenant_id" ]; then
  tenant_id="$(api POST /admin/tenants "$(jq -n --arg name "$tenant_name" '{name: $name}')" | jq -r .id)"
  log "Created Tenant $tenant_name"
fi
api PUT "/admin/tenants/$tenant_id/model-access" '{"allowedAliases": ["*"]}' >/dev/null
key="$(api POST "/admin/tenants/$tenant_id/api-keys" "$(jq -n --arg name "demo-$(date -u +%Y%m%dT%H%M%SZ)" \
  '{name: $name, scopes: ["inference", "models:read"]}')" | jq -r .key)"
[ -n "$key" ] && [ "$key" != null ] || die "the API Key was not returned"

log ""
log "Seeded. The API Key for Tenant $tenant_name is printed below (only now). Try:"
log "  curl $base_url/v1/chat/completions -H \"Authorization: Bearer \$KEY\" -H 'Content-Type: application/json' \\"
log "    -d '{\"model\":\"balanced\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}'"
printf '%s\n' "$key"
