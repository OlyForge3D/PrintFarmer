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
