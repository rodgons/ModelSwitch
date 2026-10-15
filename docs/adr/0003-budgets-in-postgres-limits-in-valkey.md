---
status: accepted
---

# Budgets are enforced in PostgreSQL; rate and concurrency limits in Valkey

Budget Reservations are taken with one conditional `UPDATE` on the Tenant's budget-period row in PostgreSQL. Budgets protect money, so they need the same durability and transactions as Usage Records, and a crashed gateway must leave a Reservation the worker can find and settle. Rate Limits and Concurrency Limits are short-lived counters shared across instances, so they live in Valkey (an open-source server that speaks the Redis protocol) and are updated with atomic Lua scripts.

## Considered Options

- **Everything in Redis/Valkey**: faster, but budgets would need a reconciliation process and could be lost on eviction or restart.
- **Everything in PostgreSQL**: one dependency fewer, but per-second rate-limit counters would create heavy write traffic on hot rows.

## Consequences

- A single very busy Tenant contends on one budget row. That is acceptable for the MVP; the load tests measure it.
- If Valkey is unreachable, limit checks **fail closed** by default (`Limits:FailureMode=Closed`) and `/ready` reports unready. Operators may choose `Open`, which lets requests through and increments a degradation metric.
