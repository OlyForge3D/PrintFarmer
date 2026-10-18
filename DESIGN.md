---
name: PrintFarmer
description: Self-hosted 3D print-farm control. Industrial precision meets modern dashboard.
colors:
  bg-page: "#08101f"
  bg-surface: "#0e1729"
  bg-inset: "#16213a"
  panel: "#0c1424"
  sidebar: "#0a1322"
  modal: "#101a2e"
  border: "#243250"
  border-strong: "#3d4f72"
  border-subtle: "#192340"
  text-primary: "#e8eef8"
  text-secondary: "#a0aec8"
  text-tertiary: "#8090ab"
  text-muted: "#566275"
  accent: "#14b8a6"
  accent-text: "#2dd4bf"
  accent-deep: "#0f766e"
  accent-deeper: "#115e59"
  accent-2: "#60a5fa"
  on-accent: "#ffffff"
  success: "#10b981"
  warning: "#f59e0b"
  error: "#ef4444"
  info: "#60a5fa"
  status-online-bg: "#052e2b"
  status-online-text: "#5eead4"
  status-printing-bg: "#082f49"
  status-printing-text: "#7dd3fc"
  status-paused-bg: "#451a03"
  status-paused-text: "#fcd34d"
  status-error-bg: "#450a0a"
  status-error-text: "#fca5a5"
  status-idle-bg: "#1c2742"
  status-idle-text: "#a0aec8"
typography:
  display:
    fontFamily: "'Space Grotesk', 'IBM Plex Sans', ui-sans-serif, system-ui, sans-serif"
    fontSize: "1.875rem"
    fontWeight: 700
    lineHeight: 1.2
    letterSpacing: "-0.015em"
  headline:
    fontFamily: "'Space Grotesk', 'IBM Plex Sans', ui-sans-serif, system-ui, sans-serif"
    fontSize: "1.5rem"
    fontWeight: 700
    lineHeight: 1.25
    letterSpacing: "-0.01em"
  title:
    fontFamily: "'Inter', 'IBM Plex Sans', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', sans-serif"
    fontSize: "1.125rem"
    fontWeight: 500
    lineHeight: 1.45
  body:
    fontFamily: "'Inter', 'IBM Plex Sans', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', sans-serif"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: 1.45
  label:
    fontFamily: "'Inter', 'IBM Plex Sans', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', sans-serif"
    fontSize: "0.625rem"
    fontWeight: 600
    lineHeight: 1.4
    letterSpacing: "0.04em"
  data:
    fontFamily: "'JetBrains Mono', ui-monospace, 'SF Mono', 'Cascadia Code', Consolas, monospace"
    fontSize: "0.875rem"
    fontWeight: 500
    lineHeight: 1.4
    fontFeature: "tnum"
rounded:
  none: "0"
  xs: "2px"
  sm: "4px"
  md: "6px"
  lg: "8px"
  full: "9999px"
spacing:
  "1": "4px"
  "2": "8px"
  "3": "12px"
  "4": "16px"
  "5": "20px"
  "6": "24px"
  "8": "32px"
  "10": "40px"
  "12": "48px"
  "16": "64px"
components:
  button-primary:
    backgroundColor: "{colors.accent-deep}"
    textColor: "{colors.on-accent}"
    rounded: "{rounded.xs}"
    padding: "0 16px"
    height: "36px"
  button-primary-hover:
    backgroundColor: "{colors.accent-deeper}"
    textColor: "{colors.on-accent}"
  button-secondary:
    backgroundColor: "transparent"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.xs}"
    padding: "0 16px"
    height: "36px"
  button-secondary-hover:
    backgroundColor: "{colors.bg-inset}"
  input:
    backgroundColor: "{colors.bg-surface}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.sm}"
    padding: "0 12px"
    height: "40px"
  card:
    backgroundColor: "{colors.bg-surface}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.lg}"
    padding: "16px"
  status-pill-printing:
    backgroundColor: "{colors.status-printing-bg}"
    textColor: "{colors.status-printing-text}"
    typography: "{typography.label}"
    rounded: "{rounded.xs}"
    padding: "2px 8px"
    height: "20px"
  progress-track:
    backgroundColor: "{colors.bg-inset}"
    rounded: "{rounded.full}"
    height: "8px"
---

# Design System: PrintFarmer

This file is the portable summary of the default theme, **PrintFarmer Dark ("Mission Control")**. The detailed authority is [`src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md`](src/Web/ReactApp/src/design-system/DESIGN-LANGUAGE.md). It holds the full token contract, the per-theme palettes, the admin surface rules, and the accessibility floor. If the two files disagree, DESIGN-LANGUAGE.md and the CSS in `design-system/themes/` win, and this file should be refreshed.

## Overview

