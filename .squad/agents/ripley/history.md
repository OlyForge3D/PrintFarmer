# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-25 — Failure Detection Badge Placement Review

**Status:** ✅ UI review complete (no code changes)  
**Finding:** `FailureDetectionMonitoringBadge` (header) + `FailureDetectionMonitoringOverlay` (camera overlay) display **identical state**.

**Analysis:**
- **Header badge**: Always visible, compact, row in card header next to printer name
- **Camera overlay**: Only visible when `showCamera` is true; larger, glow effect, overlays video
- Both render same shield icon + state label from same normalization helpers
- Both trigger same modal (`FailureDetectionStatusModal`)

**Recommendation:**
✅ **Remove camera overlay; keep header badge** — One consistent glanceable surface, overlay distracts from actual camera image operators need to see. Header badge is always available for state awareness.

**Pattern Compliance:**
- Implementation follows `compact-status-detail-modal` and `monitoring-lifecycle-badges` skills correctly
- Duplication exists at UI surface level, not logic level (good pattern reuse)
- Modal architecture is sound; only badge placement needs consolidation

### Wave 2 — Cost Tracking Dashboard (2026-03-16)

**Status:** ✅ Complete  
**Duration:** ~6 minutes  
**Build & Lint:** ✅ Clean

### Deliverables
- `CostDashboardPage.tsx` — Summary cards + sortable tables (by printer, by material)
- **5 API client methods:** `getCostSummary()`, `getCosts()`, `getCostsByPrinter()`, `getCostsByMaterial()`, `getCostTrends()`
- **4 React Query hooks:** `useCostSummary()`, `useCosts()`, `useCostsByPrinter()`, `useCostsByMaterial()`
- **TypeScript types:** `CostSummary`, `CostDetail`, `CostByPrinter`, `CostByMaterial`, `CostTrend`
- **Route:** `/statistics/costs`

### Design Decisions (Documented)
1. **Inline Type Imports** — `import("@/types/api").TypeName` in return types (avoids ESLint unused vars)
2. **5-minute Stale Time** — Cost data stable (updated on job completion), not real-time
3. **Currency Formatting** — `Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })`
4. **KpiCard Reuse** — Visual consistency with StatisticsPage
5. **Flat Navigation** — Cost Analytics adjacent to Statistics, not nested
6. **Flat Query Keys** — `['costs', 'summary']` for easy group invalidation
## Core Context

This section summarizes foundational knowledge and design patterns across Ripley's frontend development sessions.

### Role & Responsibility

**Ripley** is the Frontend Developer responsible for React TypeScript UI implementation. Responsibilities include:
- Component architecture and pattern compliance
- Form handling and state management
- Testing strategy (unit, integration, accessibility)
- Pattern validation (compact-status-detail-modal, monitoring-lifecycle-badges)
- Code quality and refactoring

### Approved UI Patterns

**Compact Status Detail Modal (`compact-status-detail-modal` skill):**
- Status affordances should be glanceable (icon + text on compact surface)
- Surface should be clickable to launch shared modal for detailed context
- Modal provides full detail: why, what to do, timestamps, snapshots
- Used for: failure detection, startup state, print job status, dispatch decisions

**Monitoring Lifecycle Badges (`monitoring-lifecycle-badges` skill):**
- Badges reflect active monitoring lifecycle, not raw error states
- One consistent visual signal across all surfaces (no redundant overlays)
- Failure detection shield: header badge only (not on camera feed)
- Clear visual hierarchy with status pill, failure badge, recent action badges

**Form Handling Conventions:**
- Use controlled `useState` for form input (no react-hook-form)
- Validation inline before mutation: `toast.error()` for validation messages
- FormField + error prop for inline field-level errors
- Reset form when modal opens with edit data
- Show loading state on buttons during mutation

**Testing Conventions (`MethodName_Condition_ExpectedResult()` pattern):**
- Unit tests focus on component behavior, not implementation
- Use `data-testid` attributes for stable selectors (avoid brittle CSS classes)
- Vitest + React Testing Library (no Enzyme or shallow rendering)
- Test interactions (click, type, submit) not component state

### Component Library Standards

