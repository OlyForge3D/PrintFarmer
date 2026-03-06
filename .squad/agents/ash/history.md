# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

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
