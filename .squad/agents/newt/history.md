# Newt History

## Core Context

Newt is a deployment & DevOps specialist. Key contributions:
- Docker build optimization & multi-stage Dockerfile refactoring
- Backend plugin system Docker integration
- Container image size reduction & layer optimization
- Deployment script improvements & error handling
- Camera fit revision & UI integration (2026-03-25)
- FailureDetectionMonitoringSummary redesign (2026-06-10)
- Infrastructure automation & cloud deployment

Early entries (pre-2026-03-25) summarized for size management. See decisions-archive.md for detailed history.

---

## iOS Design — Migrated from PFarm-Ios Parker (2026-05-20)

### Touch Target Compliance & Button Sizing (2026-03-09)
**Problem:** Full-width action buttons throughout the iOS app were ~34-36pt — below Apple HIG minimum.

**Solution:** Created `PrintFarmer/Views/Components/ActionButtonStyle.swift` with `.fullWidthActionButton()` view modifier:
- `.standard` = 44pt height (Apple HIG minimum for all interactive elements)
- `.prominent` = 50pt height (primary actions: "Start Print", "Emergency Stop", "Sign In")

**Applied across 8 view files:** `LoginView`, `PrinterDetailView`, `JobDetailView`, `NFCScanButton`, `NFCWriteView`, `AutoPrintSection`, `MaintenanceAlertRow`

**Design rules established:**
- Minimum 44pt touch target for all action buttons per Apple HIG
- 50pt for primary actions requiring extra prominence
- Font upgraded from `.caption` → `.subheadline` on small-button rows (AutoPrint, MaintenanceAlert) for readability
- Consistent 8pt gap between vertically stacked buttons
- Maintained existing `.destructive` role, color tinting, and font weights

**Key file:** `PrintFarmer/Views/Components/ActionButtonStyle.swift`

---

## FailureDetectionMonitoringSummary Redesign (2026-06-10)

**Task:** Redesign failure detection summary component to reduce visual weight on printer cards  
**Status:** ✅ COMPLETE

### Problem Analysis
- Original component: 422 lines, heavy visual treatment
- Rendered full monitoring dashboard on every card: header, icon+headline+summary, badge, 3 stat boxes, "Watching" box, operator action box
- Out of place: standalone widget styling (rounded-xl, gradient backgrounds, heavy shadows) didn't match card aesthetic
- Compact card showing non-compact information

### Design Decisions
- **Compact variant:** Slim inline row — icon + headline + badge + optional subline. Max ~40px height for healthy states.
- **Detailed variant:** Proportional section — icon + headline + badge + summary. Operator action box only when tone is critical/attention.
- Removed: SummaryStat grid (Source, Last scan, Latest result), "Watching" box
- Kept: tone system (critical/attention/healthy/standby), icon + headline pattern, color coding

