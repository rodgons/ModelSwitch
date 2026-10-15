---
status: accepted
---

# PostgreSQL is the only durable store and the only durable queue

All durable state, including usage events and background jobs, lives in PostgreSQL. Cross-process work is handed over through a transactional outbox table and a job table, consumed by the worker with `SELECT … FOR UPDATE SKIP LOCKED`. We rejected a message broker (RabbitMQ, Kafka) and job libraries with their own storage (Hangfire, Quartz) because the volumes are modest, the outbox must commit in the same transaction as the usage data it describes, and one fewer stateful dependency makes self-hosting simpler.

## Consequences

- Delivery is at-least-once. Every consumer records processed event IDs and must be idempotent.
- If throughput ever outgrows polling, the outbox relay is the single place to change.
