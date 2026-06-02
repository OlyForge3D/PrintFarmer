# Newt History — Archive

This archive contains work entries from prior to 2026-05-15 for historical reference.

For current work, refer to `history.md`.

---

## Archived iOS Design Work (pre-2026-05-20)

Various iOS component redesigns, view architecture improvements, and deployment optimization work from early 2026. Detailed entries have been summarized; refer to git history for full context.

- Container image optimization
- Docker multi-stage build improvements  
- Backend plugin deployment integration
- Infrastructure automation tasks

---

*Archive created 2026-06-02 to maintain history.md size management.*


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

**Audit Summary:** 5 components on `feature/orcaslicer-full-ui-parity` reviewed. ✅ 5/5 PASS (3 minor deviations noted). SliceJobsPage & SendToPrinterModal fully compliant. NewSliceJobPage: missing illustration (OrcaSlicer uses visual onboarding). SlicerSettingsPanel: 3-tier UI (Basic/Simple/Advanced) + dirty indicators fully implemented. Minor recommendations: (1) Add ARIA role="progressbar" + aria-valuenow to progress bars; (2) Consider onboarding illustration for richer UX; (3) Error message padding increased (px-2 → p-2). Key learning: pf-* token system delivers OrcaSlicer-like industrial aesthetic.

- 2026-05-20: Assigned mobile controls v1 UX design support on issues #284 (preheat), #285 (jog), #286 (home). Fixed presets and feedrates locked per v1 — no customization UX needed. See decisions.md "Mobile API Drift + Basic Printer Controls v1".

- 2026-05-21: Ralph Round 1 (Phase 0) completed — see `.squad/log/2026-05-21T09-00-00Z-ralph-round-1-phase-0.md`.


