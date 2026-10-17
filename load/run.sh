#!/usr/bin/env bash
# Gateway-overhead load run (docs/testing.md section 5, NFR-01). See load/README.md.
#
#   load/run.sh                                   # full run: 50/100/200/400 RPS, 5 min each, non-streaming then streaming
#   STEP_DURATION=30s RATES=20,50 load/run.sh     # smoke run
#
# What it does:
#   1. waits until no container of the `modelswitch-e2e` Compose project runs (WAIT_FOR_IDLE=0 skips), so another stack does
#      not share the Docker VM's CPUs with the measurement;
#   2. creates .env (generated secrets, a throwaway Owner) if there is none, and removes it again at the end;
#   3. starts the stack as Compose project `modelswitch-load` (Release images, profiles demo + observability, load/compose.load.yaml);
#   4. seeds the demo catalog and a Tenant with no limits (scripts/seed-demo.sh --no-ollama) and makes the Mock Provider answer
#      `mock-echo` (the first candidate of alias `balanced`) after a fixed MOCK_LATENCY_MS (POST /__scenarios latency=…);
#   5. warms up, then runs load/scenarios/overhead.js once per (variant, rate), and after each step collects:
#      - modelswitch.inference.gateway_overhead p50/p95/p99 from Prometheus (inside otel-lgtm), histogram-interpolated;
#      - the same overhead recomputed exactly per request from the inference_request / provider_attempt rows;
#      - k6 end-to-end latency minus the mock latency, throughput, error rate, dropped iterations;
#      - api working set, GC pause time, CPU, thread-pool queue, Npgsql pool usage, Valkey per-command time, outbox lag;
#   6. writes $OUT_DIR/results.json and $OUT_DIR/results.md, then tears the stack down (KEEP_STACK=1 keeps it).
#
# Environment: STEP_DURATION (5m), RATES (50,100,200,400), VARIANTS (nonstream,stream), MOCK_LATENCY_MS (200),
# WARMUP_DURATION (60s), STEP_GAP_SECONDS (20), LOAD_HTTP_PORT (8090), OUT_DIR (load/results/<UTC timestamp>),
# WAIT_FOR_IDLE (1), IDLE_TIMEOUT_MINUTES (45), KEEP_STACK (0), SKIP_BUILD (0).
# Needs: docker compose v2, k6, curl, jq.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

step_duration="${STEP_DURATION:-5m}"
rates="${RATES:-50,100,200,400}"
variants="${VARIANTS:-nonstream,stream}"
mock_latency_ms="${MOCK_LATENCY_MS:-200}"
warmup_duration="${WARMUP_DURATION:-60s}"
step_gap="${STEP_GAP_SECONDS:-20}"
port="${LOAD_HTTP_PORT:-8090}"
out_dir="${OUT_DIR:-$root/load/results/$(date -u +%Y%m%dT%H%M%SZ)}"
project=modelswitch-load
network="${project}_default"

log() { printf '[%s] %s\n' "$(date -u +%H:%M:%S)" "$*" >&2; }
die() { log "error: $*"; exit 1; }
for tool in docker k6 curl jq; do command -v "$tool" >/dev/null 2>&1 || die "$tool is required"; done

export MODELSWITCH_HTTP_PORT="$port"
compose() { docker compose -p "$project" -f compose.yaml -f load/compose.load.yaml --profile demo --profile observability "$@"; }

mkdir -p "$out_dir"

# --- 1. an otherwise idle Docker VM ---
if [ "${WAIT_FOR_IDLE:-1}" = 1 ]; then
  deadline=$(( $(date +%s) + ${IDLE_TIMEOUT_MINUTES:-45} * 60 ))
  while [ -n "$(docker ps -q --filter label=com.docker.compose.project=modelswitch-e2e)" ]; do
    [ "$(date +%s)" -lt "$deadline" ] || die "modelswitch-e2e containers are still running; not measuring on a shared VM"
    log "modelswitch-e2e containers are running; waiting 60 s"
    sleep 60
  done
fi
others="$(docker ps --format '{{.Names}}' | grep -v "^${project}-" || true)"

