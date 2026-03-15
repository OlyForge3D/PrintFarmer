# Newt — History

## Project Context

**PrintFarmer** — React TypeScript dashboard for managing multiple 3D printers. C# .NET 10 API backend, React 19 frontend with Tailwind CSS v4, SignalR real-time updates. Owner: Jeff Papiez.

**Stack:** Tailwind CSS v4 with custom `pf-` design tokens, shared UI component library at `@/common/components/ui`, MDI icons, `clsx` for class composition, `sonner` for toasts.

**Key UI files:**
- `src/Web/ReactApp/src/common/components/ui/` — shared UI components (Button, Input, Select, FormField, Card, Badge, etc.)
- `src/Web/ReactApp/src/styles/` — global styles, design tokens
- `src/Web/ReactApp/src/features/` — feature-organized pages and components
- `src/Web/ReactApp/src/common/components/PageTemplate.tsx` — page layout wrapper

## Learnings

### Design System Architecture (2026-03-10)
- **Theme System**: CSS custom properties (`--pf-*`) defined in `src/Web/ReactApp/src/styles/theme.css` with 3 themes: `github-dark` (default), `printfarmer-dark`, `light`
- **Tailwind Integration**: Custom `pf-` token classes in `tailwind.config.js` map to CSS variables for dynamic theming
- **Component Library Location**: `src/Web/ReactApp/src/common/components/ui/` — Button, Card, Badge, Input, Select, FormField, Tabs, Toggle, ProgressBar, Alert, Spinner, etc.
- **Icon Library**: MDI icons via `@/common/components/icons/MdiIcons`, some lucide-react icons mixed in
- **Page Layout Wrapper**: `PageTemplate.tsx` provides consistent page structure (title, subtitle, icon, actions, children)
- **Modal Pattern**: `src/Web/ReactApp/src/common/components/modals/Modal.tsx` — sizes: sm/md/lg/xl/full, with header/content/footer structure
- **Toast System**: `sonner` library for notifications
- **Class Composition**: Uses `clsx` (not classnames) for conditional class composition
- **Skeletons**: Located in `src/Web/ReactApp/src/common/components/skeletons/` — PrinterCardSkeleton, TableSkeleton, FormSkeleton, etc.

### Design Token Observations
- Good: Comprehensive token system for backgrounds (bg-0/1/2), text (primary/secondary/tertiary/muted), borders, status colors
- Good: Focus ring system with proper accessibility (`--pf-focus-ring`, `--pf-focus-ring-offset`)
- Good: High contrast media query support (`@media (prefers-contrast: high)`)
- Good: Reduced motion support (`@media (prefers-reduced-motion: reduce)`)
- Issue: Some raw Tailwind colors (`text-gray-*`, `bg-gray-*`) still used instead of `pf-` tokens (found in ~30+ files)

### Feature Structure
- Features organized in `src/Web/ReactApp/src/features/{feature}/` with `components/`, `pages/`, `hooks/`, `utils/` subfolders
- Key features: printers, queue, catalog, locations, maintenance, filamentManagement, slicer, admin, auth
- Printers page has 3 view modes: collapsed (cards), detailed, table
- Dashboard at `features/printers/components/PrinterDashboard.tsx` with StatsCards, ActiveJobsWidget, RecentPrintsWidget

### Layout Architecture
- Single-page app with sidebar navigation in `Layout.tsx` (~850 lines, needs refactoring)
- Sidebar supports collapsed state (persisted to localStorage)
- Mobile-responsive with hamburger menu
- Top header bar with connection status, tasks badge, user menu, theme selector

### Audit Findings Summary (2026-03-10)
- **Ghost tokens**: `text-pf-text`, `bg-pf-primary`, `bg-pf-surface`, `bg-pf-hover`, `hover:bg-pf-bg-3` — all undefined, cause rendering bugs
- **446 non-pf colors** in `features/` — systematic sweep needed, batch by feature area
- **110 hardcoded white/black** references in features/
- **SlicerConfigModal** entirely light-theme hardcoded — broken in dark theme
- **Select component** missing dropdown chevron despite `appearance-none`
- **DetailedPrinterCard** is 1,037 lines — needs decomposition into sections
- **Duplicate status color logic** in both Collapsed and Detailed printer cards
- **StatisticsPage** bypasses PageTemplate — only major page that does
- **Nav sidebar** has 17+ items without section group headers
- **Badge** references undefined tokens: `pf-warning-bg`, `pf-success-text`

### Key Files for Future Work
- `src/Web/ReactApp/src/common/components/Layout.tsx` — Main app layout (large file, ~850 lines)
- `src/Web/ReactApp/src/styles/theme.css` — Theme system entry point
- `src/Web/ReactApp/src/styles/themes/*.css` — Individual theme definitions
- `src/Web/ReactApp/src/styles/controls.css` — Global form control styling
- `src/Web/ReactApp/src/tailwind.config.js` — Tailwind configuration with `pf-` tokens
- `src/Web/ReactApp/src/features/statistics/pages/StatisticsPage.tsx` — Ghost tokens, no PageTemplate
- `src/Web/ReactApp/src/features/slicer/components/SlicerConfigModal.tsx` — Light-only hardcoded
- `src/Web/ReactApp/src/features/printers/components/DetailedPrinterCard.tsx` — 1,037-line God component
- `src/Web/ReactApp/src/common/components/ui/Select.tsx` — Missing chevron icon

