# Open Questions and Assumptions

These are decisions taken while writing the design docs that the PRD did not settle. Each one is marked with how confident we are and when it must be resolved. Once a question is settled, move its answer into the relevant doc or ADR and delete it from this list.

## Assumptions to confirm (decided, reversible)

| # | Assumption | Where | Resolve by |
|---|---|---|---|
| A1 | Budgets are monthly (calendar month, UTC) only. Daily/weekly periods are post-MVP | usage-and-budgets §3 | Phase 3 |
| A2 | Only USD. `currency` columns exist for later multi-currency support | data-model | Phase 3 |
| A3 | `budget_exceeded` returns **402** (not 429) to avoid SDK auto-retry | api §2 | Phase 3 |
| A4 | Task Type comes from the `X-ModelSwitch-Task` header, not the body | ADR-0009 | Phase 2 |
| A5 | Tools / function calling are rejected in the MVP (`unsupported_parameter`) | api §1 | Phase 1 |
| A6 | Request metadata retention defaults to 30 days | data-model §Retention | Phase 4 |
| A7 | A revoked key may keep working for ≤ 30 s on an instance (key cache) | ADR-0006 | Phase 3 |
| A8 | Valkey limits fail **closed** by default | ADR-0003 | Phase 3 |
| A9 | Three Roles (Owner / Operator / Viewer) + Tenant Grants is enough RBAC | security §3 | Phase 3 |
| A10 | The worst-case Reservation uses `maxAttempts` capped at 2, then tops up when needed | usage-and-budgets §3 | Phase 3 |
| A11 | The dashboard UI is English-only | frontend | Phase 4 |

## Open questions (need a decision)

1. **Row-level security in PostgreSQL** as defence in depth for tenant isolation: worth the pooling complexity before release? (security §4)
2. **Cached-input pricing** (OpenAI's discounted cached tokens): model it as a third price component now, or accept overestimation? It affects `model_price` and the cost formula.
3. **Evaluation run budget**: should evaluation spend count against the Tenant's production Budget (current design) or a separate evaluation Budget?
4. **Latency-aware ranking**: when the PRD says "explicitly configured dynamic health or load signals", is circuit state enough for the MVP (current design), or is an EWMA latency strategy expected?
5. **Admin API tokens** for automation (CI that pushes Routing Policies): post-MVP (current) or needed for the demo?
6. **Which hosted OpenAI-compatible Provider** is the reference for the demo and the benchmark (OpenAI, Groq, Together, OpenRouter)? It affects fixtures and pricing seeds.
7. **License** for the repository (MIT / Apache-2.0)? This should be decided before the first public push.