# --- 2. .env ---
created_env=false
if [ ! -f .env ]; then
  ./scripts/generate-secrets.sh >/dev/null
  sed -i.bak -e 's|^MODELSWITCH__Bootstrap__OwnerEmail=.*|MODELSWITCH__Bootstrap__OwnerEmail=load@example.test|' \
    -e "s|^MODELSWITCH__Bootstrap__OwnerPassword=.*|MODELSWITCH__Bootstrap__OwnerPassword=$(openssl rand -hex 16)|" .env
  rm -f .env.bak
  created_env=true
  log "Created .env for this run (removed at the end)"
fi

cleanup() {
  if [ "${KEEP_STACK:-0}" != 1 ]; then
    log "Tearing the stack down"
    compose down -v --remove-orphans >/dev/null 2>&1 || true
    if [ "$created_env" = true ]; then rm -f .env; fi
  fi
}
trap cleanup EXIT

# --- 3. stack ---
log "Starting Compose project $project on port $port"
if [ "${SKIP_BUILD:-0}" = 1 ]; then compose up -d --wait >&2; else compose up -d --build --wait >&2; fi

# --- 4. seed and mock latency ---
key="$(MODELSWITCH_URL="http://localhost:$port" ./scripts/seed-demo.sh --no-ollama 2>"$out_dir/seed.log")"
docker run --rm --network "$network" curlimages/curl:8.11.1 -fsS http://mock-provider:8080/__scenarios \
  -H 'Content-Type: application/json' -d "{\"model\":\"mock-echo\",\"fault\":\"latency=$mock_latency_ms\"}" >/dev/null
log "Seeded; mock-echo answers after $mock_latency_ms ms"

# --- helpers for the server-side numbers ---
prom() { # PROMQL UNIX_TIME → first value (or empty)
  compose exec -T otel-lgtm curl -fsS -G http://localhost:9090/api/v1/query --data-urlencode "query=$1" --data-urlencode "time=$2" \
    | jq -r '.data.result[0].value[1] // empty'
}
psql_at() { compose exec -T postgres psql -U modelswitch_migrator -d modelswitch -At -F ' ' -c "$1"; }
valkey_stats() { compose exec -T valkey valkey-cli INFO commandstats | tr -d '\r' | grep '^cmdstat_'; }
to_seconds() { case "$1" in *m) echo $(( ${1%m} * 60 )) ;; *s) echo "${1%s}" ;; *) die "STEP_DURATION must be like 90s or 5m" ;; esac; }
num() { if [ -z "$1" ] || [ "$1" = NaN ]; then echo null; else echo "$1"; fi; }

record_images() {
  for service in api worker mock-provider; do
    printf '%s %s\n' "$service" "$(docker inspect --format '{{.Image}}' "$(compose ps -q "$service")")"
  done
  for service in postgres valkey otel-lgtm proxy; do
    printf '%s %s\n' "$service" "$(docker inspect --format '{{index .RepoDigests 0}}' "$(docker inspect --format '{{.Image}}' "$(compose ps -q "$service")")" 2>/dev/null || echo unknown)"
  done
}
record_images > "$out_dir/images.txt"
{
  echo "git $(git rev-parse --short HEAD) $(git status --porcelain --untracked-files=no | wc -l | tr -d ' ') modified tracked files"
  echo "host $(uname -srm); $(sysctl -n machdep.cpu.brand_string 2>/dev/null || true); $(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1073741824 )) GB"
  echo "docker $(docker info --format '{{.ServerVersion}} {{.OperatingSystem}}; VM {{.NCPU}} CPU {{.MemTotal}} bytes')"
  echo "k6 $(k6 version | head -1)"
  echo "other containers: ${others:-none}"
} > "$out_dir/environment.txt"

