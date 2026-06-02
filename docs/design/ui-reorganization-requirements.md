# UI Reorganization Requirements

**Author:** Dallas (Lead / Architect)
**Date:** 2026-06-01
**Status:** Approved for implementation
**Inputs synthesized:** Newt PoC, Ferro PoC, Brett Bambuddy research, Jeff's nav-consolidation directive
**Implementers:** Ripley (frontend), Lambert (backend)

**Tracking issues:** F1 #435 · F2 #436 · F3 #437 · F4 #438 · F5 #439 · F6 #440

## Executive Summary

We are consolidating PrintFarmer's navigation by collapsing the three analytics
links into one destination, folding admin-style configuration (API Keys, NFC
Bindings, Printer Groups) into the existing Settings shell, and adding a
first-class System status surface (CPU, memory, disk, service versions) backed
by a new `GET /api/system/info` endpoint. The goal is a calmer main nav and a
single, discoverable home for configuration and server health, reusing the
Settings sidebar + sub-tab pattern already shipped in issue #432.

## Design Decision: Organization Model

**Decision: Adopt Newt's domain-category fold-in as the base, layered with two
of Ferro's ideas (ambient System Pulse + KPI-overview-first analytics).**

Three models were on the table:

- **Newt** — fold orphaned nav items into the existing 7 domain categories
  (`general`, `slicing`, `hardware`, `notifications`, `integrations`, `data`,
  `users`) in `SETTINGS_CATEGORIES`. Incremental, low relearning cost.
- **Ferro** — replace the 7 categories with 4 intent zones (Workspace,
  Connectivity, Governance, Platform). Innovative but a full re-label of a
  structure that shipped two weeks ago, and pulls Printer Groups *out* of
  Settings — which contradicts Jeff's directive.
- **Current** — `SettingsShell.tsx` driving `SETTINGS_CATEGORIES` with URL state
  (`?tab=&sub=`), search, and lazy sub-pages.

**Why Newt's model wins:** The `SettingsShell` + `SETTINGS_CATEGORIES` pattern is
already live (`src/Web/ReactApp/src/features/settings/`). Newt's proposal is a
data change to that array plus a few new sub-page mounts — minimal blast radius,
no component rewrite, preserves existing deep links and the search index. Ferro's
intent zones are intellectually cleaner but force every operator to relearn the
IA and require reworking the category/sub-page contract for marginal gain.

**What we take from Ferro:** Two ideas survive because they are additive, not
structural:

1. **Ambient System Pulse** — a top-bar health pill is genuinely better than
   making operators hunt for a page. We ship it *in addition to* a System page,
   not instead of it.
2. **KPI-overview-first analytics** — leading the unified analytics page with a
   cross-cutting KPI row (each KPI tagged with its legacy source) before the
   per-lens detail. This guarantees migration coverage and tells the
   cross-cutting story tabs alone would hide.

**Override noted:** Ferro argued Printer Groups is an operational/organization
concept, not a setting. Jeff's directive (`squad-nav-consolidation-directive.md`)
explicitly says Printer Groups moves into Settings. We follow the directive and
place it under Settings → Hardware. Printer Groups keeps its standalone route
(`/printer-groups`) so it can still be linked contextually from operational
views; only the *primary nav entry* moves into Settings.

**Resulting Settings categories** (additions to `SETTINGS_CATEGORIES`):

| Category | Sub-pages (after) | Change |
|---|---|---|
| General | (none) | unchanged |
| Slicing | Bed Types, Slicer Profiles | unchanged |
| Hardware | Printer Groups *(new)*, Cameras, NFC Devices, NFC Bindings *(new)*, Locations, Custom Fields | +2 sub-pages |
| Notifications | (none) | unchanged |
| Integrations | API Keys *(new)*, Webhooks | API Keys surfaced here |
| Data | Tags, Quotas, Data Management | unchanged |
| Users | User Accounts, API Keys, Login Audit | unchanged (API Keys already mounted at `users.api-keys`) |
| System *(new)* | Status, Workers | new category |

