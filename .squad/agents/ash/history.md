# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

### Auto-Dispatch System Documentation (2026-01-15)

**Files Created:**
- **docs/AUTO_DISPATCH.md** — Comprehensive documentation of auto-dispatch system with Mermaid diagrams (40KB, 1110 lines)

**Documentation Scope:**
- Architecture overview with component diagram showing all services and data flows
- Three distinct concepts explained: Auto-Dispatch (job routing), Ready Gate (bed-clear safety), Auto-Print (future hardware feature)
- Complete system component documentation: AutoDispatchTrigger, AutoDispatchBackgroundService, DispatchScorer, AutoPrintService, JobQueueService, JobDispatchService
- Mermaid diagrams for: trigger flow (sequence), dispatch cycle (flowchart), ready gate (state machine), component architecture
- 10-factor scoring system with weights: Material Match (100), Nozzle Diameter (100), Nozzle Hardness (80), Enclosure (80), Model Match (60), Build Volume (50), Preferred Printer (40), Queue Depth (30), Printer Group (0), Availability (0)
- Three dispatch modes: Manual, Suggest, Auto with detailed behavior descriptions
- Configuration options: system-level settings (singleton) and per-printer opt-in
- Complete API endpoint reference: 11 endpoints across DispatchSettings, AutoPrint, and Dispatch controllers
- SignalR events: jobautodispatched, dispatchsuggestion, dispatchfailed, autoprintstatechanged
- Frontend UI components: Global toggle, per-printer Zap icon, Bed Clear Banner with three action buttons
- Eight critical design decisions documented with rationale and alternatives rejected

**Key Architecture Insights Captured:**
- **Channel-Based Trigger System**: Uses bounded channel (capacity 64) with DropOldest policy for backpressure management. Two trigger paths: NotifyPrinterIdle (with idle threshold delay) and NotifyJobQueued (immediate, skips delay for upload-and-print).
- **Event-Driven Background Service**: Fire-and-forget Task spawning for concurrent printer processing. SemaphoreSlim(1,1) serializes job assignment to prevent race conditions. MaxConcurrentDispatches limit prevents thundering herd.
- **Weighted Scoring Algorithm**: Hard requirements eliminate printers (Material, Nozzle, Enclosure, Hardness when needed). Soft factors reduce score but don't eliminate. Weighted average: Σ(score × weight) / Σ(weights).
- **Ready Gate State Machine**: None → PendingReady → Ready → (dispatch) → None. Operator must confirm bed is clear between consecutive prints. Filament pre-flight checks (material match, weight sufficiency) before dispatch.
- **Upload-and-Print Immediate Dispatch**: Jobs queued with pre-assigned printer skip idle threshold for instant dispatch. User explicit choice bypasses delay.
- **No Compatible Printer Handling**: File uploaded but NOT queued if no printer scores above MinimumScoreThreshold. Forces manual assignment, prevents orphaned jobs.
- **Audit Trail**: All dispatch decisions logged to DispatchLogs with full score breakdown (JSON serialized). Enables post-mortem analysis and future ML improvements.
- **Thread Safety**: SemaphoreSlim prevents two printers from grabbing same job. Channel provides bounded buffer with DropOldest for backpressure. Interlocked operations track in-flight dispatch count.

**Documentation Quality:**
- 40KB comprehensive guide for both developers (architecture, code paths) and operators (configuration, UI usage)
- Four Mermaid diagrams for visual clarity: component architecture (graph), trigger flow (sequence), dispatch cycle (flowchart), ready gate (state machine)
- Complete API reference with request/response examples for all 11 endpoints
- Real-world scoring example with breakdown showing weighted average calculation
- Eight design decisions documented with rationale, alternatives rejected, and future considerations
- Frontend UI integration: Global toggle implementation, per-printer Zap icon behavior, Bed Clear Banner with action button details

**Root Causes of Documentation Gap:**
- Auto-dispatch system implemented across 12 source files with complex interactions
- Channel-based event architecture not immediately obvious from individual file reading
- Scoring algorithm weights scattered across DispatchScorer.cs without consolidated reference
- Ready Gate workflow (PendingReady/Ready states) separate from main dispatch flow, needed unified explanation
- Frontend UI integration (toggle, Zap icon, banner) spread across three React components
- Design decisions (immediate upload-and-print, no-compatible-printer handling, SemaphoreSlim locking) implicit in code, not documented

**Sources Read:**
- AutoDispatchTrigger.cs (131 lines) — Channel trigger with SkipIdleThreshold flag
- AutoDispatchBackgroundService.cs (329 lines) — Event-driven background service with fire-and-forget Tasks
- DispatchScorer.cs (507 lines) — 10-factor weighted scoring with hard/soft requirements
- DispatchModels.cs (100 lines) — Enums and data structures
- JobDispatchService.cs (112 lines) — Orchestration and audit logging
- DispatchDtos.cs (327 lines) — API DTOs and SignalR event payloads
- JobQueueService.cs (partial) — NotifyJobQueued trigger after queuing
- AutoPrintService.cs (473 lines) — Ready Gate state machine and filament checks
- AutoPrintController.cs (153 lines) — Ready Gate API endpoints
- DispatchController.cs (67 lines) — Dashboard queue status and history
- DispatchSettingsController.cs (99 lines) — System-wide settings CRUD
- PrintQueueDashboardPage.tsx (150 lines partial) — Global auto-dispatch toggle
- CollapsedPrinterCard.tsx (100 lines partial) — Per-printer Zap icon
- BedClearBanner.tsx (132 lines) — Bed clear confirmation UI

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

