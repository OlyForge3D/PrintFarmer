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

**Audit Summary:** 5 components on `feature/orcaslicer-full-ui-parity` reviewed. ✅ 5/5 PASS (3 minor deviations noted). SliceJobsPage & SendToPrinterModal fully compliant. NewSliceJobPage: missing illustration (OrcaSlicer uses visual onboarding). SlicerSettingsPanel: 3-tier UI (Basic/Simple/Advanced) + dirty indicators fully implemented. Minor recommendations: (1) Add ARIA role="progressbar" + aria-valuenow to progress bars; (2) Consider onboarding illustration for richer UX; (3) Error message padding increased (px-2 → p-2). Key learning: pf-* token system delivers OrcaSlicer-like industrial aesthetic.

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

## Archived: Mobile Controls Integration (Rounds 17–19, 2025-11-23 to 2025-11-24)

**Rounds 17–19 summarized for size.** Historic context: Designed `PrinterControlsSection` composition (helper function pattern + lazy `.task` injection), fixed Home gate logic (`canHomeAll || canHomeXY || canHomeZ`), clarified ViewModel injection timing, and integrated snapshot testing framework. Final design locked; ready for Hudson integration phase. Details in `history-archive.md`.

---