**Creative North Star: "The Shop-Floor Instrument Panel"**

PrintFarmer is hardware control software, not a SaaS dashboard. Every surface works like a gauge or readout that an operator walking past a rack of printers can trust at a glance:

- State is legible from across the room.
- Numbers sit still as they update.
- Nothing pretends to know more than the machine reported.

The look is dark first, because shops and farms are dim. It is dense but ordered, and built from crisp hairline borders, tight corners, and one precise teal signal.

Eight themes ship over one CSS variable contract: Mission Control (the default), Workshop Daylight, Terminal, Schematic, Firmware, Precision, Harvest, and Industrial Warmth. The themes change colors and the body font. Structure, the type scale, spacing, radius, and motion stay identical, so components reference semantic tokens (`--pf-text-primary`) and never literal hex values.

**Key Characteristics:**
- Dark slate-navy substrate with a single precision-teal accent.
- A strict 4px grid, with tight radii of 2–8px. Pills are reserved for things that are round by nature.
- Monospace tabular figures for every live number.
- Status is shown by color **and** shape or glyph, never by color alone.
- Flat surfaces separated by hairlines. Shadows are minimal.

## Colors

Cool, low-chroma navy surfaces carry one saturated teal accent. Semantic and status colors appear only where they report state.

### Primary
- **Precision Teal** (`accent`): the single brand signal. Use it for progress fills, focus rings, the active tab underline, and the primary-action family.
- **Deep Teal** (`accent-deep`): the primary button fill, chosen so white text passes AA. **Deeper Teal** (`accent-deeper`) is its hover state.
- **Readout Teal** (`accent-text`): accent-colored text and links on dark surfaces.

### Secondary
- **Signal Blue** (`accent-2`): the secondary accent, also used for info and links. Use it rarely, and never let it compete with teal on the same control.

### Neutral
- **Night Shift** (`bg-page`): the page background.
- **Console Slate** (`bg-surface`): cards and primary surfaces. **Inset Slate** (`bg-inset`) is for inset regions, hover fills, and progress tracks.
- **Rail Navy** (`sidebar`) and **Panel Navy** (`panel`): navigation and side panels. **Lifted Navy** (`modal`) is the modal surface.
- **Hairline** (`border`): the default 1px stroke, at 3.4:1 against the page. **Strong Hairline** (`border-strong`) is for emphasis. **Faint Hairline** (`border-subtle`) is for dividers.
- **Bright Readout** (`text-primary`): primary text, at 14.6:1 on the page. **Dim Readout** (`text-secondary`) is the floor for default text. **Faint Readout** (`text-tertiary`) is for non-essential captions only.

### Status
Status colors always come as a `{bg, text, border}` triple: online, printing, paused, error, and idle. Semantic success, warning, error, and info each come as a `{base, bg, border, fg}` group.

**The Triple Rule.** Never invent a status color outside its `{bg, text, border}` triple, and never show status with color alone.

**The One Signal Rule.** Teal means "this is the active path or the live value." If everything on screen is teal, nothing is.

## Typography

**Display Font:** Space Grotesk (fallback: IBM Plex Sans)
**Body Font:** Inter in Mission Control (fallback: IBM Plex Sans, system-ui). Each theme sets its own body font; IBM Plex Sans is the shared `--pf-font-sans` fallback.
**Data Font:** JetBrains Mono (fallback: SF Mono, Cascadia Code)

**Character:** a mechanical geometric grotesque for headings over a neutral, highly legible sans for UI. A purpose-built mono face handles every reading an operator compares.

### Hierarchy
- **Display** (700, 30px, 1.2, -0.015em): dashboard headlines.
- **Headline** (700, 24px, 1.25): page titles.
- **Title** (500, 18px, 1.45): card titles and subheadings.
- **Body** (400, 14px, 1.45): default UI and body text. Prose is capped at about 65ch.
- **Label** (600, 10px, +0.04em, uppercase): badges, status pills, and column headers only.
- **Data** (mono, tabular-nums): temperatures, percentages, durations, IPs, serials, and file sizes.

**The Steady Digits Rule.** Any value that updates live uses the data face with tabular figures, so digits never jitter.

**The Three Sizes Rule.** No visible section uses more than three type sizes.

## Layout

Spacing follows a strict 4px grid from the `--pf-space-*` scale, and ad-hoc values are not allowed. Breakpoints are the Tailwind v4 defaults and are not customized. Density is chosen once per surface:

- **Cards:** 16px padding when compact, 24px when comfortable.
- **Table rows:** 40px by default, 32px dense, 48px comfortable.
- **Form fields:** 40px tall.
- **Buttons:** 28, 36, or 44px tall. The 44px size is the mobile touch target.

Fleet views are card grids. The settings and admin surfaces are a single URL-driven shell, and DESIGN-LANGUAGE.md § "Admin Surface" defines their framing.

