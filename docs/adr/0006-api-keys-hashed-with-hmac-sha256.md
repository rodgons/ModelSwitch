---
status: accepted
---

# API Keys are hashed with HMAC-SHA256 and a server-side pepper, not a password hash

API Keys are 256-bit random secrets (`msk_<prefix>_<secret>`) and are checked on every inference call. They are stored as `HMAC-SHA256(pepper, secret)`, and the pepper comes from configuration rather than the database. Slow password hashes (bcrypt, Argon2) protect low-entropy human passwords from brute force. Against 256-bit keys they add nothing except tens of milliseconds per request, which would use up the 100 ms p95 overhead budget. Administrator passwords **do** use a slow hash: ASP.NET Core Identity's PBKDF2 hasher.

## Consequences

- Lookup is by Key Prefix (indexed), followed by a constant-time comparison of the HMAC.
- Rotating the pepper invalidates every key. Supporting a rotation would mean storing a pepper version on each key, which is post-MVP.
- Verified keys are cached in memory for at most 30 seconds, so a revoked key may keep working on an instance for up to 30 seconds. This is documented in the security notes.
