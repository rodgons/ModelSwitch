---
status: accepted
---

# Modular monolith on .NET 10 with a separate worker process

ModelSwitch is one .NET 10 (LTS) solution split into modules with enforced boundaries, deployed as two executables built from the same code: `ModelSwitch.Api` (inference and admin HTTP) and `ModelSwitch.Worker` (background processing). We chose this over microservices because the PRD forbids service boundaries without demonstrated need, and over a single process because evaluation runs and usage aggregation must not compete with the inference hot path for threads, memory or connection pools.

## Consequences

- Modules talk to each other through public interfaces in each module's `Contracts` namespace, never through each other's tables or `DbContext`. An architecture test enforces this.
- The Api host can start with only some endpoint groups enabled (`Hosting:EndpointGroups`), so inference and admin traffic can later be split into separate deployments without code changes.
