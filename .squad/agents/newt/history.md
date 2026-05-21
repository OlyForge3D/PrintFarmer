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
