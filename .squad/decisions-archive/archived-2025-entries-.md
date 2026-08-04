# Archived Squad Decisions — 2025

Consolidated, durable record of Squad decisions from 2025 worth preserving.

---

## Admin Destination Registry Contract (#934)

| Field | Value |
|-------|-------|
| **Date** | 2025-12-21 |
| **Agent** | Ripley |
| **Status** | closed |
| **Issue** | 934 |

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

## Client-Side Manifest for Essential/Advanced Mode (#937)

| Field | Value |
|-------|-------|
| **Date** | 2025-01-13 |
| **Agent** | Ash |
| **Status** | closed |
| **Issue** | 937 |

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

## Command Palette Extension (#938)

| Field | Value |
|-------|-------|
| **Date** | 2025-12-21 |
| **Agent** | Ripley |
| **Status** | closed |
| **Issue** | 938 |

**Global mount via provider in `Layout.tsx`**: Moved ownership from `SettingsShell` (route-scoped) to `<GlobalCommandPaletteProvider>` wrapping `<Outlet />`. Only way Ctrl+K works on `/admin/manage` without per-shell listener.

**Context + hook**: `useCommandPalette` and `CommandPaletteContext` in separate `commandPaletteContext.ts` file (shipping hook from component file fails `react-refresh/only-export-components`).

**Settings metadata**: Via TanStack Query `['settings','metadata']` and `['settings','groups']` keys with 5-minute stale time, gated `enabled: Boolean(user) && isOpen` so fetch doesn't happen until palette opens.

**Admin destinations source of truth**: Palette assembles from three sources: (1) `ADMIN_DESTINATIONS` filtered by role/permission, (2) `SETTINGS_NAVIGATION` restricted to `scopeId === 'user'`, (3) settings metadata → per-property items. Admin-scope settings entries would duplicate destinations (dropped).

**Deep-linking a setting**: `?field=<propertyName>` bypasses Essential mode via URL (user's preference untouched). `SettingsPagelet` emits `data-setting-property="{sectionKey}.{propertyName}"`; RAF-scheduled `document.querySelector` scrolls field into view, applies `.pf-setting-focus` for 2s (respects `prefers-reduced-motion`). Uses suffix match because palette only knows property name, not section key. URL is copy-pasteable; Back returns to pre-focus mode.

**Unified search**: Removed `<SettingsSearch>` from shell toolbar. Palette is single global settings search entry; in-page `q=` filter remains for local narrowing.

**Curated destructive actions** gate on `window.confirm`: Sign out (destructive), Refresh admin overview (farm-admin only), Switch theme.

**Validation**: 0 lint errors, 0 warnings. Build 11.68s. 2866 tests pass (added 1 file + 14 tests, deleted 3 SettingsSearch tests).