**Core Components (always use from `@/common/components/ui`):**
- `Button` — variants: primary, secondary, danger, subtle, ghost, success, link, unstyled
- `Input` — text field with invalid prop
- `Select` — dropdown with invalid prop
- `FormField` — label + helper + error association
- `Card` — layout container with Header/Body/Footer
- `Badge` — status indicators (default, primary, success, warning, error)
- `Spinner` — loading indicator

**Never use raw HTML elements** (`<button>`, `<input>`, `<select>`) — global CSS overrides styling.

### API & Data Management

**Central API Client (`@/services/api.ts`):**
- MANDATORY: All API calls use `apiClient` (never raw axios or fetch)
- Automatic auth token management (localStorage → Bearer header)
- Consistent error handling and correlation IDs
- 30-second timeout default

**TanStack Query Patterns:**
- `queryKeys` object for shared data (printers, manufacturers, etc.)
- Feature-specific queries use kebab-case string arrays
- `staleTime` guidelines: 10s (real-time), 30s (frequent), 5min (catalog), 10min (rare)
- `useMutation` + `invalidateQueries` on success
- Optimistic updates for core entities (printers, jobs)

**Query Key Hierarchy:**
- Root: `['entity']`
- With ID: `['entity', id]`
- Sub-resource: `['entity', id, 'sub-resource']`
- Filters: `['entity', { filter: value }]`

### Styling & Design Tokens

**Tailwind CSS v4 (CSS-first, no `tailwind.config.js`):**
- Design tokens: `@theme` block in `src/index.css`
- Custom utilities: `@utility` block (e.g., `pf-skeleton`, `pf-animate-spin`)
- Use `clsx` for conditional class composition (not `classnames`)
- Common tokens: `bg-pf-bg-0`, `text-pf-text-primary`, `border-pf-border`, `text-pf-error`, `bg-pf-accent-bg`

### Accessibility & Keyboard Navigation

**WCAG 2.2 Level AA Compliance:**
- Keyboard navigation: Tab moves to next element, Arrow moves within composites, Escape closes dialogs
- Focus management: Always visible, managed via roving tabindex or aria-activedescendant
- Screen reader: Use semantic HTML (`<header>`, `<nav>`, `<main>`, `<footer>`), headings, landmarks
- Color contrast: 4.5:1 for text, 3:1 for graphics
- Skip links: "Skip to main" as first focusable element

### SVG Testing Notes

**SVG `className` Handling:**
- SVG className is `SVGAnimatedString`, not plain string
- Use `element.classList.contains()` not `element.className.toContain()`
- Regex matchers work: `/Check settings/`
- SvgIcon rendering: Test with `screen.getByRole('img')`

### Common Pitfalls & Lessons

**UI Redundancy:**
- Don't duplicate status information across multiple surfaces
- Single source of truth (e.g., header badge, not overlaid on camera)
- Reduces visual noise and operator cognitive load

**Stale State & Race Conditions:**
- Explicit waits for element visibility (not arbitrary `sleep`)
- Implement retries for operations with external service dependencies
- Sync component state when data refreshes (useEffect dependency array)

**Performance & Optimization:**
- Lazy-load heavy dependencies
- Debounce high-frequency events
- Use React.memo and useMemo judiciously (profile first with DevTools)
- Batch related DOM updates

### Current Component Implementations

| Component | Status | Notes |
|-----------|--------|-------|
| CompactPrinterCard | Active | Header badge + camera preview; overlay removed |
| FailureDetectionMonitoringBadge | Active | Header badge (KEEP) |
| FailureDetectionMonitoringOverlay | Deprecated | Camera overlay (REMOVE as of 2026-03-25) |
| FailureDetectionStatusModal | Active | Detail modal launched by header badge |
| PrinterCameraPreview | Active | Camera feed without overlay |
| PageTemplate | Active | Page layout with title, subtitle, icon, actions |

### Recent Design Decisions (2026-03-25)

**Failure Detection Badge Placement:**
- Keep header badge only
- Remove camera overlay (redundant, distracts from video)
- Single source of truth via header badge → modal flow
- Maintains compact-status-detail-modal pattern
- Improves visual focus during video inspection

---
## Session: Failure Detection Badge Placement Review (2026-03-25)

