# Settings And Admin Surface Architecture

PrintFarmer's settings and admin UI is a two-layer system:

- **Backend** (`src/infra/Settings/`) — attribute-driven settings classes discovered by
  reflection. Each class is one persisted section. The `SettingsService` exposes them via
  a small set of unified endpoints.
- **Frontend** (`src/Web/ReactApp/src/features/settings/` and `.../features/admin/`) —
  a single `SettingsShell` React component drives every settings and admin page from the
  URL. It renders a `SettingsPage` that consumes the backend metadata and edits one
  section at a time.

This document is the source of truth for how the two layers connect, how new settings
show up in the UI without any React changes, and where the sharp edges are.

## Routes And Scopes

Every settings and admin page is rendered by `SettingsShell` under one of three routes,
mapped to the three settings **scopes** in
`src/Web/ReactApp/src/features/settings/types.ts`:

| Route | Scope | Access | Rendered by |
|---|---|---|---|
| `/settings` | `user` | Any authenticated user | `<SettingsShell routeScope="user" />` |
| `/admin/settings` | `system` | `farm_admin` role | `<SettingsShell routeScope="system" />` via `SystemSettingsRoute` |
| `/admin/manage` | `admin` | `farm_admin` role | `<SettingsShell routeScope="admin" />` |
| `/admin` | — | `farm_admin` role | `AdminControlCenterPage` (hub, not a shell) |

`/admin` itself is the Admin Control Center hub — a tiled landing page that links out to
individual destinations from `adminDestinations.ts`. Every other admin/settings surface
is one of the three shell routes above with query parameters.

## URL Contract

`SettingsShell` is entirely URL-driven. Deep-links, palette navigation, and the back
button all round-trip through these parameters:

| Parameter | Purpose | Values |
|---|---|---|
| `?scope` | Which of the three shells this URL belongs to. Falls back to the route's `routeScope` when omitted. | `user` / `system` / `admin` |
| `?tab` | Category within the scope (e.g. `general`, `slicing`, `operations`). | See `SETTINGS_CATEGORIES` in `types.ts`. |
| `?sub` | Sub-page within the tab. Falls back to the first sub-page in the category. | See each category's `subPages` array. |
| `?q` | Search query, applied to the current sub-page's settings metadata. | Free text. |
| `?field` | Deep-link to a single property row on the current sub-page. Section-qualified — see below. | e.g. `SystemLog.Enabled`. |

Exactly ONE `SettingsPage` mounts at a time, selected by the key `${scope}.${category}.${subPage}`
into `SUB_PAGE_CONTENT` in `SettingsShell.tsx`. Everything else on the page (sidebar,
scope switcher, breadcrumbs) is chrome around that single mounted content.

## Categories And Sub-Pages

Categories are defined in `SETTINGS_CATEGORIES` (`src/Web/ReactApp/src/features/settings/types.ts`):

- **User scope** (`/settings`):
  - `profile` → Preferences, API Keys, Notifications, Passkeys
- **System scope** (`/admin/settings`):
  - `general` → Farm Defaults, System Config, Automation & Costs
  - `slicing` → Defaults, Bed Types, Slicer Profiles
  - `hardware` → Cameras, NFC Devices, Printer Groups, NFC Bindings, Custom Fields
  - `integrations` → External Services, Webhooks
  - `quotas` (no sub-pages yet)
- **Admin scope** (`/admin/manage`):
  - `operations` → Status, Workers
  - `users` → User Accounts, Login Audit
  - `data` → Tags, Data Management

Categories/sub-pages that render *metadata-driven* settings (Farm Defaults, System Config,
Automation & Costs, External Services, Slicing Defaults) do so by mounting `<SettingsPage
allowedGroups={[...]} />` and filtering the backend metadata down to the listed groups.

## Tab-to-Group Map

