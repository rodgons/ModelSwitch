# Design System

This document defines the visual language of the admin dashboard ([frontend.md](frontend.md)): direction, tokens, type, color, spacing, containers, buttons, links, borders, the signature Route Strip, and the layout primitives every screen is built from. Components use these tokens and primitives, never raw values. If a screen needs something this file does not cover, add it here in the same change.

**Status: approved 2026-10-14** (violet palette, higher-contrast light theme). The approved rendering is [design-system-specimen.html](design-system-specimen.html): open it in a browser to see every token, both themes and the Route Strip. If this file and the specimen disagree, this file wins, and the specimen is updated in the same change.

## 0. Direction

### 0.1 Brief

- **Subject**: the control room of a self-hosted LLM gateway. Requests come in under a Model Alias, a Routing Policy picks Candidates, Attempts succeed or fall back, and every outcome has an Estimated Cost.
- **Audience**: Administrators. Owners configure Providers and policies. Operators and Viewers investigate traffic, often during an incident, often at the end of a long day.
- **Primary job**: answer *"what happened to this request, which Provider Model served it, and what did it cost?"* in seconds, and spot an unhealthy Provider before Tenants do.

### 0.2 Principles

1. **Show the signal path.** Every request travels the same path: alias → policy → Candidates → Attempts → Usage Record. Screens read left to right in that order, and the path itself is drawn as the **Route Strip** (§10). It is the one bold element of the system, and everything else stays quiet so it can be read.
2. **Legibility over style.** Operators copy ids, compare Key Prefixes and read model identifiers such as `gpt-4o-mini-2024-07-18`. The typeface was chosen so that `0/O`, `1/l/I` and `5/S` are never confused (§3).
3. **Color means something.** Neutral surfaces carry the content. Violet means "you can act here". The four status hues mean status and nothing else.
4. **Honest numbers.** Costs are labelled Estimated. Figures align in tabular columns and are never rounded so much that they mislead.
5. **Calm by default.** Nothing moves unless the Administrator did something. The only exception is the Route Strip drawing itself once, on Request detail (§11).

### 0.3 What we took from the reference, and what we refused

The visual reference was railway.com (studied October 2026). We took principles, not its text, imagery, logo, fonts or colors.

| Kept | Refused, and why |
|---|---|
| Hue-tinted neutral scales defined by distance from the canvas, so a step means the same thing in either theme | Its exact palette. Our accent is a blue-violet chosen for this project (hue 264, `#7139c6`), and the dark base is a violet graphite (`#1a1821`), visibly tinted rather than near-black |
| Depth from lighter surfaces and 1 px hairlines; shadows only on things that float | One radius and one surface for every block (the "card kit"). Radii vary by role, and most page sections sit on the canvas with no card around them (§6) |
| A 4 px grid; compact controls; a 2 px focus ring; a press response on buttons | A `→` appended to button and link text, and all-caps tracked labels above headings. Both are decoration without information |
| Mobile-first `min-width` breakpoints at 640 / 768 / 1024 / 1280 / 1536 px | Marketing-scale serif display type. This is a working tool, so there is one family in two widths |
| The product is the illustration: no stock imagery | Scattered entrance animations. Only one moment is orchestrated |

### 0.4 Layout concept

Content is **left-aligned** everywhere. Numbers are right-aligned in tables and readouts. Nothing is centered except empty states and dialogs.

Overview: one readout band, not a grid of tiles.

```
┌ sidebar ┬──────────────────────────────────────────────────────────────┐
│         │ Overview                                   [ Last 24 hours ▾]│
│ Overview│                                                              │
│ Requests│  Requests │ Success │ Est. cost │ Tokens │ p95 latency │ Fallback
│ Provid. │  48,210   │ 99.31 % │ $41.07    │ 12.4 M │ 1.84 s      │ 2.1 %
│ Routing │  ╱╲_╱╲    │ ‾‾‾╲_‾  │ _╱‾‾╲_    │ ...    │ ...         │ ...
│ Tenants │──────────────────────────────────────────────────────────────│
│ Evals   │  Requests over time (by outcome)          Fallback rate      │
│         │  ▁▂▃▅▇▆▅▃▂▁▂▃▅▆▇▆▅                         ▁▁▂▁▁▃▁           │
│         │──────────────────────────────────────────────────────────────│
│         │  Recent failed requests                                      │
│         │  req_01J…9f3a  balanced  ■■■□✕  upstream_timeout   2 min ago │
└─────────┴──────────────────────────────────────────────────────────────┘
```