## Elevation & Depth

The system is hybrid but mostly flat. Depth comes first from tonal layering (`bg-page` → `bg-surface` → `bg-inset`) and hairline borders. Shadows are a small, structural vocabulary from `--pf-shadow-xs` to `--pf-shadow-xl`, stepping from resting cards up to full-screen takeovers. The colored glows (`--pf-glow-*`) are for active and focus emphasis. Mission Control uses them sparingly; the Terminal and Schematic themes lean on them for character.

**The Flat-At-Rest Rule.** Cards rest at `xs` and lift to `sm` only on hover, and only when they are interactive. Popovers sit at `md` and modals at `lg`.

**The No-Gradient Rule.** Card and surface backgrounds are flat colors. The gradient tokens are deprecated fallbacks.

## Shapes

Corners are tight and machined: 2px for buttons and status pills, 4px for inputs, 6px for panels and modals, and 8px for cards. The full radius is reserved for shapes that are round by nature: avatars, dots, circular icon buttons, tag chips, progress bars, toggle tracks, fixed-size count badges, and rare physical-object depictions.

**The 8px Ceiling Rule.** No rectangular surface exceeds 8px. The lint rule `local/pf-no-oversized-radius` enforces this, and a genuine pill must declare `data-pf-radius="full"`.

## Components

### Buttons
Precise and unadorned. Primitive: `common/components/ui/Button.tsx`.
- **Shape:** squared-off with a 2px radius. Tab buttons have no radius.
- **Variants:** primary (Deep Teal), secondary (transparent with a hairline border), danger, success, subtle, ghost, tab, toggle, and link.
- **Sizes:** sm, md, and lg (28, 36, and 44px tall), with 12, 16, and 24px horizontal padding. The gap between icon and label is 8px.
- **States:**
  - Hover: a background shift plus `shadow-sm`.
  - Focus-visible: a 2px teal ring with a 2px offset.
  - Disabled: 50% opacity.
  - Loading: a spinner replaces the icon and the button keeps its width.

### Inputs / Fields
- **Style:** Console Slate fill, 1px control border, 4px radius, 40px tall (32px compact).
- **Focus:** the border turns teal and gets a 2px ring.
- **Error / disabled:** error uses the validation-error border and ring. Disabled uses the disabled background and text. Numeric, IP, and MAC inputs use the data face.

### Cards / Containers
- **Corner style:** 8px.
- **Background:** flat Console Slate (`bg-surface`).
- **Border:** a 1px Hairline.
- **Shadow:** `xs` at rest, `sm` on hover when interactive.
- **Structure:** a header with a bottom border, a body, and a footer with a top border and right-aligned actions.

### Status Pills
- **Style:** 20 or 24px tall, 2px radius, uppercase label type, colored by the status triple.
- **Glyph:** every pill carries a shape cue (● online, ○ offline, ▶ printing, ❚❚ paused, ! error).

### Progress Bars
- **Style:** 8px tall by default (4px thin, 12px thick), fully rounded, on an Inset Slate track.
- **Fill:** teal by default, then success, warning, or error by outcome.
- **Indeterminate:** a 1.2s shimmer, disabled under reduced motion. ARIA `progressbar` attributes are always present.

### Printer Card (signature)
The fleet unit. Primitives: `features/printers/components/CompactPrinterCard.tsx` and `DetailedPrinterCard.tsx`.
- The header band is tinted by printer state and carries the printer name in the display face.
- The body puts the printer's model image and identity on the left, above the progress bar and temperature readouts, with a vertical ghost-icon toolbar on the right.
- Camera preview, failure-detection summary, and bed-clear banners stack inside the body.

## Do's and Don'ts

### Do:
- **Do** reference semantic tokens (`--pf-*` or the `pf-*` Tailwind utilities) so every theme works unchanged.
- **Do** set every live number in JetBrains Mono with `tabular-nums`.
- **Do** pair status color with a glyph or label, and meet WCAG 2.2 AA: 4.5:1 for text and 3:1 for UI boundaries and focus rings.
- **Do** give every interactive element a visible 2px focus ring, and meet the 44px touch target on mobile.
- **Do** respect `prefers-reduced-motion`. Use `--pf-ease-out` with 120, 200, or 320ms durations for state transitions.

### Don't:
- **Don't** hard-code hex values in components. Literal colors break the other seven themes.
- **Don't** exceed an 8px radius on rectangles, or use a pill shape outside the sanctioned list.
- **Don't** put gradients on cards or surfaces.
- **Don't** use uppercase outside badges, status pills, and column headers.
- **Don't** hard-code a `font-family` in components. Body fonts are per-theme, so use the inherited body face or the `--pf-font-*` tokens.
