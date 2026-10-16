# Admin Dashboard (Frontend)

This document covers PRD §9. The dashboard is an administrative tool, not a chat UI. The UI is English-only in the MVP: strings live in components, with no i18n layer.

## 1. Stack and structure

Vue 3 (`<script setup>`, Composition API) + TypeScript (strict) + Vite. Vue Router, Pinia (session/UI state only), TanStack Query for Vue (all server state), StyleX for styling (constitution, *Stack*), PrimeVue 4 (unstyled mode, styled through pass-through classes from the token layer in [design-system.md](design-system.md)) for dialogs, ECharts via `vue-echarts` for charts. Tables are native `<table>` elements and form controls native `<input>`/`<select>` with `<label>`s, which are accessible as they are. The API client is generated from `/openapi/v1.json` with `openapi-typescript` and called through `openapi-fetch`. Generated types are committed: `web/openapi.json` is checked against the served document by `OpenApiDocumentTests` (rewrite it with `UPDATE_OPENAPI=1`), and `pnpm check:api` fails if regenerating `src/api/schema.d.ts` produces a diff.

**Styles.** `stylex.create` calls live in `*.styles.ts` modules next to the components (and tokens in `shared/design/*.stylex.ts`), not in `.vue` files; templates apply them with `v-bind="stylex.props(...)"`. `app/global.css` holds only a box-sizing reset, and the StyleX plugin appends the compiled rules to it. Fonts are served from `web/public/fonts/` (SIL OFL). Chart colors are the one place outside the token files with hex values (`shared/design/chart.ts`), because canvas charts need concrete colors.

**Development.** `pnpm dev` serves the dashboard on `http://localhost:5173` and proxies `/admin`, `/v1`, `/health`, `/ready` and `/openapi` to the api, so the browser sees one origin (ADR-0011). The api must be reached over HTTPS, because its `__Host-` cookies and antiforgery require it: run it with `ASPNETCORE_URLS=https://localhost:5081` and the ASP.NET development certificate (`dotnet dev-certs https`); the proxy accepts that certificate. `MODELSWITCH_API_URL` overrides the target.

```
web/src/
  api/            # generated schema.d.ts + typed client + query keys
  app/            # router, layout, auth guard, error boundary
  features/
    overview/ requests/ providers/ routing/ tenants/ evaluations/ admin/
      components/  pages/  queries.ts
  shared/
    design/       # StyleX tokens and themes (design-system.md §1)
    layout/       # AppShell, Container, Stack, Inline, Grid, Split, Panel… (design-system.md §9)
    components/   # DataTable wrapper, EmptyState, ErrorState, Money, Tokens, Duration, EstimatedBadge
    format/       # the ONLY place units are formatted
```

## 2. Screens

| Route | Contents | Role |
|---|---|---|
| `/` Overview | Time-range picker (1 h, 24 h, 7 d, 30 d, custom); a `Readout` band (design-system.md §9.2): requests, success %, Estimated Cost, tokens, p50/p95 latency, p50/p95 TTFT, fallback %; time-series charts; recent failed requests with a mini Route Strip | Viewer |
| `/requests` | Cursor-paginated table, filters (time, tenant, alias, provider, model, status, error code, had fallback) synced to the URL query string | Viewer |
| `/requests/:id` | Metadata, Routing Decision (chosen + excluded candidates with reasons), Attempt timeline drawn as the full Route Strip (design-system.md §10), Usage Records with source badges, error details, trace link, copy-able ids | Viewer |
| `/providers` | Provider list + health; create/edit, write-only secret field, connectivity test button with result | Owner (write) |
| `/providers/:id/models` | Models, capability editor (checkboxes + numeric fields), enable toggle, Price history + "add price" | Owner |
| `/routing` | Policies + aliases; policy editor (form view for strategy, Candidates and execution limits + JSON view for everything else), validation, version history with diff. The **simulate** panel waits for `POST /admin/routing-policies/{id}/simulate` (api.md section 3) | Owner (write) |
| `/tenants` | Tenant list, status; detail: limits, Budget gauge (the current period's spent and reserved amounts as two labelled segments against the limit, from the Tenant's `budgetPeriod`), allowed aliases, API keys (create shows the key once in a dialog with copy + "I've saved it"), usage summary | Operator |
| `/evaluations` | Datasets (upload JSONL, versions, cases), prompts, Comparisons list | Operator |
| `/evaluations/new` | Run Configuration builder (1–6 configurations) with a cost estimate before starting | Operator |
| `/comparisons/:id` | Side-by-side table (quality, errored %, p50/p95 latency, Estimated Cost per case and total), quality-vs-cost scatter, per-class metrics, Case drill-down with diff between runs, export | Viewer |
| `/admin` | Administrators, audit records | Owner |

## 3. UX rules

- **Units**: durations in ms below 1 s and s with 2 decimals above. Tokens use thousands separators (`12,480`). Money is in USD, with 4 significant decimals below $1 and 2 above. Formatting happens only through `shared/format`.
- **"Estimated" is never omitted** next to a cost. `<Money>` requires an `estimated` prop with no default, so the type system enforces this (PRD §9 UX).
- Every query-driven view handles four states: loading (skeleton), error (message + request id + retry), empty (explanation + next action), and data.
- Accessibility: every input has a `<label>`, tables are real `<table>` elements, focus is visible, dialogs trap focus, and charts have a data-table alternative ("View as table"). The Playwright smoke suite runs `axe-core`.
- Layouts are designed for desktop ≥ 1280 px and tablet ≥ 768 px (PRD §9). Phone is not a target, but nothing scrolls the page horizontally below 768 px ([design-system.md §9](design-system.md#9-responsive-behavior-and-primitives)).
- Visual rules (tokens, type, spacing, buttons, links, layout primitives) are in [design-system.md](design-system.md). Components never use raw colors, sizes or spacing.
- Secrets are never shown after they are submitted (PRD §9.3). A secret field shows only "configured · replace".
