# Load tests

Measures the gateway overhead target of NFR-01 (p95 < 100 ms) as described in [docs/testing.md §5](../docs/testing.md#5-load-tests-and-benchmark-report). Published reports live in [`docs/benchmarks/`](../docs/benchmarks/) and follow [`REPORT_TEMPLATE.md`](REPORT_TEMPLATE.md).

| File | What it is |
|---|---|
| `scenarios/overhead.js` | k6 script: constant arrival rate, one step per rate, non-streaming and/or streaming requests to a Model Alias |
| `scenarios/dashboard.js` | k6 script: signs in as the Owner and calls the dashboard's read queries in turn at a constant rate (NFR-01: p95 < 500 ms) |
| `run.sh` | Brings up the stack, seeds it, sets the mock latency, runs every step and collects the server-side numbers |
| `compose.load.yaml` | Compose overlay used by `run.sh`: own image tags, OTLP export to `otel-lgtm` every 5 s, no host ports for `otel-lgtm` |
| `REPORT_TEMPLATE.md` | Template of a published benchmark report |
| `results/` | Raw output of each run (gitignored) |

## Running

Needs Docker with Compose v2, [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/) (≥ 0.50), `curl` and `jq`.

```bash
load/run.sh                                            # 50/100/200/400 RPS, 5 min each, non-streaming then streaming (~50 min)
STEP_DURATION=2m load/run.sh                           # shorter steps
STEP_DURATION=30s RATES=20,50 WARMUP_DURATION=10s load/run.sh   # smoke run (~4 min with the build cached)
```

`run.sh` runs the stack as its own Compose project, `modelswitch-load`, on port `LOAD_HTTP_PORT` (default 8090), with its own image tags, so it does not touch a dev stack on 8080. It:

1. waits (up to `IDLE_TIMEOUT_MINUTES`, default 45) until no container of the `modelswitch-e2e` project runs, so the measurement has the Docker VM to itself (`WAIT_FOR_IDLE=0` skips the wait); other running containers are listed in `environment.txt`;
2. creates `.env` with generated secrets and a throwaway Owner when there is none, and deletes it at the end (an existing `.env` is used as is);
3. `docker compose --profile demo --profile observability up -d --build --wait` with `compose.load.yaml` (Release images built from `deploy/docker/`);
4. `scripts/seed-demo.sh --no-ollama`: Tenant `demo` with no Rate, Concurrency or Budget limit, alias `balanced` (`mock/mock-echo` first), and an API Key; then `POST /__scenarios {"model":"mock-echo","fault":"latency=200"}` on the Mock Provider, so every Provider call takes 200 ms (`MOCK_LATENCY_MS`);
5. a warm-up at 50 RPS per variant (`WARMUP_DURATION`, default 60 s, not measured), then one k6 run per (variant, rate) with `STEP_GAP_SECONDS` (default 20) plus 12 s between steps, collecting after each step;
6. writes `results/<UTC timestamp>/` and tears the stack down with `down -v` (`KEEP_STACK=1` keeps it; `SKIP_BUILD=1` skips `--build`).

Variables of `run.sh`: `STEP_DURATION` (`5m`; `Ns` or `Nm`), `RATES` (`50,100,200,400`), `VARIANTS` (`nonstream,stream`), `MOCK_LATENCY_MS`, `WARMUP_DURATION`, `STEP_GAP_SECONDS`, `LOAD_HTTP_PORT`, `OUT_DIR`, `WAIT_FOR_IDLE`, `IDLE_TIMEOUT_MINUTES`, `KEEP_STACK`, `SKIP_BUILD`, and `TENANTS`. With the default `TENANTS=1`, every request comes from Tenant `demo`, the worst case for the per-Tenant `budget_period` row. With `TENANTS=N`, the run seeds `load-1`…`load-N` (`MODELSWITCH_DEMO_TENANT`) and k6 picks one of their keys at random for each request. `overhead.js` also runs on its own against any running gateway. Its variables are listed at the top of the file, and `API_KEY` or `API_KEYS` is required.

### Supplementary steps

These steps run after the rate steps, with `TENANTS=1`:

```bash
RATES=100 VARIANTS=nonstream LIMITS_STEP=1 DASHBOARD=1 load/run.sh   # the supplementary run of docs/benchmarks/
```

- **`LIMITS_STEP=1`** gives Tenant `demo` a Rate Limit and a Concurrency Limit that never refuse (`LIMITS_RPM` default 1 000 000/min, `LIMITS_CONCURRENT` default 100 000). It then repeats one non-streaming step at `LIMITS_RATE` (default 100) as `limits_nonstream_<rate>rps`. Every request in that step goes through Valkey, so it can be compared with the no-limits step at the same rate.
  - No metric times the limits decision. The step therefore samples the `modelswitch.limits` span from Tempo inside `otel-lgtm` (`limits_span_us` in `results.json`).
  - Valkey's `INFO commandstats` gives the server-side time per command.
- **`DASHBOARD=1`** records the dataset size (`dataset.jsonl`: rows in `inference_request`, `provider_attempt`, `usage_record` and `usage_hourly`, unpublished outbox events, database size), then runs `scenarios/dashboard.js` twice:
  - on the otherwise idle stack;
  - during non-streaming inference at `DASHBOARD_LOAD_RATE` (default 100; `0` skips it).
  - Each run lasts `DASHBOARD_DURATION` (default 3m) at `DASHBOARD_RATE` queries per second (default 9, so about one per second per query).
  - The queries are the Overview for 24 h and 7 d, the Requests list (plain, filtered by status, alias, time and sorted by duration, and `status=failed`), a request detail, the usage report for 24 h by alias and the Tenant's month by alias, and Provider Health.
  - The per-query table goes to `dashboard.md`.
  - A range of 24 h or less reads the raw request rows. A longer range reads `usage_hourly` for whole hours and raw rows for the edges ([usage-and-budgets.md](../docs/usage-and-budgets.md)).

## What is collected

Per step, in `results/<run>/results.json` (one object per step) and `results.md` (the headline table):

| Number | Source |
|---|---|
| Gateway overhead p50/p95/p99, mean, share ≤ 50 ms and ≤ 100 ms | `modelswitch_inference_gateway_overhead_seconds` in the Prometheus of `otel-lgtm`, `histogram_quantile` over `increase()` across the step. The buckets are 1, 2.5, 5, 10, 25, 50, 75, 100, 250 ms…, so percentiles are interpolated inside a bucket; the share ≤ 100 ms is exact |
| Gateway overhead p50/p95/p99, max, mean (exact) | Recomputed per request from PostgreSQL, the same formula as the metric: `inference_request.duration_ms` minus the span from the first Attempt's `started_at` to the last one's `completed_at`. `duration_ms` is whole milliseconds |
| End-to-end latency, end-to-end minus the mock latency, time to first byte | k6 (`http_req_duration`, `e2e_minus_mock`, `http_req_waiting`), on the host, through Docker Desktop's port forwarding and Caddy |
| Throughput, error rate, dropped iterations | k6. A dropped iteration is a request k6 could not start on time because every VU was busy, a sign of saturation |
| Request status counts, Fallbacks | `usage.inference_request.status`; `modelswitch_fallbacks_total` |
| api working set (RSS), GC heap, GC pause time and collections, CPU, thread-pool queue | .NET runtime metrics (`dotnet_*`) of the api |
| Npgsql pool | `db_client_connection_count{state="used"}` against `db_client_connection_max`, and `db_client_operation_duration` p95/p99. Npgsql exports no pool wait-time instrument, so wait time is not measured directly: a pool that never reaches its maximum has no waiters |
| Valkey | `INFO commandstats` before and after the step: calls and server-side µs per call per command. This is time inside Valkey, not the client round trip |
| Outbox lag | `modelswitch_outbox_lag_seconds` (sampled by the worker every 15 s), max over the step |
| Container CPU and memory | one `docker stats` sample in the middle of each step |

## Writing a report

Copy `REPORT_TEMPLATE.md` to `docs/benchmarks/<YYYY-MM-DD>-<short git sha>.md`, paste `results.md`, and fill in the environment from `environment.txt` and `images.txt`. Publish only numbers that were measured, and say what was not (DoD #10).

## CI

`.github/workflows/load.yml` runs a short smoke (`workflow_dispatch` and nightly) on a GitHub runner and uploads `load/results/` as an artifact. Runner numbers are noisy and are never committed as a benchmark.
