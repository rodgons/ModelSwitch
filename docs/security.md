# Security Design

This document covers FR-01, NFR-03 and the PRD §10.4 security tests.

## 1. Threat model (summary)

| Asset | Threat | Primary control |
|---|---|---|
| Provider credentials | Exfiltration via the admin UI, logs, DB dump | Write-only API, AES-GCM at rest (ADR-0007), log redaction |
| API Keys | Theft from the DB, brute force | HMAC-SHA256 hash + pepper (ADR-0006), 256-bit entropy, rate limiting on 401s |
| Tenant data | Cross-tenant reads | Tenant id from the authenticated principal only, query filters, 404 on foreign ids, integration tests |
| Spend | Runaway cost by a client or a bug | Reservations, per-request cost cap, bounded attempts |
| Gateway host network | SSRF via a Provider endpoint | Outbound destination policy (§5) |
| Admin sessions | CSRF, XSS, session theft | SameSite=Strict + antiforgery, CSP, HttpOnly cookies (ADR-0011) |
| Prompt content | Leakage via logs/traces/DB | Never persisted (ADR-0010), redaction, no body logging |

## 2. API Keys

- Format: `msk_` + 8-char base32 prefix + `_` + 43-char base64url secret (32 random bytes from `RandomNumberGenerator`). The prefix uses the lower-case Crockford alphabet `0123456789abcdefghjkmnpqrstvwxyz` (40 random bits), and a key is exactly 56 characters.
- Stored: `key_prefix`, `key_hash = HMAC-SHA256(pepper, full_key)`. The pepper comes from `MODELSWITCH__Security__ApiKeyPepper` (≥ 32 bytes, base64) and is never stored in the DB.
- Verify: parse → look up by prefix → `CryptographicOperations.FixedTimeEquals` → check `revoked_at`, `expires_at`, tenant status.
- Cache: positive results for ≤ 30 s (and never past the key's own `expires_at`), keyed by the hash, never the raw key. Failures are not cached. The cache is in-process and driven by `TimeProvider`. Revocation and Tenant suspension clear the cache on the instance that handled the write, so they take effect there at once. Other instances keep a cached key until its entry expires, so 30 s is the bound there (the revoke bumps `config_changed`, but the key cache does not listen to it).
- Abuse: repeated 401s from one IP are throttled with ASP.NET Core's in-process rate limiter (per instance, best effort).
- The raw key appears only in the creation response, and that response has `Cache-Control: no-store`.

## 3. Administrators

- Local accounts: ASP.NET Core Identity's `PasswordHasher<T>` (PBKDF2) over a minimal custom store (`identity.administrator`), not the full `AspNetUsers` table set, because Roles, Tenant Grants and sessions are our own. Lockout after 5 failures within 15 min locks the account for 15 min (a wrong, unknown, deactivated or locked sign-in all answer the same 401 `unauthenticated`), minimum password length 12. Administrators are managed by Owners (`/admin/administrators`); the last active Owner cannot be demoted or deactivated.
- **First Owner bootstrap:** the migrator creates an Owner from `MODELSWITCH__Bootstrap__OwnerEmail` + `…__OwnerPassword` **only if no Administrator exists**, and logs that it did. The variables can be removed afterwards.
- Session: cookie `__Host-ms_session`, `HttpOnly`, `Secure`, `SameSite=Strict`, sliding expiry of 8 h with an absolute limit of 24 h. Sessions are server-side (`ITicketStore` in PostgreSQL), so sign-out and deactivation revoke them immediately.
- Antiforgery: ASP.NET Core antiforgery (cookie `__Host-ms_xsrf` + token in the `X-XSRF-TOKEN` header), required on every non-GET admin request including sign-in; a missing or invalid token is `400 validation_failed`. `GET /admin/antiforgery-token` (anonymous) issues the pre-sign-in token; sign-in and `GET /admin/session` return one bound to the session. The Data Protection key ring is stored in `identity.data_protection_key` (unencrypted at rest, like the sessions beside it) so every api instance accepts the others' cookies and tokens.
- The principal (Role, Tenant Grant) is rebuilt from the database on every request, so Role changes and deactivation apply immediately. Session ids are stored only as SHA-256 hashes.
- Authorization: endpoint policies by Role, plus a `TenantGrant` check in every query or command that takes a tenant id. Foreign resources return 404. Three Roles (Owner, Operator, Viewer) plus Tenant Grants are the whole MVP model; custom roles and per-permission grants are post-MVP.
- Audited actions: sign-in success/failure, Administrator create/modify, Tenant create/suspend, API key create/revoke, Provider create/modify/secret replace, Provider Model and Price changes, Routing Policy versions, alias binding, limit/Budget changes, Dataset and Prompt creation and new versions, Dataset deletion, evaluation start/cancel.

## 4. Tenant isolation

The tenant id always comes from the authenticated principal (`ITenantContext`) and **never** from a request field on `/v1/*`. Isolation is enforced in three layers:

1. **Application**: commands and queries take `ITenantContext`, and repositories require a tenant id parameter. Admin queries take the caller's Tenant Grant.
2. **Data access**: every query over tenant-owned rows filters `tenant_id` explicitly (in Evaluation, by the caller's Tenant Grant in `DatasetService`/`PromptService`; child rows are reached only through a root whose Tenant was checked). Raw SQL views take the grant as a parameter. EF Core global query filters are not used: the services are singletons over `IDbContextFactory`, so a per-request filter would have to be threaded through every context anyway.
3. **Tests**: a dedicated integration test class creates two Tenants and an Administrator granted only Tenant A. It then tries every tenant-scoped endpoint against Tenant B's ids and expects 404 (PRD §10.4, DoD #5).

**PostgreSQL row-level security is deferred past the MVP.** It is valuable defence in depth, but three layers plus the test matrix already enforce and prove isolation, and RLS would add: a session variable set per transaction (Npgsql pooling makes it safe only with `SET LOCAL` inside explicit transactions), a bypass role for the worker's cross-tenant jobs (rollups, the sweeper, retention), and an audit of every raw-SQL view. How it would be added later: an EF Core `DbTransaction` interceptor that runs `SET LOCAL app.tenant_id = @t`, `CREATE POLICY tenant_isolation … USING (tenant_id = current_setting('app.tenant_id')::uuid)` on tenant-owned tables, and a separate `BYPASSRLS` role for the worker's system jobs.

## 5. Outbound requests (SSRF)

Provider endpoints are entered by Owners, but are still validated as untrusted input:

- Scheme must be `https`, or `http` only for hosts in `MODELSWITCH__Outbound__AllowedPrivateHosts` (e.g. `ollama`, `mock-provider`, `host.docker.internal`).
- On save **and** at connect time (`SocketsHttpHandler.ConnectCallback`), the resolved IP addresses are checked. Loopback, link-local (incl. `169.254.169.254` metadata), private (RFC 1918), CGNAT, ULA and multicast are rejected unless the host is in the allow-list. Checking at connect time defeats DNS rebinding.
- Redirects and proxies are disabled on Adapter clients (a proxy would connect on the gateway's behalf and bypass the connect-time check). IPv4-mapped, NAT64 (`64:ff9b::/96`) and 6to4 addresses are judged by the IPv4 address they embed.
- `MODELSWITCH__Outbound__AllowedPrivateHosts` matches the endpoint host exactly (case-insensitive, no subdomain wildcards) and exempts it from the scheme and address checks.
- `db:<uuid>` Credential References point to a `registry.provider_secret` row: AES-256-GCM, a fresh 96-bit nonce per encryption, and the Provider id plus master key version as additional authenticated data, so a row copied to another Provider or relabelled with another key version fails authentication. Setting a secret writes a new row, repoints the Provider, deletes the old row and bumps the config version in one transaction. The status an administrator sees is `{kind, configured, updatedAt}`; no part of the secret (not even its last characters) is ever returned. A set but malformed master key, or an unset one while any Provider references `db:`, fails startup with exit 78 naming `MODELSWITCH__Security__MasterKey`.
- Master key rotation: set the new key and a higher `MasterKeyVersion`, move the old key to `PreviousMasterKey` with its version, restart, then run the re-encrypt operation (`IProviderSecretStore.ReencryptAllAsync`) and remove the previous key.
- `env:NAME` Credential References resolve from process environment variables. Names starting with `MODELSWITCH__` are refused so a Provider cannot read deployment secrets. An unset variable fails with `provider_unavailable`; secrets are never logged.
- Response size caps apply: 10 MiB for non-streaming, and streams are cut at `maxOutputTokens × 16 bytes + 1 MiB`.

## 6. Logging and telemetry hygiene

- Logging is structured only. The `Authorization`, `Cookie`, `Set-Cookie` and `X-XSRF-TOKEN` headers are never logged, and the HTTP logging middleware is configured to exclude bodies and those headers.
- Classified data types `[SecretData]` and `[ContentData]` are handled by `Microsoft.Extensions.Compliance.Redaction`: secrets are erased, and content is erased by default.
- Span attributes follow the OTel GenAI semantic conventions for *metadata only*: `gen_ai.request.model` (= alias), `gen_ai.response.model` (= provider model, internal telemetry only), and token counts. `gen_ai.prompt` / `gen_ai.completion` events are never emitted.
- A test runs a full request containing a canary secret and a canary prompt, captures every log, span and metric exported in-memory, and asserts neither canary appears (DoD #11).

## 7. HTTP hardening

- Kestrel: `MaxRequestBodySize` 1 MiB on `/v1`, 10 MiB on dataset upload. Header and request timeouts are set.
- Security headers come from the proxy and the app: `Content-Security-Policy: default-src 'self'; frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, and HSTS when TLS is enabled.
- JSON parsing: `System.Text.Json` source-generated contexts, max depth 32, unknown properties rejected on `/v1`.

## 8. Secrets in the repository

- `.env.example` contains placeholders only. `.env` is git-ignored.
- CI runs `gitleaks` on every push.
- `docker-compose.yml` reads every secret from `.env`, and nothing is hard-coded.
