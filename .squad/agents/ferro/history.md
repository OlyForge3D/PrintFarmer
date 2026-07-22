# Ferro — History

## Context
- **Project:** PrintFarmer — 3D printer farm management (C# .NET backend + React TypeScript frontend)
- **Owner:** Jeff Papiez
- **Stack:** React 19, TypeScript, Tailwind CSS v4, Vitest
- **Role:** Second designer providing alternative PoCs for design competitions

## Learnings
(none yet)

## Learnings

### Settings & Analytics Reorg PoC (2026-06-01)

- **PoC delivered:** src/Web/ReactApp/src/features/settings/pages/SettingsReorgProposal-Ferro.tsx — a static, self-contained mockup (4 switchable panels) competing with Newt's approach.
- **My differentiation vs. a "more tabs" approach:**
  - Settings organized by **user intent** into 4 zones (Workspace, Connectivity, Governance, Platform) instead of 7 feature-typed categories.
  - Analytics collapsed into one **Insights Hub** dashboard with an Overview + drill-down lenses (Production/Cost/Fleet), not 3 sibling pages. Each KPI is tagged with its legacy source page to prove coverage.
  - System status reframed as an **ambient "System Pulse"** top-bar pill (expands to a popover), with a permanent deep-dive home under Settings -> Platform -> Health.
  - **Printer Groups** reframed as an *Organization* concept beside Catalog (operational structure), NOT buried in Settings.
  - NFC Bindings + API Keys absorbed into Settings -> Connectivity; Workers + System into Settings -> Platform.
  - Honored the explicit directive: **Filament Inventory stays top-level**, never moved into Settings.
- **Codebase conventions confirmed/used:**
  - UI barrel @/common/components/ui exposes Card (with Card.Body/Header/Footer) and Badge (variants: default/primary/success/warning/error/info; sizes sm/md).
  - Icons live in @/common/components/icons/MdiIcons and take className + riaLabel props (not standard SVG props).
  - Tab/nav rails legitimately use raw <button> with managed focus; the lint rule local/pf-no-raw-html-controls is suppressed file-level in SettingsSidebar.tsx. I followed the same convention with a justified disable comment.
  - Design tokens: g-pf-bg-0/1, g-pf-panel, 	ext-pf-text-primary/secondary, order-pf-border, g-pf-accent-bg, 	ext-pf-success/warning/error.
  - Validation: 
px tsc --noEmit (clean) + 
px eslint <file> (0/0) from src/Web/ReactApp.
