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

---

## Docs: Settings Architecture Canonical Reference (#940)

| Field | Value |
|-------|-------|
| **Date** | 2026-06-02 |
| **Agent** | Ash |
| **Status** | closed |
| **Issue** | 940 |
Rewrote `docs/SETTINGS_ARCHITECTURE.md` as the authoritative admin-surface reference, covering:

- Two-layer architecture (backend attribute discovery + frontend `SettingsShell`)
- Three routes / three scopes (`/settings` user, `/settings?scope=system` system, `/admin` admin) plus `/admin` Control Center hub
- URL contract: `?scope`, `?tab`, `?sub`, `?q`, `?field`
- Tab-to-group `allowedGroups` map from `SettingsShell.tsx:109-194`
- Per-group save model (no Save All button in production)
- Essential vs Everything mode with silent-demotion rename gotcha
- Section-qualified `?field=Section.Property` deep-link contract (bare property names collide because `Enabled` appears in 13 settings classes)
- Global command palette mounted in `Layout.tsx`
- Admin overview endpoint architectural notes (aggregation, 8s timeout, never 500, string-enum serialization)
- Walkthrough for adding new settings section
- Full 27-entry legacy redirect table from `legacyRedirects.ts`

**Recommendation**: Treat `docs/SETTINGS_ARCHITECTURE.md` as canonical for future agents. Extend rather than create new admin/settings docs. Update tab-to-group map and legacy redirects table when new sub-pages ship.

**Known limitations** (out of scope for #940):
- `README.md:139` links non-existent `docs/DATABASE.md`
- `README.md:147` links non-existent `docs/QUICK_REFERENCE.md`
- `Job Queue` group in `HistorySeedingBackgroundService.cs` unreachable via UI (no tab lists it in `allowedGroups`)
- Old design docs under `docs/design/` describe URLs that never shipped

**Reminder**: Backend property rename must update `essential-manifest.ts` in same commit, or setting silently demotes to advanced. This is prominent in the new SETTINGS_ARCHITECTURE.md.

---

## Decision: Settings Navigation Restructure Scope (#931 Phase Planning)

| Field | Value |
|-------|-------|
| **Date** | 2026-06-06 |
| **Agent** | Bishop |
| **Status** | closed |
| **Issue** | 931 |
**Recommendation**: Two primary destinations: **Settings** (real settings only; default User Settings for all users; System Settings admin-only within Settings) and **Admin** (admin-only operations and management not configuration).

**Proposed Structure**:
- `/settings` → User Settings (theme, locale, items per page, API keys, notifications, passkeys)
- `/settings?scope=system` → System Settings (farm identity, energy/cost defaults, slicing defaults, hardware, integrations, quotas)
- `/admin` → Admin/Operations (system status, workers/jobs, user accounts, login audit, tags, data management)

Separates ownership and intent: User Settings = account-scoped; System Settings = farm-wide; Admin = operations.

**Migration Plan**: Five phases from new information architecture through redirects to legacy query-param cleanup.

**Effort**: L-sized. Frontend composition + routing (high blast radius but no backend invention). Primarily touches sidebar nav, route guards, redirects, command palette/search, settings shell behavior, tests, user mental models.

---

## Admin UI Primitives Decision (#932)

| Field | Value |
|-------|-------|
| **Date** | 2026-01-20 |
| **Agent** | Newt |
| **Status** | closed |
| **Issue** | 932 |
**Shipped module**: Scoped `@/common/components/admin`. Barrel export only (no deep imports).

**Components**: `AdminLoading`, `AdminEmpty`, `AdminError`, `AdminSaveBar`, `useDirtyState`, `adminToast`.

**Design decisions frozen** (do NOT renegotiate):
1. No auto-sync `useDirtyState` on prop change. Callers invoke `markPristine(next)` explicitly after successful save/fetch.
2. `beforeunload` guard opt-in via `guardUnload` (default true).
3. `AdminError` disclosure uses `<div>` not `<pre>` (lint rule forbids non-literal `<pre>` children).
4. `AdminSaveBar` renders `null` when clean (no space reserved).
5. `changedLabels` beats `changeCount` in `AdminSaveBar`.
6. `onSave` return type `void | Promise<void>`. Parent owns promise; bar does NOT swallow errors.
7. All primitives set correct ARIA roles up-front (`status`, `alert`, `region`).

**Adopted**: LoginAuditPage (smoke test). All future admin pages adopt these; do NOT fork.

**Downstream rules**: Do NOT add new loading/empty/error/save patterns; do NOT mount another `<Toaster />`; do NOT deep-import; do NOT wrap primitives in extra role containers; do NOT use raw `<pre>` for stringified errors; do NOT add `useDirtyState` auto-sync; do NOT commit anything under `.squad/`.

---

## Heading Typography Scope Narrowed (#941)

| Field | Value |
|-------|-------|
| **Date** | 2026-06-03 |
| **Agent** | Lambert |
| **Status** | closed |
| **Issue** | 941 |
**Decision**: Global display-heading rule in `src/Web/ReactApp/src/index.css` limited to `h1` and `h2`. `h3`-`h6` use default semantic typography path unless surface opts into display styling explicitly.

**Rationale**: Previous global rule forced Bebas and uppercase onto every heading level, making secondary headings overly loud on settings and other pages.

**Impact**: Settings, API Keys pages can use `h3`-`h6` without inheriting display styling. Teams wanting display styling below `h2` add it intentionally at component or page level.