Note: API Keys is already mounted in Settings at `users.api-keys`
(`ApiKeysPage`). Newt's PoC places it under Integrations. We keep the existing
`users.api-keys` mount as the canonical location to avoid a regression, and the
nav "API Keys" link redirects to it. We do **not** duplicate it under
Integrations; the Integrations category gains Webhooks only (already wired via
`SINGLE_PAGE_CONTENT.integrations`). This is the one place we diverge from Newt's
sketch, for backward compatibility.

## Design Decision: Analytics Consolidation

**Decision: One `/analytics` route — a KPI overview row on top, then three
lenses as horizontal tabs (Production, Cost, Fleet/Insights).**

Today three nav links point at three routes:

- Statistics → `/statistics` (`StatisticsPage`)
- Cost Analytics → `/statistics/costs` (`CostDashboardPage`)
- Analytics → `/analytics` (`AnalyticsDashboardPage`)

We unify these under a single nav entry "Analytics" at `/analytics`. The page
uses the shared `Tabs` component (matching Newt's PoC and Bambuddy's tabbed
settings) with deep-linkable lens state via `?lens=production|cost|fleet`
(default `production`). Above the tabs sits a compact KPI summary row (Ferro's
idea) — Jobs Completed, Success Rate, Cost/Print, Filament Spend, Fleet
Utilization — so the headline numbers are visible regardless of active lens.

Tab → existing component mapping (reuse, do not rewrite):

| Lens (tab) | Renders | From |
|---|---|---|
| Production | `StatisticsPage` body | `features/statistics/pages/StatisticsPage.tsx` |
| Cost | `CostDashboardPage` body | `features/statistics/pages/CostDashboardPage.tsx` |
| Fleet / Insights | `AnalyticsDashboardPage` body | `features/analytics/pages/AnalyticsDashboardPage.tsx` |

Rejected: a fully draggable widget dashboard (Bambuddy `/stats`). Brett flags it
as high-effort; defer. Tabs + KPI row deliver the consolidation directive now.

## Design Decision: System Status

**Decision: A dedicated System surface (source of truth) PLUS an ambient System
Pulse pill in the top bar. Both consume one new endpoint.**

- **Primary page:** Settings → System → Status, reusing the Settings shell. A new
  `system` category with a `status` sub-page renders a `SystemStatusPage`. This
  satisfies Jeff's ask for a view showing CPU, memory, disk, and service
  versions, and follows Bambuddy's card-per-section layout (Brett P0).
- **Ambient pill (Ferro):** a `SystemPulsePill` in `Layout.tsx` top bar, colored
  by worst service health, expanding to a popover with CPU/memory/disk meters and
  service versions. Optional second wave — does not block the page.
