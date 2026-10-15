---
status: accepted
---

# Circuit-breaker state is held in memory per gateway instance

Each gateway instance keeps its own circuit breaker per Provider Model, based on its own Attempt outcomes. With N instances, an unhealthy Provider receives at most N probe calls per break period. That cost is small and bounded. A shared breaker in Valkey would put an extra network hop on every Attempt and make inference depend on Valkey for something that can be decided locally.

## Consequences

- Instances may briefly disagree about Provider Health. The dashboard shows health per instance plus an aggregate, and the worker periodically writes each instance's snapshot to PostgreSQL.
- Circuit tests run against one instance. Cross-instance tests apply only to Rate Limits and Concurrency Limits.
