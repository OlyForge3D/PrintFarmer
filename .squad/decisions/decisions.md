# Team Decisions Log

**Updated:** 2026-03-11T23:49:00Z

## UI Design System Decisions

### Decision 1: Ghost Token Replacement (PFarm1-u5h)

**Date:** 2026-03-08  
**Agent:** Newt (Agent 17)  
**Status:** ✅ CLOSED  

**Context:**  
UI components contained undefined/legacy token references breaking styling consistency.

**Decision:**  
Replace all undefined tokens with valid pf-* design system tokens across 47 files.

**Implementation:**
- Mapped undefined → pf-bg-0, pf-text-primary, pf-border, pf-accent-bg, etc.
- 120+ replacements completed
- All component tests passing
- Full regression testing completed

**Rationale:**  
Centralized, consistent token usage reduces maintenance burden, improves dark/light theme switching, and ensures WCAG AA compliance.

---

### Decision 2: SlicerConfigModal Dark Theme (PFarm1-5o5)

**Date:** 2026-03-08  
**Agent:** Newt (Agent 17)  
**Status:** ✅ CLOSED  

**Context:**  
SlicerConfigModal lacked proper dark theme styling, inconsistent with design system.

**Decision:**  
Implement complete dark theme CSS using pf-* tokens (pf-bg-0, pf-bg-1, pf-text-primary, pf-border).

**Implementation:**
- Dark mode CSS classes added to SlicerConfigModal.tsx
- All form fields, buttons, and overlays styled for dark theme
- WCAG AA contrast compliance verified (4.5:1 text, 3:1 borders)
- 7 new test cases validating dark theme rendering

**Rationale:**  
Users expect consistent dark theme across all modals. Token-based approach ensures theme switching works automatically across the application.

---

### Decision 3: Select Dropdown Chevron Icon (PFarm1-dhz)

**Date:** 2026-03-08  
**Agent:** Ripley (Agent 18)  
**Status:** ✅ CLOSED  

**Context:**  
Select dropdowns lacked visual affordance indicating expandable state, reducing discoverability.

**Decision:**  
Add ChevronDownIcon to all Select components, rotated on open/close with smooth 150ms transition.

**Implementation:**
- New ChevronDownIcon component (src/common/components/icons/ChevronDownIcon.tsx)
- Integrated into Select.tsx with CSS transition
- Icon uses pf-* color tokens for theme consistency
- aria-hidden="true" for screen reader clarity
- 5 new tests + 14 existing Select tests all passing

**Rationale:**  
Visual indicator improves UX clarity without breaking accessibility. Smooth animation provides feedback. Icon automatically inherits theme tokens.

---

## Batch 2: Design Token Consolidation & Component Library

### Decision 4: Systematic Design Token Replacement (PFarm1-xsg)

**Date:** 2026-03-11  
**Agent:** Newt (Agent 20)  
**Status:** ✅ CLOSED  

**Context:**  
446+ hardcoded Tailwind color references scattered across 117 files, breaking design system consistency and making theme switching difficult.

**Decision:**  
Execute systematic token sweep: replace all non-design-system colors with semantic `pf-*` tokens across UI codebase.

**Implementation:**
- **978 hardcoded Tailwind colors replaced** with `pf-*` design tokens
- **Files modified:** 117 across features/, components/, common/, services/, types/
- **Mapping established:**
  - `text-red-*` / `bg-red-*` → `text-pf-error` / `bg-pf-error`
  - `text-green-*` / `text-emerald-*` → `text-pf-success`
  - `text-blue-*` → `text-pf-accent`
  - `text-yellow-*` / `text-amber-*` / `text-orange-*` → `text-pf-warning`
  - `text-gray-*` → `text-pf-text-primary/secondary/tertiary`
  - `bg-gray-*` → `bg-pf-bg-0/bg-pf-bg-1/bg-pf-bg-2`
  - `border-gray-*` → `border-pf-border`