Request detail: the Route Strip spans the page, directly under the title.

```
┌──────────────────────────────────────────────────────────────────────────┐
│ req_01J8Z…9f3a   [Copy id]                                    Open trace ↗│
│ balanced · 3 Attempts · served by ollama/llama3.1:8b · Estimated $0.0021  │
│                                                                          │
│ ├──openai/gpt-4o-mini──✕┤···├──openai/gpt-4o-mini──✕┤···├─ollama/llama──✓┤│
│ 0 ms                    812                        1,690           2,404 │
├──────────────────────────────────────────────┬───────────────────────────┤
│ Attempts (table, one row per segment)        │ Routing Decision          │
│ Usage Records                                │ chosen + excluded,        │
│ Error details                                │ with reasons              │
└──────────────────────────────────────────────┴───────────────────────────┘
```

(The `·` separators in the wireframe are a placeholder. The real metadata line is a set of labelled pairs, see §9.2 `Facts`.)

## 1. Implementation

- Styling uses **StyleX** (constitution, *Stack*). Tokens live in `web/src/shared/design/` as `*.stylex.ts` files declared with `stylex.defineVars`. Breakpoints and other compile-time values use `stylex.defineConsts`.
- Vue templates apply styles with `v-bind="stylex.props(styles.x)"`. PrimeVue stays in unstyled mode, and its pass-through (`pt`) options get their classes from the same `stylex.props` calls.
- Components import tokens. They never use hex values, pixel font sizes or ad-hoc spacing, except `0` and `1px` hairlines.
- Theme: both themes are first-class. The default follows `prefers-color-scheme`, and the Administrator can override it from the user menu. The override is stored in `localStorage` and applied with `stylex.createTheme` on `<html>`.
- Fonts are self-hosted variable `woff2` files under `web/public/fonts/` (SIL OFL), with no third-party request and no new npm dependency (§3.1).

```
web/src/shared/design/
  color.stylex.ts       # neutral, accent, status scales + semantic aliases
  type.stylex.ts        # families, sizes, line heights, weights, tracking
  space.stylex.ts       # spacing scale, radii, borders, shadows, motion
  layout.stylex.ts      # container widths, gutters, z-index
  breakpoints.stylex.ts # defineConsts: media queries
  themes.ts             # createTheme(light) / createTheme(dark)
web/src/shared/layout/  # the primitives in §9
web/src/shared/route/   # RouteStrip (§10)
```

## 2. Color

### 2.1 Core palette

| Name | Hex | Role |
|---|---|---|
| Graphite | `#1a1821` | Dark canvas. Tinted toward the accent, never neutral black |
| Mist | `#f8f7fa` | Light canvas |
| Ink | `#14101e` | Light-theme text; also the hue the neutral scale is built on (hue 258) |
| Signal violet | `#7139c6` | The single interactive accent: primary button, focus, selection, links |
| Fallback amber | `#f0b042` / `#814203` | Warning status, and a fallback Attempt in the Route Strip |

### 2.2 Neutral scale

Steps are ordered by distance from the canvas, so `n800` is always primary text and `n100` is always the subtle hover fill, in either theme. Components reference the semantic aliases, not the steps.

| Step | Dark | Light | Alias |
|---|---|---|---|
| canvas | `#1a1821` | `#f8f7fa` | `bg.canvas` |
| surface | `#221f29` | `#ffffff` | `bg.surface` (tables, panels) |
| raised | `#292631` | `#ffffff` + shadow | `bg.raised` (menus, dialogs) |
| n100 | `#302c37` | `#f1f0f5` | `bg.subtle` (hover, code, zebra) |
| n200 | `#3d3a45` | `#d4d0dc` | `border.default` (hairlines) |
| n300 | `#54515d` | `#b4b0bf` | `border.strong` |
| n400 | `#777382` | `#766f85` | `border.control`, `fg.disabled` |
| n500 | `#9e9ba6` | `#554f63` | `fg.muted` (labels, captions, placeholder) |
| n600 | `#bdbbc4` | `#3c3749` | `fg.secondary` |
| n700 | `#dad8df` | `#282334` | `fg.strong` (rare) |
| n800 | `#f2f0f4` | `#14101e` | `fg.default` |

