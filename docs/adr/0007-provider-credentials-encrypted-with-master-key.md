---
status: accepted
---

# Provider credentials are encrypted at rest with a master key from the environment

A Credential Reference is either `env:VAR_NAME`, which resolves at runtime from the process environment, or `db:<id>`, which points to a row in `registry.provider_secret` encrypted with AES-256-GCM under `MODELSWITCH_MASTER_KEY`. We chose this over requiring an external secret manager (Vault, a cloud KMS) because the PRD demands a vendor-neutral, single-`docker compose up` deployment. The resolver is behind an `ISecretResolver` interface so a Vault-backed implementation can be added later.

## Consequences

- Losing the master key makes stored credentials unrecoverable, and they must be entered again. Startup fails with an actionable message if the key is missing while `db:` references exist.
- Secrets are write-only through the admin API: they can be set or replaced but never read back.
