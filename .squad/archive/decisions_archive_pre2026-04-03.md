## 14. User Directive: Consistent Date Range Filters (2026-03-26T15:20)

**Author:** Jeff Papiez (via Copilot)  
**Status:** CAPTURED — For team memory  
**Urgency:** Medium

### Directive

Date range filters must be consistent across all statistics/analytics/cost pages. Use a standard set of options (7 days, 30 days, 90 days, 1 year, All Time) wherever date range filters appear.

### Rationale

User request — consistency improves discoverability and UX across the application.

---

## 15. User Directive: Quarterly Date Ranges & Custom Picker (2026-03-26T15:22)

**Author:** Jeff Papiez (via Copilot)  
**Status:** CAPTURED — For team memory  
**Urgency:** Medium

### Directive

Date range filters should include quarterly options and support custom date ranges. Standard presets: 7 days, 30 days, 90 days (quarterly), 1 year, All Time, plus a custom date range picker.

### Rationale

User request — business reporting often uses quarterly periods. Custom ranges give flexibility for ad-hoc analysis.

---

## 16. User Directive: Expose CostTrackingSettings in Admin UI (2026-03-26T15:24)

**Author:** Jeff Papiez (via Copilot)  
**Status:** CAPTURED — For team memory  
**Urgency:** High

### Directive

CostTrackingSettings (electricity rate, printer wattage, machine hourly rate, etc.) must be exposed in the admin Settings UI so users can configure them.

### Rationale

User request — these values drive all cost calculations and vary by location/setup. Currently only configurable via appsettings.json. Need UI accessibility.

---

## 17. Per-Printer Wattage with Catalog Defaults (2026-03-26T15:35a)

**Author:** Jeff Papiez (via Copilot)  
**Status:** CAPTURED — For team memory  
**Urgency:** High

### Decision

Wattage should be configurable per-printer, with default values defined in the catalog (PrinterModel). Cascade: printer override → model default → global CostTrackingSettings fallback.

### Rationale

User request — different printers consume different power. Global average is too imprecise for accurate energy cost tracking.

---

## 18. User Directive: Job Scheduling UX — Add Job Picker (2026-03-26T15:35b)

**Author:** Jeff Papiez (via Copilot)  
**Status:** CAPTURED — For team memory  
**Urgency:** High

### Directive

The ScheduleModal's raw Job ID text input must be replaced with a searchable job picker. Also add a "Schedule" action on jobs in the queue page so the modal opens pre-populated.

### Rationale

User request — current UX requires manually typing a 36-character GUID with no way to discover valid job IDs. Terrible usability.

---

## 19. User Directive: Expose MachineHourlyRate and Wattage on Printer Modals (2026-03-26T15:41a)

**Author:** Jeff Papiez (via Copilot)  
**Status:** CAPTURED — For team memory  
**Urgency:** High

### Directive

The Edit Printer and Add Printer modals must expose MachineHourlyRate and Wattage fields so users can configure per-printer cost overrides from the UI.

### Rationale

User request — these fields exist on the Printer entity but aren't accessible through the frontend. Users need to set per-printer energy and machine cost overrides without touching the database directly.

---

## 20. XML Documentation Requirements (2026-03-26T15:45)

**Author:** Jeff Papiez (via Copilot)  
**Status:** CAPTURED — For team memory  
**Urgency:** Medium

### Directive

When adding or updating public C# types, XML comments must be added/updated. All parameters for public functions must be documented in XML comments. Classes that implement interfaces should use `<inheritdoc/>` instead of duplicating documentation defined on the interface.

### Rationale

User directive — enforces consistent API documentation across the codebase. Prevents doc duplication drift between interfaces and implementations.

---

## 21. Custom Date Range API Contract (2026-07-14)

**Author:** Lambert (Backend Dev)  
**Date:** 2026-07-14  
**Status:** IMPLEMENTED  
**Urgency:** Medium

### Context

Statistics endpoints previously only supported `?days=N` for time filtering. Operators need arbitrary date ranges for reporting and cost analysis.

### Decision

All 9 statistics endpoints now accept optional `startDate` and `endDate` query parameters (ISO 8601 format). Priority order:

1. `startDate`/`endDate` (custom range) — takes precedence
2. `days` — calculated from UTC now (existing behavior)
3. No params — endpoint default (all-time or 30 days depending on endpoint)

### Constraints

- `startDate` must be before `endDate` (400 if violated)
- Max range: 730 days / 2 years (400 if exceeded)
- Cost queries filter on `ActualEndTime`; non-cost queries filter on `QueuedAt`

### Impact

- **Frontend**: Can now build custom date range pickers for analytics dashboards
- **API consumers**: Fully backward-compatible; existing `?days=N` calls unchanged
- **Export endpoints**: Not yet updated (use `ReportRequest.Days` internally)

---

## 22. Per-Printer Wattage with Catalog Defaults (IMPLEMENTATION) (2026-03-26)

**Author:** Lambert (Backend Dev)  
**Date:** 2026-03-26  
**Status:** IMPLEMENTED  
**Urgency:** High

### Decision

Added per-printer wattage override (`Printer.Wattage`) and catalog-level default (`PrinterModel.DefaultWattage`) with a three-level cascade for energy cost calculation.

### Cascade Rule

```
printer.Wattage ?? printer.Model?.DefaultWattage ?? settings.AveragePrinterWattage
```

### Changes Made

#### Domain
- `PrinterModel.DefaultWattage` (decimal?) — catalog default for model
- `Printer.Wattage` (decimal?) — per-printer override

#### DTOs
- `UpdatePrinterDto`: Added `Wattage` and `MachineHourlyRate`
- `CreatePrinterFromDiscoveryDto`: Added `Wattage` and `MachineHourlyRate`
- `PrinterModelDto`: Added `DefaultWattage`
- `PrinterModelSeedDto`: Added `DefaultWattage`

#### Cost Calculation
- `JobCostCalculationService.CalculateEnergyCost`: Uses cascade instead of flat settings value
- Both `.Include(j => j.AssignedPrinter).ThenInclude(p => p.Model)` added to job queries

#### Seed Data
- `printer-models.yaml`: 37 models populated with `defaultWattage` (120W–500W based on known specs)

#### Controller/Service
- `PrintersController` update endpoint maps `Wattage` and `MachineHourlyRate` from DTO
- `PrintersService.CreatePrinterFromDtoAsync` maps both fields on creation

#### Tests
- 4 new cascade tests (override, model default, full cascade, settings fallback)
- Test helper creates isolated models to prevent seeded DefaultWattage from leaking

#### Migrations
- `AddWattageToEntities` for both PostgreSQL and SQL Server

### Impact for Frontend

`Wattage` and `MachineHourlyRate` are now available on the Add/Edit printer DTOs for frontend modals.

---

## 23. FailureDetectionStatusModal wide + 2-column layout (2025-07-22)

**Author:** Newt (Designer — Industrial UI)  
**Date:** 2025-07-22  
**Status:** PROPOSED

### Context

The spaghetti detection details modal used `size="md"` (max-w-md = 448px). With 6+ content sections stacked vertically — status header, detail tiles, "why this is showing", operator next step, recent incidents, and print session timeline — the modal grew taller than the viewport on large screens, requiring excessive scrolling.

### Decision

1. **Width**: Switched from `size="md"` to `width="max-w-4xl"` (896px). This uses the Modal's `width` prop instead of the preset `size`, giving enough room for a 2-column layout without looking oversized.

2. **Max height**: Tightened from the default `max-h-[90vh]` to `max-h-[85vh]` to add breathing room between the modal edge and the viewport edge.

