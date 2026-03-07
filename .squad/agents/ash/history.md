# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

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
