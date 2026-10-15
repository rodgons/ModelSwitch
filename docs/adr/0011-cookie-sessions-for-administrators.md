---
status: accepted
---

# Administrators use server-side cookie sessions, and the dashboard is served from the same origin

Administrators sign in with local accounts (ASP.NET Core Identity) and receive an `HttpOnly`, `Secure`, `SameSite=Strict` session cookie. State-changing admin requests also need an antiforgery token. The Vue dashboard is served behind the same reverse proxy as `/admin/*`, so no CORS is configured. We rejected bearer JWTs in the browser because they have to be stored somewhere script can read them (XSS exposure) and cannot be revoked server-side without extra machinery.

## Consequences

- The admin API is not intended for programmatic clients in the MVP. Automating admin work through scoped admin API tokens is post-MVP.
- SSO, SAML and SCIM remain out of scope (PRD non-goal).
