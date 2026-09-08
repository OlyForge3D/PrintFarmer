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

The settings routes share one engine with route-locked scopes:

| Route | Scope | Access | Rendered by |
|---|---|---|---|
| `/settings` | `user` | Authenticated | SettingsShell, personal content only |
| `/admin/settings` | `system` | Per-destination grants | SettingsShell, Farm & Admin Settings |
| `/admin` | None | Authenticated, destinations filtered | AdminControlCenterPage |
| `/admin/status` | None | `system_settings:admin` | SystemStatusPage |
| `/admin/workers` | None | `dispatch-settings:manage` | WorkerManagementPage |
| `/admin/login-audit` | None | `system_settings:admin` | LoginAuditPage |
| `/admin/data-management` | None | `data_management:admin` | DataManagementPage |
| `/admin/power-monitors` | None | `power_monitors:admin` | PowerMonitorSettingsPage |

Operational routes use `AdminDestinationRoute` and `AdminPageShell`. `/admin/settings`
operates under `system` scope with unified grouped direct-leaf navigation,
single-pane mounting (no horizontal settings sub-tabs or scope switcher),
accessible mobile drawer navigation, persistent cross-group workspace search,
in-memory draft transition safety, and single page-level save presentation.
Personal `/settings` (`user` scope) remains a separate profile workspace with
category and sub-tab navigation.

## Grouped Direct-Leaf Navigation & Single-Pane Mounting

Under `/admin/settings` (`system` scope), settings navigation is organized into 8 display groups consuming `adminDestinations.ts`:

1. **Farm**: Farm Defaults (`gen-farm`)
2. **Printing & slicing**: Defaults (`slicing-defaults`), Bed Types (`slicing-bed-types`), Slicer Profiles (`slicing-profiles`)
3. **Hardware**: Locations (`hw-locations`), Printer Groups (`hw-printer-groups`), Cameras (`hw-cameras`), Power Monitors (`hw-power-monitors`), NFC Devices (`hw-nfc`), NFC Bindings (`hw-nfc-bindings`), Custom Fields (`hw-custom-fields`)
4. **Automation & costs**: Automation & Costs (`auto-costs`)
5. **Integrations**: External Services (`int-connections`), Webhooks (`int-webhooks`)
6. **People & access**: User Accounts (`users-accounts`), Roles & Permissions (`users-roles`)
7. **Organization**: Tags (`data-tags`), Catalog (`data-catalog`), Quotas (`quotas`)
8. **System**: System Config (`gen-system`)

In `system` scope, exactly ONE settings-shell content page is mounted at a time in a single-pane layout. Horizontal sub-tabs and scope switchers are hidden for `system` scope to ensure clean, focused leaf editing. Registry destinations in these groups may instead be standalone direct links: Locations, Catalog, and Power Monitors remain correctly labeled navigation entries in the sidebar, admin hub, and search surfaces without becoming settings-shell content.

## NFC Management Authorization

NFC Devices and NFC Bindings management APIs require `nfc_devices:admin`
(also held implicitly by `farm_admin`) for reads, history, linking, CRUD, and
device approval. Services additionally require `Manage` access to associated
printer groups. Reassignments check both the current and destination printers;
unassigned devices and bindings remain farm-level NFC administration resources.
Collections omit inaccessible records; per-ID access returns the same 404 as a
missing record. Linking or assigning an inaccessible or missing printer returns
a uniform 403 without changing records. Firmware heartbeat and scan routes retain
their separate device-token authentication contract.

## Mobile Grouped Navigation

Mobile navigation renders a labeled grouped dropdown control (`<nav aria-label="Settings categories">`) containing all accessible display groups and leaves:

- **Escape Key & Focus Restoration**: Pressing Escape or selecting a destination closes the drawer and restores focus to the mobile control trigger.
- **Accessibility**: Includes proper ARIA expanded states, list semantics, and high-contrast match highlighting for search queries.

## Safe Draft Transitions & Partial Saves

### In-Memory Draft Safety
Draft safety protects unsaved form edits across leaf changes, category switches, workspace exit, and palette navigation:
- **Decision Dialog**: Changing navigation while dirty intercepts the transition with a `ConfirmationModal` ("Unsaved Changes" title, "Stay" and "Discard Changes" options).
- **Stay Behavior**: Preserves current URL, form values, validation state, and field focus.
- **Discard Behavior**: Resets dirty state, discards uncommitted section edits, and proceeds to the pending navigation target.
- **Browser Unload**: An active `beforeunload` listener prompts the browser when attempting to close or navigate away from the tab while form fields are dirty.

