# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

### Design System Documentation (2026-03-21)

**Files Created:**
- **docs/DESIGN_SYSTEM.md** — Comprehensive reference for PrintFarmer UI component library, design tokens (pf-* CSS variables), theme system, and usage patterns (7,500+ words)

**Files Updated:**
- **README.md** — Added reference to Design System docs in "Implementation details" section

**Design System Scope:**
- 40+ React components with complete prop APIs and usage examples (Button, Input, Select, FormField, Card, Badge, Spinner, Modal, Tabs, DataTable, Alert, Toggle, FileUpload, Checkbox, Radio, ProgressBar, Tooltip, Textarea)
- CSS custom properties for dynamic theming (GitHub Dark, PrintFarmer Dark, Light)
- Design tokens: colors (primary, text, status, accents, errors, borders, gradients), spacing, typography, state indicators
- Accessibility features (WCAG 2.2 AA compliance) with keyboard navigation and screen reader support
- Common patterns for forms, data tables, modals, loading states, conditional rendering
- Troubleshooting guide and best practices for UI development

**Design Token System (Three Layers):**
1. **CSS Custom Properties** — `--pf-bg-0`, `--pf-text-primary`, `--pf-accent` (theme-aware variables in theme.css)
2. **Tailwind Utilities** — `bg-pf-bg-0`, `text-pf-text-primary` (defined via tailwind.config.js CSS color mappings)
3. **React Components** — Button, Input, Card, Modal (composed from Tailwind + tokens, zero hardcoded colors)

**Theme Architecture:**
- GitHub Dark (default) — GitHub's official colors, 13.6:1 contrast on primary text
- PrintFarmer Dark — Custom dark theme with 4.5:1 minimum contrast on all text
- Light — High-contrast light theme for daytime use, daylight accessibility
- Dynamic theme switching via `document.documentElement.setAttribute('data-theme', 'light')`
- All 40+ CSS variables per theme recalculate on switch (zero rebuild overhead)

**Accessibility Integration:**
- WCAG 2.2 Level AA compliance: 4.5:1 contrast for normal text, 3:1 for large text (18.5px+)
- Semantic HTML (`<button>`, `<input>`, `<label>`, `<fieldset>`) with ARIA attributes
- Keyboard navigation: Tab, Arrow keys (in composites), Enter/Space, Escape
- Focus indicators visible on all interactive elements (customizable ring color)
- Respects `prefers-reduced-motion` and `prefers-contrast` media queries
- All form inputs have associated `<label>` elements and error messages linked via `aria-describedby`

**Root Causes Addressed:**
- No centralized design system documentation — component reference scattered across multiple markdown files
- Component library grown to 40+ components without unified prop documentation
- New contributors didn't understand three-layer architecture (custom properties → Tailwind → React)
- Design token system (pf-* naming) was underdocumented and inconsistently applied
- Theme switching architecture not explained (how CSS variables enable dynamic themes)
- Accessibility features and WCAG compliance not documented for component users
- Common UI patterns (forms, modals, data tables) lacked complete code examples

**Documentation Quality:**
- 7,500+ words with 20+ complete code examples
- All 40+ components documented with props interfaces, usage patterns, and variants
- Design token reference table with default values, usage, and contrast ratios
- Three theme variants documented with color specifications
- Accessibility section with keyboard shortcuts, screen reader support, WCAG compliance info
- Troubleshooting section for common styling/theming issues
- Best practices and anti-patterns (Do's and Don'ts)

### Sprint 1+2 API Documentation (2026-03-21)

**Files Updated:**
- **docs/API.md** — Added comprehensive endpoints for locations hierarchy (tree, ancestors, descendants, move, crud) and auto-dispatch system (candidates scoring, dispatch-to, settings)
- **docs/ARCHITECTURE.md** — Added Location Hierarchy Architecture section (adjacency list + cached path design) and Auto-Dispatch Architecture section (9-factor scoring, background service, dispatch modes)
- **README.md** — Updated Key Features section to highlight hierarchical locations and auto-dispatch scoring engine

**Key Documentation Insights:**
- Location hierarchy uses adjacency list + materialized path for efficiency (Path LIKE queries replace recursion)
- Auto-dispatch scoring has 9 factors: 4 hard filters (material, nozzle, availability, build volume) + 5 soft factors (enclosure, hardness, model, queue, preferred)
- Dispatch settings are singleton entity (system-wide configuration) with two modes: "Suggest" (operator confirms) and "Auto" (full automation)
- All dispatch decisions logged for audit trail and future ML improvements
- SignalR events extended with job auto-dispatch notifications

**Root Causes of Documentation Gap:**
- Controllers implemented before API documentation was created
- Architecture decisions (from .squad/decisions.md) needed to be synthesized into ARCHITECTURE.md
- Team decisions had detailed problem statements and solutions but weren't integrated into main docs

**Best Practices Established:**
- Read source code (controllers) to document actual implementations, not theoretical APIs
- Cross-reference team decisions when adding major features
- Update ARCHITECTURE.md with design rationale (not just "this is what we do")
- Include validation rules and error codes with each endpoint

### Documentation Review & Updates (2026-03-06)

**Files Updated:**
- **README.md** — Updated ASP.NET Core version (9→10), test counts (496→1572 API, 150→365 React), added FlashForge to backend plugins
- **CONTRIBUTING.md** — Fixed repository layout (removed Blazor client/, added backends/, updated to Web/ReactApp/)
- **ARCHITECTURE.md** — Updated .NET version, documented all 6 backend plugins
- **DEVELOPMENT.md** — Added backend plugin structure to code organization diagram
- **GETTING_STARTED.md** — Updated test counts to match current suite (1572/365)

**Root Causes of Outdated Info:**
- ASP.NET Core version not updated when upgrading from 9→10
- Test count updates not reflected in marketing docs when test suite expanded
- Repository layout changed from Blazor client to React, but CONTRIBUTING.md not updated
- Backend plugin count grew from 5→6 with FlashForge, not mentioned in README

**Key Facts Now Documented:**
- 6 backend plugins: Moonraker, PrusaLink, OctoPrint, SDCP, FlashForge, Core
- Full test coverage: 1572 API tests (xUnit), 365 React tests (Vitest)
- Uses .NET 10.0.* with rollForward strategy
- Frontend: React 19, Vite, TanStack React Query, Tailwind CSS v4
- 51 comprehensive markdown docs exist in docs/ directory

### Orchestration & Decision Integration (2026-03-08)

**Status:** ✅ Orchestration logs created, decisions merged into squad decisions.md

**Work Completed:**
- Created `.squad/orchestration-log/2026-03-08T02-03-11Z-ash.md` documenting design system documentation task completion
- Merged Ash's design system decision from inbox into main decisions.md (Decision #18)
- Updated squad decision governance with design system as Decision #18

**Impact on Team:**
- Design system documentation now discoverable in squad decisions for future reference
- Architecture decisions (three-layer design system, WCAG compliance, dynamic theming) synthesized and centralized
- Team standard established: design system documentation is architectural decision, not just content update