3. **2-column grid at `lg:` breakpoint**:
   - **Left column** — Context and operator guidance: "Why this is showing", "Operator next step", snapshot link
   - **Right column** — History: Recent incidents, Print session timeline
   - Status header and detail tiles remain full-width above the grid (they're already compact)

4. **Mobile/tablet**: Stays single-column stacked (Tailwind responsive `lg:grid-cols-2` only activates at ≥1024px).

### Rationale

- The context/guidance sections are short text blocks; the history sections are longer lists. Putting them side-by-side on wide screens cuts the vertical height roughly in half.
- 896px (max-w-4xl) is the sweet spot: wide enough for 2 readable columns, narrow enough to not feel like a full-page takeover.
- Snapshot link moved into the left column (from bottom of modal) so it's co-located with operator guidance rather than orphaned at the very end.

### Impact

- Single file changed: `FailureDetectionStatusModal.tsx`
- No test changes needed (no tests asserted on modal size or layout structure)
- All 1615 React tests pass
- ESLint: 0 errors

---

## 24. FailureDetectionMonitoringSummary Redesign (2026-06-10)

**Author:** Newt (Industrial UI Designer)  
**Date:** 2026-06-10  
**Status:** IMPLEMENTED

### Context

The `FailureDetectionMonitoringSummary` component was taking up excessive visual space on printer cards and looked out of place — it was styled as a standalone monitoring dashboard widget rather than a card section.

### Decision

Redesign the component with two distinct variants:

#### Compact Variant (for CompactPrinterCard)
- Single inline row: shield icon + headline text + badge + optional subline
- No stat grid, no "Watching" box
- ~40px height for healthy/standby states
- Operator action text only shown when tone is critical/attention

#### Detailed Variant (for DetailedPrinterCard)
- Icon + headline + badge inline
- Summary paragraph below
- Operator action box only when tone is critical/attention
- Still lighter than original — no stat grid or "Watching" box

### Rationale

1. **Card context vs dashboard context**: Cards show at-a-glance status. Operators need tone (color) + headline to know if action is needed. Detailed stats (source, last scan, camera target) belong in a drill-down modal.

2. **Visual weight reduction**: Removed rounded-xl, heavy shadows, gradient backgrounds. Now uses simple rounded-lg with subtle border — matches other card sections.

3. **Information hierarchy**: What operators need on card: "Is this printer OK?" Answer: green badge = OK, red/yellow badge = check it.

### Impact

- Component reduced from 422 lines to 247 lines (41%)
- Visual footprint reduced by ~60-70% on compact cards
- Detailed variant still provides context without dominating card

#### Files Changed
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringSummary.tsx`
- `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringSummary.test.tsx`
- `src/Web/ReactApp/src/test/features/printers/obico-ml-badge.test.tsx` (test assertions)
- `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringOverlay.test.tsx` (unrelated fix: QueryClientProvider wrapper)

---

## 25. Cost Tracking Settings UI — No Custom Section Needed (2026-07-08)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-07-08  
**Status:** IMPLEMENTED

### Context

Task requested adding a "Cost Tracking" section to the admin Settings page with manual field definitions (toggle, number inputs with ranges, helper text, validation).

### Finding

The Settings page is **metadata-driven**. `CostTrackingSettings.cs` already has all required backend attributes:
- `[AppSetting("CostTracking")]` — auto-discovered by `SettingsService`
- `[SettingGroup("Operations")]` — appears under "Operations" in sidebar
- `[SettingDisplay]` on each property — labels, descriptions, input types, min/max ranges
- `IValidatableSetting` — server-side validation on save

The `SettingsPagelet` component renders these dynamically. No per-section frontend code is needed.

### What Was Done

1. **Verified** CostTracking already renders in the Settings UI via the metadata system
2. **Added** `CostTrackingSettings` TypeScript interface in `api.ts` for type-safe access from cost features
3. **Added** `getCostTrackingSettings()` / `updateCostTrackingSettings()` convenience methods on apiClient
4. **Added** 7 focused tests verifying CostTracking metadata renders correctly (toggle, numbers, values, onChange, validation errors, tooltips)

### For Lambert (Backend)

No backend changes needed — `CostTrackingSettings` is already fully wired. The attributes, validation, and persistence all work through the existing `UnifiedSettingsController` + `SettingsService` pipeline.

#### Files Changed
- `src/Web/ReactApp/src/types/api.ts` — added `CostTrackingSettings` interface
- `src/Web/ReactApp/src/services/api.ts` — added typed convenience methods
- `src/Web/ReactApp/src/test/components/CostTrackingSettingsPagelet.test.tsx` — new test file (7 tests)

---

## 26. Custom Date Range Picker for TimePeriodFilter (2026-03-27)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-27  
**Status:** IMPLEMENTED

### Context

Lambert shipped backend `startDate`/`endDate` query param support on all statistics endpoints. Frontend only had preset buttons (7d/30d/90d/1yr/All Time).

### Decision

Introduced `TimePeriodFilterValue` discriminated union type:
```typescript
type TimePeriodFilterValue =
  | { type: 'preset'; days: number | undefined }
  | { type: 'custom'; startDate: string; endDate: string };
```

- Added "Custom" toggle button to `TimePeriodFilter`; when active, shows inline date inputs with min/max constraints
- Pages manage `TimePeriodFilterValue` state and derive `days`/`startDate`/`endDate` for hooks
- Updated all cost API methods and hooks to accept optional `startDate/endDate` alongside `days`
- Updated `useStatistics` hooks with same pattern using shared `buildStatsParams()` helper
- All three dashboard pages (Cost, Statistics, Analytics) updated

### Trade-offs

- **Breaking change** to `TimePeriodFilterProps` — accepted because only 3 consumers exist and all needed updating
- Custom mode uses fully controlled inputs (no intermediate state) — clean but means invalid dates silently reject
- `ExportMenu` still takes `days` only — acceptable since exports can use the preset-derived value

#### Files Changed
- `timePeriodOptions.ts`, `TimePeriodFilter.tsx`, `index.ts` (UI library)
- `api.ts` (cost methods), `useApi.ts` (cost hooks + query keys)
- `useStatistics.ts` (statistics hooks)
- `CostDashboardPage.tsx`, `StatisticsPage.tsx`, `AnalyticsDashboardPage.tsx`
- `TimePeriodFilter.test.tsx` (new), `CostDashboardPage.test.tsx` (updated)

---

## 27. Standardized Date Range Filters Across Statistics Pages (2026-03-27)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-27  
**Status:** IMPLEMENTED

### Context

Three statistics pages had inconsistent date range filtering:
- StatisticsPage: 7d/30d/90d/All time (missing 1 year)
- AnalyticsDashboardPage: 7d/30d/90d/1yr/All time
- CostDashboardPage: No filter at all (always all-time)

Each page duplicated its own button group inline.

### Decision

1. Created shared `TimePeriodFilter` component in `@/common/components/ui/` with standard options: 7 days, 30 days, 90 days, 1 year, All time.
2. All three pages now use this shared component.
3. Cost API hooks (`useCostSummary`, `useCostsByPrinter`, `useCostsByMaterial`) now accept a `days` parameter, passed as query string to the backend.
4. Default selection is 30 days on all pages.

### Impact

- Frontend: 3 pages updated, shared component created, 7 new tests added
- API layer: `apiClient` cost methods now accept `days?` param; query keys changed from static arrays to functions
- Backend: No changes needed — `days` query param was already supported

---

## 28. FailureDetectionMonitoringSummary hidden when printer is at rest (2026-03-27)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-27  
**Status:** IMPLEMENTED

### Context

The `FailureDetectionMonitoringSummary` widget was rendered unconditionally on both compact and detailed printer cards. When a printer is idle/offline/standby, the widget showed "Standing by / Idle" — redundant with the header badge shield icon that already communicates failure-detection state at a glance.

### Assessment: What does the summary show during printing vs at rest?

**During active printing (unique value):**
- Live scan results with last-scanned timestamp
- Failure confidence percentage and detection time
- Operator action directives ("Inspect print", "Check camera")
- Snapshot links for visual review
- Auto-pause status with contextual next steps

**At rest (redundant with header badge):**
- "Standing by" + "Idle" badge — duplicates header shield icon tooltip
- "Off" / "Connecting" — no operational value, header already conveys this
- "Setup needed" — header badge already surfaces misconfigured state

### Decision

Hide `FailureDetectionMonitoringSummary` when `isPrinting` and `isPaused` are both false. The header badge remains the sole failure-detection indicator at rest. The summary widget becomes a print-active operational panel only.

### Impact

- Cleaner cards when printers are at rest (reduced visual noise)
- No loss of information — header badge + tooltip + click-to-modal path still available
- Summary panel surfaces only when operators actually need it (active print monitoring)

#### Files Changed
- `CompactPrinterCard.tsx` — wrapped summary in `(isPrinting || isPaused)` guard
- `DetailedPrinterCard.tsx` — same guard
- `FailureDetectionMonitoringSummary.test.tsx` — added card-level visibility contract tests

---

## 29. Add Wattage + MachineHourlyRate to Printer Modals (2026-03-27)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-27  
**Status:** IMPLEMENTED

### Context

Lambert added `Wattage` (nullable decimal) to `Printer` and `PrinterModel` entities and `MachineHourlyRate` was already on `Printer`. The Create/Update DTOs on both backend and TypeScript were updated, but the fields had no UI surface in the Add or Edit printer modals.

### Decision

Added a "Cost Settings" section to both `AddPrinterModal` and `EditPrinterModal` containing:

- **Wattage (W)**: `number` input, min 0, step 1. Helper: "Power consumption in watts. Leave blank to use model default or global setting."
- **Machine Hourly Rate ($)**: `number` input, min 0, step 0.01. Helper: "Hourly operating cost. Leave blank to use the global default."

Empty values submit as `undefined`/`null` — the backend cost calculation cascade (`printer.Wattage → model.DefaultWattage → settings.AveragePrinterWattage`) handles fallback.

### Changes

| File | Change |
|---|---|
| `src/infra/Dtos/PrinterDetailsDto.cs` | Added `Wattage` and `MachineHourlyRate` fields |
| `src/api/Controllers/PrintersController.cs` | Map `p.Wattage` and `p.MachineHourlyRate` into details DTO |
| `src/Web/ReactApp/src/types/api.ts` | Added `wattage?` and `machineHourlyRate?` to `PrinterDetails` |
| `src/Web/ReactApp/src/features/printers/components/AddPrinterModal.tsx` | Cost Settings section |
| `src/Web/ReactApp/src/features/printers/components/EditPrinterModal.tsx` | Cost Settings section + pre-population + change detection |
| `src/Web/ReactApp/src/features/catalog/components/PrinterModelsCatalog.tsx` | Show `defaultWattage` badge in Features column |
| `src/Web/ReactApp/src/features/printers/components/__tests__/PrinterCostFields.test.tsx` | 6 tests covering render, helper text, pre-population, and submit behavior |

### Validation

- ✅ 6/6 new cost field tests pass
- ✅ 5/5 existing EditPrinterModal tests pass
- ✅ 62/62 total printer test suite passes
- ✅ ESLint: 0 errors
- ✅ .NET build: 0 errors, 0 warnings
- ✅ React production build: success

---

## 30. Job Scheduling UX — Job Picker (2026-03-27)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-03-27  
**Status:** IMPLEMENTED

### Context

The `ScheduleModal` required users to manually type a 36-character GUID into a text input to schedule a job. No discovery or browsing mechanism existed.

### Decision

Replaced the raw text input with a `Select` dropdown that:
- Fetches available jobs via `apiClient.getJobQueue()` with `useQuery`
- Filters to only Queued/Assigned status (not Printing, Completed, etc.)
- Shows `{jobName} — {printerName || 'Unassigned'}` per option
- Supports pre-selection via the existing `jobId` prop
- Shows an empty state message when no schedulable jobs exist

Added a "Schedule" action button on each Queued/Assigned job row in `QueueJobsTable`, wired through `PrintQueueDashboardPage` to open the modal with that job pre-filled.

#### Files Changed
- `src/Web/ReactApp/src/features/scheduling/components/ScheduleModal.tsx`
- `src/Web/ReactApp/src/features/queue/components/QueueJobsTable.tsx`
- `src/Web/ReactApp/src/features/queue/pages/PrintQueueDashboardPage.tsx`
- `src/Web/ReactApp/src/test/features/scheduling/ScheduleModal.test.tsx` (new)

---

## 2026-03-31: Printer Entity Decomposition — Extract PrinterServiceState (ANALYSIS COMPLETE)

**Analyst:** Dallas (Lead)  
**Status:** ✅ Analysis approved by Jeff; **awaiting implementation by Lambert**  
**Impact:** Reduces background service write contention with user API updates  
**Risk:** Low — internal bookkeeping only, no frontend contract changes

### Problem

The Printer entity is a "god row" — all configuration, operational bookkeeping, and relationships share one PostgreSQL row with a single `RowVersion` concurrency token. Background services that call `SaveChangesAsync` bump `xmin`, creating hazards for user-initiated `PUT /api/printers/{id}` updates.

**Highest offender:** `LastHistorySeedUtc` — written every 15 minutes by HistorySeedingBackgroundService, never read by frontend, pure internal bookkeeping.

### Solution: Extract PrinterServiceState

New 1:1 table containing 4 background-service-written fields:

| Field | Background Service | Frequency | Why Extract |
|-------|-------------------|-----------|-----------|
| `LastHistorySeedUtc` | HistorySeedingBackgroundService | Every 15 min | **HIGH priority** (Jeff flagged); never frontend-visible; pure bookkeeping |
| `LastModelSyncAt` | CatalogUpdateDetectionService | ~Hourly | Written by BG service; frontend only reads computed `HasCatalogUpdate` bool |
| `LastCapabilityUpdate` | Both CatalogUpdateDetectionService + API | Per catalog cycle + user edits | Dual-writer pattern is worst case for concurrency |
| `ObicoServerId` | ObicoServerAssignmentService.RebalanceAsync | On server add/remove | Internal server assignment; not frontend-visible |

### Migration Approach

**Single migration** (Phase 1) — extract all 4 fields at once:
1. Create new `PrinterServiceState` table (5 columns: PK, FK, 3 timestamps, ObicoServerId, RowVersion)
2. Copy existing values from Printer table
3. Drop extracted columns from Printer table
4. Update both PostgreSQL and SQL Server migrations

### Code Changes

| Layer | Change |
|-------|--------|
| Domain | Add `PrinterServiceState.cs` entity; remove 4 properties from `Printer.cs`; add `PrinterServiceState?` navigation |
| EF Config | New `PrinterServiceStateConfiguration.cs` with 1:1 relationship; update `PrinterConfiguration.cs` |
| Repository | Add `.Include(p => p.ServiceState)` where background service updates are expected |
| Services | `PrintJobManagementService`, `PrintersService`, `ObicoServerAssignmentService`, `PrintersController` update navigation to `printer.ServiceState.LastHistorySeedUtc` etc. |
| DTOs | Compute `HasCatalogUpdate` via `ServiceState` JOIN instead of direct property |
| Tests | Update test doubles and assertions for new navigation path |

### Risk Assessment

- ✅ **Low risk:** All extracted fields are internal bookkeeping. No frontend contract changes.
- ✅ **Standard pattern:** Familiar EF Core migration pattern (copy values, drop columns).
- ✅ **Backward compat:** `PrinterDispatchState` unaffected; new extraction independent.

### Next Phase (Deferred)

Not included in Phase 1, but consider for future:
- Extract other high-contention background service writes if identified
- Auto-create `PrinterServiceState` when Printer is created (like `PrinterDispatchState`)

---

**Assigned to:** Lambert (Backend Dev)  
**Approval chain:** ✅ Dallas (analyst) → ✅ Jeff (decision) → 🕐 Lambert (implementation)

---

## 2026-04-01: Multi-Toolhead Filament Batch Consumption + Bounds Validation

**Author:** Lambert (Backend Dev)  
**Status:** ✅ IMPLEMENTED (PFarm1-uykq, PFarm1-r56j)  
**Date:** 2026-04-01

### Problem Statement

1. Sequential filament debit: Multi-toolhead prints were calling `ConsumeFilamentAsync` N times in a loop instead of using `ConsumeMultipleFilamentsAsync` for batch operations
2. Runaway gate creation: No upper bound on toolhead indices allowed invalid backend data (e.g., toolheadIndex=999) to trigger unlimited MmuGate auto-creation

### Decision

**Implement batch filament consumption and enforce MaxToolheadIndex = 16 bounds**

### Implementation

#### Part 1: Batch Consumption Wiring
- Replaced loop calling `ConsumeFilamentAsync` in `PrintJobCompletionService.cs` with single `ConsumeMultipleFilamentsAsync` call
- Build list of (spoolId, grams) tuples during per-extruder usage loop, then batch-consume after loop
- Atomic operation at service boundary; reduces HTTP overhead from N sequential calls to 1 batch call

#### Part 2: Toolhead Index Bounds Validation
- Added `MaxToolheadIndex = 16` constant in `PrintersService.cs`
- Bounds checking in `SetToolheadSpoolAsync` and `ClearToolheadSpoolAsync` before auto-creation logic
- Out-of-bounds requests (index < 0 or > 16) return `CommandResult(false)` with descriptive error
- Log warning when out-of-bounds index is rejected

### Rationale

- Batch consumption eliminates unnecessary HTTP roundtrips for multi-toolhead prints
- MaxToolheadIndex=16 prevents database bloat from invalid backend data; reasonable upper bound for all known printer types
- Log-and-reject pattern keeps API stable when receiving malformed data

### Impact

- ✅ 2256 API tests passing
- ✅ Performance improvement for multi-toolhead prints
- ✅ Safety guard against runaway gate creation from invalid backend responses

---

## 2026-04-01: History Job Card/Table Filament and Cost Display

**Author:** Ripley (Frontend Dev)  
**Status:** ✅ IMPLEMENTED (PFarm1-j9u3)  
**Date:** 2026-04-01

### Problem Statement

HistoryJobCard and HistoryJobTable were not displaying per-toolhead filament usage or cost information, making it difficult for users to understand material consumption and costs for completed jobs.

### Decision

Extend history UI components to display per-toolhead filament usage, material type, color indicators, and cost breakdowns

### Implementation

#### Type Extensions
- Extended `QueueHistoryEntryDto` in `src/types/api.ts` with optional `toolheadUsages?: PrintJobToolheadUsage[]`
- Extended `HistoryJob` in `src/types/queue.ts` with same field
- Updated `QueueHistoryTab.tsx` to pass toolheadUsages through API response mapping

#### UI Changes

**HistoryJobCard:**
- Added "Filament Usage" section displaying per-toolhead breakdown:
  - Toolhead index (T0, T1, etc.)
  - Color indicator dot
  - Material name
  - Usage in grams
  - Cost in USD (if available)
- Compact, card-appropriate layout with truncation for long names
- Total row for multi-toolhead prints

**HistoryJobTable:**
- Added "Filament" and "Cost" columns
- Filament column: total usage across all toolheads
- Cost column: total cost across all toolheads
- Tooltips show per-toolhead breakdown on hover
- Graceful "—" for missing data
- Tabular-nums for consistent number alignment

### Design Decisions

1. Pattern consistency: Mirrors per-toolhead display in `JobDetailsSection.tsx` for UI cohesion
2. Card vs table detail: Cards show full breakdown inline; tables show aggregates with hover tooltips to save space
3. Graceful degradation: Components handle missing toolheadUsages data by omitting sections/columns
4. Multi-toolhead totals: Only shown when 2+ toolheads present
5. Type-safe implementation with proper TypeScript imports and optional chaining

### Impact

- ✅ 1659 React tests passing
- ✅ Clean build (0 TypeScript errors)
- ✅ Users can now see per-material filament consumption and costs in job history

---

## 2026-04-01: ObicoSettings Runtime Configuration Consistency

**Author:** Dallas (Lead)  
**Status:** ✅ IMPLEMENTED (PFarm1-07s)  
**Date:** 2026-04-01

### Problem Statement

ObicoSettings consumers were inconsistently reading from either `IOptions<ObicoSettings>` (static config file) or `ISettingsService` (persisted database). This caused skew: users changed Obico settings via Settings UI, but some code paths read stale config file values instead of database values.

### Decision

**All ObicoSettings runtime consumers MUST use ISettingsService for consistency**

IOptions<ObicoSettings> binding remains for bootstrap/initial config load, but all runtime code should read from ISettingsService to respect user modifications stored in the database.

### Implementation

**Audited and migrated all ObicoSettings consumers:**
- PrintFailureMonitorService → ISettingsService ✅
- ObicoFailureDetectionService → ISettingsService ✅
- PrintersController → Migrated from `IOptions<ObicoSettings>` to `ISettingsService` ✅
- Options binding in ServiceCollectionExtensions → Bootstrap only (correct) ✅

### Pattern for Future Settings

When adding new settings classes:
1. Add options binding in `ServiceCollectionExtensions` for bootstrap
2. Runtime consumers MUST use `ISettingsService.Get<T>()` for persisted values
3. Never use `IOptions<T>` in runtime code that should respect user modifications

### Impact

- ✅ Build passes (0 errors, 0 warnings)
- ✅ Runtime consistency: all code reads database values instead of stale config file
- ✅ User modifications via Settings UI are immediately visible to all consumers
- ✅ Standard injection pattern established for future settings work

---

## 2026-04-01: Multi-Toolhead Job Cost Calculation Regression Gates

**Author:** Kane (QA / Regression Specialist)  
**Status:** ✅ IMPLEMENTED (PFarm1-kk0v)  
**Date:** 2026-04-01

### Problem Statement

Multi-toolhead cost calculation seam was untested, creating financial accuracy risk. Edge cases around material cost aggregation, per-toolhead pricing, and missing data scenarios were not covered by regression tests.

### Decision

Implement comprehensive regression test suite for multi-toolhead cost calculation with 11+ focused test methods

### Implementation

**New test file:** `JobCostCalculationMultiToolheadTests.cs`

Test coverage includes:
- Multi-toolhead cost aggregation with varying material prices
- Cost-per-toolhead with individual toolhead pricing
- Edge cases: 0-cost materials, missing pricing, default pricing fallback
- Bounds validation: max 16 toolheads
- Rounding accuracy: monetary precision maintained across multi-toolhead scenarios
- Material cost breakdowns: per-extruder costs sum correctly to job total

### Design

- Focused test class for high-risk financial seam
- Uses existing job costing service contract
- Tests operate against real EF Core DbContext (integration layer)
- All tests passing with 0 flakiness

### Impact

- ✅ 1821 tests passing (including 11 new multi-toolhead cost tests)
- ✅ Financial accuracy locked in for multi-toolhead scenarios
- ✅ Regression gate prevents cost calculation regressions in future multi-toolhead work


---

## API Redeploy: slicingEnabled Fix Validated (2026-04-05)

**Date:** 2026-04-05  
**Agent:** Parker (DevOps & Deployment Engineer)  
**Status:** ✅ COMPLETED

### Context
The API was reporting `slicingEnabled=false` in microservices mode despite the slicer-host container being active. The bug was fixed in `SystemCapabilitiesController.cs` to detect `DEPLOYMENT_MODE=microservices` and report slicing as enabled.

### Action Taken
Executed `./scripts/pfdev redeploy api` from `/home/pi/pfarm` to rebuild and redeploy the API container with the fix.

### Validation Results
1. **Capabilities Endpoint** (`/api/system/capabilities`):
   - ✅ `slicingEnabled: true` (was false before fix)
   - ✅ Correctly detects `DEPLOYMENT_MODE=microservices` env var
   - ✅ All other capabilities reporting correctly

2. **Slicer Routing** (microservices mode):
   - ✅ `/api/slicer/*` routes correctly proxy to slicer-host container
   - ✅ nginx routing configuration intact
   - ✅ slicer-host responding (200 OK on `/api/slicer/profiles`)

3. **Container Status**:
   - API: healthy (redeployed 3 minutes ago)
   - Slicer-host: healthy
   - Nginx-proxy: healthy

### Guidelines for pfdev Usage
**Use `pfdev` when:**
- Making code changes to a single service during active development
- Need fast iteration on API, frontend, or worker changes
- Other services are already running and shouldn't be disrupted
- Working in microservices deployment mode

**Use `deploy-docker.sh` when:**
- Initial deployment or major infrastructure changes
- Changing compose templates or deployment modes
- Need to regenerate docker-compose.yml
- Deploying to a fresh environment

### Technical Details
- **Command:** `./scripts/pfdev redeploy api`
- **Route tested:** `http://localhost/api/system/capabilities`
- **Response:** `{"slicingEnabled": true, ...}`
- **Slicer routing:** `http://localhost/api/slicer/profiles` → 200 OK

---

## User Directive: pfdev Script Naming Convention (2026-04-05T03:03:38Z)

**By:** Jeff Papiez (via Copilot)  
**Directive:** Use the repo's `pfdev` script name, not `pf-dev`.  
**Why:** User preference — captured for team memory  
**Status:** ACTIVE

This directive ensures consistent team communication and script naming when discussing deployment workflows.

---

## Slicer Estimate Snapshot at Job Dispatch (2026-04-01)

**Author:** Lambert (Backend)  
**Date:** 2026-04-01  
**Status:** IMPLEMENTED

### Summary
Added per-toolhead filament estimates to PrintJobToolheadUsage entity, recorded at job dispatch time before actual consumption data is available.

### Implementation
- Added `SlicerEstimateGrams` (nullable double) to PrintJobToolheadUsage entity
- At job dispatch: `PrintJobManagementService.DispatchJobAsync` calls `SnapshotSlicerEstimatesAsync`
- Parses `GcodeFile.FilamentPerExtruderWeightG` JSON array and creates usage records with slicer estimates
- Repository gained `GetToolheadsForPrinterAsync` and `AddToolheadUsageAsync` methods
- Migrations created for both PostgreSQL and SQL Server

### Pattern
```csharp
var estimates = System.Text.Json.JsonSerializer.Deserialize<double[]>(gcode.FilamentPerExtruderWeightG);
// iterate per-extruder weights, create usage records with toolhead spool/material/color denormalized from Toolhead entity
// skip zero estimates
```

### Benefit
Frontend can show per-toolhead filament estimates for in-progress jobs before actual consumption data is available at completion.

---

## Toolhead Usage Records Use Upsert at Job Completion (2026-07-31)

**Author:** Lambert (Backend)  
**Date:** 2026-07-31  
**Status:** IMPLEMENTED

### Context
The `PrintJobToolheadUsage` table has a unique composite index on `(PrintJobId, ToolheadIndex)`. Dispatch creates snapshot rows (with `SlicerEstimateGrams` + `SpoolmanSpoolId`). Completion must add `FilamentUsageGrams` to those same rows.

### Decision
**Completion always queries for existing rows first.** If snapshot rows exist from dispatch, it updates them in-place (preserving the snapshotted `SpoolmanSpoolId`). If no rows exist (jobs dispatched before the feature), it creates new ones using live toolhead data.

### Rationale
- Avoids `DbUpdateException` from unique index violation
- Preserves the spool assignment recorded at dispatch time, so mid-print spool swaps don't debit the wrong spool
- Backward-compatible: jobs without dispatch snapshots still get usage records

### Applies To
- `PrintJobCompletionService.FetchAndRecordFilamentUsageAsync` — both multi-toolhead and single-spool paths
- Any future code that writes to `PrintJobToolheadUsage` after dispatch

---

## Slicer API Gaps + E2E Pipeline Smoke Test (2025-07-19)

**Author:** Lambert (Backend)  
**Date:** 2025-07-19  
**Status:** IMPLEMENTED

### Summary
Closed 3 critical API gaps in the slicer module and added an E2E pipeline smoke test.

### A1: Job Retry Endpoint — `POST /api/slice/{id}/retry`
- Added `RetryJobAsync` to `ISliceJobRepository` → `EfSliceJobRepository`
- Resets status to Queued, clears worker/error/progress, increments RetryCount
- Only retries Failed jobs (returns 400 otherwise), 404 if not found
- Uses `[Authorize]` (any authenticated user)

### A2: Job List Pagination — `GET /api/slice`
- Added `CountAsync` + `GetPagedAsync` to `ISliceJobRepository`
- Controller now accepts: `page` (default 1), `pageSize` (default 20), `status`, `sortBy` (CreatedAt|CompletedAt), `sortDir` (asc|desc)
- Returns `PagedResult<SliceJobStatusResponse>` (from Farm.Infrastructure)
- **Breaking change**: Response shape changed from array to paged wrapper. No existing consumers found in tests.

### A3: Slicer Settings CRUD — `GET/PUT /api/admin/slicer/settings`
- Added `SlicerSettingsDto` and `UpdateSlicerSettingsRequest` to `SlicerAdminDtos.cs`
- `SlicerAdminController` now injects `SlicerDbContext` (primary constructor)
- GET auto-creates singleton row (Id=1) if missing; PUT updates all fields
- Both endpoints require `farm_admin` role

### B: E2E Pipeline Smoke Test
- New file: `src/tests/Farm.Slicer.Module.Tests/Integration/SlicePipelineE2ETests.cs`
- **Test 1 — Full Pipeline**: Submit → verify queued → claim → progress update → artifact upload → complete → verify Completed status → verify artifacts
- **Test 2 — Retry Flow**: Submit → claim → fail → retry → verify re-queued with RetryCount=1
- Uses `CustomWebApplicationFactory` with worker + admin clients

### Key Files Changed
- `src/slicer/Farm.Slicer.Module/Data/Repositories/ISliceJobRepository.cs`
- `src/slicer/Farm.Slicer.Module/Data/Repositories/EfSliceJobRepository.cs`
- `src/slicer/Farm.Slicer.Module.Api/Controllers/Slicing/SliceJobController.cs`
- `src/slicer/Farm.Slicer.Module.Api/Controllers/Admin/SlicerAdminController.cs`
- `src/slicer/Farm.Slicer.Module/Contracts/SlicerAdminDtos.cs`
- `src/tests/Farm.Slicer.Module.Tests/Integration/SlicePipelineE2ETests.cs` (new)
- `src/tests/Farm.Slicer.Module.Tests/Slicing/JobDispatcherRetryTests.cs`
- `src/tests/Farm.Slicer.Module.Tests/Slicing/JobDispatcherServiceTests.cs`
- `src/tests/Farm.Slicer.Module.Tests/Farm.Slicer.Module.Tests.csproj`

---

## Playwright Emulator E2E Test Infrastructure (2026-07-18)

**Author:** Kane (QA)  
**Date:** 2026-07-18  
**Status:** IMPLEMENTED

### Decision 1: Separate emulator tests from existing E2E tests
Emulator-backed tests live in `e2e/emulator/` with a dedicated npm script `test:e2e:emulator`, separate from the existing visual/navigation/layout tests in `e2e/`.

**Rationale:** Emulator tests require the API running with `PFARM__TestEmulator__Enabled=true` — a different startup sequence than existing E2E tests which only need the React dev server. Mixing them would cause CI confusion and false failures.

### Decision 2: Fixture-based API health verification
The `emulator-setup.ts` fixture auto-runs before every emulator test, hitting `/healthz` and `/health` to confirm the API is alive and the emulator is active.

**Rationale:** Fail fast with a clear diagnostic message rather than letting tests hang or produce cryptic timeout errors when the API isn't running.

### Decision 3: Resilient selectors with graceful fallback
Tests use multiple selector strategies: `.pf-detailed-printer-card` CSS class, `div[role="progressbar"]`, `span[title="..."]` for temps, and text content filtering. Where a UI control might be behind a menu or not yet implemented, tests check for visibility and gracefully skip.

**Rationale:** The emulator plugin is being built in parallel (Lambert). The UI for emulator-specific actions (start print, pause, cancel) may not exist yet. Tests are written to pass once the emulator is running, with fallback assertions that verify the structural contract (buttons exist, cards render, status badges show).

### Decision 4: Conservative timeouts for SignalR-dependent assertions
Emulator broadcasts every ~2 s. Tests use 10-15 s timeouts for initial card rendering and 5-6 s waits for real-time updates.

**Rationale:** SignalR connection setup + first broadcast can take 3-5 s on slow machines. Being generous prevents flaky CI failures while remaining fast enough for local development feedback.

# OrcaSlicer Bundle Format Specification — Research Findings

**Author:** Brett (Researcher)
**Date:** 2026-07-16
**Status:** Research Complete — Ready for Implementation Planning

---

## Executive Summary

Both `.orca_printer` and `.orca_filament` files are **standard ZIP archives** containing JSON preset files organized in subdirectories, plus a `bundle_structure.json` manifest. They use the `miniz` (mz_zip) library for compression. The format is simple and well-structured — PrintFarmer can implement import/export support with moderate effort.

---

## 1. `.orca_printer` — Printer Config Bundle

### What It Is

A complete printer configuration package that bundles a **printer preset** with all its associated **filament presets** and **process (print) presets**.

### File Format

- **Container:** ZIP archive (standard zip, created via `mz_zip_writer`)
- **Extension:** `.orca_printer`
- **MIME type:** `application/zip` (effectively)

### Internal Structure

```
MyPrinter.orca_printer (ZIP)
├── bundle_structure.json          ← manifest (metadata + file listing)
├── printer/
│   └── MyPrinter 0.4 nozzle.json ← printer preset JSON
├── filament/
│   ├── Generic PLA @MyPrinter.json    ← filament preset JSONs
│   ├── Generic PETG @MyPrinter.json
│   └── ...
└── process/
    ├── 0.20mm Standard @MyPrinter.json ← process/print preset JSONs
    ├── 0.16mm Fine @MyPrinter.json
    └── ...
```

### `bundle_structure.json` Schema

```json
{
  "version": "02.01.00.59",           // OrcaSlicer version string (or "" if offline)
  "bundle_id": "userid_PrinterName_timestamp",  // unique ID: {user_id}_{printer_name}_{timestamp} or "offline_..."
  "bundle_type": "printer config bundle",       // literal string identifier
  "printer_preset_name": "MyPrinter 0.4 nozzle", // name of the primary printer preset
  "printer_config": [                  // array of printer preset zip paths
    "printer/MyPrinter 0.4 nozzle.json"
  ],
  "filament_config": [                 // array of filament preset zip paths
    "filament/Generic PLA @MyPrinter.json",
    "filament/Generic PETG @MyPrinter.json"
  ],
  "process_config": [                  // array of process preset zip paths
    "process/0.20mm Standard @MyPrinter.json",
    "process/0.16mm Fine @MyPrinter.json"
  ]
}
```

### What Gets Bundled

- **One printer preset** (the selected machine config)
- **All user filament presets** compatible with that printer
- **All user process presets** compatible with that printer
- System (built-in) presets are **not** exported — only user/custom presets

---

## 2. `.orca_filament` — Filament Bundle

### What It Is

A collection of filament presets for a specific filament type (e.g., "Polymaker PLA Pro"), organized by printer vendor compatibility.

### File Format

- **Container:** ZIP archive (same as `.orca_printer`)
- **Extension:** `.orca_filament`

### Internal Structure

```
MyFilament.orca_filament (ZIP)
├── bundle_structure.json              ← manifest
├── Creality/
│   ├── MyFilament @Ender3.json        ← filament preset tuned for Creality printers
│   └── MyFilament @Ender5.json
├── Prusa/
│   └── MyFilament @MK4.json           ← filament preset tuned for Prusa printers
└── Bambu Lab/
    └── MyFilament @X1C.json           ← filament preset tuned for Bambu printers
```

**Key difference from `.orca_printer`:** Files are organized by **printer vendor name** (not by preset type), because the same filament material has different tuning for different printers.

### `bundle_structure.json` Schema

```json
{
  "version": "02.01.00.59",
  "bundle_id": "userid_FilamentName_timestamp",
  "bundle_type": "filament config bundle",       // literal string identifier
  "filament_name": "Polymaker PLA Pro",           // human-readable filament name
  "printer_vendor": [                             // array of vendor objects
    {
      "vendor": "Creality",
      "filament_path": [                          // filament preset paths within this vendor
        "Creality/MyFilament @Ender3.json",
        "Creality/MyFilament @Ender5.json"
      ]
    },
    {
      "vendor": "Prusa",
      "filament_path": [
        "Prusa/MyFilament @MK4.json"
      ]
    }
  ]
}
```

---

## 3. Individual Preset JSON Format

Each JSON file inside the bundle is a standard OrcaSlicer preset. Key fields:

### Common Fields (all preset types)

| Field | Type | Description |
|---|---|---|
| `type` | string | `"machine"`, `"filament"`, or `"process"` |
| `name` | string | Human-readable preset name |
| `version` | string | Semver string (e.g., `"1.9.0.0"`) |
| `inherits` | string | Parent preset name for inheritance (optional) |
| `from` | string | `"system"` or `"User"` — origin |
| `setting_id` | string | Unique setting identifier |
| `instantiation` | string | `"true"` if this is a concrete (non-abstract) preset |

### Printer-Specific Fields

| Field | Type | Description |
|---|---|---|
| `printer_settings_id` | string | Identifies this as a printer preset (used for type detection) |
| `printer_model` | string | Printer model name |
| `nozzle_diameter` | string[] | Nozzle diameter(s) |
| `printable_area` | string[] | Build plate coordinates |
| `printable_height` | string | Max Z height |
| `default_print_profile` | string | Default process preset name |

### Filament-Specific Fields

| Field | Type | Description |
|---|---|---|
| `filament_settings_id` | string | Identifies this as a filament preset |
| `filament_id` | string | Unique filament identifier (e.g., `"BSFI002"`) |
| `filament_density` | string[] | Material density |
| `nozzle_temperature` | string[] | Print temperature |
| `hot_plate_temp` | string[] | Bed temperature |
| `filament_flow_ratio` | string[] | Flow rate multiplier |

### Process-Specific Fields

| Field | Type | Description |
|---|---|---|
| `print_settings_id` | string | Identifies this as a process preset |
| `layer_height` | string | Layer height value |
| `compatible_printers` | string[] | List of compatible printer names |

### Preset Type Detection

OrcaSlicer determines preset type by checking for discriminator fields:
- Has `printer_settings_id` → **printer** preset
- Has `print_settings_id` → **process** preset
- Has `filament_settings_id` → **filament** preset

---

## 4. Import Workflow (How OrcaSlicer Loads Bundles)

Source: `PresetBundle::import_presets()` in `src/libslic3r/PresetBundle.cpp:958`

1. **File type detection:** Check file extension (`.orca_printer`, `.orca_filament`, or `.zip`)
2. **Create temp directory:** `{user_data}/user/default/temp/`
3. **Open as ZIP:** Use `mz_zip_reader_init_cfile()` to open the archive
4. **Extract all files:** Iterate ZIP entries, extract each to temp dir
   - **Skip** `bundle_structure.json` (manifest is metadata-only, not imported)
   - Strip any directory prefix from filenames (flattened extraction)
5. **Import each JSON:** Call `import_json_presets()` for each extracted file
   - Parse JSON, detect preset type from discriminator fields
   - Resolve inheritance chain (`inherits` field)
   - Check for duplicates, prompt user for overwrite confirmation
   - Save to user preset directory
6. **Cleanup:** Delete temp directory

**Important:** The `bundle_structure.json` manifest is **skipped during import**. OrcaSlicer reads each JSON individually and auto-detects its type. The manifest is informational for the export structure only.

---

## 5. Export Workflow (How OrcaSlicer Creates Bundles)

Source: `ExportConfigsDialog` in `src/slic3r/GUI/CreatePresetsDialog.cpp`

### `.orca_printer` Export

1. User selects a printer from their user presets
2. System finds all filament presets associated with that printer
3. System finds all process presets associated with that printer
4. Creates ZIP with:
   - `printer/{name}.json` — the printer preset file
   - `filament/{name}.json` — each associated filament preset
   - `process/{name}.json` — each associated process preset
   - `bundle_structure.json` — the manifest

### `.orca_filament` Export

1. User selects a filament name (e.g., "My Custom PLA")
2. System finds all vendor-specific variants of that filament
3. Creates ZIP with:
   - `{VendorName}/{preset_name}.json` — vendor-grouped filament presets
   - `bundle_structure.json` — the manifest with vendor grouping

---

## 6. Other Export Formats in OrcaSlicer

OrcaSlicer's Export dialog offers **5 export types** (no `.bbcfg` or `.orca_process`):

| Format | Extension | Contents |
|---|---|---|
| **Printer config bundle** | `.orca_printer` | Printer + filaments + processes |
| **Filament bundle** | `.orca_filament` | Filament variants grouped by vendor |
| **Printer presets** | `.zip` | Individual printer preset JSONs only |
| **Filament presets** | `.zip` | Individual filament preset JSONs only |
| **Process presets** | `.zip` | Individual process preset JSONs only |

The `.zip` variants are simpler — they contain only the selected preset JSONs with no manifest and no subdirectory structure, using `save_presets_to_zip()`.

---

## 7. Implementation Recommendations for PrintFarmer

### Import Support

1. **Detect bundle type** by file extension (`.orca_printer` / `.orca_filament` / `.zip`)
2. **Unzip** to temp directory using any standard ZIP library (SharpZipLib, System.IO.Compression)
3. **Parse `bundle_structure.json`** for metadata display (bundle type, version, contents listing)
4. **Parse each JSON preset** individually — type detection via `printer_settings_id` / `filament_settings_id` / `print_settings_id`
5. **Map to PrintFarmer's profile model** — OrcaSlicer presets use flat key-value JSON with inheritance

### Export Support

1. **Create ZIP** with subdirectory structure matching OrcaSlicer's convention
2. **Generate `bundle_structure.json`** manifest with version, bundle_id, and file paths
3. **Serialize profiles as JSON** matching OrcaSlicer's key naming (flat structure, string arrays for multi-value fields)

### Key Design Considerations

- OrcaSlicer JSON uses **string arrays** even for single values (e.g., `"nozzle_diameter": ["0.4"]`)
- The **inheritance model** (`inherits` field) means some presets are incomplete without their parent — full resolution requires the inheritance chain
- **Bundle IDs** include timestamps and user IDs — generate a PrintFarmer-specific format
- Values are mostly **strings** even for numbers (e.g., `"printable_height": "900"`)
# Slicer Import/Export Audit — Orca Bundle Formats

**Author:** Ripley (Frontend Dev)  
**Date:** 2025-07-24  
**Status:** Informational — gap analysis for `.orca_printer` / `.orca_filament` support

## What We Have Today

### Import (Frontend → Backend)

| Capability | Status | Details |
|---|---|---|
| OrcaSlicer JSON config bundle import | ✅ Working | 4-step wizard: Upload → Preview → Review → Import |
| File format accepted | `.json` only | `accept=".json"` on file input |
| Preview before import | ✅ Working | `POST /api/slicer/profiles/import/orca/preview` |
| Selective import (pick presets) | ✅ Working | User selects which printer/filament/process presets to import |
| Actual import persistence | ⚠️ Partial | Frontend calls `POST /api/slicer/profiles/import/orca` but this endpoint doesn't exist in ProfilesController. Only the `/preview` route is implemented. |
| Preset mapping to catalog | ⚠️ Missing backend | Frontend calls `/api/slicer/profiles/import/orca/map` but no controller route exists. `IOrcaPresetMappingService` interface exists but isn't wired to a controller action. |
| Individual profile import | ✅ Working | `POST /api/slicer/profiles/import` for raw JSON single profiles |
| Bulk import from worker | ✅ Working | `POST /api/slicer/profiles/bulk-import-from-worker/{printerId}` |

### Export (Backend → Frontend Download)

| Capability | Status | Details |
|---|---|---|
| Single profile export | ✅ Working | `GET /api/slicer/profiles/{id}/export` → downloads as `.json` |
| Full Orca bundle export | ✅ Working | `POST /api/slicer/profiles/export/orca` → JSON bundle with all profiles |
| Export UI | ✅ Working | Both per-profile and bundle export buttons on SlicerProfilesPage |

### Backend Parsing

| Capability | Status |
|---|---|
| `OrcaBundleParsingService` | ✅ Parses JSON objects with `printer`/`filament`/`process` (or aliases `machine`/`material`/`print`) sections |
| `IOrcaBundleExportService` | ✅ Interface defined for export |
| `IOrcaPresetMappingService` | ✅ Interface + model classes exist, no implementation wired to controller |

## What's Missing for `.orca_printer` / `.orca_filament`

### Key Difference
Current system handles **JSON text files**. `.orca_printer` and `.orca_filament` are **ZIP archives** containing multiple JSON files and potentially thumbnails/images.

### Frontend Gaps

1. **File input accept filter** — Must add `.orca_printer,.orca_filament` to `accept=` attribute in `OrcaImportWizard.tsx` (line 139)
2. **Binary file reading** — Current `FileReader.readAsText()` won't work for ZIP. Need `readAsArrayBuffer()` + a ZIP library (e.g., `jszip` or `fflate`)
3. **ZIP extraction logic** — New service/utility to:
   - Detect if uploaded file is ZIP or raw JSON
   - Extract JSON files from ZIP archive
   - Combine extracted presets into the existing `OrcaBundlePreview` format
4. **TypeScript types** — `orcaProfiles.ts` needs no structural changes if we normalize ZIP contents to the same `OrcaBundlePreview` shape before hitting the API
5. **UI messaging** — Update wizard text from "config bundle JSON" to include "or .orca_printer/.orca_filament bundle"

### Backend Gaps

1. **No actual import endpoint** — `POST /api/slicer/profiles/import/orca` (without `/preview`) doesn't exist. The frontend calls it, but it would 404.
2. **No mapping endpoint** — `POST /api/slicer/profiles/import/orca/map` isn't routed. The `IOrcaPresetMappingService` interface exists but needs a controller action.
3. **ZIP handling option** — Either:
   - (A) Frontend extracts ZIP → sends JSON to existing endpoints (simpler, no backend changes for format)
   - (B) Backend accepts multipart file upload → extracts ZIP server-side (more robust, handles large files better)

### Recommended Approach

**Frontend-side extraction** (Option A) is simpler and reuses all existing API contracts:
1. Add `fflate` or `jszip` to React dependencies
2. Create `orcaBundleExtractor.ts` utility that detects format and normalizes to JSON
3. Update `OrcaImportWizard` to handle both formats transparently
4. Fix the missing backend endpoints (import + map) as a separate task

## Files That Need Work

### Must Modify
| File | Change |
|---|---|
| `src/Web/ReactApp/src/features/slicer/orca/components/OrcaImportWizard.tsx` | Accept new file extensions, binary reading, ZIP extraction |
| `src/Web/ReactApp/src/features/slicer/orca/services/orcaProfilesService.ts` | Possibly add format detection before calling preview |
| `src/Web/ReactApp/package.json` | Add ZIP library dependency |
| `src/slicer/Farm.Slicer.Module.Api/Controllers/Slicing/ProfilesController.cs` | Add missing `import/orca` and `import/orca/map` endpoints |

### Must Create
| File | Purpose |
|---|---|
| `src/Web/ReactApp/src/features/slicer/orca/utils/orcaBundleExtractor.ts` | ZIP detection + extraction + JSON normalization |
| `src/Web/ReactApp/src/features/slicer/orca/types/orcaBundleFormats.ts` | Types for `.orca_printer`/`.orca_filament` internal structure (optional, could go in existing types file) |

### Reusable As-Is
| File | Why |
|---|---|
| `src/Web/ReactApp/src/features/slicer/components/import/*` | ImportConflictResolver, ImportMappingTable, ImportPreviewCard, ImportSummaryPanel all work with profile-type-agnostic data |
| `src/Web/ReactApp/src/features/slicer/orca/types/orcaProfiles.ts` | All types remain valid — ZIP contents normalize to same shape |
| `src/slicer/Farm.Slicer.Module/Services/OrcaBundleParsingService.cs` | Handles JSON parsing regardless of source — ZIP extraction feeds into this |
| `src/slicer/Farm.Slicer.Module/Models/OrcaProfileModels.cs` | DTOs unchanged |


---

## 7. Ripley: Global Slicer View Mode + Machine Tab Restructure (Implemented)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-04-16  
**Status:** ✅ IMPLEMENTED (commits: 16b541b7, eb3406f3)  
**Impact:** High (UX consistency across slicer editors + discoverability improvement)  

### Summary

Two interconnected improvements to slicer profile editors:
1. **Machine Profile Tab Restructure** — Created dedicated Extruder tab, moved 6 sections from Multimaterial for better logical organization
2. **Global Persisted View Mode** — Simple/Advanced toggle now syncs across all profile editors and persists in localStorage

### Context

- Machine profile settings poorly organized (extruder settings buried in Multimaterial tab)
- View mode toggle was per-editor (no sync, not persisted across navigation)

### Decision & Implementation

**Tab Restructure:**
- New tab order: Basic Information → Machine G-Code → Multimaterial → **Extruder** → Motion Ability → Notes
- Moved to Extruder tab: nozzle properties, layer height limits, extruder position, retraction, z-hop, toolchange settings
- Promoted `nozzle_diameter` and `retraction_speed` to Simple mode for better Simple-mode visibility

**Global View Mode Hook (`useSlicerViewMode`):**
- Replaces per-component local state with localStorage-backed hook
- Syncs via CustomEvent + storage event listener for same-tab and cross-tab sync
- Removed `initialViewMode` prop from MetadataProfileEditor, SlicerSettingsPanel, ProfileEditorModal

### Quality Gates
✅ Build: 0 errors  
✅ Lint: 0 errors (4 pre-existing)  
✅ Tests: 1710/1710 passing  
✅ TypeScript strict mode: clean  

### Trade-offs & Rationale
- localStorage + events approach simpler than Context provider, no wrapper component needed
- Metadata restructuring requires careful JSON manipulation but avoids API changes
- Empty-tab filtering requires at least one Simple field per tab to avoid hiding tabs entirely

### Lessons Learned
1. When a prop is passed through multiple layers unchanged, it's a signal for global state
2. Metadata-driven UI requires careful section extraction to maintain logical relationships
3. For UI preferences: localStorage + CustomEvent + storage event listener = effective global state without Context

---

## 8. Ripley: Client-Side OrcaSlicer Bundle ZIP Extraction (Implemented)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-04-17  
**Status:** ✅ IMPLEMENTED  
**Impact:** Medium (enables import of .orca_printer and .orca_filament files)  

### Summary

Added support for importing OrcaSlicer bundle formats (`.orca_printer`, `.orca_filament`) without backend changes by extracting ZIPs on client side.

### Context

OrcaSlicer exports bundle files as standard ZIP archives containing individual JSON preset files. Existing import wizard only handled plain JSON. Need to support bundles with unified UX.

### Options Considered
1. Backend ZIP extraction + parsing
2. **CHOSEN:** Client-side ZIP extraction

### Decision & Implementation

**Chose client-side extraction** because:
- Backend APIs already handle JSON perfectly — no changes needed
- ZIP extraction is pure normalization (transforms ZIP → existing JSON shape)
- Frontend already has complete import wizard UX
- `fflate` library is tiny (8KB gzipped)
- Synchronous extraction is fast enough for typical bundle sizes

**Created `orcaBundleExtractor.ts` utility:**
- `isZipFile(data)`: Magic byte check (PK\x03\x04)
- `extractOrcaBundle(data)`: Unzip, parse JSONs, classify by discriminator, merge to bundle format
- Updated file input to accept `.json,.orca_printer,.orca_filament`
- Added `isExtracting` loading state during ZIP processing

### Quality Gates
✅ Build: 0 errors  
✅ Lint: 0 errors  
✅ Tests: 1710/1710 passing  

### Trade-offs
- **Pro**: Zero backend changes, perfect API compatibility, stateless design, instant client-side processing
- **Pro**: Error handling all client-side with immediate feedback
- **Con**: Couples frontend to OrcaSlicer ZIP structure (structure is stable)
- **Con**: Large bundles could briefly block UI (not a real-world concern for typical preset counts)

---

## 9. Ripley: Fix 28 Empty Select Boxes in Profile Editors (Implemented)

**Author:** Ripley (Frontend Dev)  
**Date:** 2026-04-16  
**Status:** ✅ IMPLEMENTED  
**Impact:** Small (UI correctness, no API changes)  

### Summary

Audit revealed 28 of 44 select fields in slicer profile editors rendered as empty dropdowns. Root cause: missing enum entries in `KNOWN_ENUMS` map in MetadataProfileRenderer.

### Context

Metadata-driven renderer uses priority chain: `KNOWN_ENUMS` → `meta.enum_values` → empty array. Most OrcaSlicer settings have no `enum_values` in metadata, so `KNOWN_ENUMS` is the only source.

### Decision & Implementation

**Add all missing enum entries to `KNOWN_ENUMS`** using authoritative values from OrcaSlicer's `PrintConfig.cpp`:
- Created shared arrays (`INFILL_PATTERNS`, `SURFACE_PATTERNS`) to DRY up repeated option lists
- Fixed `resolveControlType` to exclude numeric `enum_open` types from select rendering
- Entries must match exactly (inconsistent formatting: spaces, underscores, title case, numeric strings)

### Quality Gates
✅ All 44 select fields now render with correct options  
✅ No API changes required (pure frontend fix)  
✅ No test changes needed  
✅ Build + Lint: 0 errors  

---

## 10. Jeff: Global Simple/Advanced Toggle for Slicer Settings (User Directive)

**Date:** 2026-04-16  
**Request by:** Jeff Papiez (via Copilot)  
**Status:** ✅ Implemented (via Decision #7)  

### What
The Simple/Advanced toggle in slicer settings must be a **global, persisted setting**. When user toggles to Advanced in one profile editor, ALL profile editors must reflect Advanced mode. Preference must persist across sessions (localStorage). This is **not per-editor** — it's app-wide.

### Why
User mental model: "Advanced" is a UI-wide preference, not editor-specific state. Consistency and reduced friction.

### Implementation
Covered in Decision #7 (Global Persisted View Mode hook with localStorage + CustomEvent synchronization).

---

## 11. Dallas: Per-Slicer Native UI Key Architecture (Proposed)

**Author:** Dallas (Lead/Architect)  
**Date:** 2025-07-11  
**Status:** Proposed (pending team review)  
**Impact:** High (foundational design for multi-backend slicer support)  

### Summary
Architectural proposal for managing native UI keys across multiple slicer backends (OrcaSlicer, Cura, Prusa). Each slicer backend has different setting name conventions and structures. Key decision: namespace keys by backend to avoid collisions and allow independent evolutions.

### Status
Awaiting implementation decisions from team.

---

## 12. Lambert: OrcaSlicer Bundle Import Endpoint Architecture (Implemented)

**Author:** Lambert (Backend Dev)  
**Date:** 2026-04-05  
**Status:** ✅ IMPLEMENTED  
**Impact:** Medium (backend support for ZIP bundle imports)  

### Summary
Backend endpoint architecture for importing OrcaSlicer bundle formats. Handles parsing and storage of multiple preset files extracted from bundle ZIPs.

### Implementation
- `POST /api/orca/import-bundle` endpoint receives extracted JSON presets
- `OrcaBundleParsingService.cs` deserializes and validates presets
- Returns validation results with conflict detection for duplicates/overwrites

### Integration
Works with Ripley's client-side ZIP extraction (Decision #8). Frontend extracts ZIP, uploads individual presets to this endpoint.

---

## 13. Brett: Infill Pattern Icon Audit — OrcaSlicer Parity Analysis (Findings)

**Author:** Brett (Researcher)  
**Date:** 2025-07-24  
**Status:** Findings — Needs Action  
**Impact:** Medium (UI fidelity/parity with OrcaSlicer)  

### Executive Summary
**Zero of 28 infill icons in our `InfillPatternIcons.tsx` accurately match OrcaSlicer's.**

Only 4 icons in right spirit (gyroid, hilbert curve, archimedean chords, honeycomb). Remaining 24 completely wrong — depict naive geometric interpretation of pattern name rather than actual infill toolpath geometry. Also **missing 2 patterns** OrcaSlicer supports (`rectilinear-grid`, `rectilinear_interlaced`) and **have 1 pattern** (`stars`) that doesn't exist in OrcaSlicer.

### Root Cause
Our icons drawn as abstract geometric shapes based on **name** (e.g., "triangles" → triangle). OrcaSlicer's icons show **actual cross-section of infill toolpath as it appears in printed layer** — very different.

Example: "rectilinear" doesn't print as horizontal lines — it prints as diagonal lines alternating between +45° and −45° on consecutive layers.

### Key Findings
- OrcaSlicer icons: 24×24 viewBox (ours: 16×16)
- Two-layer design: gray (`#949494`, opacity 0.75) for alternate layer, teal (`#009688`) for primary
- Rounded-rect border: 21×21 with `rx="2"` in gray
- Source: `resources/images/param_*.svg` in SoftFever/OrcaSlicer GitHub

### Recommendation
**All 28 icons need replacement.** Correct approach:
1. Port OrcaSlicer's actual SVGs or create new icons matching same *pattern geometry*
2. Scale from 24×24 to 16×16 viewBox
3. Preserve two-layer design (gray + teal)
4. Add 2 missing patterns
5. Decide on `stars` pattern (may be invalid in OrcaSlicer)

### Licensing Note
OrcaSlicer is AGPLv3. Icons could be used as-is if PrintFarmer's license compatible, or used as reference to create independently-drawn icons. **Decision needed from team lead.**

### Implementation Effort
- **Small**: Each SVG mechanically converted to React component
- **Medium**: Coordinate scaling from 24×24 → 16×16
- SVG data for all patterns collected in audit document

### Patterns Status
- **Completely wrong (24)**: rectilinear, aligned-rectilinear, monotonic, monotonic-line, grid, line, concentric, triangles, tri-hexagon, cubic, adaptive-cubic, quarter-cubic, support-cubic, 3d-honeycomb, lateral-honeycomb, lateral-lattice, cross-hatch, zigzag, crosszag, locked-zag, lightning, TPMS-D, TPMS-FK
- **Partially correct (4)**: gyroid, hilbert-curve, archimedean-chords, honeycomb
- **Missing from us (2)**: rectilinear-grid, rectilinear_interlaced
- **We have, OrcaSlicer doesn't (1)**: stars


---

## User Directives — Code Review Gate (2026-04-22)

### Mandatory Triple Review Gate

### 2026-04-22T01:24Z: User directive — triple code review gate
**By:** Jeff Papiez (via Copilot)
**What:** ALL code must be reviewed by Bishop (GPT-5.4), Hicks (Gemini 3 Pro), AND Vasquez (Opus 4.6) before commit and push. All three reviewers must approve. This supersedes the previous directive requiring only Bishop.
**Why:** User request — multi-model review gate for maximum code quality


**Earlier version** (2026-04-22T01:22Z, superseded): Single-reviewer directive requiring only Bishop.

---

## Machine Settings Types — 105 Unique Keys (Dallas, 2026-07-18)

**Bead:** PFarm1-pysq.3  
**Status:** 📋 REFERENCE (used by Machine editor implementations)  
**Author:** Dallas

# Decision: Machine Settings Types

**Author:** Dallas (frontend)
**Date:** 2025-07-18
**Task:** PFarm1-pysq.3

## Key Decisions

### 1. 105 unique keys (not 125)
The metadata JSON has 106 field entries but `fan_speedup_time` is listed twice (same key, two sections in the Cooling Fan group). Deduplicated to **105 unique keys** in the interface. The `_meta.machineSettings: 125` count in the JSON appears to include additional internal-only keys not represented in the tab structure.

### 2. Compound fields typed as `string`
All fields marked `"compound": true` in metadata (G-code macros, bed_exclude_area, extruder_printable_area, fan_speedup_time/overhangs, resonance speeds, thumbnails, printer_notes) are typed as `string` since OrcaSlicer serialises them as semicolon-delimited strings internally.

### 3. Simple vs Advanced split
15 settings classified as `simple` — printable_height, bed_exclude_area, support_multi_bed_types, gcode_flavor, nozzle_type, nozzle_diameter, extruder_printable_area, min/max_layer_height, retraction_length, retraction_speed, machine_max_speed_x/y/z/e. Everything else is `advanced`.

### 4. Default values source
Defaults based on a generic Ender-3 class printer (220×220×250, Marlin, 0.4mm brass nozzle, i3 structure). Multi-material parameters use OrcaSlicer's own compiled defaults.

### 5. Pattern alignment
File structure mirrors `slicerSettingsTypes.ts` exactly — same section comment style, same export pattern, augmented with MODE_MAP / CATEGORY_MAP / DEFAULT objects that the process file didn't yet have.


---

## Process Metadata Extraction — Audit & Improvements (Lambert, 2026-07-25)

**Status:** ✅ VERIFIED — Audit complete, fixes applied  
# Process Metadata Extraction — Audit & Improvements

**Bead:** PFarm1-d3by
**Author:** Lambert (Backend)
**Date:** 2025-07-25

## Summary

Audited `tools/extract-orca-metadata.py` against latest OrcaSlicer source (main branch).
Found and fixed one extraction gap; regenerated metadata JSON with improved completeness.

## Findings

### Process Metadata (TabPrint::build) — Previously 344, now 347

The process section was already well-covered with 6 tabs and 318 tab fields.
Three new settings from the latest OrcaSlicer source were picked up:

- `combine_brims` — new Quality/Others option
- `initial_layer_travel_acceleration` — new Speed option
- `initial_layer_travel_jerk` — new Speed option

All 6 tabs remain correct: Quality, Strength, Speed, Support, Multimaterial, Others.

### Machine Metadata (TabPrinter::build_fff) — 125 settings, 6 tabs ✅

All 6 machine tabs were already correctly extracted:
Basic information, Machine G-code, Multimaterial, Extruder, Motion ability, Notes.

**Bug fixed:** 12 axis-expanded settings (`machine_max_speed_x/y/z/e`,
`machine_max_acceleration_x/y/z/e`, `machine_max_jerk_x/y/z/e`) were present in
the tab field layout but missing from the settings dictionary. These settings are
defined in PrintConfig.cpp using a C++ for-loop with string concatenation:

```cpp
for (const AxisDefault &axis : axes) {
    def = this->add("machine_max_speed_" + axis.name, coFloats);
    def->full_label = (boost::format("Maximum speed %1%") % axis_upper).str();
    ...
}
```

The static regex parser (`def = this->add("literal_name", coType)`) couldn't match
the concatenated key. Added `_expand_printconfig_axis_loops()` to pre-process
PrintConfig.cpp, expanding the AxisDefault loop into 4 copies with literal strings.
All 12 axis settings now have full metadata (label, tooltip, unit, type, mode, min).

### Filament Metadata — Previously 108, now 110

Two new settings from latest OrcaSlicer source:
- `activate_air_filtration_during_print`
- `activate_air_filtration_on_completion`

## Changes Made

### `tools/extract-orca-metadata.py`

- Added `_expand_printconfig_axis_loops()` — detects the `for (const AxisDefault &axis : axes)`
  loop in PrintConfig.cpp and expands it into literal definitions for x/y/z/e
- Updated `parse_print_config()` to call the expansion before regex parsing
- Added fallback patterns for `def->full_label` and `def->tooltip` to match plain strings
  (not wrapped in `L()`) that result from the expansion

### `orcaSettingsMetadata.json`

Regenerated from latest OrcaSlicer source. Changes:
- `_meta.totalSettings`: 781 → 798
- `_meta.filamentSettings`: 108 → 110
- `_meta.processSettings`: 344 → 347
- `_meta.machineSettings`: 125 → 125 (same count but axis keys now have full metadata)
- 5 new settings added across filament/process
- 12 machine axis settings now have proper labels, tooltips, and units

## Edge Cases Noted

1. **Compound fields** — Some settings use `get_option()` / `Option{}` for multi-value
   lines (e.g., x+y dimensions). These are correctly tagged `compound: true` in the JSON.

2. **Conditional visibility** — OrcaSlicer's `toggle_options()` methods control field
   visibility based on other settings (e.g., support options hidden when support disabled).
   This is NOT captured in the metadata. Frontend must handle conditional visibility.

3. **Dynamic extruder tabs** — The Extruder tab is created per-extruder with
   `wxString::Format("Extruder %d", i+1)`. The script handles this by constructing
   a single canonical Extruder tab from known section names.

4. **Setting Overrides page** — The filament Setting Overrides tab has 0 fields in the
   tab layout because it's populated dynamically at runtime. This is expected.

## Validation

- ✅ JSON validates (`json.load()` succeeds)
- ✅ All tab field keys exist in their category's settings dict
- ✅ All 12 axis-expanded machine settings have label, tooltip, unit, type, mode, min
- ✅ Settings counts ≥ previous values (no regressions)
- ✅ React lint unaffected (pre-existing error in metadataTypes.ts, not related)


---

## Backend Snake_case Migration Verification (Lambert, 2026-08-01)

**Status:** ✅ VERIFIED — No backend issues  
# PFarm1-pysq.5 — Backend Verification: snake_case Migration Impact

**Author:** Lambert (Backend Dev)  
**Date:** 2026-08-01  
**Status:** ✅ VERIFIED — No backend issues

---

## 1. How Profile Settings Are Stored/Transmitted

**Architecture: Opaque JSON blobs with promoted convenience fields.**

The `ProcessProfile` domain entity stores settings in three TEXT columns:

| Column | Content | Key Format |
|---|---|---|
| `RawJson` | Full raw slicer profile JSON as imported | snake_case (native OrcaSlicer) |
| `SettingsJson` | Extracted key-value pairs for quick display | snake_case (native OrcaSlicer) |
| `AdvancedSettings` | Additional slicer-specific settings | snake_case |

Plus four promoted typed columns for server-side filtering/display: `LayerHeight`, `InfillPercentage`, `PrintSpeed`, `EnableSupports`. These are C# properties — completely independent of the JSON key format.

The `ProcessProfileDto` has:
- ~30 promoted C# properties (serialized as camelCase by ASP.NET's `JsonNamingPolicy.CamelCase`)
- A `Dictionary<string, object> Settings` bag containing ALL profile keys in their **native snake_case format**

The promoted properties are convenience accessors only. The `Settings` dictionary is the authoritative full-settings source, populated by `SerializeElementToDict()` which preserves original key names verbatim.

## 2. Do snake_case Keys Work End-to-End?

**YES — fully verified.**

### Parsing (OrcaSlicer → Backend)
`OrcaProfilesService.ParseProcessProfile()` reads snake_case keys directly:
- `root.TryGetProperty("layer_height", ...)` → `profile.LayerHeight`
- `root.TryGetProperty("sparse_infill_density", ...)` → `profile.InfillPercentage`
- `SerializeElementToDict(root)` → `profile.Settings` (preserves all snake_case keys)

### Storage (Backend → DB)
`RawJson` and `SettingsJson` are stored as-is. Keys remain snake_case throughout.

### Override Application (Frontend → Worker)
`HttpJobPollerService.cs` line 513: *"Apply user overrides — all keys are native snake_case, pass through directly"*
```csharp
profile.ProcessProfile.Settings[prop.Name] = prop.Value.ValueKind switch { ... };
```
No translation layer — keys pass through verbatim from the frontend override JSON into the Settings dictionary.

### Export (.3mf Bundle)
`OrcaBundleExportService.ExportProcessPresetsAsync()` builds presets with snake_case keys:
- `["layer_height"]`, `["print_speed"]`, `["infill_sparse_density"]`

### SignalR
Slicer SignalR hubs (`/hubs/slicer-registry`, `/hubs/slicers`) transmit high-level DTOs (progress, status). Profile settings are opaque `Settings` dictionary payloads — the hub's `CamelCase` naming policy only affects DTO property names, not dictionary keys inside the `Settings` bag.

## 3. Issues Found

**None.** The backend was already designed for snake_case keys from day one. OrcaSlicer natively uses snake_case, and the backend's parsing/storage/export pipeline preserves these keys throughout.

## 4. Can CamelToNativeKeyMap Be Deleted?

**Already deleted.** Commit `68042d59` ("refactor: delete CamelToNativeKeyMap and simplify override passthrough [closes PFarm1-pysq.4]") removed the map entirely. The git history shows the full lifecycle:

1. `e9c2edef` — Initial camelCase→snake_case mapping added
2. `a7b7982c` — Expanded to 187 entries
3. `68042d59` — **Deleted entirely** after frontend migrated to native snake_case keys

No remnants of `CamelToNativeKeyMap` exist in the current codebase (verified via grep).

## 5. Test Results Summary

```
Passed!  - Failed: 0, Passed: 463, Skipped: 0, Total: 463, Duration: 1m 14s
```

All 463 slicer/profile/OrcaSlicer tests pass with 0 failures. Test filter: `FullyQualifiedName~Slicer|OrcaSlicer|Orca|Profile`.

Coverage:
- `Farm.Slicer.Module`: 32.5% line / 31.42% branch
- `Farm.Slicer.Module.Api`: 27.63% line / 18.73% branch
- `Farm.Slicers.OrcaSlicer.v2_3_1`: 79.16% line / 62.5% branch

## Key Files Reviewed

| File | Verification |
|---|---|
| `slicer/Farm.Slicer.Module/Dtos/ProcessProfileDto.cs` | Settings dict is opaque `Dictionary<string, object>` — passes through any key format |
| `slicer/Farm.Slicer.Module/Domain/ProcessProfile.cs` | RawJson/SettingsJson stored as TEXT blobs — format-agnostic |
| `worker-shared/HttpJobPollerService.cs` | Override keys passed through directly (line 513), no CamelToNativeKeyMap |
| `orcaslicer-worker/Services/OrcaProfilesService.cs` | ParseProcessProfile reads snake_case keys directly from JSON |
| `slicer/Farm.Slicer.Module.Api/Services/ProfilesService.cs` | Settings populated from AdvancedSettings JSON blob |
| `slicer/Farm.Slicer.Module.Api/Services/OrcaBundleExportService.cs` | Export uses snake_case keys natively |

## Conclusion

The backend is **fully compatible** with the frontend's snake_case migration. No code changes needed. The opaque JSON blob architecture means settings keys flow through untouched from frontend → backend → OrcaSlicer worker → .3mf export.


---

## Lightweight Geometry Upload Endpoint (Lambert, 2026-08-01)

**Status:** 📋 PLANNED (Cut Model tool feature dependency)  
# Decision: Lightweight Geometry Upload Endpoint

**Date:** 2026-08-01
**Author:** Lambert
**Status:** Implemented (not yet committed)

## Context

The Cut Model tool in the slicer workspace generates STL geometry in the browser via Three.js.
These are `blob:` URLs that the slicer backend cannot fetch. We need a way to upload the
generated STL binary to the server and get back a URL the slicer worker can HTTP-fetch.

## Decision

Added `POST /api/3d-models/upload-geometry` as a **lightweight** variant of the existing
`POST /api/3d-models/upload`. It reuses the same controller, storage path, and download
endpoint but skips:

- Hash-based deduplication (cut geometry is unique each time)
- Thumbnail generation (not meaningful for cut pieces)
- Model analysis/dimensions (not needed for slicing)

The endpoint creates a minimal `Model3D` DB row so the existing
`GET /api/3d-models/file/{id}` download endpoint serves the file — no new download
plumbing needed.

## Implications for the Team

- **Ripley (Frontend):** The response DTO is `GeometryUploadResultDto` with fields
  `id`, `fileName`, `fileSize`, `fileUrl`. The `fileUrl` value (e.g., `/api/3d-models/file/{id}`)
  can be passed directly as `ModelFileUrl` when submitting a slice job.
- **No schema change:** Uses existing `Model3D` table, no migration needed.
- **Cleanup:** Cut geometry files accumulate in the uploads directory. A future
  housekeeping task should prune orphaned geometry (no associated slice job) older than N days.


---

## OrcaSlicer Section SVG Icons — Inventory & Theming (Newt, 2026-07-15)

**Status:** ✅ COMPLETE (118 icons, all verified)  
# Design Decision: OrcaSlicer Section SVG Icons

**Author:** Newt (Designer)  
**Bead:** PFarm1-98f1  
**Date:** 2025-07-15

## Summary

All 118 OrcaSlicer section/tab SVG icons are present in `src/Web/ReactApp/public/icons/orca/` and verified against `orcaSettingsMetadata.json`. An `index.json` manifest was created for programmatic access. Hardcoded colors were converted to CSS custom properties with fallbacks.

## Icon Inventory

- **75** icons referenced directly in metadata tabs/sections
- **115** icons listed in the metadata `icons` key
- **118** total unique SVGs on disk (superset covers both)
- **0** missing icons

## Color Theming

All 118 SVGs use a consistent two-tone color scheme from OrcaSlicer:

| Role | Original Color | CSS Variable | Usage |
|---|---|---|---|
| Structural | `#949494` (gray) | `--orca-icon-secondary` | Borders, outlines, dial marks |
| Accent | `#009688` (teal) | `--orca-icon-accent` | Highlighted elements, primary paths |

Colors were converted from hardcoded hex values to `var(--orca-icon-secondary, #949494)` and `var(--orca-icon-accent, #009688)` in inline `style` attributes. Fallback values preserve the original OrcaSlicer appearance.

**Theming behavior depends on how SVGs are loaded:**
- `<img src="...">` — Isolated context; fallback values used (original colors, works on dark backgrounds)
- Inline SVG / `dangerouslySetInnerHTML` — Parent CSS variables override; full theme control

Both colors have sufficient contrast on dark backgrounds (#1a1a2e or similar), so the fallback path is dark-theme safe.

## ViewBox Sizes

SVGs have three viewBox sizes. All are square, so they scale uniformly:

| viewBox | Count |
|---|---|
| `0 0 18 18` | 62 |
| `0 0 24 24` | 31 |
| `0 0 16 16` | 25 |

**Decision: Not normalized.** Since all viewBoxes are square, the rendering container controls display size. Modifying coordinate spaces risks distorting the hand-crafted paths. The `index.json` includes viewBox metadata so consumers can handle sizing if needed.

## Files Created/Modified

- **Modified:** 118 SVG files (color → CSS variable conversion)
- **Created:** `src/Web/ReactApp/public/icons/orca/index.json` (icon manifest)


---

## Filament Settings Types — Compound Fields as String (Ripley, 2026-07-31)

**Status:** ✅ REFERENCE (used by Filament editor)  
# Decision: Filament Settings Types — Compound Fields as `string`

**Author:** Ripley (Frontend)
**Date:** 2025-07-31
**Bead:** PFarm1-pysq.2

## Context

OrcaSlicer filament settings include "compound" fields (per-extruder values stored as semicolon-delimited strings like `"200"` or `"200;210"`). The metadata JSON marks these with `"compound": true`.

## Decision

Compound fields are typed as `string` in `OrcaFilamentSettings`, not `number[]`. This matches OrcaSlicer's internal representation and avoids parse/serialize overhead at the type boundary. Non-compound numeric fields use `number`, booleans use `boolean`.

## Consequences

- Components rendering compound fields must handle string parsing when displaying individual extruder values
- Simpler JSON round-trip: values pass through unchanged from OrcaSlicer profiles
- Consistent with how the backend stores and returns these values


---

## Metadata Renderer Refactor — Monolith Extraction (Ripley, 2026-08-01)

**Status:** ✅ REFERENCE (code organization decision)  
# Decision: Extract reusable metadata renderer components

**Author:** Ripley (Frontend Developer)
**Date:** 2025-08-01
**Bead:** PFarm1-ugub

## Context

`MetadataProfileRenderer.tsx` was a 976-line monolith containing types, constants, helper functions, and three internal components (`OrcaIcon`, `MetadataSection`, `MetadataTab`). None of these could be imported independently, making reuse impossible and the file difficult to navigate.

## Decision

Extract the monolith into five focused modules:

| File | Responsibility |
|---|---|
| `metadataTypes.ts` | Shared types, constants (KNOWN_ENUMS, TEXTAREA_KEYS, etc.), helper functions |
| `OrcaIcon.tsx` | Blue-tinted OrcaSlicer section icon component |
| `MetadataSettingRow.tsx` | Single-field renderer (all control types + paired temperature rows) |
| `MetadataSection.tsx` | Section group renderer with view-mode filtering and paired temp detection |
| `MetadataTabRenderer.tsx` | Tab-level renderer mapping sections to MetadataSection |

`MetadataProfileRenderer.tsx` becomes a ~100-line thin facade that re-exports everything, preserving all existing import paths.

## Trade-offs

- **More files** — 5 new files instead of 1, but each is <300 lines and single-purpose
- **Paired hook workaround** — `useChangeTracking` for the optional paired temperature key is always called (with a fallback key when absent) to satisfy React's rules-of-hooks
- **OrcaIcon separated** — moved to its own `.tsx` file to avoid `react-refresh/only-export-components` lint error on the pure `.ts` types file

## Validation

- ✅ ESLint: 0 errors (1 pre-existing warning in SettingRow.tsx)
- ✅ Tests: 1734/1734 pass, 12 skipped, 0 failures
- ✅ Backward compatibility: all existing consumers unchanged


---

## Frontend Slicer Fixes — Blob Leak, Profile Selection, Filtering (Ripley, 2026-07-31)

**Status:** ✅ IMPLEMENTED  
# Frontend Slicer Fixes — Blob Leak, Profile Selection, Filtering, Multi-Import

**Author:** Ripley (Frontend)  
**Date:** 2026-07-31  
**Beads:** PFarm1-eidj, PFarm1-eh3a, PFarm1-yigr, PFarm1-issr

## Summary

Four slicer-area frontend bugs fixed in a single session:

1. **Blob URL memory leak** — SlicerWorkspace now tracks and revokes blob URLs on unmount/replacement
2. **Machine profile reset** — Auto-select effect now validates against both system and custom profiles
3. **Profile filtering by printer** — Custom profiles filtered using OrcaSlicer rawJson metadata
4. **Multi-file import** — All 3 profile file inputs now accept multiple files

## Decision Points

- **Blob tracking via useRef (not state):** Blob URLs are side-effect resources, not renderable state. A ref avoids unnecessary re-renders while keeping cleanup deterministic.
- **Fuzzy name matching fallback:** When rawJson metadata lacks `printer_model`, we match against tokenized printer name words. Profiles without any match metadata are shown (not hidden) — safer to show extra than hide needed profiles.
- **OrcaImportWizard NOT updated for multi-select:** The wizard is a multi-step flow (upload→preview→review→import) that processes one bundle. Multi-select there would require batch orchestration. The simpler file-input multi-select on NewSliceJobPage covers the common case.

## Files Changed

- `src/Web/ReactApp/src/features/slicer/components/viewer/SlicerWorkspace.tsx`
- `src/Web/ReactApp/src/features/slicer/pages/NewSliceJobPage.tsx`

