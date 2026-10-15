---
status: accepted
---

# Model Aliases belong to routing, and Prices are separate immutable rows

This deviates from PRD §7. The PRD puts `Alias` and the per-token prices directly on the `Model` entity. We split these out:

- A **Model Alias** maps to a Routing Policy, and the policy lists the Provider Models that may serve it. One alias can therefore route across several Provider Models, and one Provider Model can serve several aliases. A single `Alias` column cannot express either.
- **Prices** live in `registry.model_price`, one immutable row per Provider Model per `effective_from`. Usage Records reference the price row they used, so a price change cannot alter past Estimated Costs (FR-06).

## Consequences

- `/v1/models` lists the Model Aliases a Tenant may use, never Provider Models (FR-02: provider identifiers stay internal).
