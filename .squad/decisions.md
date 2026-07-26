# Decisions

## Upload Thumbnail Replacement (#842)

- Store each replacement thumbnail under a unique immutable filename.
- Promote the validated temporary PNG before updating the model metadata pointer; delete the previous thumbnail only after the database commit.
- On validation, storage, cancellation, or commit failure, delete only the new candidate and retain the prior metadata and file.
- Use `RowVersion` for ETags where the provider generates it, with `UpdatedAt` as the provider-neutral concurrency token and ETag fallback.
- Treat `If-Match` as optional for compatibility; when supplied, stale values return HTTP 412. EF concurrency still protects overlapping writes.

**Status:** APPROVED (PR #856, commit `32659d2db`). Zoe verdict APPROVED after verifying endpoint auth, ETag, atomicity, rollback, cleanup, migrations, routing, and test matrix. All validations passing; pre-existing MySQL deployment failure unrelated.

---
## Merged from Inbox: 2026-07-13T13:10:45Z

## Decision: Hicks Reviewer Model Upgrade to gpt-5.6-sol

| Field | Value |
|-------|-------|
| **Date** | 2026-07-12T17:17:53.916-07:00 |
| **Agent** | Scribe |
| **Status** | Proposed |
| **Requested by** | Jeff Papiez |

## Directive

Update the GPT code reviewer (Hicks) persistently from GPT-5.5/older GPT-5.x to model `gpt-5.6-sol` with reasoning effort `max`.

## Scope

- `.squad/team.md`
- `.squad/routing.md`
- `.squad/config.json`
- `.squad/casting/registry.json`
- `.squad/agents/hicks/charter.md`

## Rationale

Model upgrade enables deeper reasoning capability for complex backend code reviews. Sol variant optimized for coding tasks; max reasoning maximizes rigor for triple-consensus gate.

## Notes

- Preserve append-only semantics in all files.
- Do not modify application code, GitHub issues, or PR #741.
- Applied: Hicks history updated; used `gpt-5.6-sol/max` for 2026-07-13 #708 review.

---
## Decision: Narrow Global Heading Typography Scope

| Field | Value |
|-------|-------|
| **Date** | 2026-06-03T12:42:57-07:00 |
| **Agent** | Lambert |
| **Status** | Proposed |

## Decision

Limit the shared display-heading rule in `src/Web/ReactApp/src/index.css` to `h1` and `h2`.
Leave `h3` through `h6` on the default semantic typography path unless a surface opts into display styling explicitly.

## Rationale

The previous global rule forced Bebas and uppercase styling onto every heading level,
which made secondary headings overly loud on settings and unrelated pages.
Narrowing the rule solves the issue at the design-system layer instead of relying on
page-specific overrides.

## Impact

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

## Recommendation

Use a normalized query model inside the existing settings shell:

- `scope=user|system|admin`
- `tab=<category>`
- `sub=<sub-page>`

Keep the current `/settings` route alive for Phase 1, but resolve legacy `?tab=` links through shared navigation helpers so the shell can infer the correct scope and destination.

## Why

This lets the frontend ship the new User/System/Admin information architecture without moving page components or changing route ownership yet.
It also keeps existing bookmarks working while the sidebar, command palette, and search all move onto the new scope-aware model.

## Compatibility Notes

- Legacy `tab=notifications` now resolves to User Settings → Profile → Notifications.
- Legacy `tab=users&sub=api-keys` now resolves to User Settings → Profile → API Keys.
- Legacy operational tabs such as `system`, `users`, and `data` continue to resolve into Admin categories.


---
## Decision: Lockout Policy Dropped for #785/#816/#817

**Date:** 2026-07-21
**Author:** coordinator (via owner directive from jpapiez)
**Status:** ACTIVE — supersedes all prior clean-room / author-lockout guidance on the mobile snapshot line

**Ruling:**
Owner (jpapiez) has rescinded the #785/#816/#817 clean-room lockout policy. The #816 and #817 GitHub issue bodies have been amended at source to remove the hardcoded lockout rosters; those issue bodies are now authoritative.

**New governing rules:**
1. **No agent lockouts** on #785/#816/#817. No one is barred from implementing, revising, advising, contributing, or producing evidence.
2. **Author owns all revisions, including post-REJECT.** On a trio REJECT, the SAME author revises their own branch (standard iterate-on-feedback). No forced handoff, no reviewer-authored-fix routing.
3. **Clean-room isolation rescinded.** Prior-attempt learnings (Dietrich, Apone, Crowe, Morse, or any other source) may be shared and consulted freely if useful. Deriving criteria from acceptance criteria + iOS Review Rubric remains good hygiene, but is not a rule.
4. **Findings-hygiene "describe properties not mechanisms" cancelled.** Reviewers may give direct, specific, actionable findings including concrete mechanism suggestions and fixes. Standard code-review posture.

**Gates that STAND:**
- 3-way adversarial trio review (Bishop / Hicks / Vasquez must converge on unanimous APPROVE) before any PR opens. Owner dropped lockout, not the review gate.

**Ownership per owner decision:**
- #816: Clemens is sole owner. Iterates own branch on REJECT. Under review at HEAD `6c57ec067010626f631c933550db475eb7e9d317`.
- #817: Drake is owner. Parked on #816-merge data dependency (not a lockout). Session `bfb226f0-4d0a-49ce-bb65-3e7cb61338b1`.
- Kane: unblocked.

**Prior orders now VOID:** all "no standby-author content relays," "clean-room isolation halts," "no locked-author-attributed review lenses," "no Apone intel to Drake," "reviewer-authored-fix workflow," and "describe properties not mechanisms" guidance issued earlier in the mobile snapshot coordination session.

**Coordinator handoff:** feature/705 session (this coordinator, `10d59103`) now owns the mobile snapshot coordination end-to-end. Prior coordinator session (`7188e04c`) stood down after handing over.

**References:** #785, #816, #817

---
## Decision: Sync Feature 705 After Every Merge

**Date:** 2026-07-22
**Author:** coordinator (via owner directive from jpapiez)
**Status:** ACTIVE

After each issue pull request merges, immediately synchronize the local
`feature/705-operator-redesign` branch with
`origin/feature/705-operator-redesign` before dispatching, rebasing,
validating, or integrating dependent work.

Confirm local and remote alignment, then use the refreshed remote tip as the
base for subsequent issue branches and immutable-SHA reviews.
**Note**: currentUserRole + gate were already partially implemented on origin/development; this PR formalizes branch and adds explicit test coverage.

---
---
date: 2026-07-25
owner: Multiple
status: closed
issue: 931
---

## Epic #931 Resolved — Admin Console & Settings Redesign (Four Gate Rounds, Six Defects Fixed)

**Scope**: Issues #932–#941, merged to `feature/admin-console-redesign` PR #955.

### Final Status

- **All ten child issues merged**: #932 (UI primitives), #933 (admin overview), #934 (destination registry), #935 (settings rebuild), #936 (Control Center hub), #937 (essential manifest), #938 (command palette), #939 (redirects), #940 (architecture docs), #941 (regressions).
- **Four gate rounds conducted**: Round 1 (Bishop, Hicks, Vasquez), Round 2 (all three), Round 3 (Hicks locked out; Drake, Newt, Ripley, Lambert). Round 4 (Ripley regate).
- **Six defects found and fixed**:
  1. Deep links to command palette items used bare property names → collided across sections.
  2. DOM `id` attributes from `prop.name` → duplicate IDs on multi-section pages.
  3. Form `name` attributes in `SettingsPagelet` → duplicates across 5 input types on pages rendering multiple sections.
  4. `SettingsPage.extractFieldErrors` pinned validation errors to first section containing field name instead of section that was edited.
  5. Empty Slicing Defaults tab (backend group mapping mismatch).
  6. Authorization regression: per-section save endpoint missing `farm_admin` role guard and validator invocation.

### Most Important Decision for Future Work

**`prop.name` in the settings system is the camelCase JSON wire name, not the .NET property name.** `getSettings` returns raw wire JSON with no normalization. `enabled` appears on 13 settings classes, `intervalSeconds` on 4, `baseUrl` on 3, and several render on the same page. This mismatch caused four separate defects: broken command-palette deep-links, mispaired DOM `id`/`htmlFor`, wrong form `name` attributes, mis-attributed validation errors.

**Treat any bare-property-name lookup in this codebase as suspect until proven otherwise.** Any identifier keyed on `prop.name` alone is a bug and blocks review. Always use section-qualified identifiers: `` `${sectionKey}.${propName}` `` for DOM `id`, DOM `name`, aria relationships, error attribution, and deep-link lookups.

### Additional Process Lessons

1. **ValidationException memberless vs. member-named throws**: A memberless throw (`throw new ValidationException("reason")`) produces an error keyed by section rather than field. **21 of 23 throw sites across settings classes are memberless.** Both backend response shape and frontend error extraction must handle this. When a failure is naturally scoped to a specific property, use member-names form for superior UX; memberless form is acceptable for whole-section errors only.

2. **FieldErrors self-healing vs. sectionErrors non-healing**: `fieldErrors` self-heals via `handleFieldChange` recomputing on every keystroke. Server-sourced `sectionErrors` cannot be re-derived on the client and must be explicitly cleared. Copying the merge pattern between them caused a real bug.

3. **Green test suite insufficient**: Nine implementation agents each delivered fully-passing lint/build/test runs that still contained real defects, including a privilege regression. Every finding was verified by reading the code. Tests cannot substitute for careful code review.

4. **Independent reviewers more effective than any single model**: Round 1 produced three confidently-stated findings factually incorrect on inspection. Round 3 one reviewer cleared code the other correctly flagged. Multi-model review across independent agents caught what any single reviewer missed.

### Decision: Admin Settings Per-Section Save Requires Auth and Validation

**Date**: 2026-07-25 (Hicks)

Both `POST /api/settings` (bulk) and `POST /api/settings/{keyName}` (per-key) now require `[Authorize(Roles = "farm_admin")]` and invoke `IValidatableSetting.Validate()`. Both return the same 400 shape on validation failure. This was missing on the per-key endpoint after #935 moved the settings page's primary save path onto it.

**Rule for future settings work**: Any new endpoint mutating a section must include HTTP-level tests: (1) Unauthenticated → 401, (2) Authenticated but under-privileged → 403, (3) Correctly privileged + valid payload → success + persistence verified, (4) Correctly privileged + invalid `IValidatableSetting` → 400 with populated `errors` dict, (5) Blocklisted section (Telegram/HomeAssistant) → 404 even for admin.

### Decision: Settings Error Attribution — Memberless vs. Member-Named ValidationException Throws

**Date**: 2026-07-25 (Ripley)

- **Memberless throw** (`throw new ValidationException("reason")`): Quick; error only reaches user via the section-level `message` field (alert banner + save banner). Does NOT highlight a specific field.
- **Member-names throw** (`throw new ValidationException(new ValidationResult("reason", new[] { nameof(FooProperty) }))`): Error attached inline next to the field via FormField `error` slot. Visually superior for field-scoped errors.

**Guidance for future authors**: If failure is naturally scoped to a property (bad URL, negative timeout, missing token when toggle is on), use member-names. If failure crosses properties or section-wide (required combination missing, conflict between fields), memberless is acceptable — the alert lands on the card header where whole-section errors belong.

**Do NOT add more section-name-aware heuristics to `SettingsPage.extractFieldErrors`.** Current implementation recognizes exactly one convention (bare key == section key) and further magic will conceal bugs. If frontend needs nuance, backend should send it explicitly.

### Related Architecture Decisions (Settled in This Epic)

- **Settings navigation restructure** (#931 epic scope): Two primary destinations (Settings with User/System scopes, and Admin) with per-group save model. No Save All button in production (`saveAllSettings` is dead code).
- **Essential/Everything toggle via client-side manifest** (#937): Not a backend attribute. Manifest keys on backend `SectionName`; hidden fields survive round-trips unchanged. Search always searches everything.
- **Command palette global mount** (#938): Moved from `SettingsShell` (route-scoped) to `GlobalCommandPaletteProvider` wrapping `<Outlet />` in `Layout.tsx`. Ctrl+K now works on every authenticated route.
- **Admin UI primitives frozen** (#932): `AdminLoading`, `AdminEmpty`, `AdminError`, `AdminSaveBar`, `useDirtyState`, `adminToast`. All downstream pages adopt these; do NOT fork.
- **Admin destination registry contract** (#934): Single typed source of truth (`src/Web/ReactApp/src/features/admin/registry/`). ALL admin UI consumes registry; hardcoded lists are banned.
- **Admin overview endpoint** (#933): Composed from existing health checks; 8s timeout; no 500s (hub is what users hit when broken); graceful degradation on probe failure.
- **Settings architecture documentation** (#940): `docs/SETTINGS_ARCHITECTURE.md` is canonical reference for future agents. Covers two-layer architecture, three routes/scopes, URL contract, deep-link sections, per-group save, essential manifest, global command palette, legacy redirects.
- **Heading typography narrowed** (#941 gate finding): Global display-heading rule limited to `h1` and `h2`; `h3`-`h6` use default semantic path unless opted into display style explicitly.

### Recommendation: Phase 1 Settings IA Query Model

Use normalized query inside existing shell: `scope=user|system|admin`, `tab=<category>`, `sub=<sub-page>`. Resolve legacy `?tab=` links through shared helpers. Keeps existing bookmarks working while new IA rolls out.

### Special Team Directive (User Input)

**Date**: 2026-06-06T10:11:08-07:00

Vasquez must use GPT-5.5 for all 3-way pre-PR reviews (user request from Jeff Papiez).

---
---
date: 2026-06-02
owner: Ash
status: closed
issue: 940
---

## Docs: Settings Architecture Canonical Reference (#940)

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
---
date: 2026-06-06
owner: Bishop
status: closed
issue: 931
---

## Decision: Settings Navigation Restructure Scope (#931 Phase Planning)

**Recommendation**: Two primary destinations: **Settings** (real settings only; default User Settings for all users; System Settings admin-only within Settings) and **Admin** (admin-only operations and management not configuration).

**Proposed Structure**:
- `/settings` → User Settings (theme, locale, items per page, API keys, notifications, passkeys)
- `/settings?scope=system` → System Settings (farm identity, energy/cost defaults, slicing defaults, hardware, integrations, quotas)
- `/admin` → Admin/Operations (system status, workers/jobs, user accounts, login audit, tags, data management)

Separates ownership and intent: User Settings = account-scoped; System Settings = farm-wide; Admin = operations.

**Migration Plan**: Five phases from new information architecture through redirects to legacy query-param cleanup.

**Effort**: L-sized. Frontend composition + routing (high blast radius but no backend invention). Primarily touches sidebar nav, route guards, redirects, command palette/search, settings shell behavior, tests, user mental models.

---
---
date: 2026-01-20
owner: Newt
status: closed
issue: 932
---

## Admin UI Primitives Decision (#932)

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
---
date: 2026-07-25
owner: Lambert
status: closed
issue: 933
---

## Admin Overview Endpoint Response Contract & Aggregation (#933)

**Route**: `GET /api/admin/overview` (auth: `farm_admin`, no caching).

**Response** (camelCase, string enums): `AdminOverviewDto` with `checkedAt`, `subsystems`, `attention`.

**Subsystems** (composed from health checks; aggregates `comprehensive`, `signalr`, `spoolman`):
- `api`: Healthy by responding
- `database`: From `comprehensive` sub-payload
- `backends`: From `comprehensive` sub-payload; per-printer failures become individual `attention` items
- `signalr`: Direct 1:1 mapping; non-Healthy → one `attention` item
- `spoolman`: Direct 1:1 when configured; hidden when "not configured"

**Graceful degradation**: 8s hard timeout. On timeout/exception/partial failure: `api` downgrades to `Degraded`, others become `Unknown`, `admin-overview-probe-failed` `Error` attention item emitted. **A single failing subsystem check must never 500 this endpoint.**

**Attention items** (sorted Error → Warning → Info, stable by title): Database unhealthy, printer backends unreachable, registered health check non-Healthy.

**Enum vocabulary** (serialized as strings): `SubsystemStatus: "Healthy" | "Degraded" | "Unhealthy" | "Unknown"`, `AttentionSeverity: "Info" | "Warning" | "Error"`.

**Client routing**: `actionRoute` always client route (`/admin/system`, `/printers`), never raw API URL.

---
---
date: 2025-12-21
owner: Ripley
status: closed
issue: 934
---

## Admin Destination Registry Contract (#934)

**Single source of truth**: `src/Web/ReactApp/src/features/admin/registry/` with `AdminDestination` type, destination table, and query helpers.

**Files**: `adminDestinations.ts` (types + table + queries), `legacyRedirects.ts` (old-path → new-path), `index.ts` (barrel).

**Type** (`AdminDestination`):
- `id`: Stable, unique
- `label`: Short nav/card/palette label
- `description`: One sentence
- `path`: Canonical route (may include `?query`)
- `icon`: `ComponentType<{ className?: string }>` (MdiIcons)
- `group`: `'overview' | 'operations' | 'users' | 'data' | 'hardware' | 'slicing' | 'integrations' | 'general' | 'automation' | 'quotas'`
- `requiredRole`: `undefined` = `'farm_admin'` (default), `null` = no gate (any authenticated), else exact-role match
- `requiredPermission`: `{ resource, action }` (checked **in addition** to role)
- `keywords`: Palette fuzzy search
- `isHubTile`: `true` = show as card on `/admin` (domain entry points only; deep destinations are palette-only)

**Access gating** via `filterDestinationsByAccess(destinations, { hasRole, hasPermission })`.

**Consumer patterns**:
- Hub (#936): `getHubGroupedDestinations(access)` → grouped by category, filtered, hub-tiles only
- Palette (#938): Iterate `ADMIN_DESTINATIONS`, fuzzy-match, group by category, show ALL (palette is where deep destinations become discoverable)

**Legacy redirects** (28 entries): `from`, `to`, `description`, optional `notes`. Two custom App.tsx components remap query params (`/admin/workers?tab=X` → `?workerTab=X`, `/admin/system` branches on `?tab=`).

**Non-negotiables**:
1. Never delete a redirect; bookmarks depend on them.
2. Never render admin destinations from hardcoded list; consume registry.
3. New destinations MUST be added to registry.
4. Icons from `@/common/components/icons/MdiIcons`.
5. Default role is `farm_admin`; set `requiredRole: null` explicitly for non-admin surfaces.

---
---
date: 2025-01-13
owner: Ash
status: closed
issue: 940
---

## Client-Side Manifest for Essential/Advanced Mode (#937)

**Decision**: Essential/Everything toggle uses client-side manifest (`src/Web/ReactApp/src/features/admin/settings/essential-manifest.ts`) rather than extending backend `SettingDisplayAttribute`.

**Why not backend**: Would require editing 15+ settings classes + metadata mapping + DTO (large surface). Would pull `dotnet build` into validation path for every tweak (product will iterate). "Essential" varies with audience (backend attribute is wrong place).

**Why manifest is safe**:
- Save and validation walk **full** metadata list. Manifest only filters rendering; hidden fields survive round-trips unchanged.
- Section keys use backend `SectionName` value (not React/C# class names), so renames can't drift.
- Missing sections gracefully return false from `isEssentialProperty`.

**Search always searches everything**, independent of mode toggle.

**Toggle is global** (`pf.settings.mode` storage key), not per-tab. Rationale: settings shell only mounts one `SettingsPage` at a time; per-tab preference would surprise users.

**Compiler-rule avoidances**: All four `react-hooks` traps avoided; zero `eslint-disable` comments added.

---
---
date: 2025-12-21
owner: Ripley
status: closed
issue: 938
---

## Command Palette Extension (#938)

**Global mount via provider in `Layout.tsx`**: Moved ownership from `SettingsShell` (route-scoped) to `<GlobalCommandPaletteProvider>` wrapping `<Outlet />`. Only way Ctrl+K works on `/admin/manage` without per-shell listener.

**Context + hook**: `useCommandPalette` and `CommandPaletteContext` in separate `commandPaletteContext.ts` file (shipping hook from component file fails `react-refresh/only-export-components`).

**Settings metadata**: Via TanStack Query `['settings','metadata']` and `['settings','groups']` keys with 5-minute stale time, gated `enabled: Boolean(user) && isOpen` so fetch doesn't happen until palette opens.

**Admin destinations source of truth**: Palette assembles from three sources: (1) `ADMIN_DESTINATIONS` filtered by role/permission, (2) `SETTINGS_NAVIGATION` restricted to `scopeId === 'user'`, (3) settings metadata → per-property items. Admin-scope settings entries would duplicate destinations (dropped).

**Deep-linking a setting**: `?field=<propertyName>` bypasses Essential mode via URL (user's preference untouched). `SettingsPagelet` emits `data-setting-property="{sectionKey}.{propertyName}"`; RAF-scheduled `document.querySelector` scrolls field into view, applies `.pf-setting-focus` for 2s (respects `prefers-reduced-motion`). Uses suffix match because palette only knows property name, not section key. URL is copy-pasteable; Back returns to pre-focus mode.

**Unified search**: Removed `<SettingsSearch>` from shell toolbar. Palette is single global settings search entry; in-page `q=` filter remains for local narrowing.

**Curated destructive actions** gate on `window.confirm`: Sign out (destructive), Refresh admin overview (farm-admin only), Switch theme.

**Validation**: 0 lint errors, 0 warnings. Build 11.68s. 2866 tests pass (added 1 file + 14 tests, deleted 3 SettingsSearch tests).

---
---
date: 2026-06-03
owner: Lambert
status: closed
issue: 941
---

## Heading Typography Scope Narrowed (#941)

**Decision**: Global display-heading rule in `src/Web/ReactApp/src/index.css` limited to `h1` and `h2`. `h3`-`h6` use default semantic typography path unless surface opts into display styling explicitly.

**Rationale**: Previous global rule forced Bebas and uppercase onto every heading level, making secondary headings overly loud on settings and other pages.

**Impact**: Settings, API Keys pages can use `h3`-`h6` without inheriting display styling. Teams wanting display styling below `h2` add it intentionally at component or page level.

---
---
date: 2026-07-25
owner: Newt
status: closed
issue: 931
---

## Team Decision: Bare Property-Name Lookups Are Bugs (#931)

**Root cause across four defects**: Code treating bare property name as identifier when name is only unique inside its section.

**Instances**:
1. Command-palette deep links keyed on `prop.name` collided across sections.
2. DOM `id`s keyed on `prop.name` produced duplicate ids (connections page renders 5 sections; `enabled` declared on 13 settings classes).
3. Form `name` attributes in `SettingsPagelet` (5 control types) emitted duplicates.
4. `SettingsPage.extractFieldErrors` pinned errors to first section whose properties contained field name (was almost never the one being edited).

Only safe global identifier: `` `${key}.${name}` ``. `SettingsPagelet.tsx:101` computes this as `fieldId` — just wasn't used consistently.

**Team rule**: Any identifier keyed on `prop.name` alone is a bug and blocks review. Applies to DOM ids, DOM `name`, routing hrefs, deep-link params, error routing, lookups.

**Concrete guidance**:
- Metadata-driven forms: Always use section-qualified id (`` `${sectionKey}.${propName}` ``) for DOM `id`, DOM `name`, aria relationships, error attribution.
- Backend returns bare or `section.field` error keys: Bare form is by contract tied to section just posted. Never search metadata to guess.
- Two-sided data structures: Test guards must assert **both** directions.

**Related lesson**: Deliberate-break proofs mandatory for every new test. Revert fix, run test, confirm fail, restore. Six agents shipped green suite with real defects; agent doing proofs shipped clean. Cost under a minute per fix.


---

# 2026-07-26: Reviewer-rejection author lockout — RESCINDED

**By:** Jeff (repo owner) — directive, restated multiple times
**Status:** Authoritative. Supersedes every prior lockout decision in this file and in
`decisions-archive.md`.

## Decision

**When a reviewer rejects work, the original author fixes it.** There is no author lockout.
Nobody is ever excluded from an artifact because they authored a version of it that was rejected.

- Reviewers report *what* is wrong and *why*. They do not assign who does the fix.
- Repeated rejection is a signal to give the author better information — the specific finding,
  a failing test, a clearer spec — not a signal to rotate authors.
- Reassignment is a normal routing decision (skill gap, capacity, explicit user request) and must
  be justified on those grounds. It is never a consequence of rejection.
- Do not add a team member to work around a rejection.

## Why this kept coming back

The user rescinded this rule more than once and it kept reappearing. The cause was **duplication**,
not forgetfulness: the rule was written into twelve separate files, including the coordinator's own
agent definition (reloaded into the system prompt every session) and several agent `history.md`
files (read at every spawn). Recording the rescission in `decisions.md` alone could never win
against those, because they are loaded earlier and treated as governance.

## Files corrected

| File | Why it mattered |
|---|---|
| `.github/agents/squad.agent.md` | Coordinator definition — loaded into system prompt every session. **Primary cause.** |
| `.github/skills/reviewer-protocol/SKILL.md` | An entire skill dedicated to enforcing lockout |
| `.github/skills/agent-collaboration/SKILL.md` | Told every agent with review authority to lock the author out |
| `.github/rai-policy.md`, `.squad/templates/rai-policy.md` | Rai red-verdict escalation path |
| `.github/Rai-charter.md`, `.squad/templates/rai-charter.md`, `.squad/agents/Rai/charter.md` | Rai's charter, three copies |
| `.squad/templates/fact-checker-policy.md` | Fact Checker contradiction path |
| `.squad/issue-lifecycle.md`, `.squad/templates/issue-lifecycle.md` | Squad-member PR review |
| `.squad/agents/{bishop,brett,dallas,hicks,kane,lambert,ripley,vasquez}/history.md` | Carried it as a **learning**, re-teaching it at every spawn |
| `.squad/agents/drake/charter.md` | Seat was justified by the rescinded rule |

Historical records in `decisions.md` and `decisions-archive.md` are left intact — they document
what happened at the time and are append-only. This entry is the current word.

## Cost of the rule, for the record

During epic #931 a fourth frontend dev (**Drake**) was added purely because Ripley, Newt and Lambert
were each considered locked out of the same artifact. That hire was unnecessary. Earlier, on
2026-05-12, the rule had to be explicitly overridden twice because Lambert was the only Backend Dev
on the roster and the escalation path was unavailable.

## What is retained

Multi-reviewer consensus with fresh-hand rotation stays — it is genuinely effective and caught real
defects across four gate rounds. What is dropped is only the part that **barred the author from
their own fix**.