Contrast was checked with WCAG 2.2 relative luminance:
- `fg.default` is ≥ 14.3:1 on surface.
- `fg.muted` is ≥ 5.9:1 on surface and ≥ 4.9:1 on `bg.subtle`.
- `border.control` is ≥ 3.5:1 on surface (WCAG 1.4.11).
- The light theme is held to a higher bar, because thin text on white looks fainter than the same ratio on a dark ground: every text tier, including `fg.muted`, and every `*.fg` reaches **≥ 7:1** (WCAG AAA) on surface and ≥ 6.4:1 on its own `.subtle`. Hairlines are 1.5:1 so table rows stay visible.
- The `n200` / `n300` hairlines are decorative. Never use them as the only boundary of an input.

### 2.3 Accent and status

| Token | Dark | Light | Use |
|---|---|---|---|
| `accent.solid` | `#7139c6` | `#7139c6` | Primary button fill (white text 6.7:1) |
| `accent.solidHover` | `#824ed0` | `#5f2da9` | Primary hover (white text ≥ 5.3:1) |
| `accent.fg` | `#bd94f9` | `#5a2ba1` | Links, active nav, selected text |
| `accent.subtle` | `#34244c` | `#f3edfd` | Selected row, active nav item |
| `focus.ring` | `#bd94f9` | `#7139c6` | 2 px focus outline |
| `success.fg` / `.subtle` | `#5ec994` / `#203c2e` | `#115f38` / `#e5f5ed` | Succeeded, healthy, circuit closed |
| `warning.fg` / `.subtle` | `#f0b042` / `#40331c` | `#814203` / `#fdefd8` | Fallback used, half-open, near Budget |
| `danger.fg` / `.subtle` | `#f1827e` / `#41211f` | `#a51c18` / `#fce9e8` | Failed, circuit open, destructive |
| `danger.solid` | `#a72925` | `#a51c18` | Destructive confirm button fill |
| `info.fg` / `.subtle` | `#5abee2` / `#1c3640` | `#035172` / `#e5f5fa` | Estimated, informational. Shifted to cyan (hue 196) so it never reads as the violet accent |

Every `*.fg` is ≥ 5:1 on surface in dark and ≥ 7:1 in light (§2.2).

Rules:
- Status is never shown by color alone. Badges carry a text label, and Route Strip segments carry an end glyph (§10). This meets WCAG 1.4.1.
- `EstimatedBadge` uses `info`. Estimated Cost is a provenance, not a warning.
- Status pairs are always `fg` on `subtle`.
- Chart series take the categorical palette from the chart theme, never the status hues. Status hues mean status inside charts too.
- The only alpha colors are `overlay.scrim` (black at 60 %) and `border.onSolid` (white at 15 %).

## 3. Typography

### 3.1 Families

One family in two widths, chosen for character distinction. Atkinson Hyperlegible was designed by the Braille Institute so that look-alike glyphs stay distinct, which matters in a tool where people read ids and model names all day.

| Token | Stack | Use |
|---|---|---|
| `font.sans` | `"Atkinson Hyperlegible Next", system-ui, sans-serif` | All UI text and all numbers |
| `font.mono` | `"Atkinson Hyperlegible Mono", ui-monospace, Menlo, monospace` | Machine strings a person copies or compares: request ids, Key Prefixes, Provider Model identifiers, JSON, headers |

- Both are variable fonts (`wght` 200–800). Vendor the two `[wght]` files and subset them to Latin.
- Numbers use the sans with `font-variant-numeric: tabular-nums` (token `type.numeric`); Atkinson Hyperlegible Next ships `tnum`. Do not use the mono to align numbers.
- The mono enables `zero` (slashed zero) through `font-feature-settings: "zero"`.
- Mono is never used for labels, captions or decoration.

### 3.2 Scale

The scale is a short classical progression: 12, 14, 16, 18, 24. The default is 14 px. There are no in-between sizes.

