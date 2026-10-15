# ModelSwitch: agent notes

A self-hosted LLM gateway. .NET 10 modular monolith (api + worker + migrator), PostgreSQL, Valkey, Vue 3 dashboard.

Before changing anything, read:
- `docs/constitution.md`: it overrides every other doc. If a plan contradicts it, change the plan. Constitution edits go in a commit of their own.
- `CONTEXT.md`: use its terms exactly in code and docs (`Attempt`, `ProviderModel`, `EstimatedCost`, `Reservation`…). Words listed under _Avoid_ are wrong.
- The design doc for the area you touch (index in `README.md`). If the code must diverge from a doc, update the doc in the same change.
- `docs/adr/`: do not "fix" an ADR decision without writing a superseding ADR. The decisions most often undone by mistake are: no `HttpClient` resilience handlers on Adapter clients (0004), HMAC rather than bcrypt for API keys (0006), and never persisting or logging prompt or completion content (0010).

Rules:
- Modules talk only through each other's `Contracts/` namespace. Everything else is `internal`.
- Inject `TimeProvider` and the UUIDv7 generator. Never call `DateTime.UtcNow` or `Guid.NewGuid()` directly.
- Every new error goes into the closed error-code set in `docs/api.md §2`.
- Tests never call a real external Provider. Use the Mock Provider or WireMock fixtures.
- Work from `docs/roadmap.md` and tick tasks off there.
