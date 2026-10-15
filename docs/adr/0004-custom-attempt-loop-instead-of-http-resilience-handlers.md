---
status: accepted
---

# A custom attempt loop, not HttpClient resilience handlers, drives retries and fallbacks

Retries, Fallbacks, Deadlines and Ambiguous-Outcome handling are implemented in one explicit loop in the Inference module (`AttemptExecutor`). It walks the ordered Candidates of a Routing Decision and records every Attempt. Polly-based `HttpClient` handlers (`Microsoft.Extensions.Http.Resilience`) are **not** used on Adapter clients. Those handlers retry invisibly below the Adapter: the gateway cannot record each Attempt, cannot switch Provider Models between tries, and cannot stop retrying once a stream has started forwarding. All three are PRD requirements.

## Consequences

- Adapter `HttpClient`s have only a per-Attempt timeout and no retry handler. Adding a standard resilience handler to them is a bug, not a cleanup.
- Polly is still used as a library for its circuit-breaker state machine (see ADR-0005) and backoff-with-jitter calculation.