| Token | Size / line height | Weight | Tracking | Use |
|---|---|---|---|---|
| `text.caption` | 12 / 16 | 500 | +0.01em | Table headers, badges, helper text, axis labels |
| `text.body` | 14 / 20 | 400 | 0 | Default UI text, fields, buttons, table cells |
| `text.lede` | 16 / 24 | 400 | 0 | Page descriptions, empty states, dialog bodies |
| `text.title` | 18 / 24 | 600 | -0.01em | Section and panel titles |
| `text.heading` | 24 / 32 | 600 | -0.015em | The page title (one per page) |
| `text.readout` | 24 / 32 | 500 | -0.01em | Readout figures only, always `tabular-nums` |

Rules:
- Use three weights: 400 for reading, 500 for labels, controls and figures, 600 for titles.
- Headings and labels are in sentence case. There are no all-caps labels and no tracked-out eyebrows above headings. A section's title is its label.
- Emphasis comes from weight or `fg` level. Never color or italicize a single word in a heading.
- Prose (help text, empty states, dialog bodies) is at most 68 ch wide (`container.prose`).
- Identifiers are truncated in the middle (`req_01J8Z…9f3a`), with the full value in a tooltip and a copy button. Free text is truncated at the end.

## 4. Spacing

The grid is 4 px. Only these steps exist (`space.*`):

`0 · 2 · 4 · 6 · 8 · 12 · 16 · 20 · 24 · 32 · 40 · 48 · 64`

| Context | Value |
|---|---|
| Inside a control, vertical × horizontal | 6 × 12 (md), 4 × 8 (sm), 8 × 16 (lg) |
| Icon to label | 6 (sm, md) or 8 (lg) |
| Fields or rows within a group | 12 |
| Groups within a section | 24 |
| Between page sections | 40 (tablet) or 48 (desktop) |
| `PageHeader` to first section | 24 |
| Table cell | 8 × 12; 6 × 12 in the dense variant |

Rule: spacing inside a group is always smaller than spacing between groups. Grouping is shown by proximity first, a hairline second and a surface third. Never reach for a surface when space alone works.

## 5. Containers and page structure

| Token | Value | Use |
|---|---|---|
| `container.page` | `max-width: 1440px` | Default page content |
| `container.wide` | fluid | Request Explorer and Comparison tables |
| `container.form` | `max-width: 640px` | Single-column create and edit forms |
| `container.prose` | `max-width: 68ch` | Text blocks inside any container |
| `layout.sidebar` | 232 px expanded, 56 px rail | App navigation |
| `layout.topbar` | 56 px | Time range, theme, Administrator menu |
| `layout.aside` | 360 px | Secondary column in `Split` |

Gutters step up with the viewport: **16 px** below 768, **24 px** from 768, **40 px** from 1280.

Page anatomy: a `PageHeader` (title, optional lede, at most one primary action), an optional `Toolbar`, then sections. Each section is a `title` followed by its content directly on the canvas. A section gets a surface only when its content is a table, a form or a chart, which need a defined edge.

## 6. Borders, radii, elevation

- **Hairlines**: `1px solid border.default`. They separate table rows, section bands and the readout columns. On hover, an interactive surface's border moves to `border.strong`. Hover never adds a shadow.
- **Control borders**: inputs, selects and checkboxes use `border.control` so they meet 3:1.
- **Radii scale with the size and role of the object**, so that hierarchy is visible:

| Token | Value | Objects |
|---|---|---|
| `radius.hair` | 2 | Route Strip segments, badges, checkboxes |
| `radius.control` | 6 | Buttons, inputs, nav items, chips |
| `radius.surface` | 8 | Table and chart surfaces, menus, popovers |
| `radius.dialog` | 12 | Dialogs and sheets |
| `radius.full` | 9999 | Status dots, toggles, avatars |

  A nested element's radius is never larger than its parent's.
- **Elevation**: in dark mode, depth is lightness (canvas → surface → raised). Shadows exist only on things that float: menus, popovers, dialogs and toasts.
  - `shadow.overlay` (light): `0 1px 2px rgb(20 16 30 / .08), 0 12px 32px -8px rgb(20 16 30 / .22)`. The shadow is tinted with Ink, not grey.
  - `shadow.overlay` (dark): `0 0 0 1px rgb(255 255 255 / .08), 0 12px 32px -8px rgb(0 0 0 / .55)`.