### Ghost Token Sweep & SlicerConfigModal Fix (2026-03-10)
- **Ghost tokens eliminated**: All 5 ghost token families purged across 47 files:
  - `text-pf-text` (no suffix) → `text-pf-text-primary` — ~70 instances across 40+ files (slicer, statistics, fileBrowser, gcode, catalog, auth, maintenance, models3d, printers)
  - `bg-pf-primary` → `bg-pf-accent-bg` — 11 instances (ExplorerView, WorkerSelector, StatisticsPage, Slider, TreeView, GridView, MaintenancePage)
  - `bg-pf-surface` (no suffix) → `bg-pf-bg-1` — 20+ instances (slicer settings editors, gcode harvest, Slider, ColorPicker)
  - `bg-pf-hover` → `bg-pf-bg-2` — 7 instances (ImportOfficialProfilesPage, HarvestWizard steps, AuditTimeline, IssuesList, StatisticsPage)
  - `hover:bg-pf-bg-3` → `hover:bg-pf-bg-2` — 13 instances (SetupWizard, ModelViewer3D, SlicerLeftTools, HistoryFiltersBar, ContextMenu, ExplorerView, FileUpload, TaskCatalogTab)
- **SlicerConfigModal** fully migrated from hardcoded light-theme colors to `pf-*` design tokens:
  - All `bg-gray-*`, `text-gray-*`, `border-gray-*` replaced with semantic tokens
  - `bg-blue-*` progress bar replaced with `bg-pf-accent`
  - `bg-green-500`/`bg-red-500` status dots replaced with `bg-pf-success`/`bg-pf-error`
  - Raw `<select>` → `Select`, `<input type="number/range">` → `Input`, `<input type="radio">` → `Radio`, `<input type="checkbox">` → `Checkbox`
  - Removed `eslint-disable local/pf-no-raw-html-controls` pragma
- **Lint**: 0 errors, 0 warnings after changes
- **Tests**: 1,311/1,311 passing (all 118 test files green)
- **Lesson**: `sed` with `find -name '*.tsx' -o -name '*.ts'` is safe for bulk token renames — 47 files fixed in seconds vs. manual editing
## Learnings

### Systematic Token Sweep (2026-03-11)
- **978 token replacements** across **117 files** — every hardcoded Tailwind color class in `features/`, `components/`, `common/`, `services/`, `types/` replaced with semantic `pf-*` design tokens
- **Mapping rules established:**
  - `text-red-*` → `text-pf-error`, `bg-red-*` (tinted) → `bg-pf-error/10`, `bg-red-*` (solid) → `bg-pf-error`
  - `text-green-*` / `text-emerald-*` → `text-pf-success`, `bg-green-*` → `bg-pf-success` / `bg-pf-success/10`
  - `text-blue-*` → `text-pf-accent`, `bg-blue-*` → `bg-pf-accent-bg` / `bg-pf-accent-bg/15`
  - `text-yellow-*` / `text-amber-*` / `text-orange-*` → `text-pf-warning`, `bg-*` → `bg-pf-warning/10`
  - `text-gray-300-400` → `text-pf-text-secondary/tertiary`, `text-gray-700-900` → `text-pf-text-primary`
  - `bg-gray-100-200` → `bg-pf-bg-1/2`, `bg-gray-800-900` → `bg-pf-bg-0/1`
  - `border-gray-*` → `border-pf-border`, `border-red-*` → `border-pf-error(/30)`
  - `bg-slate-400` → `bg-pf-disabled`
  - Purple/indigo/teal/cyan → nearest semantic token (`pf-accent` or `pf-success`)
- **dark: variants removed entirely** — pf-* tokens handle theme switching via CSS custom properties, making `dark:text-gray-400`, `dark:bg-red-900/20` etc. redundant