### Router-Level Draft Boundary
In-app links that the shell does not own — main-navbar `NavLink`s — and browser
Back/Forward are guarded by a React Router `useBlocker` predicate rather than by
per-link `onClick` handlers. `useBlocker` only functions under a **data router**,
so `src/common/router/AppRouterProvider.tsx` composes the app with
`createBrowserRouter` + `RouterProvider`; the declarative `<Routes>` tree is
hosted unchanged as a descendant of a single splat route. Composing with plain
`BrowserRouter` silently disables this guard and lets drafts be discarded without
a prompt — the defect behind issue 2525.

The Admin Control Center's **Pin admin links** chooser and each admin page's own
pin toggle add explicit, authorized admin destinations beneath the existing
navbar **Admin** section (not the top-level Favorites rail used by regular
non-admin pinned items). Pins are stored only in browser-local
`pf_nav_preferences_v1:${userId}` state as stable registry IDs in the user's
chosen order; they are never defaults, shared across devices, or treated as
permission grants. Layout, the chooser, and the per-page controls share live
state, so pinning or reordering updates the navbar immediately.

Two react-router behaviours the implementation has to absorb:
- **Blocked state is transient.** React Router resets *every* blocker to idle
  whenever any navigation completes, and the shell self-navigates constantly
  (`?q=` commits, `?tab=`/`?sub=` normalisation). The blocked state can therefore
  be created and destroyed inside a single React batch, so the shell records the
  blocked destination in the predicate itself instead of observing
  `blocker.state === 'blocked'` from an effect.
- **Handles go stale.** A captured `proceed`/`reset` throws once the router has
  released the blocker, so both are called defensively and "Discard" falls back
  to resuming the navigation itself. For a Back/Forward the fallback replays the
  recorded history **delta**, not the destination URL: navigating by URL would
  push a duplicate entry, so a discarded Back would leave
  `[Printers, Settings, Printers]` and the next Back would surprise the user by
  returning to Settings.
- **Routers must be disposed.** `createBrowserRouter` calls `initialize()`, which
  installs a `popstate` listener that only `dispose()` removes — and
  `RouterProvider` never disposes. React also invokes state initialisers
  speculatively under StrictMode and keeps one result, so construction cannot be
  assumed to happen once. `AppRouterProvider` therefore tracks every instance it
  builds, disposes the ones React discarded, and defers unmount disposal by a
  microtask so StrictMode's simulated remount does not tear down a live router.
  Without this, every remount strands another router still reacting to
  Back/Forward. Covered by
  `src/test/common/AppRouterProviderLifecycle.test.tsx`.

Regression coverage lives in
`src/test/features/settings/SettingsRealRouterDraftGuard.test.tsx`, which renders
the real `App` — a `MemoryRouter`/`createMemoryRouter` stand-in satisfies the
data-router branch for free and would not have caught this.

### Page-Level Save Presentation & Registry
- **Save Bar Presentation**: Single page-level save bar (`SettingsSaveBar`) docked at the bottom of the viewport, backed by `SettingsSaveRegistryContext`.
- **Dirty Section Requests**: Fanned out per dirty group to `POST /api/settings/{keyName}`. Successful section responses advance baseline values for saved sections.
- **Partial Failure Handling**: If some sections fail validation or network save while others succeed, successful baselines advance, error messages remain pinned to failed sections, and in-flight user edits are strictly preserved.
- **No Batch Saves**: Strictly no `saveAllSettings` or batch `POST /api/settings` calls.

## URL Contract

`SettingsShell` is entirely URL-driven. Deep-links, palette navigation, and the back
button all round-trip through these parameters:

| Parameter | Purpose | Values |
|---|---|---|
| `?scope` | Optional scope, normalized from the route when omitted or inconsistent. | `user` / `system` |
| `?tab` | Category within the scope (e.g. `general`, `slicing`, `users`). | See `SETTINGS_CATEGORIES` in `types.ts`. |
| `?sub` | Sub-page within the tab. Falls back to the first accessible sub-page in the category. | See each category's `subPages` array. |
| `?q` | Search query. Filters the current sub-page's settings metadata (legacy, pre-#2505 behavior) **and** seeds the persistent workspace search box (§ Persistent Workspace Search) on admin routes. | Free text. |
| `?field` | Deep-link to a single property row on the current sub-page. Section-qualified — see below. | e.g. `SystemLog.enabled`. |

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
  - `quotas` → Print Quotas (`QuotaManagementPage`)
  - `users` → User Accounts, Roles & Permissions
  - `data` → Tags