## 7. Buttons

Shared base: `inline-flex`, centered, `font.sans` at weight 500, `radius.control`, a 1 px border (transparent unless the variant sets one), and `transition: background-color, border-color, color` at `motion.fast`.

| Size | Height | Padding | Text | Icon |
|---|---|---|---|---|
| `sm` | 28 | 4 × 8 | `text.body` | 14 |
| `md` (default) | 32 | 6 × 12 | `text.body` | 16 |
| `lg` | 40 | 8 × 16 | `text.lede` | 18 |

| Variant | Rest | Hover | Use |
|---|---|---|---|
| `primary` | `accent.solid`, white text; dark theme adds `border.onSolid` | `accent.solidHover` | The one main action of a view, at most one per header, dialog or form |
| `secondary` | `bg.subtle`, `border.default`, `fg.default` | border → `border.strong` | Every other action |
| `ghost` | transparent, `fg.secondary` | `bg.subtle`, `fg.default` | Toolbars, row actions, nav, icon buttons |
| `danger` | `danger.solid`, white text | 8 % darker | Only as the confirm button of a destructive dialog. The button that opens that dialog is `secondary` with `danger.fg` text |

States, shared by all variants:
- **Focus**: `:focus-visible` shows a 2 px `focus.ring` outline with a 2 px offset.
- **Pressed**: `scale(0.98)` for 50 ms. Off under reduced motion.
- **Disabled**: 50 % opacity, `cursor: not-allowed`. Prefer an enabled button that explains why on click over a disabled one with no reason.
- **Loading**: keep the label and width, replace the leading icon with a spinner, and set `aria-busy="true"`.
- **Icon-only**: square, with an `aria-label` that is also shown as the tooltip.

Labels say exactly what happens, as a verb in sentence case: "Test connection", "Add price", "Revoke key". The result keeps the same verb: "Revoke key" → toast "Key revoked". Buttons never carry decorative arrows.

## 8. Links

| Style | Look | Use |
|---|---|---|
| Inline | `accent.fg`, 1 px underline at a 3 px offset; on hover the underline grows to 2 px | Inside sentences: help text, empty states, errors |
| Quiet | `fg.secondary`, no underline; on hover → `fg.default` + underline | Ids that open detail views, breadcrumbs, metadata |
| Section | `accent.fg`, weight 500, no underline, text names the destination ("All failed requests") | At the end of a section that shows a preview of a longer list |

- External links (trace viewer, Provider docs) add a 12 px `↗` icon, which is functional because it means "leaves the dashboard". They open in a new tab with `rel="noopener noreferrer"`.
- Navigation is `<a>`/`RouterLink`, actions are `<button>`. They are never swapped.

### 8.1 Other components

| Component | Spec |
|---|---|
| `Badge` | 20 px tall, 0 × 6 padding, `radius.hair`, `text.caption`. Status badges are `*.fg` text on `*.subtle` with a 6 px dot in `currentColor` before the label. `EstimatedBadge` has no dot. A neutral badge (for example "Skipped, circuit open") is `fg.muted` on `bg.subtle`. The label always names the state; the dot is never alone |
| `Sparkline` | 24 px tall, full width of its readout cell. A 1.5 px line, an area fill of the same color at 12 % opacity, and a 2.5 px dot on the last point. `accent.fg` by default, or a status color when the figure is itself a status (fallback rate → `warning.fg`). Decorative (`aria-hidden`); the figure and delta carry the meaning |
| `SegmentedControl` | For 2–4 mutually exclusive views (theme, chart range). A `bg.subtle` track with a 1 px `border.default` border, `radius.control` and 2 px inner padding. Segments are 26 px tall in 13 px weight 500 `fg.secondary`. The selected segment is `bg.surface` with a 1 px `border.default` ring and `fg.default`. Each segment is a button with `aria-pressed` |
| `Field` | `<label>` (weight 500) above the control, with 6 px gaps, then optional help text in `text.caption` `fg.muted`. Inputs are 32 px tall, with 0 × 10 padding, `radius.control`, a `border.control` border and a `bg.surface` fill. Fields that take ids, Key Prefixes or JSON use `font.mono` with the slashed zero |
| `Alert` | A `*.subtle` fill, a 1 px border of the status color at 35 % opacity, `radius.surface`, 12 × 16 padding, and an 18 px status icon before the message. Error alerts follow §12 and end with a `Facts` row holding the request id |
| `ConfirmDialog` | `bg.raised`, `radius.dialog`, `shadow.overlay`, 24 px padding, max 440 px wide. The title names the object ("Revoke key `msk_3F9A2K7Q`?") and the body names the consequence. Actions are right-aligned, Cancel (`secondary`) then the action. For irreversible actions on a named object (revoking an API Key, deleting a Provider), the `danger` button stays disabled until the Administrator types the object's identifier |