The subset of tabs that host `<SettingsPage>` filter backend metadata by group. The
mapping is declared in `SUB_PAGE_CONTENT` in
`src/Web/ReactApp/src/features/settings/pages/SettingsShell.tsx`:

| Tab key | `allowedGroups` on `<SettingsPage>` | Additional content |
|---|---|---|
| `general.farm` | `['General']` | `<FarmSettingsSection />` via `afterContent` |
| `general.system` | `['System', 'Networking', 'Catalog', 'Files', 'Printers']` | — |
| `general.automation` | `['Operations', 'Monitoring', 'Maintenance']` | — |
| `integrations.connections` | `['Integrations']` | `<TelegramSettingsCard />` via `afterContent` |
| `slicing.defaults` | `['Slicing']` | — |

Other sub-pages (`operations.status`, `data.tags`, `hardware.cameras`, etc.) render
bespoke pages instead of a metadata-driven `<SettingsPage>`.

### Groups declared in the code but not reachable via `allowedGroups`

- **`General`** — no backend settings class currently declares `Group = "General"`, so
  the Farm Defaults tab renders only `<FarmSettingsSection />` (its `afterContent`) with
  an empty metadata section list. If you add a class with `[SettingDisplay(Group = "General")]`,
  it will begin appearing on that tab automatically.

### `Job Queue` — fixed during this epic

`HistorySeedingBackgroundService.cs` declares `Group = "Job Queue"`. Until #939 no tab
listed that group in `allowedGroups` and `SETTINGS_GROUP_TO_LOCATION` had no entry for
it, so the section rendered nowhere *and* the command palette skipped it — leaving it
unreachable by any route. It is now mapped onto the **Automation** sub-page alongside
`Operations`, `Monitoring` and `Maintenance`, and is a normal configurable section.

> **If you add a new group,** add it in **both** places or it will silently disappear:
> the owning tab's `allowedGroups` in `SettingsShell.tsx`, and `SETTINGS_GROUP_TO_LOCATION`
> in `settings-navigation.ts`. The palette skips any group missing from the latter via its
> `if (!location) continue` guard, and it does so without warning.

## Backend Settings Classes

Settings classes live in `src/infra/Settings/` (and feature-specific sub-folders such as
`Settings/Maintenance/`, `Settings/OctoPrint/`). Each class is a persisted section, keyed
by a stable `SectionName` string.

A typical class:

```csharp
[AppSetting(SystemLogSettings.SectionName)]
[SettingGroup("System", DisplayName = "System",
    Description = "System-level configuration",
    Icon = "pf-icon-system", Order = 10)]
[SettingDisplay(Name = "System Logging",
    Description = "Database logging configuration, retention, and export settings.",
    Icon = "pf-icon-systemlog", Group = "System", Order = 4)]
public class SystemLogSettings : IAppSetting, IValidatableSetting
{
    public const string SectionName = "SystemLog";
    public static string SectionKey => SectionName;

    [SettingDisplay(Name = "Enable Database Logging",
        Description = "Write application logs to the database.",
        InputType = SettingInputType.Boolean, Order = 1)]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [SettingDisplay(Name = "Retention Days",
        MinValue = 1, MaxValue = 365,
        InputType = SettingInputType.Number, Order = 3)]
    [Range(1, 365)]
    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = 30;

    public void Validate()
    {
        if (RetentionDays is < 1 or > 365)
            throw new ValidationException("RetentionDays must be between 1 and 365.");
    }
}
```

Key rules:

- `[AppSetting("SectionName")]` on the class registers it with `SettingsService`.
- `[SettingGroup("System", ...)]` declares the group the class belongs to. Multiple classes
  can share a group; each group is rendered as a sidebar entry on the sub-page whose
  `allowedGroups` includes it.
- `[SettingDisplay(...)]` at the class level provides the display name, description, and
  icon for the metadata card.
- `[SettingDisplay(...)]` at the property level controls how each field is rendered
  (`InputType`, `Order`, `MinValue`/`MaxValue`, `AllowedValues`, etc.).