Access is defined once in `ADMIN_DESTINATIONS`: resource permissions, integration
any-of grants, and the farm_admin-only Slicer Profiles exception. Neither the outlet
nor the workspace requires that role globally. Bare/unknown/category URLs select the
first accessible editor; explicit denied editors never mount. No admin URL falls back
to personal content.

The registry classifies destinations as hub/configuration/operational, with configuration
display groups Farm, Printing & slicing, Hardware, Automation & costs, Integrations,
People & access, Organization and System. Stable IDs, including overview
`actionDestinationId`, do not change. These display groups are the admin settings
navigation; they replace the earlier intermediate category-first admin presentation.

Power Monitors (`/admin/power-monitors`), Locations (`/locations`) and Catalog
(`/catalog`) stay standalone configuration links. They count toward workspace/hub
availability even without rendering inside the settings shell. Standalone-only
users see their authorized links and an honest no-editor state without `tab`,
`sub` or `field` editor state.

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
| `general.automation` | `['Operations', 'Monitoring', 'Maintenance', 'Job Queue']` | — |
| `integrations.connections` | `['Integrations']` | Resource-gated Spoolman, Home Assistant and Telegram cards; generic editor requires `system_settings:admin`. |
| `slicing.defaults` | `['Slicing']` | — |

Other sub-pages (`users.accounts`, `data.tags`, `hardware.cameras`, etc.) render
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

Spoolman uses the generic Integrations editor for users with `system_settings:admin`.
A dedicated Spoolman card serves `spoolman:admin` delegates without that permission;
the two editors never mount together for the same section.

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
`?field=SystemLog.enabled`) rather than bare property names. This is load-bearing:

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
3. **Individual setting properties** (`system_settings:admin`, filtered to accessible destinations) — `buildSettingCommandItems(metadata,
   groups)` walks the metadata API and emits one row per property, each linking to a
   `?field=Section.Property` deep-link.
4. **Actions** — a curated list: **Sign out** (any user, with in-app confirmation),
   **Refresh admin overview** (`system_settings:admin` — invalidates `ADMIN_OVERVIEW_QUERY_KEY`),
   **Switch to light/dark theme** (any user).

Keyboard handler details:

- Triggers on `Ctrl+K` or `Meta+K`. Ignores modifier combos (Alt, Shift alone) and edits
  inside `<input>`, `<textarea>`, `<select>`, or `contentEditable` elements.
- Confirmations use the in-app `ConfirmationModal`, not `window.confirm`.
- The settings metadata query is disabled until the palette is first opened. This avoids
  a background `401` for signed-out users; the metadata endpoint is `[Authorize]` under
  the hood.

## Persistent Workspace Search (#2505)

The admin settings shell (`/admin/settings`) renders a **persistent** search box in its
page header — distinct from, and complementary to, the modal `Ctrl+K` command palette
above. It never opens as an overlay: on admin settings routes it's always mounted,
always focusable, and its results appear as an inline listbox beneath the input.

| | Command Palette (`Ctrl+K`) | Persistent Workspace Search |
|---|---|---|
| Mount | Global, one instance in `Layout.tsx`, opens as a modal overlay | Header of `SettingsShell`, gated on `isAdminRoute`; always mounted, never modal |
| Scope | Every authenticated route | Admin settings routes only — never on personal `/settings` (preserves personal/system separation) |
| Result surface | `CommandPalette.tsx`, overlay dialog | `WorkspaceSearchResults.tsx`, inline `role="listbox"` beneath the input |
| Shared logic | Both rank the same `FuzzyResult` shape via `settings-navigation.ts` and highlight matches with the same `HighlightedFuzzyText` component | |

### Typing must never itself navigate

This is the central design invariant, and the reason the box needed new state beyond
`?q`: **typing a character must never, by itself, select and navigate to a result.**
Only an explicit action — pressing **Enter**, or **clicking** a result — commits a
navigation. This matters because `?q` already drove *legacy* auto-navigation behavior
before #2505 (a bookmarked `?q=slicer` lands on the first matching category on load), and
that behavior has to keep working for old links while the *new* persistent box must not
reproduce it while the user is still typing.

`SettingsShell` resolves this with `isSelfAuthoredQuery`: a `lastSelfWrittenQueryRef` records
the most recent `q` value the box itself wrote to the URL. When the URL's `q` changes to a
value that matches that ref, the change is attributed to the box's own typing and the
legacy auto-navigation path is skipped. When `q` arrives some other way — a pasted URL, a
browser back/forward to a bookmarked link, a fresh page load — the ref doesn't match (or is
empty) and the pre-#2505 auto-navigation still applies. This is why the two sit side by
side in tests: a genuinely external `?q=slicer` still auto-navigates on load, while typing
`slicer` into the persistent box, with nothing else changing, does not.