## 9. Responsive behavior and primitives

### 9.1 Breakpoints

Mobile-first `min-width` queries, declared once in `breakpoints.stylex.ts`:

| Token | Query | Effect |
|---|---|---|
| `bp.sm` | `(min-width: 640px)` | Toolbar filters move from stacked to inline |
| `bp.md` | `(min-width: 768px)` | **Lowest supported layout** (PRD §9): sidebar as a 56 px rail |
| `bp.lg` | `(min-width: 1024px)` | Sidebar expands; `Readout` shows all figures in one band |
| `bp.xl` | `(min-width: 1280px)` | **Primary target**: `Split` shows its aside; 40 px gutters |
| `bp.2xl` | `(min-width: 1536px)` | No layout change; only `container.page` centers |

Phone is not a target (frontend.md §3). Below 768 px the dashboard must still be usable: no horizontal page scroll, the sidebar becomes a top bar with a menu sheet, and every primitive collapses to one column.

### 9.2 Primitives (`web/src/shared/layout/`)

A feature component that writes its own `display: grid` or a media query is a review finding. The exceptions are charts and the Route Strip.

| Primitive | Props | Behavior |
|---|---|---|
| `AppShell` | none | Sidebar + topbar + scrolling main. Sidebar: menu sheet below md, rail at md, expanded from lg |
| `Container` | `size: 'page' \| 'wide' \| 'form'` | Max-width (§5) and the stepped gutters |
| `Stack` | `gap` | Vertical flow with a uniform gap |
| `Inline` | `gap`, `align`, `justify`, `wrap = true` | Horizontal row that wraps |
| `Grid` | `min` (`narrow` 160 · `field` 200 · `card` 280 · `chart` 420 px, layout tokens), `gap` | `repeat(auto-fit, minmax(min(<min>, 100%), 1fr))`: the column count follows the available width |
| `Split` | `aside` slot, `asideAt = 'xl'` | Main + 360 px aside from the breakpoint; stacked below it |
| `PageHeader` | `title`, `lede?`, `actions` slot | Title and actions share a row; the actions wrap under the title when space runs out |
| `Section` | `title`, `actions?`, `surface = false` | A titled block on the canvas. `surface` adds `bg.surface`, a hairline and `radius.surface` for tables, forms and charts |
| `Readout` | `items: { label, value, delta?, trend?, estimated? }[]` | One band of figures separated by vertical hairlines. Each figure is a `text.caption` label in `fg.muted` (with an `EstimatedBadge` when `estimated`), a `text.readout` value, an optional `delta` line in `text.caption` against the previous period of equal length, and an optional 24 px sparkline. Wraps to rows of 3 below lg and 2 below sm. This replaces separate stat cards |
| `Facts` | `items: { label, value }[]` | A `<dl>` of label/value pairs laid out with `Inline`. Labels in `fg.muted`, values in `fg.default`. Used for request metadata, never a dot-joined string |
| `Toolbar` | default slot | `Inline` with gap 8 that wraps filters |
| `TableScroll` | none | Wide tables scroll inside themselves (`overflow-x: auto`, focusable, labelled). The page never scrolls sideways. Below lg the first column stays in view: its cells take the `table.pinned` style (StyleX has no descendant selectors, so the table opts in per cell; the Request Explorer does) |
| `VisuallyHidden` | none | Screen-reader-only text |