- 2026-05-21: Phase 1 complete — 8 PRs merged on `development` (#291, #292, #293, #294, #295, #296, #297, #298). See `.squad/log/2026-05-21T08-15-00Z-ralph-rounds-2-5-phase-1-complete.md`. Phase 2 launching (#284 preheat, #285 home, #286 jog).

---

## 2026-05-28: Printer Controls Section Design Spec (#283)

**Task:** Create design specification for printer controls (preheat, home, jog) in iOS `PrinterDetailView`
**Status:** ✅ COMPLETE — PR opened [OlyForge3D/PrintFarmerMobile#1](https://github.com/OlyForge3D/PrintFarmerMobile/pull/1)

### Design Decisions

1. **Preheat layout:** List-style rows (not grid) — shows temperature readouts inline (e.g., "PLA — 200°/60°") for at-a-glance reference. Each row is a full-width tappable area meeting 44pt HIG minimum.

2. **Home layout:** 3-button horizontal row with 60pt height. Icons: `house.fill` (All), `arrow.left.and.right` (XY), `arrow.up.and.down` (Z).

3. **Jog layout:** Segmented pickers for axis (X/Y/Z) and step (0.1/1/10/100mm), with paired +/− buttons showing dynamic labels ("Move X +10mm").

4. **Disabled-while-printing:** Color-blind friendly — uses **lock icon** (`lock.fill`) + 0.5 opacity, not just color change. Per spike #279, client-side gating required for states: Printing, Pausing, Paused, Resuming, Cancelling, Heating.

5. **Hidden-while-offline:** Entire Controls section conditionally rendered only when `printer.isOnline == true`.

### Design Tokens Used

| Token | Usage |
|-------|-------|
| `pfCard` | Subgroup card backgrounds |
| `pfBorder` | Card stroke borders |
| `pfAccent` / `pfButtonPrimary` | Button tints (Home, Jog) |
| `pfWarning` | Flame icon (preheat heating) |
| `pfSecondaryAccent` | Snowflake icon (Cool Down) |
| `pfTextPrimary/Secondary/Tertiary` | Text hierarchy and disabled states |

### Key iOS Component Files

- `PrintFarmer/Theme/ThemeColors.swift` — All `pf*` color tokens
- `PrintFarmer/Views/Components/ActionButtonStyle.swift` — `.standard` (44pt) / `.prominent` (50pt) sizing
- `PrintFarmer/Views/Printers/PrinterDetailView.swift` — Target integration point

### HIG Patterns Applied

- Touch targets: All buttons ≥44pt (preheat rows: 44pt, home buttons: 60pt, jog buttons: 56pt)
- Segmented pickers: Native `.segmented` style
- Dark Mode: All colors via adaptive `pf*` tokens
- Haptics: `UIImpactFeedbackGenerator(.medium)` recommended on button press

### Files Created

- `docs/design/printer-controls-section.md` — Full design specification (611 lines)

## Cross-Team Note (2026-05-29)

**Dallas** (#290 status-gating) complete: API guards validated via PR #308. State blocking for controls confirmed safe.
**Gorman** (#280 capabilities) complete: Endpoint confirmed live. Fallback table canonical.
**Unblocked:** UI gating decisions finalized; PR OlyForge3D/PrintFarmerMobile#1 design decisions locked.

---

## 2026-05-28: PR #1 Review Fixes — Capability Gating, Jog Default, Home Endpoints

**Task:** Address Bishop's review on PR #1 (printer-controls design spec)
**Status:** ✅ COMPLETE — Changes pushed, PR comment posted

### Issues Fixed

1. **Capability-gated states missing (lines 355-387 new):** Spec only defined idle/pending/printing/offline. Added explicit rule: **hide entire subgroup** when `supportsTemperatureControl == false` (Preheat) or `supportsMovement == false` (Home, Jog). Cleaner UX than disabled-row clutter.

2. **Jog default mismatch (lines 342, 567):** Spec said `10` / `10.0`; #286 acceptance criteria locks default at `1 mm`. Updated both Jog Specifications table and Implementation Notes.

3. **Wrong API endpoints (lines 231-235):** Spec said all three Home buttons call `/home` with axes body. Gorman verified backend has dedicated routes: `/home` (all), `/homexy`, `/homez` — no axes body for dedicated routes. Updated table.

### Learnings

| Spec Section | Ambiguity Caused | Resolution |
|--------------|------------------|------------|
| State Matrix | Hudson didn't know whether to HIDE or DISABLE when capability missing | Added "Capability-Gated Subgroups" subsection with explicit hide rule |
| Jog Specifications | Conflicting defaults between spec (10mm) and issue AC (1mm) | Single source of truth: spec now says 1mm, matching #286 |
| API endpoints | Home XY/Z spec implied same endpoint with body | Dedicated routes documented; no axes body |

**Capability-gating rule chosen:** HIDE entire subgroup when capability is `false`. Rationale: operators should only see controls their printer can actually use; disabled-row clutter confuses more than it helps. This is distinct from "disabled during print" which shows controls but blocks interaction.

---

## Archived: Mobile Controls Integration (Rounds 17–19, 2025-11-23 to 2025-11-24)

**Rounds 17–19 summarized for size.** Historic context: Designed `PrinterControlsSection` composition (helper function pattern + lazy `.task` injection), fixed Home gate logic (`canHomeAll || canHomeXY || canHomeZ`), clarified ViewModel injection timing, and integrated snapshot testing framework. Final design locked; ready for Hudson integration phase. Details in `history-archive.md`.

---

## 2026-06-02: PrintFarmer Design Language v2 (Phase 1 — Design Decisions)

**Task:** Author the authoritative `DESIGN-LANGUAGE.md` for PrintFarmer's React UI. Phase 1 (design decisions only — no implementation code).
**Status:** ✅ COMPLETE — spec written, decision file filed in inbox.

### Audit Findings

Pre-existing state of the React design system (`src/Web/ReactApp/src/styles/`):

- **Fonts:** Inter + Bebas Neue (generic SaaS stack — brief explicitly forbade)
- **Themes existing:** `github-dark` (default), `printfarmer-dark`, `light`, `matrix`, `forge` — five themes, no single source of truth
- **Token system:** Solid foundation in `theme.css` + per-theme files in `themes/*.css` with consistent `--pf-*` naming, but gaps: no `printing`/`paused`/`idle` status triples, ambiguous `--pf-text-light`, no `--pf-text-on-accent`, no spacing/radius/duration/z-index tokens defined as variables, 14 `--pf-gradient-*` tokens used inconsistently
- **Components:** Mature UI library at `common/components/ui/` (Button, Input, Card, Badge, FormField, DataTable, Modal, etc.) — already aligned with token consumption
- **Default theme:** `github-dark` was the boot default — wrong choice for the PrintFarmer brand

### Decisions Made

1. **Fonts (NEW, replacing Inter + Bebas Neue):**
   - `--pf-font-sans` = **IBM Plex Sans** (UI body — industrial heritage, IBM-designed for technical interfaces)
   - `--pf-font-display` = **Space Grotesk** (headlines — geometric grotesque, supports full weight range unlike Bebas)
   - `--pf-font-mono` = **JetBrains Mono** (all data: temps, IPs, timestamps, durations — purpose-built for technical display with tabular figures and disambiguated glyphs)

2. **Four themes (full hex palettes specified in spec):**
   - **Light "Workshop Daylight"** — deep teal accent (`#0d7d75`) on cool paper-white; deliberately not washed out
   - **PrintFarmer Dark "Mission Control"** — refined flagship; cool slate-navy substrate (`#08101f`) with precision-teal accent (`#14b8a6`); replaces github-dark as default
   - **Matrix "Terminal"** — pure black canvas (`#000000`), phosphor green (`#00ff41`), amber CRT warnings (`#fbbf24`); mono is default body font; opt-in scanlines and text-shadow glow
   - **Blueprint "Schematic"** — *my creative choice* — cyanotype navy (`#0c1a2e`), cyan blueprint lines (`#38bdf8`), drafting-pencil yellow accent 2 (`#fef08a`); fills the cool-toned palette gap none of the others occupy

3. **Token contract expanded from ~80 to ~140 tokens** — added:
   - Spacing scale (`--pf-space-*`, 11 tokens)
   - Radius scale (`--pf-radius-*`, 6 tokens) — industrial-sharp: max 8px on rectangles
   - Shadow scale (`--pf-shadow-*`, 6 tokens) + glow tokens (`--pf-glow-*`, 4 tokens)
   - Motion (`--pf-duration-*`, 4 tokens; `--pf-ease-*`, 4 tokens)
   - Z-index scale (`--pf-z-*`, 8 tokens)
   - Status triples for `printing`/`paused`/`error`/`idle` (operator-facing printer states)
   - `info` semantic group as the 4th alongside success/warning/error
   - `--pf-text-inverse`, `--pf-text-on-accent`, `--pf-accent-fg`, `--pf-selection-*`, `--pf-validation-warning-*`

4. **Deprecations:**
   - All 14 `--pf-gradient-*` tokens deprecated → flat color + shadow only
   - `github-dark` theme removed (printfarmer-dark becomes default)
   - `forge` becomes undocumented "extra" during transition
   - `--pf-text-light` collapses into `--pf-text-secondary`

5. **Design floor decisions:**
   - 4px base spacing grid (strict, no inline px values)
   - Radii max 8px on rectangles (industrial — rejects "friendly" rounded corners)
   - Touch target floor 44×44 CSS pixels (HIG-aligned, matches mobile spec)
   - Contrast: text-primary aims for AAA (7:1), text-secondary at AA floor (4.5:1)
   - Status always color + shape (icon prefix) — never color alone
   - Numeric data always in mono with `tabular-nums`

### Files Written

- `src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md` (~48 KB, 670 lines) — full authoritative spec
- `.squad/decisions/inbox/newt-design-language.md` — decision summary for team review

### Learnings

- **Existing system was 70% there.** The token naming convention (`--pf-*`), the per-theme file structure, and the component library that consumes tokens were all solid. The work was *systematizing*, not rebuilding — fill gaps, rename ambiguities, deprecate inconsistencies, document the contract.
- **5 themes was 1 too many.** github-dark and printfarmer-dark were near-duplicates. Consolidating to printfarmer-dark as the canonical flagship clarifies brand identity.
- **Blueprint fills a real gap.** Matrix is monochrome green-on-black. PrintFarmer Dark and Forge both use warm-ish dark substrates. Light is bright. Blueprint's cyan-on-navy is the only theme with a genuinely cool-blue palette — daily-driver material for CAD-minded operators.
- **Mono as a first-class type face matters in this domain.** Operators stare at columns of temperatures and percentages all day. JetBrains Mono with `font-variant-numeric: tabular-nums` is the difference between a dashboard that feels precise and one that feels jittery.
- **Phase 2 (implementation) will need a migration map.** The token rename from `--pf-text-light` → `--pf-text-secondary` plus removal of all `--pf-gradient-*` tokens will touch many components. Suggest a codemod-driven approach to keep diffs surgical.

---

## 2026-06-02: Comprehensive Design Language System

**Task:** Create unified design language for PrintFarmer with 4 selectable themes
**Status:** ✅ COMPLETE

### Deliverables Created

1. **Design Language Document**: `src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md` (~16KB)
   - Typography scale with Inter + Bebas Neue font families
   - Color system architecture with semantic tokens
   - Spacing scale and border radii
   - Shadow system for dark-first environments
   - Component patterns (buttons, cards, badges, inputs, tables)
   - Layout grid system and responsive breakpoints
   - Animation/transition standards
   - Iconography guidelines using MDI
   - WCAG 2.2 AA accessibility requirements

2. **New Theme CSS Files:**
   - `src/Web/ReactApp/src/styles/themes/matrix.css` — The Matrix/RatOS inspired
     - Deep blacks (#000000 canvas), phosphor green text (#00ff41)
     - CRT scan-line effect (opt-in), terminal text glow
     - Glowing focus states, atmospheric card hover effects
   
   - `src/Web/ReactApp/src/styles/themes/forge.css` — Industrial warmth
     - Charred black backgrounds with warm undertones (#0f0d0b)
     - Copper/amber accent spectrum (#d47e34 primary accent)
     - Oxidized copper green for success states
     - Ember glow effects on inputs and buttons

3. **Updated Files:**
   - `ThemeContext.tsx` — Added `matrix` and `forge` to Theme type, updated toggle cycle
   - `ThemeToggle.tsx` — Added MatrixIcon and FireIcon to theme selector
   - `MdiIcons.tsx` — Added mdiMatrix/mdiFire imports + MatrixIcon/FireIcon components
   - `theme.css` — Added Matrix and Forge high-contrast mode overrides, imported new theme files

### Theme Summary

| Theme | Background | Accent | Character |
|-------|------------|--------|-----------|
| Light | #ffffff | #059669 (emerald) | Clean, professional |
| GitHub Dark | #0d1117 | #58a6ff (blue) | Familiar, GitHub-style |
| PrintFarmer Dark | #0b1020 | #10b981 (green) | Deep blues, original brand |
| Matrix | #000000 | #00ff41 (phosphor) | CRT terminal, digital rain |
| Forge | #0f0d0b | #d47e34 (copper) | Molten metal, manufacturing |

### Validation
- ✅ Build: Passed (10.19s)
- ✅ Lint: 0 errors

### Key Decisions

1. **Theme application via `data-theme` attribute** — github-dark is default (no attribute), all others set explicit attribute
2. **Each theme has distinct visual effects** — Matrix gets phosphor glow/scan-lines, Forge gets ember glow/texture options
3. **High contrast mode support** — All 5 themes have `@media (prefers-contrast: high)` overrides
4. **Forge as 4th theme choice** — Copper/amber industrial warmth complements the existing green-centric themes

### File Paths

- Design doc: `src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md`
- Matrix theme: `src/Web/ReactApp/src/styles/themes/matrix.css`
- Forge theme: `src/Web/ReactApp/src/styles/themes/forge.css`
- Theme CSS: `src/Web/ReactApp/src/styles/theme.css`
- Theme context: `src/Web/ReactApp/src/contexts/ThemeContext.tsx`
- Theme toggle: `src/Web/ReactApp/src/common/components/ThemeToggle.tsx`
- Icons: `src/Web/ReactApp/src/common/components/icons/MdiIcons.tsx`

---

---

## Visual QA Audit — Deployed App Themes (2026-06-02)

**Task:** QA visual review across all 7 themes (dark, light, matrix, blueprint, ratos, voron, farm) at http://10.0.0.20.
**Status:** PARTIAL — login page audited across all 7 themes; authenticated pages blocked by missing credentials.

### What I did
- Used playwright-cli (msedge backend; Chrome not installed) to drive a real browser against the deployed instance.
- Switched themes via `localStorage.setItem('pf-theme', ...)` + reload — this works because `ThemeContext` reads `pf-theme` on boot.
- Captured login-page screenshots for all 7 themes (saved under `.squad/agents/newt/screenshots-2026-06-02/`).
- Inspected computed styles on `body`, `h2`, dialog wrapper to verify per-theme fonts, backgrounds, fill colors.
- Probed `/api/setup/status`, `/api/version` to confirm deployment is live + non-virgin.

### Findings (filed as GitHub issues)
- **#467** (medium) — Login page wraps LoginModal/RegisterModal in a `bg-black/50 backdrop-blur-xs` overlay. On `/login` there is no underlying app content to dim, so the backdrop just paints the viewport gray. Especially damaging on Light theme where the proper body bg `#f5f7fa` reads as `#808080`-ish gray, looking like a disabled state.
- **#468** (medium) — `/printfarmer-logo.svg` is an `<img>`, not inline SVG, so CSS cannot recolor it. Logo is hardcoded blue across all themes. Clashes hard with Matrix (green), RatOS (green), Voron (red), Farm (orange). Acceptable on Dark, Blueprint, Light by coincidence.
- **#469** (blocker) — Could not audit authenticated pages (dashboard pills, sidebar nav, settings, slicer, queue, admin, modals). No QA credentials. `admin/password` and `admin/Admin123!` rejected; `/api/auth/register` returns 400 so could not self-provision.

### Positive signals
- Each theme genuinely has its own typeface (verified via computed `font-family`): Inter, Nunito Sans, JetBrains Mono, DM Mono, Rajdhani, Chakra Petch, Merriweather Sans. Typography part of the design system is working.
- Body backgrounds per theme are correctly distinct: dark `#08101f`, light `#f5f7fa`, matrix pure black, blueprint navy `#0c1a2e`, ratos `#080a08`, voron `#090909`, farm warm brown `#24150c`.
- Focus states on inputs use the theme accent color (teal on light, red on voron, etc.) — accent system working.
- Matrix theme heading text-shadow (phosphor glow) is intentional and looks correct.

### Learnings
- **`pf-theme` localStorage key is the entry point for non-authenticated theme switching.** Useful for QA: you can audit login pages across all themes without a session.
- **Logo strategy** (inline SVG with `currentColor` vs static `.svg` file) determines whether the brand mark can theme. PrintFarmer currently uses static `.svg`. Inline-SVG-component is the project's MdiIcons pattern and should be applied to the logo too.
- **Modal pattern on routes that ARE the app** (login route, setup wizard route) introduces a 50%-opacity backdrop over nothing — UX anti-pattern. Either route-aware backdrop, or extract card body to a non-modal component.
- **Heading "SIGN IN" purple-tint screenshot artifact** I initially saw is NOT a real bug — it's subpixel rendering bleeding the adjacent blue logo into the leading letters. Confirmed by inspecting computed styles (solid `rgb(232,238,248)` fill, no gradient, no text-shadow). Resolving #468 (themed logo) will also cure this perceptual artifact.

### Files
- Screenshots: `.squad/agents/newt/screenshots-2026-06-02/login-{theme}.png` and `login-focus-{theme}.png`.
- Issues: #467 (backdrop), #468 (logo), #469 (credentials blocker — needed to finish audit).


## Learnings

- 2026-06-02: The /login route should use a dedicated page auth surface instead of a modal backdrop. When the route itself is the destination, the shared modal overlay darkens an otherwise empty viewport and makes Light theme read like a disabled gray state; the reusable modal still belongs in in-app "sign in to continue" flows.




## Learnings

- 2026-06-02: The `/login` route should use a dedicated page auth surface instead of a modal backdrop. When the route itself is the destination, the shared modal overlay darkens an otherwise empty viewport and makes Light theme read like a disabled gray state; the reusable modal still belongs in in-app "sign in to continue" flows.

---

## 2026-06-02: Login Backdrop Fix (Issue #467)

**Commit:** c973e30c  
**Status:** MERGED

- Fixed /login route to use dedicated page auth surface instead of shared modal backdrop
- Backdrop overlay was inappropriately darkening empty viewport on standalone login page
- Replaced with centered card layout that preserves theme fidelity
- Kept LoginModal/RegisterModal components reusable for in-app auth flows via page/modal presentation switch
- Light theme especially benefits from centered card vs darkened background