Value-equality alone isn't sufficient, though: a browser back/forward can land on an older
`q` that *coincidentally* equals a value the box previously wrote itself (type "slicer",
navigate to an unrelated category, then go Back). `commitSearchQuery` only ever writes `q`
via a history *replace*, so a genuine back/forward is always reported as a React Router
`POP` navigation (`useNavigationType()`); a same-value match is therefore only trusted when
the most recent navigation wasn't a `POP`, so history restoration always re-triggers legacy
auto-navigation regardless of what the ref remembers.

Explicit selection (`WorkspaceSearchResults`'s `onSelect`, wired to Enter and click) always
retains the query in the URL (`withRetainedQuery()`) and pushes a real history entry, so
Back returns to the pre-selection state rather than replacing it.

### Cross-group, permission-filtered result index

`useSettingsSearchIndex` (`src/Web/ReactApp/src/features/settings/hooks/useSettingsSearchIndex.ts`)
is the single hook backing both result rows in the box and the box's own permission
filtering. It merges three item shapes into one ranked, grouped list:

1. **Destinations** — every entry in `ADMIN_DESTINATIONS` the current user can reach,
   filtered the same way the sidebar and standalone links are (`filterDestinationsByAccess`).
   A delegate who only holds `printers:admin` sees `Printer Groups` but not `Login Audit`.
2. **Settings-nav items** (user scope) — `buildSettingsCommandItems()` filtered to
   `scopeId === 'user'`. This list is not permission-gated (same as the palette's
   equivalent source) — it stays populated even for a signed-out user.
3. **Individual setting fields** — `buildSettingCommandItems(metadata, groups)`, gated on
   the literal `hasPermission('system_settings', 'admin')` grant. This is why a delegate
   with `printers:admin` alone gets zero field results: field search reaches *any*
   settings section, including ones the delegate has no business editing, so it requires
   the broad `system_settings:admin` grant rather than a narrower resource permission.

`enabled: false` (the box is closed/blurred) disables the underlying metadata/groups
queries entirely rather than merely hiding results — no background fetch happens until
the user actually opens the box.

### Exact qualified field navigation

Selecting a field result always lands on `?field=Section.Property` (never a bare
property name) for the same reason the command palette does — see
[§ `?field=` must be section-qualified](#field-must-be-section-qualified) above. The
persistent search reuses `buildSettingCommandItems`, so this qualification is automatic;
there is no separate field-linking code path to keep in sync.

If the resolved field doesn't actually render on the destination page (stale metadata, a
typo carried over from an old link, or — now that field search can reach *any* admin
page — a field that lives elsewhere entirely), `SettingsPage` surfaces a
`toast.error(...)` instead of silently doing nothing. The page and its current editor
stay mounted exactly as they were, and `?field=` stays in the URL so the link remains
inspectable.

### Draft safety is preserved

Explicit selection from the persistent search goes through the exact same
`ConfirmationModal` dirty-guard as every other navigation source (category switch,
palette selection, sidebar link) — see [§ Safe Draft Transitions](#safe-draft-transitions--partial-saves).
Selecting a result while a section is dirty intercepts the navigation with the "Unsaved
Changes" dialog; **Stay** leaves the current URL, query, and form state untouched;
**Discard Changes** resets the dirty section and then proceeds to the selected result.
Typing alone — since it never navigates — never triggers this dialog.

### Maintaining the shared ranking/highlighting code

`settings-navigation.ts` and `HighlightedFuzzyText.tsx` are shared between the modal
palette and the persistent search. If you change fuzzy-match ranking, grouping order
(`KIND_SECTION_ORDER`), or match highlighting, both surfaces pick up the change — verify
both `GlobalCommandPaletteProvider.test.tsx` and `WorkspaceSearchResults.test.tsx` still
pass. `HighlightedFuzzyText` renders one `<span>` per character when there are matches to
highlight; it sets `aria-label={text}` on the wrapping span specifically so the
accessible name stays the literal, space-preserving `text` — per-character spans are
`aria-hidden` and contribute only visual highlighting. Removing that `aria-label` will
silently merge words in the computed accessible name (e.g. "Login Audit" reads as
"LoginAudit" to a screen reader) without failing any visual/snapshot check.

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

## One Default Home Per Admin Destination

Every admin destination is reachable from exactly **one** default surface. The Admin
Control Center at `/admin` is the default home for the whole admin surface, and the
`Admin` entry in the main navigation rail is the one default link that reaches it.

Consequences, all enforced by tests:

- **The rail carries no second route to a Control Center destination.** Maintenance,
  Locations, Analytics, Auto-Dispatch and Catalog used to be anchored rail entries *and*
  hub tiles; the rail entries are gone. Printed Parts (`/parts-inventory`) is also an
  `ADMIN_DESTINATIONS` hub tile, so it follows the same one-default-home rule.
- **The Control Center never links to itself.** `admin-home` is `kind: 'hub'`, so
  neither `OPERATIONAL_DESTINATION_IDS` nor `getStandaloneConfigurationDestinations`
  can render it, `AdminControlCenterPage` passes no `parent` to `PageTemplate`, and
  `resolveAttentionActionRoute` drops any attention action resolving to `/admin`
  exactly (a `/admin/...` child route is still a valid target). Child pages linking
  *back* to the hub are a different surface and are unaffected.
- **The settings workspace is not a second admin directory.**
  `getSettingsGroupedDestinations` returns only destinations embedded under
  `/admin/settings`. Catalog, Locations and Power Monitors carry a `settingsGroup` for
  classification but render their own pages, so the sidebar no longer lists them.
- **One authorized exception, and it is a recovery affordance, not a directory.** A
  delegate whose only configuration grant is one of those standalone destinations can
  still open the settings shell but has no category to render. `SettingsShell` shows the
  `Standalone configuration` link strip only for that user — anyone with an embedded
  settings destination does not see it.

Removing a rail entry must never strand a user. The `Admin` entry is gated on
`requiresAnyAccessibleHubTile`, so every permission that used to unlock a removed rail
entry still lights up `/admin`, including custom roles with no `farm_admin`.

Opt-in **pinning** of an admin destination back onto the rail is separate work (#2527).
A pin is an explicit user choice and is not a default home, so it does not contradict
this rule. Stored navigation preferences naming a removed entry are filtered out by
`uniqueKnownIds`, so legacy automatic ordering is never read as an intentional pin.

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

## Canonical Navigation

All supported React navigation must link directly to the current route. Do not add
compatibility aliases or redirects for renamed URLs, and do not depend on historical
bookmarks in tests. Update every in-app caller and its assertions in the same change as
a route rename.

The settings and admin shell routes are:

- `/settings` for user settings.
- `/admin/settings?tab=<category>&sub=<page>` for system settings.
- `/admin/status`, `/admin/workers`, `/admin/login-audit`, `/admin/data-management`
  for operations.
- `/admin/power-monitors` for standalone Power Monitors configuration.

Use additional query parameters only when the destination owns them. For example, the
canonical slice-job list is
`/admin/workers?workerTab=jobs`. WorkerManagementPage alone reads/writes `workerTab`; tab clicks and browser Back/Forward preserve that ownership.

Default children within a current route hierarchy use React Router index routes. For
example, the Locations dashboard renders at the `/locations` index route without
rewriting the URL.

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
- Router composition: `src/Web/ReactApp/src/common/router/AppRouterProvider.tsx`.
- Metadata-driven page: `src/Web/ReactApp/src/features/admin/pages/SettingsPage.tsx`.
- Categories / scopes: `src/Web/ReactApp/src/features/settings/types.ts`.
- Group → location map: `src/Web/ReactApp/src/features/settings/settings-navigation.ts`.
- Admin destination registry: `src/Web/ReactApp/src/features/admin/registry/adminDestinations.ts`.
- Essential manifest: `src/Web/ReactApp/src/features/admin/settings/essential-manifest.ts`.
- Command palette: `src/Web/ReactApp/src/features/settings/components/GlobalCommandPaletteProvider.tsx`.
- Palette mount: `src/Web/ReactApp/src/common/components/Layout.tsx`.
- Persistent workspace search: `src/Web/ReactApp/src/features/settings/components/WorkspaceSearchResults.tsx`.
- Shared search index: `src/Web/ReactApp/src/features/settings/hooks/useSettingsSearchIndex.ts`.
- Shared fuzzy-match/rank/highlight helpers: `src/Web/ReactApp/src/features/settings/settings-navigation.ts`,
  `src/Web/ReactApp/src/features/settings/components/HighlightedFuzzyText.tsx`.

## Related Documentation

- [API.md](./API.md) — HTTP contracts for the settings and admin-overview endpoints.
- [UI.md](./UI.md) — general frontend documentation, including the Admin surface.