- **Workers:** the existing `/admin/workers` page moves under Settings → System →
  Workers (matches Newt's `system.workers` sub-page), keeping operator/server
  concerns together.

**Backend (Lambert):** new `GET /api/system/info` returning camelCase JSON (per
serialization rules), string enums for health. Shape (from Brett's research):

```json
{
  "app": { "version": "string", "uptime": "string", "hostname": "string" },
  "cpu": { "cores": 8, "usagePercent": 12.4 },
  "memory": { "usedBytes": 0, "totalBytes": 0 },
  "disk": { "usedBytes": 0, "totalBytes": 0, "archiveBytes": 0, "databaseBytes": 0 },
  "services": [
    { "name": "Backend API", "version": "string", "health": "Healthy" }
  ],
  "database": { "engine": "SQLite", "version": "string", "printerCount": 0, "archiveCount": 0 }
}
```

Data sources on .NET 10: `Environment.ProcessorCount` (cores),
`Process.GetCurrentProcess().WorkingSet64` (process RSS), `DriveInfo` (disk),
`/proc/stat` + `/proc/meminfo` on Linux or `GlobalMemoryStatusEx` P/Invoke on
Windows (host CPU%/RAM). `health` is a string enum (`Healthy` / `Degraded` /
`Critical`) serialized via `JsonStringEnumConverter`. Endpoint lives on the Main
API (not slicer-host) and requires `farm_admin`, matching the Settings gate.

Frontend polls every 30s with a manual Refresh button (`staleTime: 10_000`).

## Feature Breakdown

### F1 — Backend: System Info API

- **Description:** `GET /api/system/info` returning app/cpu/memory/disk/services/
  database metrics. Cross-platform CPU and memory sampling. `farm_admin` gated.
- **Affected:** new `SystemInfoController` in `src/api/Controllers/`; new
  `ISystemInfoService` + implementation in `src/infra/`; register in DI; add
  `SystemInfo` DTOs in `src/shared/`. Add TS types to
  `src/Web/ReactApp/src/types/api.ts` and an `apiClient.getSystemInfo()` method
  in `src/services/api.ts`.
- **Complexity:** M
- **Dependencies:** none (start immediately)
- **Owner:** Lambert

### F2 — Frontend: System Status page + Settings `system` category

- **Description:** Add `system` category (`status`, `workers` sub-pages) to
  `SETTINGS_CATEGORIES`; build `SystemStatusPage` (card-per-section: Application,
  CPU, Memory, Disk, Services, Database) with progress meters and a service
  versions table; mount Workers under `system.workers`.
- **Affected:** `src/features/settings/types.ts` (new category);
  `src/features/settings/pages/SettingsShell.tsx` (`SUB_PAGE_CONTENT` entries
  `system.status`, `system.workers`); new
  `src/features/system/pages/SystemStatusPage.tsx`; reuse existing Workers page
  component.
- **Complexity:** M
- **Dependencies:** F1 (consumes `getSystemInfo`)
- **Owner:** Ripley

### F3 — Frontend: Unified Analytics page

- **Description:** Single `/analytics` page with KPI summary row + 3 lens tabs
  (Production/Cost/Fleet) rendering existing dashboard components. Deep link via
  `?lens=`.
- **Affected:** `src/features/analytics/pages/AnalyticsDashboardPage.tsx` becomes
  the host (or a new `AnalyticsHubPage.tsx`); imports `StatisticsPage`,
  `CostDashboardPage` bodies; `src/App.tsx` routes; refactor the three pages so
  their content is renderable without their own `PageTemplate` wrapper (extract
  body components if needed).
- **Complexity:** L
- **Dependencies:** none for build; coordinates with F5 for nav/redirects
- **Owner:** Ripley

### F4 — Frontend: Move admin items into Settings

- **Description:** Add `hardware.printer-groups` and `hardware.nfc-bindings`
  sub-pages; ensure `integrations` shows Webhooks; confirm API Keys remains at
  `users.api-keys`.
- **Affected:** `src/features/settings/types.ts` (Hardware sub-pages);
  `SettingsShell.tsx` `SUB_PAGE_CONTENT` (`hardware.printer-groups` →
  `PrinterGroupsPage`, `hardware.nfc-bindings` → `NfcBindingsPage`). Keep
  standalone routes for contextual linking.
- **Complexity:** S
- **Dependencies:** none
- **Owner:** Ripley

### F5 — Frontend: Navigation cleanup + redirects

- **Description:** Remove Statistics / Cost Analytics / Analytics → single
  "Analytics" entry. Remove standalone API Keys, NFC Bindings, Printer Groups nav
  entries (now in Settings). Add redirect routes so old URLs survive.
- **Affected:** `src/common/components/Layout.tsx` (nav array);
  `src/App.tsx` (add `Navigate` redirects, see Migration Plan);
  `src/test/features/navigation/navigation-sections.test.tsx` (update
  expectations).
- **Complexity:** S
- **Dependencies:** F3 (analytics route must exist), F4 (settings sub-pages must
  exist) before redirects point at them
- **Owner:** Ripley

### F6 — Frontend: Ambient System Pulse pill (second wave)

- **Description:** Top-bar health pill colored by worst service health, expanding
  to a popover with CPU/memory/disk meters + versions. Reuses `getSystemInfo`.
- **Affected:** `src/common/components/Layout.tsx` (top bar); new
  `src/features/system/components/SystemPulsePill.tsx`.
- **Complexity:** M
- **Dependencies:** F1, F2
- **Owner:** Ripley

## Implementation Order

Parallel track A (backend) and track B (frontend) start together:

1. **F1 (Lambert)** and **F3 + F4 (Ripley)** in parallel — F3/F4 don't need the
   API; F1 unblocks the System work.
2. **F2 (Ripley)** once F1 merges (needs `getSystemInfo`).
3. **F5 (Ripley)** last among the core set — redirects must point at the new
   `/analytics` page (F3) and Settings sub-pages (F4) that already exist.
4. **F6 (Ripley)** second wave, after F1/F2 — additive polish, not gating.

Critical path: F1 → F2 → F6. F3/F4 are independent and can land first. F5 is the
integration gate that flips the user-visible nav.

## Migration Plan

Preserve every existing bookmark with `Navigate` redirects in `src/App.tsx`
(this repo already uses this pattern for `nfc-devices`, `cameras`, `locations`,
`users`). Keep the underlying feature routes mounted where a contextual deep link
still makes sense.

| Old URL | New target | Mechanism |
|---|---|---|
| `/statistics` | `/analytics?lens=production` | `Navigate ... replace` |
| `/statistics/costs` | `/analytics?lens=cost` | `Navigate ... replace` |
| `/analytics` | unified hub (default `production`) | route now renders hub |
| `/profile/api-keys` | keep (per-user route stays open) + nav points to `/settings?tab=users&sub=api-keys` | keep route; renav |
| `/nfc-bindings` | keep route; nav → `/settings?tab=hardware&sub=nfc-bindings` | keep route; renav |
| `/printer-groups` | keep route (contextual); nav → `/settings?tab=hardware&sub=printer-groups` | keep route; renav |
| `/admin/workers` | keep route; nav → `/settings?tab=system&sub=workers` | keep route; renav |

Rules:

- `/profile/api-keys` must remain reachable by all authenticated users (existing
  access decision in `App.tsx` — do not gate behind `farm_admin`).
- Redirects use `replace` so back-button doesn't trap users on the old URL.
- Settings deep links require `farm_admin` (the `/settings` route gate); the
  standalone routes that stay (api-keys, nfc-bindings) keep their current gates.

## Acceptance Criteria

**F1 — System Info API**

- `GET /api/system/info` returns 200 with camelCase JSON matching the documented
  shape; `health` values are string enums.
- CPU%, memory, and disk values are non-negative and populated on both Windows and
  Linux.
- Endpoint requires `farm_admin`; returns 401/403 otherwise.
- Unit/integration test in `Farm.Web.Api.Tests` asserts shape and auth.

**F2 — System Status page**

- Settings sidebar shows a "System" category with Status and Workers sub-tabs.
- `SystemStatusPage` renders Application, CPU, Memory, Disk, Services, Database
  cards with accessible progress meters (`role="meter"` + aria values) and a
  service-versions table using `<th scope>` headers.
- Auto-refreshes every 30s; manual Refresh button works; loading and error
  states use `Spinner` / error pattern.

**F3 — Unified Analytics**

- `/analytics` shows a KPI summary row + three lens tabs.
- Each tab renders the corresponding existing dashboard's data.
- `?lens=production|cost|fleet` deep-links select the correct tab; default is
  `production`.
- Tab strip is keyboard navigable and exposes `tablist`/`tab`/`tabpanel`
  semantics.

**F4 — Admin items in Settings**

- Settings → Hardware shows Printer Groups and NFC Bindings sub-tabs rendering
  `PrinterGroupsPage` / `NfcBindingsPage`.
- API Keys remains reachable at Settings → Users → API Keys.
- Settings search returns these by keyword.

**F5 — Navigation + redirects**

- Main nav shows a single "Analytics" entry; Statistics, Cost Analytics, standalone
  API Keys, NFC Bindings, and Printer Groups entries are gone.
- All seven legacy URLs in the Migration Plan resolve to the correct destination.
- `navigation-sections.test.tsx` updated and green.

**F6 — System Pulse pill**

- Top bar shows a health pill whose color reflects worst service health.
- Pill opens a popover (keyboard accessible, `Escape` closes, focus managed) with
  CPU/memory/disk meters and versions.
- Pill degrades gracefully (hidden or neutral) if `getSystemInfo` fails.

## Parallelization Summary

- **Independent now:** F1 (backend), F3 (analytics), F4 (settings sub-pages).
- **Sequential:** F2 after F1; F5 after F3 + F4; F6 after F1 + F2.
- **Cross-layer coordination:** F1 ↔ F2 share the `SystemInfo` contract — agree
  the DTO/TS types before F2 UI work begins.