# --- 5. warm-up (JIT, connection pools, tiered compilation); not measured ---
log "Warm-up: $warmup_duration at 50 RPS per variant"
for variant in ${variants//,/ }; do
  API_KEY="$key" BASE_URL="http://localhost:$port" RATES=50 STEP_DURATION="$warmup_duration" STEP_GAP=0s \
    STREAM=$([ "$variant" = stream ] && echo true || echo false) MOCK_LATENCY_MS="$mock_latency_ms" \
    SUMMARY_FILE="$out_dir/warmup-$variant.json" k6 run -q --no-color load/scenarios/overhead.js >/dev/null 2>&1 || true
done
sleep "$step_gap"

results="$out_dir/steps.jsonl"
: > "$results"
for variant in ${variants//,/ }; do
  streaming=$([ "$variant" = stream ] && echo true || echo false)
  for rate in ${rates//,/ }; do
    step="${variant}_${rate}rps"
    log "Step $step for $step_duration"
    valkey_before="$(valkey_stats)"
    start=$(date +%s)
    start_iso=$(date -u -r "$start" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d "@$start" +%Y-%m-%dT%H:%M:%SZ)
    # One docker stats sample in the middle of the step.
    # shellcheck disable=SC2046 # one argument per container id
    ( sleep $(( $(to_seconds "$step_duration") / 2 )) \
      && docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' $(compose ps -q) > "$out_dir/docker-stats-$step.txt" ) &
    stats_pid=$!
    API_KEY="$key" BASE_URL="http://localhost:$port" RATES="$rate" STEP_DURATION="$step_duration" STEP_GAP=0s STREAM="$streaming" \
      MOCK_LATENCY_MS="$mock_latency_ms" SUMMARY_FILE="$out_dir/k6-$step.json" \
      k6 run -q --no-color load/scenarios/overhead.js > "$out_dir/k6-$step.txt" 2>&1 || log "k6 exited non-zero for $step (see k6-$step.txt)"
    wait "$stats_pid" || true
    end=$(date +%s)
    # Requests k6 started up to its exit, plus a margin for clock skew between the host and the VM; the next step is later.
    end_iso=$(date -u -r "$(( end + 5 ))" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d "@$(( end + 5 ))" +%Y-%m-%dT%H:%M:%SZ)
    # Two metric export intervals (5 s each) so the last samples of the step reach Prometheus.
    sleep 12
    at=$(( end + 10 )); window="$(( at - start ))s"
    sel="streaming=\"$streaming\""
    q() { num "$(prom "$1" "$at")"; }
    hq() { q "histogram_quantile($1, sum by (le) (increase(modelswitch_inference_gateway_overhead_seconds_bucket{$sel}[$window])))"; }

    db="$(psql_at "
      with per_request as (
        select r.duration_ms - extract(epoch from (max(a.completed_at) - min(a.started_at))) * 1000 as overhead_ms
        from usage.inference_request r
        join usage.provider_attempt a on a.inference_request_id = r.id and a.started_at >= r.started_at
          and a.status <> 'skipped_circuit_open'
        where r.started_at >= '$start_iso' and r.started_at < '$end_iso' and r.streaming = $streaming and r.status = 'succeeded'
        group by r.id, r.duration_ms)
      select count(*),
             round(percentile_cont(0.5) within group (order by overhead_ms)::numeric, 2),
             round(percentile_cont(0.95) within group (order by overhead_ms)::numeric, 2),
             round(percentile_cont(0.99) within group (order by overhead_ms)::numeric, 2),
             round(max(overhead_ms)::numeric, 2),
             round(avg(overhead_ms)::numeric, 2)
      from per_request;")"
    read -r db_n db_p50 db_p95 db_p99 db_max db_avg <<<"$db"
    db_status="$(psql_at "select coalesce(json_object_agg(status, n), '{}') from (select status, count(*) n from usage.inference_request
      where started_at >= '$start_iso' and started_at < '$end_iso' and streaming = $streaming group by status) s;")"

    valkey_after="$(valkey_stats)"
    valkey_json="$(jq -n --arg before "$valkey_before" --arg after "$valkey_after" '
      def parse: split("\n") | map(select(length > 0) | capture("^cmdstat_(?<cmd>[^:]+):calls=(?<calls>[0-9]+),usec=(?<usec>[0-9]+)"))
        | map({key: .cmd, value: {calls: (.calls | tonumber), usec: (.usec | tonumber)}}) | from_entries;
      ($before | parse) as $b | ($after | parse) as $a
      | $a | to_entries | map(.key as $k | {cmd: $k, calls: (.value.calls - ($b[$k].calls // 0)), usec: (.value.usec - ($b[$k].usec // 0))})
      | map(select(.calls > 0) | . + {usec_per_call: ((.usec / .calls * 100 | round) / 100)}) | sort_by(-.calls)')"

    # Label selectors are built in variables: a literal `{a,b}` in the queries below would be brace-expanded by bash.
    api='job="modelswitch-api"'
    sel100="$sel,le=\"0.1\""
    sel50="$sel,le=\"0.05\""
    pool="$api,db_client_connection_pool_name!~\".*Pooling=False.*\""
    pool_used="$pool,db_client_connection_state=\"used\""
    gen2="$api,dotnet_gc_heap_generation=\"gen2\""
    jq -nc \
      --arg step "$step" --arg variant "$variant" --argjson rate "$rate" --arg start "$start_iso" --arg end "$end_iso" \
      --slurpfile k6 "$out_dir/k6-$step.json" \
      --argjson p50 "$(hq 0.5)" --argjson p95 "$(hq 0.95)" --argjson p99 "$(hq 0.99)" \
      --argjson mean "$(q "sum(increase(modelswitch_inference_gateway_overhead_seconds_sum{$sel}[$window])) / sum(increase(modelswitch_inference_gateway_overhead_seconds_count{$sel}[$window]))")" \
      --argjson le100 "$(q "sum(increase(modelswitch_inference_gateway_overhead_seconds_bucket{$sel100}[$window])) / sum(increase(modelswitch_inference_gateway_overhead_seconds_count{$sel}[$window]))")" \
      --argjson le50 "$(q "sum(increase(modelswitch_inference_gateway_overhead_seconds_bucket{$sel50}[$window])) / sum(increase(modelswitch_inference_gateway_overhead_seconds_count{$sel}[$window]))")" \
      --argjson fallbacks "$(q "sum(increase(modelswitch_fallbacks_total[$window]))")" \
      --argjson rss_max "$(q "max(max_over_time(dotnet_process_memory_working_set_bytes{$api}[$window]))")" \
      --argjson heap_max "$(q "max(max_over_time(dotnet_gc_last_collection_heap_size_bytes{$api}[$window]))")" \
      --argjson gc_pause "$(q "sum(increase(dotnet_gc_pause_time_seconds_total{$api}[$window]))")" \
      --argjson gc_count "$(q "sum(increase(dotnet_gc_collections_total{$api}[$window]))")" \
      --argjson gc_gen2 "$(q "sum(increase(dotnet_gc_collections_total{$gen2}[$window]))")" \
      --argjson cpu_cores "$(q "sum(rate(dotnet_process_cpu_time_seconds_total{$api}[$window]))")" \
      --argjson threadpool_queue_max "$(q "max(max_over_time(dotnet_thread_pool_queue_length_total{$api}[$window]))")" \
      --argjson threadpool_threads_max "$(q "max(max_over_time(dotnet_thread_pool_thread_count_total{$api}[$window]))")" \
      --argjson pool_used_max "$(q "max(max_over_time(db_client_connection_count{$pool_used}[$window]))")" \
      --argjson pool_max "$(q "max(db_client_connection_max{$pool})")" \
      --argjson db_op_p95 "$(q "histogram_quantile(0.95, sum by (le) (increase(db_client_operation_duration_seconds_bucket{$api}[$window])))")" \
      --argjson db_op_p99 "$(q "histogram_quantile(0.99, sum by (le) (increase(db_client_operation_duration_seconds_bucket{$api}[$window])))")" \
      --argjson outbox_lag_max "$(q "max(max_over_time(modelswitch_outbox_lag_seconds[$window]))")" \
      --argjson db_n "$(num "$db_n")" --argjson db_p50 "$(num "$db_p50")" --argjson db_p95 "$(num "$db_p95")" \
      --argjson db_p99 "$(num "$db_p99")" --argjson db_max "$(num "$db_max")" --argjson db_avg "$(num "$db_avg")" \
      --argjson db_status "$db_status" --argjson valkey "$valkey_json" \
      '($k6[0].metrics) as $m | ("{scenario:" + $step + "}") as $s | {
        step: $step, variant: $variant, target_rps: $rate, start: $start, end: $end,
        k6: {
          requests: ($m["requests" + $s].values.count // 0),
          achieved_rps: ($m["requests" + $s].values.rate // null),
          dropped_iterations: ($m["dropped_iterations" + $s].values.count // 0),
          error_rate: ($m["request_failed" + $s].values.rate // null),
          e2e_ms: ($m["http_req_duration" + $s].values // null),
          ttfb_ms: ($m["http_req_waiting" + $s].values // null),
          e2e_minus_mock_ms: ($m["e2e_minus_mock" + $s].values // null)
        },
        overhead_prometheus_ms: {p50: ($p50 | if . == null then null else . * 1000 end), p95: ($p95 | if . == null then null else . * 1000 end),
          p99: ($p99 | if . == null then null else . * 1000 end), mean: ($mean | if . == null then null else . * 1000 end),
          share_le_50ms: $le50, share_le_100ms: $le100},
        overhead_db_ms: {requests: $db_n, p50: $db_p50, p95: $db_p95, p99: $db_p99, max: $db_max, mean: $db_avg},
        request_status_db: $db_status,
        fallbacks: ($fallbacks // 0),
        api: {working_set_max_bytes: $rss_max, gc_heap_max_bytes: $heap_max, gc_pause_seconds: $gc_pause, gc_collections: $gc_count,
          gc_gen2: $gc_gen2, cpu_cores: $cpu_cores, threadpool_queue_max: $threadpool_queue_max, threadpool_threads_max: $threadpool_threads_max},
        npgsql: {pool_used_max: $pool_used_max, pool_max: $pool_max, operation_p95_ms: ($db_op_p95 | if . == null then null else . * 1000 end),
          operation_p99_ms: ($db_op_p99 | if . == null then null else . * 1000 end)},
        outbox_lag_max_seconds: $outbox_lag_max,
        valkey_commands: $valkey
      }' >> "$results"
    log "$(tail -1 "$results" | jq -r '"  \(.k6.requests) req, \(.k6.error_rate * 100 | . * 100 | round / 100)% failed, \(.k6.dropped_iterations) dropped; overhead p95 \(.overhead_prometheus_ms.p95 // "n/a") ms (prom), \(.overhead_db_ms.p95 // "n/a") ms (db)"')"
    sleep "$step_gap"
  done
done

jq -s '.' "$results" > "$out_dir/results.json"
jq -r '
  def f: if . == null then "n/a" else (. * 10 | round / 10 | tostring) end;
  def pct: if . == null then "n/a" else (. * 10000 | round / 100 | tostring) + "%" end;
  "| Step | Target RPS | Achieved RPS | Requests | Errors | Dropped | Overhead p50 / p95 / p99 (Prometheus, ms) | Overhead p50 / p95 / p99 (DB, ms) | ≤ 100 ms | k6 e2e p50 / p95 / p99 (ms) | e2e − mock p95 (ms) |",
  "|---|---|---|---|---|---|---|---|---|---|---|",
  (.[] | "| \(.step) | \(.target_rps) | \(.k6.achieved_rps | f) | \(.k6.requests) | \(.k6.error_rate | pct) | \(.k6.dropped_iterations) | \(.overhead_prometheus_ms.p50 | f) / \(.overhead_prometheus_ms.p95 | f) / \(.overhead_prometheus_ms.p99 | f) | \(.overhead_db_ms.p50 | f) / \(.overhead_db_ms.p95 | f) / \(.overhead_db_ms.p99 | f) | \(.overhead_prometheus_ms.share_le_100ms | pct) | \(.k6.e2e_ms.med | f) / \(.k6.e2e_ms["p(95)"] | f) / \(.k6.e2e_ms["p(99)"] | f) | \(.k6.e2e_minus_mock_ms["p(95)"] | f) |")
' "$out_dir/results.json" > "$out_dir/results.md"
cat "$out_dir/results.md" >&2
log "Results in $out_dir"