- `IValidatableSetting.Validate()` runs on save. Throw `ValidationException` to reject
  invalid input — the API translates it into `400 Bad Request` with per-field errors.
- `[JsonPropertyName("...")]` on every property is required. The metadata API exposes
  `property.name` as the `JsonPropertyName`, and the frontend uses that name for the
  save payload, the Essential-mode manifest, and palette deep-links.
- Secrets: set `InputType = SettingInputType.Password` and the metadata surface will
  render the field as `<input type="password">` in the UI.

### Sections that handle their own secrets

Two sections manage encrypted tokens and are **blocked from the generic settings API**
so their secret fields cannot be read or overwritten in the clear:

- `HomeAssistantSettings.SectionName` — served by a dedicated admin controller.
- `TelegramSettings.SectionName` — served by a dedicated admin controller.

They still appear in the UI (Telegram is rendered by `<TelegramSettingsCard />` on the
`integrations.connections` tab), but their save path is separate. Do not try to save them
via `POST /api/settings/{keyName}` — the controller returns `404 Not Found`.

## Save Model — One Section At A Time

There is **no "Save All" button** anywhere in the settings UI. The page renders a **single
page-level save bar** (docked to the bottom of the viewport via the shell's footer slot),
which fans out through the save registry (`settingsSaveRegistry.ts`) to each dirty group.
Individual groups do *not* render their own save buttons. Every save still fires one
section at a time via:

```http
POST /api/settings/{keyName}
Content-Type: application/json

{
  "enabled": true,
  "retentionDays": 30,
  "minimumLevel": "Warning"
}
```

`keyName` is the `SectionName` (`"SystemLog"`, `"NetworkDiscovery"`, `"CostTracking"`, …).

Responses:

- **200 OK** — section saved.
- **400 Bad Request** — validation failed. Body shape:
  `{ "message": "Validation failed for class 'SystemLog'", "errors": { "<propertyName>": "..." } }`.
- **404 Not Found** — the section is blocklisted (`HomeAssistant`, `Telegram`) or does
  not exist.

After a successful save the group calls `state.acceptKeys(savedSectionKeys)`, which advances
the *baseline* for exactly those sections. It deliberately does not call
`markPristine(state.values)`: `markPristine` also replaces the working values, and
`state.values` is the snapshot taken when Save was clicked — so an edit the user made while
the request was in flight would be silently discarded. The page intentionally does not
refetch other groups' values either, as that would clobber unsaved edits elsewhere.
Refreshing the page always reflects server state.

### `saveAllSettings` is dead code

An older batch endpoint (`POST /api/settings`) and its API-wrapper `saveAllSettings` still
exist for tests and seed scripts, but they have zero production callers and the settings
page tests explicitly assert that `saveAllSettingsMock` is **not** invoked on save. Do
not add a "Save All" button — the per-group save is the intended UX.

## Essential vs. Everything Mode

`SettingsPage` renders in one of two modes controlled by a scope-specific persisted
preference:

- **Essential** (default) — hides anything not on the essential list. Fewer knobs,
  friendlier landing for new operators.
- **Everything** — shows every property.

The classification lives in `src/Web/ReactApp/src/features/admin/settings/essential-manifest.ts`
and currently marks **22 properties across 12 sections** as essential. Examples:

- `SystemLog` → `enabled`, `retentionDays`
- `NetworkDiscovery` → `enableDiscovery`, `discoverySubnets`, `backgroundScanEnabled`
- `CostTracking` → `enableAutomaticCostCalculation`, `electricityRatePerKwh`, `defaultMachineHourlyRate`
- `Spoolman` → `baseUrl`

### ⚠️ Rename Gotcha — Silent Demotion

The manifest keys settings by their **backend `SectionName` and `JsonPropertyName`**, not
by property identity. That has one dangerous consequence:

> **Renaming a backend `SectionName` or `JsonPropertyName` silently demotes that setting
> from Essential to Advanced.** The property still exists, still appears in Everything
> mode, still saves correctly — but it disappears from the default Essential landing
> without any build error, without any warning, and without any test failure unless the
> essential-manifest unit tests were updated in the same change.

If you rename a settings property on the backend, you **must** update
`essential-manifest.ts` in the same PR. There is no metadata-side check for this because
the classification is intentionally client-side (see the file's JSDoc for why).

## Search And Deep-Links

`?q=<query>` filters the current sub-page's settings by title, description, group, or
property name. Search matches expand advanced properties even when Essential mode would
otherwise hide them.

`?field=<Section.Property>` deep-links to a single property row. When present:

- `SettingsPage` overrides `effectiveMode` to `'everything'` so the target row is
  visible even if it's advanced.
- The row is scrolled into view and briefly highlighted with `.pf-setting-focus`
  (2-second flash).
- The URL param stays put so the link remains copy-pasteable.

### `?field=` must be section-qualified

The palette generates section-qualified `?field=Section.Property` values (e.g.
`?field=SystemLog.Enabled`) rather than bare property names. This is load-bearing:

> `public bool Enabled` is declared on **13 different settings classes** — several of
> which render on the same page (for example, Telegram, HomeAssistant, Obico, and
> SystemLog can all appear on the integrations or system tabs). A bare `?field=Enabled`
> would scroll to whichever section rendered first, not the one the user wanted.

Selector logic in `SettingsPage.tsx`:

- Dotted param → exact match `[data-setting-property="Section.Property"]`.
- Bare param → suffix match `[data-setting-property$=".Property"]`. Kept for legacy
  bookmarks; do not generate new bare links.

## Global Command Palette

`GlobalCommandPaletteProvider` is mounted **once, globally, in `Layout.tsx`** so `Ctrl+K`
(or `Cmd+K` on macOS) works on every authenticated route — not just settings.

- Provider: `src/Web/ReactApp/src/features/settings/components/GlobalCommandPaletteProvider.tsx`
- Mount point: `Layout.tsx` inside every authenticated `<Outlet />`.

Palette items are assembled from four sources:

1. **Places** — `buildAdminDestinationCommandItems(ADMIN_DESTINATIONS)`. Points at every
   registered admin destination, grouped by hub group.
2. **Settings sections** (user scope only) — `buildSettingsCommandItems()` filtered to
   `scopeId === 'user'`. Avoids duplicating admin destinations that already appear under
   Places.
3. **Individual setting properties** (farm_admin only) — `buildSettingCommandItems(metadata,
   groups)` walks the metadata API and emits one row per property, each linking to a
   `?field=Section.Property` deep-link.
4. **Actions** — a curated list: **Sign out** (any user, with in-app confirmation),
   **Refresh admin overview** (farm_admin — invalidates `ADMIN_OVERVIEW_QUERY_KEY`),
   **Switch to light/dark theme** (any user).

Keyboard handler details:

- Triggers on `Ctrl+K` or `Meta+K`. Ignores modifier combos (Alt, Shift alone) and edits
  inside `<input>`, `<textarea>`, `<select>`, or `contentEditable` elements.
- Confirmations use the in-app `ConfirmationModal`, not `window.confirm`.
- The settings metadata query is disabled until the palette is first opened. This avoids
  a background `401` for signed-out users; the metadata endpoint is `[Authorize]` under
  the hood.

## Admin Control Center Overview

The `/admin` hub renders `AdminControlCenterPage` and fetches
`GET /api/admin/overview` for the health tiles and attention list. That endpoint is
documented in [API.md](./API.md#admin-control-center) — the important architectural
notes here are:

- The endpoint **aggregates existing `HealthCheckService` results**; it does not run
  new probes. Time budget is 8 seconds.
- It never returns 500. On probe failure it marks non-API subsystems `Unknown` and adds
  an `Error`-severity attention item.
- Adding a new tile means either registering the sub-check under the existing
  `comprehensive` health check and adding a `BuildTileFromEntry` / `BuildTileFromSubcheck`
  call in `AdminOverviewService.BuildSubsystems`, or a new top-level health check plus a
  tile builder. Adding a new attention item means appending to `AppendAttentionForEntry`
  or `AppendExternalServicesAttention` in the same service.

## Adding A New Settings Section

The end-to-end steps to expose a new setting in the UI, without touching any React
component code:

1. **Create the settings class** in `src/infra/Settings/` (or a feature-specific
   sub-folder):
   - Add `[AppSetting("<SectionName>")]` at the class level.
   - Pick the `[SettingGroup(...)]` you want the section to live under. If the group is
     new, make sure a sub-page in `SUB_PAGE_CONTENT` includes it in `allowedGroups` — or
     add a new sub-page there.
   - Add `[SettingDisplay(...)]` at the class level (for the section card) and on every
     property (for the field rendering).
   - Add `[JsonPropertyName("...")]` on every property.
   - Implement `IAppSetting`. Add `public const string SectionName = "..."` and
     `public static string SectionKey => SectionName;`.
   - Add `IValidatableSetting.Validate()` if you have cross-field validation.
2. **(Optional) Mark essential properties.** If any property is essential to a
   day-one operator experience, add it to `ESSENTIAL_SETTINGS_MAP` in
   `src/Web/ReactApp/src/features/admin/settings/essential-manifest.ts`. Use the
   `SectionName` as the map key and `JsonPropertyName` values inside the set.
3. **(Optional) Register a palette entry.** Metadata-driven properties automatically
   appear in the palette via `buildSettingCommandItems`. You only need to touch the
   palette code if you want a bespoke command (e.g. a specific action, not a settings
   deep-link).
4. **Test it.** The settings shell picks up the new class from the metadata endpoint
   automatically, but adjust or add tests near the changed class and update
   `essential-manifest.ts` tests if the property count changed.

You do **not** need to write React form code, add a save handler, wire validation, or
add the property to a hand-maintained list. The metadata pipeline handles all of that.

## Legacy Path Redirects

`src/Web/ReactApp/src/features/admin/registry/legacyRedirects.ts` is the canonical list
of moved URLs. It backs both this documentation and the redirect regression tests. Do
not delete entries when you rename a route — add a NEW entry pointing the old path to the
new path so external bookmarks keep working.

The current list:

| Legacy path | Now lands at | Notes |
|---|---|---|
| `/admin/printers` | `/printers` | Duplicate of the top-level Printers destination. |
| `/admin/file-health` | `/admin/manage?tab=operations&sub=status` | Folded into System Status. |
| `/admin/slicer-profiles` | `/admin/settings?tab=slicing&sub=profiles` | Moved into System Settings > Slicing. |
| `/admin/tags` | `/admin/manage?tab=data&sub=tags` | Moved into Admin Console > Data. |
| `/admin/bed-types` | `/admin/settings?tab=slicing&sub=bed-types` | Moved into System Settings > Slicing. |
| `/admin/custom-fields` | `/admin/settings?tab=hardware&sub=custom-fields` | Moved into System Settings > Hardware. |
| `/admin/webhooks` | `/admin/settings?tab=integrations` | Lands on the Integrations tab. |
| `/admin/quotas` | `/admin/settings?tab=quotas` | Moved into System Settings > Quotas. |
| `/admin/data` | `/admin/manage?tab=data&sub=management` | Moved into Admin Console > Data. |
| `/admin/monitoring` | `/admin/manage?tab=operations&sub=status` | Folded into System Status. |
| `/admin/cameras` | `/admin/settings?tab=hardware&sub=cameras` | Moved into System Settings > Hardware. |
| `/admin/security/login-audit` | `/admin/manage?tab=users&sub=audit` | Moved into Admin Console > Users. |
| `/admin/settings-legacy` | `/admin/settings?tab=general` | Legacy alias for the pre-shell admin settings page. |
| `/admin/workers` | `/admin/manage?tab=operations&sub=workers` | Any incoming `?tab=` is remapped to `?workerTab=` so deep-links to a specific worker tab keep working. |
| `/admin/system` | `/admin/manage?tab=operations&sub=status` | Legacy `?tab=services\|logs\|connections\|monitoring` values each map to a specific Operations sub-page. |
| `/users` | `/admin/manage?tab=users&sub=accounts` | Top-level Users route now under the admin namespace. |
| `/settings/system` | `/admin/settings?tab=general` | Legacy top-level System Settings shortcut. |
| `/preferences` | `/settings` | Legacy preferences shortcut — now the user Settings page. |
| `/locations` | `/locations/dashboard` | Bare `/locations` lands on the dashboard. |
| `/cameras` | `/admin/settings?tab=hardware&sub=cameras` | Legacy top-level Cameras page. |
| `/nfc-devices` | `/admin/settings?tab=hardware&sub=nfc` | Legacy top-level NFC Devices page. |
| `/statistics` | `/analytics?lens=production` | Legacy Statistics route — now the Analytics production lens. |
| `/statistics/costs` | `/analytics?lens=cost` | Legacy statistics cost view — now the Analytics cost lens. |
| `/slicer-profiles` | `/admin/settings?tab=slicing&sub=profiles` | Legacy top-level Slicer Profiles page. |
| `/slicer/import-official` | `/profiles/import` | Legacy import-official shortcut — now the shared profile import wizard. |
| `/slice-jobs` | `/admin/manage?tab=operations&sub=workers&workerTab=jobs` | Legacy Slice Jobs list — now the Jobs tab under Workers. |
| `/files/projects` | `/projects` | Legacy nested Projects path. |

Most redirects drop incoming search params (they use a plain `<Navigate>`). The two
exceptions — `/admin/workers` and `/admin/system` — remap parameters so their historical
deep-links keep working; see the `notes` field in `legacyRedirects.ts` for each rule.

## File Locations

Backend:

- Attributes and interfaces: `src/infra/Settings/` (e.g. `SettingDisplayAttribute.cs`,
  `IAppSetting.cs`, `IValidatableSetting.cs`).
- Service: `src/infra/Settings/SettingsService.cs`.
- Settings classes: `src/infra/Settings/` and feature-specific sub-folders.
- Admin overview: `src/api/Controllers/Admin/AdminOverviewController.cs`,
  `src/api/Services/Admin/AdminOverviewService.cs`,
  `src/infra/Dtos/AdminOverviewDto.cs`.
- Settings HTTP controller: `src/api/Controllers/UnifiedSettingsController.cs`.

Frontend:

- Shell: `src/Web/ReactApp/src/features/settings/pages/SettingsShell.tsx`.
- Metadata-driven page: `src/Web/ReactApp/src/features/admin/pages/SettingsPage.tsx`.
- Categories / scopes: `src/Web/ReactApp/src/features/settings/types.ts`.
- Group → location map: `src/Web/ReactApp/src/features/settings/settings-navigation.ts`.
- Admin destination registry: `src/Web/ReactApp/src/features/admin/registry/adminDestinations.ts`.
- Legacy redirects: `src/Web/ReactApp/src/features/admin/registry/legacyRedirects.ts`.
- Essential manifest: `src/Web/ReactApp/src/features/admin/settings/essential-manifest.ts`.
- Command palette: `src/Web/ReactApp/src/features/settings/components/GlobalCommandPaletteProvider.tsx`.
- Palette mount: `src/Web/ReactApp/src/common/components/Layout.tsx`.

## Related Documentation

- [API.md](./API.md) — HTTP contracts for the settings and admin-overview endpoints.
- [UI.md](./UI.md) — general frontend documentation, including the Admin surface.