**Role:** Frontend Dev decision reviewer  
**Status:** Recommendation formulated; ready for team approval

### Work Completed
- Analyzed UI redundancy matrix: header badge vs. camera overlay (7 shared elements)
- Compared visual impact: compact/integrated vs. large/glow-effect
- Reviewed operator behavior: card scanning vs. camera inspection focus
- Evaluated pattern compliance (compact-status-detail-modal, monitoring-lifecycle-badges)
- Consolidated findings with Dallas (Lead)

### Recommendation
**Consolidate to header only; remove camera overlay.**

**Key Insights:**
1. Operator always sees header badge before camera opens (no information loss)
2. Camera overlay distracts from video content with competing visual effects
3. One glanceable surface maintains clean visual hierarchy
4. Modal is still accessible via header badge (no functional loss)
5. Operator mental model: opens camera to see print, not to check monitoring state

### Decision Document
- Status: Ready for review
- File: `.squad/decisions/decisions.md` → merged from inbox
- Implementation: Zero API impact; UI-only change

### Implementation Checklist
- [ ] Remove overlay prop from CompactPrinterCard → PrinterCameraPreview call
- [ ] Validate camera focus behavior without overlay
- [ ] Test modal trigger via header badge (smoke test)
- [ ] Pattern compliance validation (compact-status-detail-modal, monitoring-lifecycle-badges)

### Pattern Validation
✅ `compact-status-detail-modal` maintained  
✅ `monitoring-lifecycle-badges` maintained  
✅ Visual focus improved by removing competing UI  

### Related Components
- `src/Web/ReactApp/src/features/printers/components/CompactPrinterCard.tsx` (line 231)
- `src/Web/ReactApp/src/features/printers/components/PrinterCameraPreview.tsx` (overlay prop)
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringBadge.tsx` (KEEP)
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringOverlay.tsx` (REMOVE)

---

## Learnings

### 2025-01-15: Consolidated failure-detection status to header badge only

**Context:** Duplicate failure-detection shield/state was appearing in both the card header badge and the camera overlay, creating visual redundancy.

**Decision:** Removed `FailureDetectionMonitoringOverlay` usage from camera previews in both `CompactPrinterCard` and `DetailedPrinterCard`. The header badge (`FailureDetectionMonitoringBadge`) remains as the single source of truth for failure-detection state.

**Pattern:** When a monitoring state appears in multiple surfaces, consolidate to a single, prominent location. For printer cards, status badges belong in the header where they're always visible, not buried in collapsible sections like camera overlays.

