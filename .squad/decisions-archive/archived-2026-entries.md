# Archived Squad Decisions — 2026

Consolidated, durable record of Squad decisions worth preserving.
Source: `inbox-merged-2026-06-30T20-39-12Z/` (raw per-decision inbox files, since removed).
Only decisions with lasting design/architecture value are retained here; transient
per-issue review outcomes were not carried forward.

---

## Decision: Narrow Global Heading Typography Scope

| Field | Value |
|-------|-------|
| **Date** | 2026-06-03T12:42:57-07:00 |
| **Agent** | Lambert |
| **Status** | Proposed |

### Decision

Limit the shared display-heading rule in `src/Web/ReactApp/src/index.css` to `h1` and `h2`.
Leave `h3` through `h6` on the default semantic typography path unless a surface opts into display styling explicitly.

### Rationale

The previous global rule forced Bebas and uppercase styling onto every heading level,
which made secondary headings overly loud on settings and unrelated pages.
Narrowing the rule solves the issue at the design-system layer instead of relying on
page-specific overrides.

### Impact

Pages such as Settings and API Keys can use `h3`-`h6` without inheriting display
heading treatment automatically.
Teams that want display styling below `h2` should add it intentionally at the component
or page level.

---

## Decision: Phase 1 Settings IA Query Model

| Field | Value |
|-------|-------|
| **Date** | 2026-06-06T08:58:45.350-07:00 |
| **Agent** | Ripley |
| **Requested by** | Jeff Papiez |
| **Status** | Proposed |
| **Scope** | React settings information architecture |

### Recommendation

Use a normalized query model inside the existing settings shell:

- `scope=user|system|admin`
- `tab=<category>`
- `sub=<sub-page>`

Keep the current `/settings` route alive for Phase 1, but resolve legacy `?tab=` links through shared navigation helpers so the shell can infer the correct scope and destination.

### Why

This lets the frontend ship the new User/System/Admin information architecture without moving page components or changing route ownership yet.
It also keeps existing bookmarks working while the sidebar, command palette, and search all move onto the new scope-aware model.

### Compatibility Notes

- Legacy `tab=notifications` now resolves to User Settings → Profile → Notifications.
- Legacy `tab=users&sub=api-keys` now resolves to User Settings → Profile → API Keys.
- Legacy operational tabs such as `system`, `users`, and `data` continue to resolve into Admin categories.