Rules:
- Prefer intrinsic layouts (`Grid` with auto-fit, wrapping `Inline`) to breakpoint switches. Use breakpoints only for the app shell, gutters, `Split` and `Readout`.
- Never hide data at a breakpoint. If a column does not fit, it scrolls inside `TableScroll`.
- Hit targets are at least 24 × 24 px (WCAG 2.5.8).

## 10. Route Strip (signature element)

The Route Strip draws one request's path: each Attempt in order, how long it took, how it ended, and the waits between Attempts. It is the most characteristic thing in this product's world, so it is the only place the system spends visual boldness.

**Anatomy**, on a horizontal time axis that starts when the request is received:
- **Segment**: one Attempt. Its length is proportional to the Attempt's duration (minimum 4 px). It is 8 px tall (detail) or 6 px tall (table), with `radius.hair`. Its fill is the outcome's `*.fg` color.
- **End glyph**: the shape that encodes the outcome, so that color is never the only signal. ✓ succeeded, ✕ failed, ⤳ fell back (failed and another Candidate followed), ⊘ skipped because the circuit was open (a hatched, zero-length marker), ◷ timed out.
- **Gap**: a dotted `border.strong` line for backoff between Attempts. Its length is proportional to the wait.
- **Label** (detail only): the Provider Model identifier above each segment in `font.mono` 12 px, truncated in the middle, with the time ticks below in `text.caption` tabular figures.

| Outcome | Fill | Glyph |
|---|---|---|
| Succeeded | `success.fg` | ✓ |
| Failed, a Fallback followed | `warning.fg` | ⤳ |
| Failed, final | `danger.fg` | ✕ |
| Timed out | `danger.fg` | ◷ |
| Skipped (circuit open) | hatched `border.strong` | ⊘ |

**Variants:**
- `full` (Request detail): spans the content width under the title. Hovering or focusing a segment highlights its row in the Attempts table, and the reverse also works.
- `mini` (Request Explorer row, Overview "Recent failed requests"): 120 px wide with no labels and glyphs only at the ends of segments. It is compact enough to scan down a column, so a fallback pattern shows up as a shape.

**Accessibility:** the strip is an `<ol>` of Attempts styled horizontally, not an image. Each segment has an accessible name: "Attempt 2, openai/gpt-4o-mini, failed after 812 ms, fell back". It has a "View as table" toggle like every chart (frontend.md §3).

## 11. Imagery, icons, motion

- **No decorative imagery.** Data, the Route Strip and charts are the visual content. Empty states use one 24 px line icon, one sentence and one action.
- **Icons**: one outline set with a 1.5 px stroke at 14 / 16 / 18 px, in `currentColor`. An icon next to text is `aria-hidden`.
- **Motion that responds to the Administrator** is welcome when it shows what changed. Menus and sheets open over `motion.base` (200 ms, `cubic-bezier(0.2, 0, 0, 1)`), and colors change over `motion.fast` (120 ms).
- **The one orchestrated moment**: on Request detail, the Route Strip draws its segments left to right in time order, in ≤ 600 ms total, the first time the page renders. Nothing else animates on load: no fade-up sections and no hover lifts on surfaces.
- **Reduced motion** (`prefers-reduced-motion: reduce`): the Route Strip renders complete, the press scale is off, and the skeleton shimmer becomes a static fill.
- **Texture**: none. The canvas is flat.

## 12. Writing in the interface

- Use the [CONTEXT.md](../CONTEXT.md) terms in the UI exactly as in code: Provider, Provider Model, Model Alias, Attempt, Estimated Cost. A word listed under *Avoid* there is wrong on screen too.
- Use sentence case everywhere, and plain verbs. Name things by what the Administrator manages, not by how the system is built: "Spending limit" is the label for a Budget field when the field needs explaining, and "Budget" is the noun.
- **Errors** say what happened and what to do, in the interface's own voice. They show the error code from [api.md §2](api.md) and the request id. They do not apologize. Example: "No Provider Model can serve `balanced` with these Capabilities (`no_eligible_model`). Enable a model that supports JSON-schema output, or add one to the Routing Policy."
- **Empty states** invite the next action: "No Providers yet. Add one to start routing requests." with an "Add Provider" button.
- **Confirmations** name the object and the consequence: "Revoke key `msk_3F9A2K7Q`? Applications that use it will get 401 errors immediately."