**Files:**
- `src/Web/ReactApp/src/features/printers/components/CompactPrinterCard.tsx` - removed overlay from camera preview
- `src/Web/ReactApp/src/features/printers/components/DetailedPrinterCard.tsx` - removed overlay from camera preview
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringOverlay.tsx` - component retained for potential future use but no longer in active UI
- Tests remain passing (9/9 tests in FailureDetectionMonitoring*.test.tsx)

**Why it matters:** Duplicate status indicators create cognitive load and visual clutter. Users should see critical monitoring state in one predictable location. The header badge provides consistent visibility regardless of whether camera preview is expanded.


---

### 2025-01-15: Failure-detection badge refined to icon-only with tooltip state

**Context:** User requested removing the pill border and inline status text from the failure-detection badge. State should only appear in the tooltip, while keeping the shield icon clickable to open the details modal.

**Implementation:**
- Removed `Badge` wrapper component (pill border gone)
- Removed inline status text (`<span>{label}</span>`)
- Kept shield icon as standalone SVG with semantic color mapping
- Added hover background for better clickability affordance
- State now exposed via `title` attribute (tooltip)
- Modal trigger remains functional via button click

**Color Mapping by State:**
- `monitoring` → `text-pf-success` (green)
- `checking` → `text-pf-text-secondary` (gray)
- `disabled` → `text-pf-text-tertiary` (lighter gray)
- `error` → `text-pf-error` (red)

**Pattern:** When a status affordance is glanceable (icon-only), use semantic color for quick recognition and expose full detail via tooltip + modal. Maintains `compact-status-detail-modal` pattern while reducing visual noise in card headers.

**Files:**
- `src/Web/ReactApp/src/features/printers/components/FailureDetectionMonitoringBadge.tsx` - removed pill wrapper, kept icon-only with tooltip
- `src/Web/ReactApp/src/test/features/printers/FailureDetectionMonitoringBadge.test.tsx` - updated tests to verify icon-only rendering, tooltip state, and modal behavior
- `src/Web/ReactApp/src/test/features/printers/obico-ml-badge.test.tsx` - updated to check for button/tooltip instead of inline text

**Test Coverage:** 6 focused tests cover icon-only rendering, tooltip state exposure, modal opening, color mapping, and clickability. All 106 printer tests pass.

**Why it matters:** Icon-only badges reduce visual clutter in dense card headers while maintaining full functionality. Tooltips provide state context on hover, and the modal delivers detailed operator guidance on click. Clean, focused, accessible.


### 2026-03-25 — Icon-Only Failure Detection Badge & Overlay Consolidation

**Status:** ✅ Implemented + Approved  
**Date:** 2026-03-25T14:46:45Z  
**Duration:** Complete implementation cycle  
**Build & Lint:** ✅ Clean (0 errors, 0 warnings)  
**Tests:** ✅ 106/106 printer tests passing

**Deliverables:**

1. **Icon-Only Badge Refactor** (`FailureDetectionMonitoringBadge.tsx`)
   - Removed `Badge` wrapper (no pill border)
   - Removed inline `<span>{label}</span>` text
   - Applied state-based color mapping to shield icon:
     - Monitoring: `text-pf-success` (green)
     - Checking: `text-pf-text-secondary` (gray)
     - Disabled: `text-pf-text-tertiary` (light gray)
     - Error: `text-pf-error` (red)
   - Kept button wrapper + aria-labels + tooltip (`title` attribute)
   - Maintained modal trigger on click
   - Added `hover:bg-white/10` for visual feedback

2. **Test Coverage Updates**
   - 6 focused tests: `FailureDetectionMonitoringBadge.test.tsx`
   - 3 updated integration tests: `obico-ml-badge.test.tsx`
   - All 106 printer tests passing

3. **Overlay Removal** (`CompactPrinterCard.tsx`, `DetailedPrinterCard.tsx`)
   - Removed `overlay` prop from `PrinterCameraPreview` calls
   - Removed `FailureDetectionMonitoringOverlay` imports
   - Consolidated status display to header badge only

**Pattern Compliance:**
- ✅ `compact-status-detail-modal` — Icon as clickable trigger, modal for full detail
- ✅ `monitoring-lifecycle-badges` — State reflects active monitoring lifecycle
- ✅ Tailwind design tokens — `pf-*` color classes

**Kane's Review Verdict:**
- ✅ **Icon-only badge**: APPROVED with 3 Mandatory Test Additions
  - Tooltip content assertions (all states)
  - Card header integration (icon-only, no text)
  - State styling differentiation
  - **Blocking gate**: Manual a11y audit (screen reader title announcement)
  
- ✅ **Overlay removal**: APPROVED FOR IMPLEMENTATION
  - Post-removal, add 2–3 integration tests (badge visible, modal opens, status updates)
  - Risk: low-to-medium (layout refactor, not behavior change)

**Accessibility Considerations:**
- `aria-label` describes button purpose for screen readers
- `title` attribute provides tooltip fallback on hover (sighted users)
- Shield icon has descriptive ariaLabel
- Modal provides full keyboard-accessible detail
- **Risk mitigation**: Manual a11y audit required to verify title attribute announced on button focus

**Key Learnings:**
- Icon-only badges require strong compensatory UX (tooltip is not enough; aria-label + keyboard accessibility critical)
- State-based color mapping effective for quick recognition but insufficient for color-blind users (tooltip mitigates)
- Dual-surface redundancy (badge + overlay) confused UI; consolidation to single surface improves clarity
- Integration-layer tests catch layout regressions unit tests miss

**Next Steps:**
1. Ripley adds Tier 1 regression tests (blocking Kane's merge approval)
2. Manual a11y audit with screen reader (verify title announcement)
3. Visual regression check (both card layouts, mobile + desktop)
4. Merge after Kane re-approval