### Maintenance Module Design Review (2026-03-14)
- **Module architecture**: 5 top-level tabs (Dashboard, Schedule, Library, Analytics, Inventory). Library has nested sub-tabs for Task Catalog and Maintenance Plans.
- **Key finding — Plan scoping gap**: `MaintenancePlan` domain model has `PrinterId`, `PrinterModelId`, `ManufacturerId`, `MotionType` scoping fields, all supported by the API and DTOs, but the `PlanFormModal` only exposes Name/Description/Active. Zero UI for plan scoping.
- **Key finding — Deploy flow missing**: The `useScheduleDeployments` hook, `useDeployPlan` mutation, and `maintenancePlanService.deployPlan()` all exist and work, but no component renders deployment management. The Plan→Printer binding is completely invisible.
- **Key finding — Library tab default**: Defaults to "tasks" sub-tab but "Maintenance Plans" tab header appears first visually — confusing mismatch.
- **Parts inventory edit friction**: Current flow is find→click edit icon→full XL modal→change field→save→close. Five interactions for a stock count change. Proposed: inline +/- stepper on cards + full table view with editable cells.
- **Clone pattern**: Clone is a pure client-side pre-fill of existing create modals for Parts and Tasks. Plans need PlanTask join entity deep copy (can be done client-side with N API calls or via a server-side clone endpoint).
- **PrinterGroup integration**: `PrinterGroup` types exist in api.ts (lines 2994-3042). Future plan scoping should include printer group as a scope option.
- **Data model reference files**: Types in `features/maintenance/hooks/` (13 hooks), `types/maintenance.ts`, `services/maintenancePlanService.ts`. Backend domain at `infra/Domain/MaintenancePlan.cs` and `infra/Domain/PrinterMaintenanceSchedule.cs`.
- **Intentionally excluded:**
  - `colorFamilies.ts` — literal filament swatch colors (12 references) that represent actual material colors, not UI chrome
  - `bg-black/50` overlays — standard backdrop dimming pattern, not a design token concern
  - `text-white` — kept for contrast on accent/solid-color buttons
- **Lesson: NEVER apply `re.sub(r'  +', ' ', content)` to entire file content** — it destroys indentation. First pass had this bug, corrupted 472 files. Caught and reverted immediately. Fixed script to only modify class token strings, not whitespace.
- **Lesson: Two-pass approach works well for large sweeps** — Pass 1 handles common patterns (628 matches), Pass 2 handles edge cases (75 matches with uncommon shades like `bg-red-950/30`, `text-emerald-300/80`, `from-purple-500 to-pink-500`)
- **Validation:** 1,233/1,233 tests pass, 0 lint errors, bead PFarm1-xsg closed

### Batch 3 — Navigation Headers & Loading State Consistency (2026-03-08)
- **Beads:** PFarm1-egw (Nav sidebar headers), PFarm1-42p (Loading state consistency)
- **Deliverables:**
  - Layout.tsx: Added section headers ("Dashboard", "Printers", "Management", "Admin") with semantic grouping
  - ChartSkeleton.tsx: New component for unified chart loading animations
  - Loader/Skeleton standardization: ~25 files refactored from `animate-pulse` to pf-skeleton + pf-animate-skeleton design tokens
- **Files changed:** 30
- **Validation:** All tests pass, design tokens applied consistently, both beads closed
- **Branch:** `feature/nav-headers-and-loading-states`

### Help System UX Design (2026-03-12)
- **Deliverable:** Comprehensive UX spec for operator help system (`.squad/decisions/inbox/newt-help-ux-design.md`)
- **Hybrid approach:** Guided tours + contextual help + help panel (slide-over)
- **Design decisions:**
  - Tours: Spotlight + popover pattern, 85% overlay opacity, `pf-accent` highlight glow, dot step indicators
  - Help panel: Slide-over from right (not sidebar nav), 320-384px width, context-aware "This Page" section
  - Contextual help: Tooltip for 1-2 sentences, popover for 3+, "Learn more" links to full docs
  - First-run: Non-blocking banner prompt, per-page localStorage tracking, "Reset tours" in Settings
- **Component architecture:** TourProvider, TourPopover, TourSpotlight, HelpPanel, HelpPopover, FirstRunBanner
- **Key styling:** All components use existing `pf-*` tokens (bg-pf-bg-1, text-pf-text-secondary, border-pf-border, etc.)
- **Accessibility:** Full keyboard nav (Tab/Enter/Escape/←→), ARIA dialog/modal, `prefers-reduced-motion` support
- **Industrial constraints addressed:** Dark-first, glove-friendly targets, fast/scannable content, non-intrusive prompts

### Tour Highlight Clipping Fix (2026-07-22)
- **Bug:** Guided tour spotlight blue glow was clipped/cut off on some steps where the highlighted element sat inside a parent with `overflow: hidden/auto/scroll`
- **Root cause:** `box-shadow` renders inside the element's overflow boundary — any ancestor with `overflow: hidden` clips it
- **Fix:** Replaced `box-shadow` with `outline` + `outline-offset` on `.driver-active-element` in `tour-theme.css`. CSS `outline` is rendered outside the box model and is never clipped by overflow. Added `filter: drop-shadow()` for the subtle outer blue radiance (also not overflow-clipped).
- **Before:** `box-shadow: 0 0 0 4px var(--pf-accent), 0 0 20px rgba(88, 166, 255, 0.3)`
- **After:** `outline: 3px solid var(--pf-accent)` + `outline-offset: 3px` + `filter: drop-shadow(0 0 10px rgba(88, 166, 255, 0.35))`
- **Lesson:** When you need visible effects that must survive `overflow: hidden` ancestors, use `outline` (not `box-shadow`) and `filter: drop-shadow()` (not `box-shadow` glow). Both are painted outside the overflow clip boundary.