### Changes
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringSummary.tsx`: 422 → 247 lines (41% reduction)
- Updated test file with new assertions
- Fixed unrelated test issues (FailureDetectionMonitoringOverlay tests needed QueryClientProvider wrapper)

### Validation
- ✅ ESLint: 0 errors
- ✅ React Tests: 1615/1615 passing

### Learnings
- Card components should show "at-a-glance" status, not embedded dashboards
- Operators need tone + headline at card level; detailed stats belong in drill-down modals
- Visual weight = border-radius + shadow + gradient + padding + number of elements

---

## Camera Fit Revision (2026-03-25)

**Task:** Revise Ripley's camera fit implementation based on Kane's review findings  
**Timestamp:** 2026-03-25T06:25:00Z  
**Status:** ✅ COMPLETE — Approved for deployment

### Changes Applied
- **Fix #1:** Changed PrinterCameraPreview.tsx line 179 from `object-cover` to `object-contain`
- **Fix #2:** Increased DetailedPrinterCard.tsx line 544 from `max-w-[28rem]` (448px) to `max-w-[40rem]` (640px)

### Design Decisions
- Chose 640px over 576px recommendation to maximize visibility for monitoring use case
- Used responsive `w-full max-w-[40rem]` instead of fixed width for flexibility
- Maintained black letterboxing for non-16:9 camera feeds

### Validation Results
- ✅ ESLint: 0 errors
- ✅ React Tests: 1499/1499 passing
- ✅ Regression Tests: 3/3 passing
- ✅ No new failures, no regressions

### Approval
- Kane re-reviewed and approved for deployment
- 308% size improvement (208px → 640px from original)
- Zero blockers, ready for immediate production deployment

### Learnings
- Clear line-number specific feedback from reviewer enabled precise fixes
- Regression tests provided confidence that fixes worked correctly
- Responsive design preferred over fixed widths for layout flexibility

---

## FailureDetectionStatusModal Wide Layout (2025-07-22)

**Task:** Fix spaghetti detection details modal overflowing viewport on large screens  
**Status:** ✅ COMPLETE

### Changes
- `FailureDetectionStatusModal.tsx`: Switched from `size="md"` (448px) to `width="max-w-4xl"` (896px)
- Added `maxHeight="max-h-[85vh]"` to tighten viewport clearance
- Restructured body into `lg:grid-cols-2` layout: left column (context/guidance), right column (incidents/timeline)
- Status header + detail tiles remain full-width above the grid
- Snapshot link relocated from modal bottom into left column with operator guidance
- Mobile/tablet stays single-column stacked (responsive breakpoint at lg: 1024px+)

### Validation
- ✅ ESLint: 0 errors
- ✅ React Tests: 1615/1615 passing

### Learnings
- Modal `size` presets (sm–xl) top out at 576px — use `width` prop for content-heavy modals that need more room
- Content-heavy modals benefit from semantic column splits (context vs. history) rather than arbitrary left/right splits
- `max-w-4xl` (896px) is the right size for 2-column modal layouts — wide enough for readability, narrow enough to feel modal-like

---

## OrcaSlicer UI Parity Audit (2026-07-23)

**Task:** Audit UI components on `feature/orcaslicer-full-ui-parity` branch for OrcaSlicer visual parity  
**Status:** ✅ COMPLETE — 5 components audited

### Files Audited

1. **SliceJobsPage.tsx** — ✅ PASS (minor deviation noted)
2. **SendToPrinterModal.tsx** — ✅ PASS
3. **NewSliceJobPage.tsx (onboarding)** — ⚠️ DEVIATION (fixable)
4. **SlicerSettingsPanel.tsx** — ✅ PASS
5. **App.tsx (routes)** — ✅ PASS

### Detailed Findings

**SliceJobsPage.tsx (656 lines):**
- ✅ PASS: Status badge mapping uses correct variants (Completed→success, Failed→error, Processing→primary, Cancelled→warning, Queued→default)
- ✅ PASS: Progress bar uses `bg-pf-accent` for fill with proper `bg-pf-bg-2` track — matches OrcaSlicer dense progress style
- ✅ PASS: Table header uses `bg-pf-bg-1 text-pf-text-secondary` — industrial dark header pattern
- ✅ PASS: Row hover state `hover:bg-pf-bg-1/50` — subtle interaction feedback
- ✅ PASS: Card grid uses responsive columns `sm:grid-cols-2 lg:grid-cols-3` — appropriate density
- ✅ PASS: Icons from MDI (LayersIcon, PrinterIcon, DownloadIcon, CloseIcon, etc.)
- ✅ PASS: Empty state centered with icon + CTA pattern — matches OrcaSlicer onboarding
- ⚠️ DEVIATION (minor): Error message uses `text-xs text-pf-error bg-pf-error/10` — acceptable but OrcaSlicer uses solid error background with more padding. Consider `p-2` instead of `px-2 py-1` for better readability.

**SendToPrinterModal.tsx (104 lines):**
- ✅ PASS: Uses Modal component API correctly (`size="sm"`, `titleIcon`, `title`)
- ✅ PASS: Child form pattern (mount/unmount with `isOpen`) — prevents stale state
- ✅ PASS: Online-only printer filter — good UX pattern
- ✅ PASS: Empty state message uses `text-sm text-pf-text-secondary` — correct token
- ✅ PASS: Button layout uses `flex items-center justify-end gap-2 mt-6` — standard modal footer pattern
- ✅ PASS: Primary action button uses `variant="primary"` with icon
- ✅ PASS: Cancel button uses `variant="secondary"` — correct hierarchy
- ✅ PASS: Checkbox component for "Start printing immediately" — no raw HTML

**NewSliceJobPage.tsx (onboarding banner at lines 767-803):**
- ✅ PASS: Icon size `w-16 h-16` — appropriate for hero/onboarding
- ✅ PASS: Text hierarchy: `text-xl font-semibold` heading, `text-sm text-pf-text-secondary` body
- ✅ PASS: Button variants correct (primary for main CTA, secondary for alternative)
- ⚠️ DEVIATION: Missing illustration — OrcaSlicer uses visual illustrations for onboarding. Currently uses just `LayersIcon`. Consider adding an SVG illustration or richer visual treatment.
- ⚠️ DEVIATION: `py-16` padding is generous but OrcaSlicer empty states are more vertically compact (`py-12` typical). Minor.
- ✅ PASS: Max-width on description `max-w-md` — good readability constraint

**SlicerSettingsPanel.tsx:**
- ✅ PASS: Three-tier view mode tabs (Basic/Simple/Advanced) — matches OrcaSlicer exactly
- ✅ PASS: Category tabs for advanced mode with dirty indicator dots — OrcaSlicer pattern
- ✅ PASS: Uses `bg-pf-bg-1 rounded-lg` wrapper — correct panel styling
- ✅ PASS: Tab buttons use `variant="tab"` and `variant="subtle"` appropriately
- ✅ PASS: Setting sections use `divide-y divide-pf-border` — clean separation
- ✅ PASS: New `advancedSettings` and `onAdvancedSettingsChange` props — extensibility for dynamic Orca settings
- ✅ PASS: DynamicAdvancedSettingsSection for unmodeled settings — good architecture

**App.tsx (routes):**
- ✅ PASS: New routes use FeatureGate + RouteSuspense pattern
- ✅ PASS: Lazy loading for SliceJobsPage and ImportOfficialProfilesPage — performance

### Accessibility Check

- ✅ aria-label on all icon-only buttons ("Cancel job", "Download gcode", "Send to printer")
- ✅ aria-label on Select components ("Filter by status", "Select printer")
- ✅ role="grid" on table, role="row" on rows — semantic structure
- ✅ data-testid on onboarding elements — testability
- ⚠️ NOTE: Progress bar lacks `role="progressbar"` and `aria-valuenow` — minor a11y gap (line 531-536)

### Recommendations

1. **Progress bar a11y (SliceJobsPage.tsx:531):** Add ARIA attributes:
   ```tsx
   <div
     role="progressbar"
     aria-valuenow={job.progressPercent}
     aria-valuemin={0}
     aria-valuemax={100}
     className="h-full bg-pf-accent..."
   />
   ```

2. **Onboarding illustration (NewSliceJobPage.tsx):** Consider adding a slicing-themed SVG illustration above the LayersIcon for richer onboarding visual. OrcaSlicer uses printer/slicer imagery.

3. **Error message padding (SliceJobsPage.tsx:479):** Change `px-2 py-1` to `p-2` for more breathing room on error messages.

### Learnings
- OrcaSlicer's 3-tier settings panel (Basic/Simple/Advanced) is now fully implemented with dirty indicators
- Progress bars should always include ARIA progressbar role for screen readers
- Onboarding empty states benefit from illustrations — icon-only feels minimal for "first run" experience
- PrintFarmer's pf-* token system provides good OrcaSlicer-like dark industrial aesthetic when used consistently

- 2026-05-20: Assigned mobile controls v1 UX design support on issues #284 (preheat), #285 (jog), #286 (home). Fixed presets and feedrates locked per v1 — no customization UX needed. See decisions.md "Mobile API Drift + Basic Printer Controls v1".

- 2026-05-21: Ralph Round 1 (Phase 0) completed — see `.squad/log/2026-05-21T09-00-00Z-ralph-round-1-phase-0.md`.


- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291, #292, #293, #294, #295, #296, #297, #298). See `.squad/log/2026-05-21T08-15-00Z-ralph-rounds-2-5-phase-1-complete.md`. Phase 2 launching (#284 preheat, #285 home, #286 jog).

---

## 2026-05-28: Printer Controls Section Design Spec (#283)

**Task:** Create design specification for printer controls (preheat, home, jog) in iOS `PrinterDetailView`
**Status:** ✅ COMPLETE — PR opened [OlyForge3D/PrintFarmerMobile#1](https://github.com/OlyForge3D/PrintFarmerMobile/pull/1)

### Design Decisions

1. **Preheat layout:** List-style rows (not grid) — shows temperature readouts inline (e.g., "PLA — 200°/60°") for at-a-glance reference. Each row is a full-width tappable area meeting 44pt HIG minimum.

2. **Home layout:** 3-button horizontal row with 60pt height. Icons: `house.fill` (All), `arrow.left.and.right` (XY), `arrow.up.and.down` (Z).

3. **Jog layout:** Segmented pickers for axis (X/Y/Z) and step (0.1/1/10/100mm), with paired +/− buttons showing dynamic labels ("Move X +10mm").

4. **Disabled-while-printing:** Color-blind friendly — uses **lock icon** (`lock.fill`) + 0.5 opacity, not just color change. Per spike #279, client-side gating required for states: Printing, Pausing, Paused, Resuming, Cancelling, Heating.

5. **Hidden-while-offline:** Entire Controls section conditionally rendered only when `printer.isOnline == true`.

### Design Tokens Used

| Token | Usage |
|-------|-------|
| `pfCard` | Subgroup card backgrounds |
| `pfBorder` | Card stroke borders |
| `pfAccent` / `pfButtonPrimary` | Button tints (Home, Jog) |
| `pfWarning` | Flame icon (preheat heating) |
| `pfSecondaryAccent` | Snowflake icon (Cool Down) |
| `pfTextPrimary/Secondary/Tertiary` | Text hierarchy and disabled states |

### Key iOS Component Files

- `PrintFarmer/Theme/ThemeColors.swift` — All `pf*` color tokens
- `PrintFarmer/Views/Components/ActionButtonStyle.swift` — `.standard` (44pt) / `.prominent` (50pt) sizing
- `PrintFarmer/Views/Printers/PrinterDetailView.swift` — Target integration point

### HIG Patterns Applied

- Touch targets: All buttons ≥44pt (preheat rows: 44pt, home buttons: 60pt, jog buttons: 56pt)
- Segmented pickers: Native `.segmented` style
- Dark Mode: All colors via adaptive `pf*` tokens
- Haptics: `UIImpactFeedbackGenerator(.medium)` recommended on button press

### Files Created

- `docs/design/printer-controls-section.md` — Full design specification (611 lines)

## Cross-Team Note (2026-05-29)

**Dallas** (#290 status-gating) complete: API guards validated via PR #308. State blocking for controls confirmed safe.
**Gorman** (#280 capabilities) complete: Endpoint confirmed live. Fallback table canonical.
**Unblocked:** UI gating decisions finalized; PR OlyForge3D/PrintFarmerMobile#1 design decisions locked.

---

## 2026-05-28: PR #1 Review Fixes — Capability Gating, Jog Default, Home Endpoints

**Task:** Address Bishop's review on PR #1 (printer-controls design spec)
**Status:** ✅ COMPLETE — Changes pushed, PR comment posted

### Issues Fixed

1. **Capability-gated states missing (lines 355-387 new):** Spec only defined idle/pending/printing/offline. Added explicit rule: **hide entire subgroup** when `supportsTemperatureControl == false` (Preheat) or `supportsMovement == false` (Home, Jog). Cleaner UX than disabled-row clutter.

2. **Jog default mismatch (lines 342, 567):** Spec said `10` / `10.0`; #286 acceptance criteria locks default at `1 mm`. Updated both Jog Specifications table and Implementation Notes.

3. **Wrong API endpoints (lines 231-235):** Spec said all three Home buttons call `/home` with axes body. Gorman verified backend has dedicated routes: `/home` (all), `/homexy`, `/homez` — no axes body for dedicated routes. Updated table.

### Learnings

| Spec Section | Ambiguity Caused | Resolution |
|--------------|------------------|------------|
| State Matrix | Hudson didn't know whether to HIDE or DISABLE when capability missing | Added "Capability-Gated Subgroups" subsection with explicit hide rule |
| Jog Specifications | Conflicting defaults between spec (10mm) and issue AC (1mm) | Single source of truth: spec now says 1mm, matching #286 |
| API endpoints | Home XY/Z spec implied same endpoint with body | Dedicated routes documented; no axes body |

**Capability-gating rule chosen:** HIDE entire subgroup when capability is `false`. Rationale: operators should only see controls their printer can actually use; disabled-row clutter confuses more than it helps. This is distinct from "disabled during print" which shows controls but blocks interaction.

---

## Round 19 (2025-11-24): PrinterControlsSection Integration Fix-up — PR #15

**Task:** Correct Home gate logic, ViewModel injection details, test scope for integration plan
**Status:** ✅ COMPLETE — Fix-up pushed to `squad/289-printer-controls-ios-integration-fix-up`

### Fixes Applied

1. **Home gate logic:** Changed from `canHomeAll` alone to `canHomeAll || canHomeXY || canHomeZ` (matches `HomeSubgroup.hasAnyHomeCapability`). Ensures subgroup visible if ANY home capability present, not just all-axes.

2. **ViewModel injection clarified:** Added explicit `init(printerId:)` constructor. Wired `configure(printerService:)` method called from `.task` receiving `@EnvironmentObject ServiceContainer.printerService`. Avoids ambiguity about when/how service becomes available.

3. **Test scope corrected:** 
   - New test file (not added to existing suite)
   - swift-snapshot-testing SPM dependency added to Package.swift
   - Test target updated in `Package.swift` `testTargets`
   - References PR #14 (snapshot spike) for capability fixtures

### Learnings

- Integration plan specs must call out **every injection point and its timing** — `init` vs `.task` vs `@Environment` ambiguity causes follow-up CR cycles.
- Test scope changes (new files, new deps) belong in the scope section, not hidden in "implementation notes".
- Single-capability gates (`canHomeAll`) should be ORed with per-axis gates (`canHomeXY || canHomeZ`) at the subgroup level, not just at individual button level.

---

## Round 17 (2025-11-23): PrinterControlsSection Integration Plan — PR #15

**Task:** Design composition strategy for `PrinterControlsSection` integration into `PrinterDetailView`  
**Status:** ✅ COMPLETE — Integration plan PR opened

### Design Decisions

1. **Composition pattern:** Private `controlsSection()` helper on `PrinterDetailView` (mirrors existing `actionSection` pattern). Placement: after `actionSection` in view hierarchy.

2. **State management:** Single `@State var controlsViewModel: PrinterControlsViewModel` on parent. Lazy-injected via `.task(id:)` whenever printer ID or capability set changes. Cleaner than per-subgroup state; centralizes capability filtering.

3. **Scope for Hudson integration:**
   - ~40 lines `PrinterDetailView.swift` (controlsSection helper + state binding)
   - ~10 lines `PrinterControlsViewModel.swift` (init + property)
   - Subgroup files (PreheatSubgroup, HomeSubgroup, JogSubgroup, ControlsViewModel extensions) ship complete from PR stack #11–#13

### Key Architecture Decisions

- **Single ControlsViewModel instance:** Avoids duplication; all subgroups tap same printer/command sources
- **Lazy `.task` injection:** Efficiency — only computed when printer ID or caps change, not on every view redraw
- **Subgroup encapsulation:** Each subgroup (Preheat, Home, Jog) owns its own ViewModel extension + View file; can be tested + reused independently
- **Integration point locked:** No blocking design review feedback; ready for Hudson's implementation phase

### Design Deliverable

- PR https://github.com/OlyForge3D/PrintFarmerMobile/pull/15
- Integration plan approved by squad; design frozen for coding phase

### Learnings

- Composition via helper functions avoids `@ViewBuilder` complexity for mid-size sections
- Lazy state injection via `.task` is preferred over on-demand injection for predictability
- Stacked PR workflow allows subgroups to ship from parent stack without requiring immediate integration

---
