# Admin Dashboard (Frontend)

This document covers PRD §9. The dashboard is an administrative tool, not a chat UI.

## 1. Stack and structure

Vue 3 (`<script setup>`, Composition API) + TypeScript (strict) + Vite. Vue Router, Pinia (session/UI state only), TanStack Query for Vue (all server state), PrimeVue (unstyled mode with a small design-token layer) for accessible tables, forms and dialogs, ECharts via `vue-echarts` for charts. The API client is generated from `/openapi/v1.json` with `openapi-typescript` and called through `openapi-fetch`. Generated types are committed, and CI fails if regenerating them produces a diff.

```
web/src/
  api/            # generated schema.d.ts + typed client + query keys
  app/            # router, layout, auth guard, error boundary
  features/
    overview/ requests/ providers/ routing/ tenants/ evaluations/ admin/
      components/  pages/  queries.ts
  shared/
    components/   # DataTable wrapper, EmptyState, ErrorState, Money, Tokens, Duration, EstimatedBadge
    format/       # the ONLY place units are formatted
```

## 2. Screens

| Route | Contents | Role |
|---|---|---|
| `/` Overview | Time-range picker (1 h, 24 h, 7 d, 30 d, custom); tiles: requests, success %, Estimated Cost, tokens, p50/p95 latency, p50/p95 TTFT, fallback %; time-series charts; recent failed requests | Viewer |
| `/requests` | Cursor-paginated table, filters (time, tenant, alias, provider, model, status, error code, had fallback) synced to the URL query string | Viewer |
| `/requests/:id` | Metadata, Routing Decision (chosen + excluded candidates with reasons), Attempt timeline (Gantt-style bars on a time axis), Usage Records with source badges, error details, trace link, copy-able ids | Viewer |
| `/providers` | Provider list + health; create/edit, write-only secret field, connectivity test button with result | Owner (write) |
| `/providers/:id/models` | Models, capability editor (checkboxes + numeric fields), enable toggle, Price history + "add price" | Owner |
| `/routing` | Policies + aliases; policy editor (form view + JSON view), validation, **simulate** panel, version history with diff | Owner (write) |
| `/tenants` | Tenant list, status; detail: limits, Budget gauge (spent / reserved / limit), allowed aliases, API keys (create shows the key once in a dialog with copy + "I've saved it"), usage summary | Operator |
| `/evaluations` | Datasets (upload JSONL, versions, cases), prompts, Comparisons list | Operator |
| `/evaluations/new` | Run Configuration builder (1–6 configurations) with a cost estimate before starting | Operator |
| `/comparisons/:id` | Side-by-side table (quality, errored %, p50/p95 latency, Estimated Cost per case and total), quality-vs-cost scatter, per-class metrics, Case drill-down with diff between runs, export | Viewer |
| `/admin` | Administrators, audit records | Owner |

## 3. UX rules

- **Units**: durations in ms below 1 s and s with 2 decimals above. Tokens use thousands separators (`12,480`). Money is in USD, with 4 significant decimals below $1 and 2 above. Formatting happens only through `shared/format`.
- **"Estimated" is never omitted** next to a cost. `<Money>` requires an `estimated` prop with no default, so the type system enforces this (PRD §9 UX).
- Every query-driven view handles four states: loading (skeleton), error (message + request id + retry), empty (explanation + next action), and data.
- Accessibility: every input has a `<label>`, tables are real `<table>` elements, focus is visible, dialogs trap focus, and charts have a data-table alternative ("View as table"). The Playwright smoke suite runs `axe-core`.
- Layouts are designed for desktop ≥ 1280 px and tablet ≥ 768 px (PRD §9). Phone is not a target.
- Secrets are never shown after they are submitted (PRD §9.3). A secret field shows only "configured · replace".
