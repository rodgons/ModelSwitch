# Gateway overhead benchmark: <YYYY-MM-DD>, `<short git sha>`

Measured with `load/run.sh` ([load/README.md](../../load/README.md)) on <date> at commit `<sha>`. Target: NFR-01, gateway overhead p95 < 100 ms. Only measured numbers are reported (DoD #10).

## Result

<One paragraph: was NFR-01 met at each rate, where did the stack saturate, anything surprising.>

## Environment

| | |
|---|---|
| Hardware | <CPU, cores, RAM> |
| OS | <host OS and version> |
| Docker | <engine version; VM CPUs and memory> |
| Load generator | <k6 version; where it ran (same host / other host)> |
| Other load on the machine | <idle / what else ran> |
| Images | <image ids or digests of api, worker, mock-provider; tags of postgres, valkey, caddy, otel-lgtm> |
| Configuration | <Compose profiles; telemetry export; trace sampling; anything changed from the defaults> |

## Workload

| | |
|---|---|
| Provider | Mock Provider, `mock-echo`, fixed latency <N> ms (`POST /__scenarios latency=<N>`) |
| Alias and policy | <alias, candidates, limits> |
| Tenant and dataset | <Tenant, API Key, limits; DB size before the run> |
| Request | <body: messages, max_tokens, stream> |
| Executor | k6 `constant-arrival-rate`; rates <…> RPS; <duration> per step; warm-up <…>; gap between steps <…> |
| Concurrency | <pre-allocated / max VUs; peak VUs observed> |

## Gateway overhead

<results.md table: per step, target and achieved RPS, requests, errors, dropped, overhead p50/p95/p99 from Prometheus and from the DB, share ≤ 100 ms, k6 end-to-end, end-to-end minus mock.>

## Resources

<Per step: api working set, GC pause time and collections, CPU, thread-pool queue, Npgsql pool used/max and operation p95, Valkey µs per call, outbox lag, docker stats.>

## Raw numbers

<The relevant part of results.json; raw output is not committed.>

## Related

- Retry amplification under injected failures: [retry-amplification.md](retry-amplification.md).

## Limitations

- <Hardware is a laptop; Docker VM size; load generator on the same host.>
- <Step duration if shorter than 5 min.>
- <Bucket interpolation of the Prometheus percentiles.>
- <What was not measured and why.>
