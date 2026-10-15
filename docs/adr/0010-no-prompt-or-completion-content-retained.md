---
status: accepted
---

# Inference prompts and completions are never persisted or logged

The gateway stores metadata about Inference Requests (sizes, token counts, timings, routing, errors) but never the message content or the generated output. No setting turns this on in the MVP. A "debug capture" flag was considered and rejected because it would require retention, access control and deletion rules for user content that the MVP does not define (PRD §7: define deletion before retaining content).

## Consequences

- Evaluation is the deliberate exception. Case inputs, expected results and model outputs for Evaluation Runs are stored, because scoring and inspection need them. That content is supplied by Administrators, scoped to a Tenant, and deleted together with its Dataset or Run.
- Diagnosing a bad completion in production means reproducing it, not looking it up. This limitation is documented.
