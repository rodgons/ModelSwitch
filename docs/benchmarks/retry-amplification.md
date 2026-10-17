# Retry amplification

Measured on 2026-10-17 at commit `cab585c` (branch `failure-injection-catalogue`) by running `dotnet test --project tests/ModelSwitch.FailureInjectionTests` (PRD §13, [testing.md §3](../testing.md#3-failure-injection-scenarios-prd-103)).

**Amplification** is `provider_calls / client_requests` for one scenario. Provider calls are counted by the Mock Provider (`GET /__stats`), not by the gateway, so the gateway's own bookkeeping cannot make the number look better. Every scenario asserts its ratio against the bound in the last column, so the suite fails if a change ever makes the gateway amplify more than that.

## Results

| Scenario | Client requests | Provider calls | Amplification | Asserted bound |
|---|---|---|---|---|
| 503 x2 then ok | 1 | 3 | 3.00 | ≤ 4 (`maxAttempts` = 4) |
| 429 Retry-After 1 | 1 | 2 | 2.00 | ≤ 4 (`maxAttempts` = 4) |
| 429 Retry-After 120 | 1 | 2 | 2.00 | ≤ 4 (`maxAttempts` = 4) |
| provider hang | 1 | 2 | 2.00 | ≤ 4 (`maxAttempts` = 4) |
| provider 401 | 1 | 2 | 2.00 | ≤ 4 (`maxAttempts` = 4) |
| invalid json | 1 | 2 | 2.00 | ≤ 4 (`maxAttempts` = 4) |
| stream cut after 5 chunks | 1 | 1 | 1.00 | ≤ 1 (no replay after the First Token) |
| all candidates failing x20 (circuit opens) | 22 | 10 | 0.45 | ≤ 2 (`maxRetriesPerCandidate` + 1 on the one Candidate) |
| postgres stopped mid-load | 11 | 6 | 0.55 | ≤ 2 (`maxAttempts` = 2) |
| valkey stopped (fail-closed) | 1 | 0 | 0.00 | ≤ 0 (refused before routing) |
| valkey stopped (fail-open) | 3 | 3 | 1.00 | ≤ 1 (healthy Provider: no retry) |
| worker killed during evaluation (per Case) | 12 | 12–14 | 1.00–1.17 | ≤ 1.17 (each Case once, plus the 2 in flight at the kill) |
| otlp backend stopped mid-load | 80 | 80 | 1.00 | ≤ 1 (healthy Provider: no retry) |
| otlp backend hangs | 80 | 80 | 1.00 | ≤ 1 (healthy Provider: no retry) |
| otlp backend down at startup | 40 | 40 | 1.00 | ≤ 1 (healthy Provider: no retry) |

The numbers were the same in each of three runs, except "worker killed during evaluation". There the count depends on how many Cases were in flight when the process was killed (0, 1 or 2 at `concurrency` 2): the runs measured 14, 13 and 14 calls, and earlier runs of the scenario on its own measured 12.

## Reading the numbers

- **Single-request scenarios** stop well under `maxAttempts`. An ambiguous outcome (hang) or a permanent one (401) is never retried on the same model, and a long `Retry-After` is not waited out, so each of these costs exactly one Fallback call. Only the 503 case uses its one same-model retry before falling back.
- **Sustained failure:** the circuit breaker drives amplification *below* 1. 20 failing requests and 2 refused ones cost 10 Provider calls, against the 44 that two calls per request would have cost without a breaker.
- **Infrastructure faults** (PostgreSQL, Valkey, telemetry) add no Provider calls. A request that cannot open its Reservation, or is refused for limits, never reaches a Provider.
- **Worker crash:** a resumed Evaluation Run only repeats the Cases that were in flight when the worker died. Cases with a stored Case Result are never called again.

## Setup and limitations

- Apple M1 Pro, 16 GB, macOS 27.2, Docker Desktop 29.8.2 (Docker VM 3.8 GB). .NET 10.0.10, Debug build. PostgreSQL 17 and `valkey/valkey:8` in Testcontainers. Mock Provider in-process.
- Each scenario uses a Routing Policy with small timeouts (`deadlineMs` 5000, `attemptTimeoutMs` 1000, `maxAttempts` 4, `maxRetriesPerCandidate` 1, backoff 5–10 ms), unless the row's bound says otherwise. Production defaults would change timings, not call counts.
- The single-request rows show the per-request behaviour of the retry policy, not a throughput measurement. Load figures (k6) are a separate report.
- Waits the product measures in minutes (a job lease, a Reservation's expiry) are fast-forwarded in the tests. They do not affect call counts.

To regenerate: run the suite with `MODELSWITCH_AMPLIFICATION_REPORT_DIR=<dir>`. It writes `retry-amplification.md` (this table) and `retry-amplification.json` to that directory, or to `TestResults/` under the test output directory when the variable is not set.