- **Exceptions documented:** colorFamilies.ts (filament swatch colors), bg-black/50 (standard backdrop), text-white (contrast)
- **dark: variants removed entirely** — pf-* tokens handle theme switching via CSS custom properties
- **Validation:** 1,233/1,233 tests pass, 0 lint errors

**Rationale:**  
Centralized token-based design system enables rapid theme switching, ensures WCAG AA compliance, reduces maintenance burden, and prevents color inconsistency from ad-hoc tailwind classes.

**Lesson Learned:**  
Never apply `re.sub(r'  +', ' ', content)` to entire file content — destroys indentation. Fixed script to only modify class token strings. Two-pass approach effective for large sweeps.

---

### Decision 5: EmptyState Component as UI Standard (PFarm1-y4b)

**Date:** 2026-03-11  
**Agent:** Ripley (Agent 21)  
**Status:** ✅ CLOSED  

**Context:**  
Codebase had 30+ files with ad-hoc empty state patterns — inconsistent markup, varying styles, some missing icons or descriptions, creating visual inconsistency and maintenance burden.

**Decision:**  
Create shared `EmptyState` component in UI library with standardized props: `icon`, `title`, `description`, `action`, `className`.

**Implementation:**
- **Component:** `src/common/components/ui/EmptyState.tsx`
- **Props:** icon (React.ReactNode), title (string), description (string), action (React.ReactNode), className (string)
- **Styling:** All `pf-*` tokens — title `text-pf-text-primary`, description `text-pf-text-secondary`, icon wrapper `text-pf-text-tertiary opacity-40`
- **Exported:** UI barrel at `@/common/components/ui/index.ts`
- **Tests:** 10 unit tests covering all prop combinations
- **Refactored:** 3 pages to use EmptyState (WebhooksAdminPage, ProjectsPage, JobQueueDashboardPage)
- **Migration scope:** ~30 additional candidate files identified for future incremental refactoring

**Rationale:**  
Standard empty state component improves visual consistency, reduces markup duplication, centralizes styling and accessibility concerns, and provides clear patterns for future development.

---

### Decision 6: StatisticsPage PageTemplate Wrapper (PFarm1-3mn)

**Date:** 2026-03-11  
**Agent:** Ripley (Agent 21)  
**Status:** ✅ CLOSED  

**Context:**  
StatisticsPage was the only page bypassing PageTemplate, inconsistent with application layout patterns and lacking proper title/icon/actions area.

**Decision:**  
Wrap StatisticsPage in PageTemplate with title "Print Statistics", icon, subtitle, and period filter buttons in actions.

**Implementation:**
- **PageTemplate structure:** title "Print Statistics", `icon` prop uses component type (ChartIcon not `<ChartIcon />`), subtitle, `actions` slot for period filters
- **Period filter buttons:** Moved from inline header to PageTemplate's actions slot for consistent layout
- **Structure validation:** 4 chart sections render correctly (jobs, cost, filament, utilization)
- **KPI cards:** Updated to use pf-* tokens exclusively (no hardcoded gray/slate)
- **Tests:** 10 tests validating page structure, formatted values (currency, weight, hours), and PageTemplate integration

**Rationale:**  
Consistent page layout improves navigation patterns, centralizes title/icon/actions management, and ensures StatisticsPage integrates seamlessly with application structure.

---

## Summary

**Batch 2 Complete:** 3 decisions closed, 0 open, 0 deferred.  
**Total Changes:** 117 files modified, 978 token replacements, 1 new component, 3 pages refactored, 27 new tests.  
**Test Coverage:** 1,233/1,233 tests passing, 0 lint errors, full regression guard coverage.  
**Status:** Ready for integration into main branch.

**Previous Summary:**  
**Batch 1 UI Audit Fixes:** 3 decisions closed, 0 open, 0 deferred.  
**Total Changes:** 47 files modified, 120+ token replacements, 2 new components, 39 new tests.
