# PrintFarmer Design Language

> **Industrial precision meets modern dashboard.**
> Dark-first, data-forward, workshop-grade.

This document is the authoritative reference for PrintFarmer's visual design system. It defines the typography, color, spacing, motion, and component vocabulary that every screen in PrintFarmer is built from. All four shipped themes — **Light**, **PrintFarmer Dark**, **Matrix**, and **Blueprint** — implement the same CSS variable contract defined at the end of this document. Themes swap pigment; structure stays identical.

This is **Phase 1: design decisions only**. No CSS or TSX is written here. The contract at the end of this file is the API that the Phase 2 implementation must conform to.

---

## Design Philosophy

PrintFarmer is **hardware control software** — not a generic SaaS dashboard. Operators monitor temperatures, watch print progress, intervene in failures, and manage a physical fleet. The UI should feel like the control surface of a CNC machine, not a marketing website.

Five non-negotiable principles:

1. **Industrial precision.** Lines are crisp. Corners are tight (`2px`–`6px`). Spacing is on a strict 4px grid. Nothing decorative without a reason.
2. **Dark-first.** Workshops, makerspaces, and print farms are dim. The dark themes are the daily driver. Light mode must still feel industrial — never washed out.
3. **Data-forward.** Numerical values (temperatures, percentages, durations, IPs) use a monospace face so digits align across rows. Status is conveyed by both color **and** shape — never color alone.
4. **Consistent.** A button is a button. A card is a card. Every surface, border, and elevation comes from this document. No one-off styling.
5. **Accessible.** WCAG 2.2 AA is the minimum floor — 4.5:1 contrast on text, 3:1 on UI parts, visible 2px focus rings on every interactive element, 44px touch targets.

---

## Typography

### Font Selection Rationale

PrintFarmer deliberately rejects the SaaS defaults (Inter, Roboto, system stacks). Three faces were chosen for their industrial-technical pedigree, distinctive silhouettes, and excellent screen rendering at the small sizes typical of dashboard UI.

| Token | Family | Role | Why |
|---|---|---|---|
| `--pf-font-sans` | **IBM Plex Sans** | Body, UI controls, navigation | IBM-commissioned for technical interfaces. Slightly humanist, exceptional at 11–16px, distinctive without being weird. Industrial heritage. |
| `--pf-font-display` | **Space Grotesk** | Headlines, page titles, section headers | Geometric grotesque with mechanical character. Replaces the previous Bebas Neue — equally industrial but readable at smaller sizes and supports a full weight range. |
| `--pf-font-mono` | **JetBrains Mono** | Temperatures, IPs, timestamps, durations, code | Purpose-built for technical display. Disambiguated glyphs (`0/O`, `1/l/I`), tabular figures, and a wide weight range. Numbers align perfectly in tables. |

**Fallback stacks** are defined to degrade gracefully if the web font fails to load:

```text
--pf-font-sans:    'IBM Plex Sans', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', sans-serif;
--pf-font-display: 'Space Grotesk', 'IBM Plex Sans', ui-sans-serif, system-ui, sans-serif;
--pf-font-mono:    'JetBrains Mono', ui-monospace, 'SF Mono', 'Cascadia Code', Consolas, monospace;
```

### Font Loading

All three faces ship from Google Fonts with subsetted Latin glyphs and only the weights we use, served as variable fonts where possible:

```html
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600;700&family=Space+Grotesk:wght@500;600;700&family=JetBrains+Mono:wght@400;500;700&display=swap">
```

`display=swap` is mandatory — operators must see content immediately even on a cold load. The fallback stacks above ensure layout shift is minimal.

### Type Scale

A modular scale derived from a 16px base. Use these tokens — never inline arbitrary `px` or `text-[13px]` values.

| Token | Size | Line Height | Weight | Tracking | Use |
|---|---|---|---|---|---|
| `text-2xs` | 10px / 0.625rem | 1.4 | 500 | +0.04em | Microcopy, badge text |
| `text-xs` | 12px / 0.75rem | 1.4 | 400 | 0 | Captions, table cells, helper text |
| `text-sm` | 14px / 0.875rem | 1.45 | 400 | 0 | Default body, form labels, secondary UI |
| `text-base` | 16px / 1rem | 1.5 | 400 | 0 | Primary body, card content |
| `text-lg` | 18px / 1.125rem | 1.45 | 500 | 0 | Subheadings, card titles |
| `text-xl` | 20px / 1.25rem | 1.35 | 600 | 0 | Section headings |
| `text-2xl` | 24px / 1.5rem | 1.25 | 700 | -0.01em | Page titles (display font) |
| `text-3xl` | 30px / 1.875rem | 1.2 | 700 | -0.015em | Hero / dashboard headlines (display font) |
| `text-4xl` | 36px / 2.25rem | 1.15 | 700 | -0.02em | Empty-state hero, splash |

### Typographic Rules

1. **Headlines** (h1–h3) use `--pf-font-display`, semibold or bold, with negative tracking at large sizes.
2. **Body** uses `--pf-font-sans` at 400 or 500.
3. **All numeric data** — temperatures (`208°C`), percentages (`64%`), durations (`02:14:33`), IP addresses, MACs, serials, file sizes — uses `--pf-font-mono` with `font-variant-numeric: tabular-nums` so digits do not jitter as values change.
4. **No more than three** type sizes per visible section.
5. **Line length**: cap body paragraphs at `max-w-prose` (~65ch). Tabular content is exempt.
6. **Uppercase** is reserved for badges, status pills, and column headers. Always pair with `letter-spacing: 0.04em` for legibility.

---

## Spacing Scale

A strict **4px base unit**. Industrial UIs benefit from tight, predictable rhythm — no ad-hoc spacing. Tailwind's default 4px scale is preserved, with the following project-specific tokens added for consistency.

| Token | Value | Use |
|---|---|---|
| `--pf-space-0` | 0 | Resets |
| `--pf-space-1` | 4px (0.25rem) | Tight inline (icon-to-text), badge padding |
| `--pf-space-2` | 8px (0.5rem) | Compact group spacing, input internal |
| `--pf-space-3` | 12px (0.75rem) | Form row gap, button padding-x |
| `--pf-space-4` | 16px (1rem) | Default card padding, between related items |
| `--pf-space-5` | 20px (1.25rem) | Loose group spacing |
| `--pf-space-6` | 24px (1.5rem) | Section gap, card padding (comfortable) |
| `--pf-space-8` | 32px (2rem) | Major section gap |
| `--pf-space-10` | 40px (2.5rem) | Page top spacing |
| `--pf-space-12` | 48px (3rem) | Hero spacing |
| `--pf-space-16` | 64px (4rem) | Full-bleed section break |

### Density Targets

- **Cards**: `p-4` (16px) compact, `p-6` (24px) comfortable. Pick one per surface — never mix.
- **Tables**: row height `40px` default, `32px` dense, `48px` comfortable. Cell padding `px-3 py-2`.
- **Form fields**: `40px` standard height. Labels sit `8px` above their control.
- **Buttons**: `sm = 28px`, `md = 36px`, `lg = 44px` height. `lg` is the minimum touch target on mobile.

---

## Border Radii

Industrial design rejects soft, rounded "friendly" corners. PrintFarmer's radii are tight and purposeful.

| Token | Value | Use |
|---|---|---|
| `--pf-radius-none` | 0 | Full-bleed surfaces, dividers |
| `--pf-radius-xs` | 2px | Badges, status pills, inline tags |
| `--pf-radius-sm` | 4px | Buttons, inputs, selects, small controls |
| `--pf-radius-md` | 6px | Cards, panels, modals |
| `--pf-radius-lg` | 8px | Hero cards, large containers |
| `--pf-radius-full` | 9999px | Avatars, circular icon buttons, progress dots |

**Rule**: never exceed `--pf-radius-lg` for rectangular surfaces. The only fully-rounded shapes are avatars and dots.

---

## Elevation & Shadows

Five elevation layers. Each theme defines its own RGBA values so shadows look correct on both light and dark surfaces.

| Token | Description | Layer | Use |
|---|---|---|---|
| `--pf-shadow-none` | No shadow | 0 | Flat surfaces, divider-bound regions |
| `--pf-shadow-xs` | `0 1px 2px rgba(0,0,0,0.06)` | 1 | Default cards, inputs |
| `--pf-shadow-sm` | `0 2px 4px rgba(0,0,0,0.08)` | 2 | Hover state of cards, secondary panels |
| `--pf-shadow-md` | `0 4px 8px rgba(0,0,0,0.12)` | 3 | Dropdowns, popovers, tooltips |
| `--pf-shadow-lg` | `0 8px 24px rgba(0,0,0,0.18)` | 4 | Modals, dialogs |
| `--pf-shadow-xl` | `0 16px 48px rgba(0,0,0,0.24)` | 5 | Sheet overlays, full-screen takeovers |

**Glow shadows** (`--pf-glow-accent`, `--pf-glow-success`, `--pf-glow-warning`, `--pf-glow-error`) provide colored emphasis for active/focused states. The Matrix and Blueprint themes lean on glows for character; Light and PrintFarmer Dark use them sparingly.

---

## Color Architecture

Colors are **semantic** first, **literal** never. UI code references `--pf-text-primary`, never `#e8eef8`. This is what makes a 4-theme system possible.

### Token Layers (from foundation to surface)

The token names are theme-agnostic; only the values change between themes.

```text
Surface     →  bg-0, bg-1, bg-2, panel, card, sidebar, modal
Border      →  border, border-strong, border-subtle, border-divider
Text        →  text-primary, text-secondary, text-tertiary, text-muted, text-inverse, text-on-accent
Accent      →  accent, accent-bg, accent-hover, accent-fg, accent-2
Semantic    →  success, warning, error, info (each with -bg, -border, -fg variants)
Status      →  status-online, status-offline, status-printing, status-paused, status-error, status-idle
              (each with -bg, -text, -border)
Control     →  control-bg, control-border, control-border-hover, control-border-focus,
              control-text, control-placeholder, control-disabled-bg, control-disabled-text
Button      →  button-{primary|secondary|danger|success}-{bg|hover|active|text|border}
Validation  →  validation-{error|success|warning}-{bg|border|text}
Feedback    →  focus-ring, focus-ring-offset, hover-overlay, active-overlay,
              selection-bg, selection-text
Skeleton    →  skeleton-bg, skeleton-bg-alt, skeleton-accent
Glow        →  glow-accent, glow-success, glow-warning, glow-error
```

### Naming Rules

- `bg-*` = surface fill. Number suffix increases visual depth (0 = page, 2 = inset).
- `*-bg` = the *background* of a semantic role (e.g., `status-online-bg`).
- `*-fg` = the *foreground* (text/icon) of a semantic role on its matching `-bg`.
- `*-hover`, `*-active` = interactive variants of the base token.
- Status colors must always come as a `{bg, text, border}` triple. Never invent a status color outside this trio.

### Contrast Floor (per WCAG 2.2 AA)

| Pair | Required ratio | Notes |
|---|---|---|
| `text-primary` on any `bg-*` | ≥ 7.0 : 1 (AAA) | Aim higher than the 4.5 floor for primary text |
| `text-secondary` on any `bg-*` | ≥ 4.5 : 1 | The hard floor for default text |
| `text-tertiary` on any `bg-*` | ≥ 3.0 : 1 | Allowed only for non-essential text (timestamps, captions) |
| `border` on adjacent `bg` | ≥ 3.0 : 1 | UI boundary contrast |
| Icon / focus ring on background | ≥ 3.0 : 1 | UI component contrast |
| Status colors against their `-bg` | ≥ 4.5 : 1 | Text+icon legibility |

Every theme below has been picked to meet these floors. The Matrix theme's bright phosphor green on pure black achieves ~13:1.

---

## Theme 1 — Light ("Workshop Daylight")

Clean, professional, high-contrast for well-lit environments. Designed to feel like a printed engineering datasheet — never washed out. Deep teal accent (the canonical "manufacturing" color) instead of the typical SaaS blue.

| Token | Hex | Notes |
|---|---|---|
| `--pf-bg-0` | `#f5f7fa` | Page (cool paper-white, slight blue tint) |
| `--pf-bg-1` | `#ffffff` | Cards, primary surfaces |
| `--pf-bg-2` | `#eceff4` | Inset, code blocks, table stripes |
| `--pf-panel` | `#ffffff` | Side panels |
| `--pf-card-bg` | `#ffffff` | Cards |
| `--pf-sidebar-bg` | `#f0f3f7` | Navigation rail |
| `--pf-modal-bg` | `#ffffff` | Modal surface |
| `--pf-border` | `#d8dde6` | Default 1px stroke |
| `--pf-border-strong` | `#aab4c2` | Emphasis stroke |
| `--pf-border-subtle` | `#e8ecf2` | Hairline dividers |
| `--pf-border-divider` | `#e2e6ec` | Internal table dividers |
| `--pf-text-primary` | `#0b1320` | 16.8:1 on `bg-0` |
| `--pf-text-secondary` | `#3e4a5e` | 8.9:1 |
| `--pf-text-tertiary` | `#6b7689` | 4.7:1 |
| `--pf-text-muted` | `#8a93a3` | 3.4:1 (non-essential only) |
| `--pf-text-inverse` | `#ffffff` | On dark accents |
| `--pf-text-on-accent` | `#ffffff` | On accent button bg |
| `--pf-accent` | `#0d7d75` | Deep teal — primary brand |
| `--pf-accent-bg` | `#0d7d75` | Buttons, fills |
| `--pf-accent-hover` | `#0a6862` | Hover |
| `--pf-accent-fg` | `#ffffff` | Text on accent bg |
| `--pf-accent-2` | `#1d4ed8` | Secondary blue accent (links) |
| `--pf-success` | `#15803d` | |
| `--pf-success-bg` | `#dcfce7` | |
| `--pf-success-border` | `#86efac` | |
| `--pf-success-fg` | `#14532d` | |
| `--pf-warning` | `#a16207` | |
| `--pf-warning-bg` | `#fef9c3` | |
| `--pf-warning-border` | `#fde047` | |
| `--pf-warning-fg` | `#713f12` | |
| `--pf-error` | `#b91c1c` | |
| `--pf-error-bg` | `#fee2e2` | |
| `--pf-error-border` | `#fca5a5` | |
| `--pf-error-fg` | `#7f1d1d` | |
| `--pf-info` | `#1d4ed8` | |
| `--pf-info-bg` | `#dbeafe` | |
| `--pf-info-border` | `#93c5fd` | |
| `--pf-info-fg` | `#1e3a8a` | |
| `--pf-status-online-bg` | `#dcfce7` | |
| `--pf-status-online-text` | `#14532d` | |
| `--pf-status-online-border` | `#15803d` | |
| `--pf-status-offline-bg` | `#fee2e2` | |
| `--pf-status-offline-text` | `#7f1d1d` | |
| `--pf-status-offline-border` | `#b91c1c` | |
| `--pf-status-printing-bg` | `#cffafe` | |
| `--pf-status-printing-text` | `#155e75` | |
| `--pf-status-printing-border` | `#06b6d4` | |
| `--pf-status-paused-bg` | `#fef3c7` | |
| `--pf-status-paused-text` | `#78350f` | |
| `--pf-status-paused-border` | `#f59e0b` | |
| `--pf-status-error-bg` | `#fee2e2` | |
| `--pf-status-error-text` | `#7f1d1d` | |
| `--pf-status-error-border` | `#dc2626` | |
| `--pf-status-idle-bg` | `#f1f5f9` | |
| `--pf-status-idle-text` | `#475569` | |
| `--pf-status-idle-border` | `#94a3b8` | |
| `--pf-focus-ring` | `rgba(13, 125, 117, 0.45)` | Accent-tinted |
| `--pf-focus-ring-offset` | `#ffffff` | |
| `--pf-hover-overlay` | `rgba(11, 19, 32, 0.04)` | |
| `--pf-active-overlay` | `rgba(11, 19, 32, 0.08)` | |
| `--pf-selection-bg` | `#b8e8e4` | |
| `--pf-selection-text` | `#0b1320` | |
| `--pf-skeleton-bg` | `#e2e8f0` | |
| `--pf-skeleton-bg-alt` | `#eceff4` | |
| `--pf-skeleton-accent` | `#cbd5e1` | |
| `--pf-glow-accent` | `0 0 0 transparent` | Light theme uses no glows |
| `--pf-glow-success` | `0 0 0 transparent` | |
| `--pf-glow-warning` | `0 0 0 transparent` | |
| `--pf-glow-error` | `0 0 0 transparent` | |
| `color-scheme` | `light` | |

---

## Theme 2 — PrintFarmer Dark ("Mission Control")

The flagship theme. The existing dark mode is good but inconsistent — accents drift between `#10b981` and `#047857`, borders are too light at `#475569`, and the surface hierarchy is muddled. This refinement systematizes the palette around a cool slate-blue substrate with a precision-teal accent.

| Token | Hex | Notes |
|---|---|---|
| `--pf-bg-0` | `#08101f` | Page (deep slate-navy) |
| `--pf-bg-1` | `#0e1729` | Cards, primary surfaces |
| `--pf-bg-2` | `#16213a` | Inset, hover surfaces |
| `--pf-panel` | `#0c1424` | Side panels |
| `--pf-card-bg` | `#0e1729` | Cards |
| `--pf-sidebar-bg` | `#0a1322` | Navigation rail |
| `--pf-modal-bg` | `#101a2e` | Modal surface (lifted) |
| `--pf-border` | `#243250` | Default stroke (3.4:1 on bg-0) |
| `--pf-border-strong` | `#3d4f72` | Emphasis stroke |
| `--pf-border-subtle` | `#192340` | Hairline divider |
| `--pf-border-divider` | `#1c2742` | Table dividers |
| `--pf-text-primary` | `#e8eef8` | 14.6:1 on bg-0 |
| `--pf-text-secondary` | `#a0aec8` | 7.2:1 |
| `--pf-text-tertiary` | `#6b7892` | 4.5:1 (the floor for default text) |
| `--pf-text-muted` | `#566275` | 3.1:1 (captions only) |
| `--pf-text-inverse` | `#08101f` | On bright accents |
| `--pf-text-on-accent` | `#ffffff` | On accent button bg |
| `--pf-accent` | `#14b8a6` | Precision teal |
| `--pf-accent-bg` | `#0f766e` | Accent button bg |
| `--pf-accent-hover` | `#115e59` | |
| `--pf-accent-fg` | `#ffffff` | |
| `--pf-accent-2` | `#60a5fa` | Secondary blue (links) |
| `--pf-success` | `#10b981` | |
| `--pf-success-bg` | `#064e3b` | |
| `--pf-success-border` | `#065f46` | |
| `--pf-success-fg` | `#a7f3d0` | |
| `--pf-warning` | `#f59e0b` | |
| `--pf-warning-bg` | `#451a03` | |
| `--pf-warning-border` | `#78350f` | |
| `--pf-warning-fg` | `#fde68a` | |
| `--pf-error` | `#ef4444` | |
| `--pf-error-bg` | `#450a0a` | |
| `--pf-error-border` | `#7f1d1d` | |
| `--pf-error-fg` | `#fecaca` | |
| `--pf-info` | `#60a5fa` | |
| `--pf-info-bg` | `#1e3a8a` | |
| `--pf-info-border` | `#1d4ed8` | |
| `--pf-info-fg` | `#bfdbfe` | |
| `--pf-status-online-bg` | `#052e2b` | |
| `--pf-status-online-text` | `#5eead4` | |
| `--pf-status-online-border` | `#0f766e` | |
| `--pf-status-offline-bg` | `#450a0a` | |
| `--pf-status-offline-text` | `#fca5a5` | |
| `--pf-status-offline-border` | `#7f1d1d` | |
| `--pf-status-printing-bg` | `#082f49` | |
| `--pf-status-printing-text` | `#7dd3fc` | |
| `--pf-status-printing-border` | `#0369a1` | |
| `--pf-status-paused-bg` | `#451a03` | |
| `--pf-status-paused-text` | `#fcd34d` | |
| `--pf-status-paused-border` | `#b45309` | |
| `--pf-status-error-bg` | `#450a0a` | |
| `--pf-status-error-text` | `#fca5a5` | |
| `--pf-status-error-border` | `#b91c1c` | |
| `--pf-status-idle-bg` | `#1c2742` | |
| `--pf-status-idle-text` | `#a0aec8` | |
| `--pf-status-idle-border` | `#3d4f72` | |
| `--pf-focus-ring` | `rgba(20, 184, 166, 0.5)` | Teal accent |
| `--pf-focus-ring-offset` | `#08101f` | |
| `--pf-hover-overlay` | `rgba(255, 255, 255, 0.04)` | |
| `--pf-active-overlay` | `rgba(255, 255, 255, 0.08)` | |
| `--pf-selection-bg` | `#0f766e` | |
| `--pf-selection-text` | `#ffffff` | |
| `--pf-skeleton-bg` | `#16213a` | |
| `--pf-skeleton-bg-alt` | `#1c2742` | |
| `--pf-skeleton-accent` | `#3d4f72` | |
| `--pf-glow-accent` | `0 0 12px rgba(20, 184, 166, 0.25)` | Subtle |
| `--pf-glow-success` | `0 0 12px rgba(16, 185, 129, 0.20)` | |
| `--pf-glow-warning` | `0 0 12px rgba(245, 158, 11, 0.20)` | |
| `--pf-glow-error` | `0 0 12px rgba(239, 68, 68, 0.25)` | |
| `color-scheme` | `dark` | |

---

## Theme 3 — Matrix ("Terminal")

Inspired by The Matrix and RatOS. Phosphor-green CRT terminal aesthetic — pure black canvas, glowing green data, amber for warnings (like an old vector display). Genuinely usable, not novelty: the type sizes and component shapes are identical to the other themes; only color and a few subtle text-shadow glows change.

The Matrix theme uses `--pf-font-mono` as its body face by default — flip via `font-family: var(--pf-font-mono)` on `body[data-theme="matrix"]`. This is the only theme where mono is the default body font.

| Token | Hex | Notes |
|---|---|---|
| `--pf-bg-0` | `#000000` | True black CRT canvas |
| `--pf-bg-1` | `#050a05` | Cards (barely-lifted green-black) |
| `--pf-bg-2` | `#0a120a` | Inset surfaces |
| `--pf-panel` | `#040804` | Side panels (darkest) |
| `--pf-card-bg` | `#050a05` | Cards |
| `--pf-sidebar-bg` | `#030603` | Navigation rail |
| `--pf-modal-bg` | `#080f08` | Modal surface |
| `--pf-border` | `#1a2e1a` | Default green-tinted stroke |
| `--pf-border-strong` | `#2d4a2d` | Emphasis stroke |
| `--pf-border-subtle` | `#0d150d` | Hairline divider |
| `--pf-border-divider` | `#102010` | Table dividers |
| `--pf-text-primary` | `#00ff41` | Iconic Matrix phosphor green (13.1:1 on black) |
| `--pf-text-secondary` | `#4ade80` | Dimmer green (9.5:1) |
| `--pf-text-tertiary` | `#22c55e` | Muted green (6.4:1) |
| `--pf-text-muted` | `#15803d` | Faint green (3.3:1) |
| `--pf-text-inverse` | `#000000` | On bright accents |
| `--pf-text-on-accent` | `#000000` | Black text on green button |
| `--pf-accent` | `#00ff41` | Primary green |
| `--pf-accent-bg` | `#00cc34` | Solid green button bg |
| `--pf-accent-hover` | `#00b32d` | Hover |
| `--pf-accent-fg` | `#000000` | Black on green |
| `--pf-accent-2` | `#fbbf24` | Amber — secondary CRT warning accent |
| `--pf-success` | `#00ff41` | Success = primary green |
| `--pf-success-bg` | `#003d10` | |
| `--pf-success-border` | `#00802a` | |
| `--pf-success-fg` | `#7fff96` | |
| `--pf-warning` | `#fbbf24` | Amber CRT warning |
| `--pf-warning-bg` | `#3b2a06` | |
| `--pf-warning-border` | `#a16207` | |
| `--pf-warning-fg` | `#fde68a` | |
| `--pf-error` | `#ff3344` | High-saturation alarm red |
| `--pf-error-bg` | `#3d0008` | |
| `--pf-error-border` | `#991b25` | |
| `--pf-error-fg` | `#ff8088` | |
| `--pf-info` | `#00d4ff` | Cyan diagnostic |
| `--pf-info-bg` | `#003344` | |
| `--pf-info-border` | `#006688` | |
| `--pf-info-fg` | `#7fe0ff` | |
| `--pf-status-online-bg` | `#001a08` | |
| `--pf-status-online-text` | `#00ff41` | |
| `--pf-status-online-border` | `#00802a` | |
| `--pf-status-offline-bg` | `#1a0004` | |
| `--pf-status-offline-text` | `#ff3344` | |
| `--pf-status-offline-border` | `#660010` | |
| `--pf-status-printing-bg` | `#001a1f` | |
| `--pf-status-printing-text` | `#00d4ff` | |
| `--pf-status-printing-border` | `#006688` | |
| `--pf-status-paused-bg` | `#1f1500` | |
| `--pf-status-paused-text` | `#fbbf24` | |
| `--pf-status-paused-border` | `#a16207` | |
| `--pf-status-error-bg` | `#1a0004` | |
| `--pf-status-error-text` | `#ff3344` | |
| `--pf-status-error-border` | `#991b25` | |
| `--pf-status-idle-bg` | `#0a120a` | |
| `--pf-status-idle-text` | `#4ade80` | |
| `--pf-status-idle-border` | `#1a2e1a` | |
| `--pf-focus-ring` | `rgba(0, 255, 65, 0.6)` | Heavy phosphor glow |
| `--pf-focus-ring-offset` | `#000000` | |
| `--pf-hover-overlay` | `rgba(0, 255, 65, 0.08)` | |
| `--pf-active-overlay` | `rgba(0, 255, 65, 0.15)` | |
| `--pf-selection-bg` | `#00ff41` | |
| `--pf-selection-text` | `#000000` | |
| `--pf-skeleton-bg` | `#0a120a` | |
| `--pf-skeleton-bg-alt` | `#102010` | |
| `--pf-skeleton-accent` | `#1a2e1a` | |
| `--pf-glow-accent` | `0 0 16px rgba(0, 255, 65, 0.45), 0 0 4px rgba(0, 255, 65, 0.25)` | Strong CRT halo |
| `--pf-glow-success` | `0 0 16px rgba(0, 255, 65, 0.45)` | |
| `--pf-glow-warning` | `0 0 16px rgba(251, 191, 36, 0.40)` | |
| `--pf-glow-error` | `0 0 16px rgba(255, 51, 68, 0.45)` | |
| `color-scheme` | `dark` | |

### Matrix-Only Effects (opt-in)

These are layered on top of the variable contract, applied via the `[data-theme="matrix"]` selector:

- **CRT scanlines** (opt-in via `.matrix-scanlines` class) — `repeating-linear-gradient` overlay at 2px stripe, 15% black.
- **Phosphor text-shadow on headings** — `text-shadow: 0 0 10px rgba(0, 255, 65, 0.5), 0 0 20px rgba(0, 255, 65, 0.3)`.
- **Glow on focused inputs** — focus ring is augmented with `0 0 15px rgba(0, 255, 65, 0.2)` halo.

All Matrix effects respect `prefers-reduced-motion` and degrade to flat color when reduced.

---

## Theme 4 — Blueprint ("Schematic")

**My creative choice.** Inspired by architectural and mechanical engineering blueprints — deep cyanotype paper, white technical line work, drafting-pencil yellow annotations. Fills a real gap in the palette: none of the other three themes use a cool blue substrate, and Blueprint is what a CAD operator would feel at home in.

Why it complements the other three:
- **vs. Light**: cool/dark, not warm/bright
- **vs. PrintFarmer Dark**: brighter, with an aqua-cyan accent instead of teal
- **vs. Matrix**: a deep-color palette (not pure black) and uses sans body type, not mono

| Token | Hex | Notes |
|---|---|---|
| `--pf-bg-0` | `#0c1a2e` | Blueprint paper (deep navy-cyanotype) |
| `--pf-bg-1` | `#102137` | Cards, primary surfaces |
| `--pf-bg-2` | `#15293f` | Inset, hover surfaces |
| `--pf-panel` | `#0e1d33` | Side panels |
| `--pf-card-bg` | `#102137` | Cards |
| `--pf-sidebar-bg` | `#0a1729` | Navigation rail |
| `--pf-modal-bg` | `#13243a` | Modal surface |
| `--pf-border` | `#1d3552` | Default stroke |
| `--pf-border-strong` | `#2a4d7a` | Emphasis stroke |
| `--pf-border-subtle` | `#16263e` | Hairline divider |
| `--pf-border-divider` | `#1a2f4a` | Table dividers |
| `--pf-text-primary` | `#e6f2ff` | Technical white (14.9:1 on bg-0) |
| `--pf-text-secondary` | `#9ec5e8` | Faded blue-white (7.8:1) |
| `--pf-text-tertiary` | `#6790b8` | Dim drafting blue (4.6:1) |
| `--pf-text-muted` | `#4d7299` | Annotation blue (3.2:1) |
| `--pf-text-inverse` | `#0c1a2e` | On bright accents |
| `--pf-text-on-accent` | `#0c1a2e` | Dark text on cyan |
| `--pf-accent` | `#38bdf8` | Cyan — blueprint line color |
| `--pf-accent-bg` | `#0284c7` | Solid cyan button bg |
| `--pf-accent-hover` | `#0369a1` | |
| `--pf-accent-fg` | `#ffffff` | |
| `--pf-accent-2` | `#fef08a` | Drafting-pencil yellow (annotation accent) |
| `--pf-success` | `#4ade80` | Annotation green |
| `--pf-success-bg` | `#052e16` | |
| `--pf-success-border` | `#166534` | |
| `--pf-success-fg` | `#bbf7d0` | |
| `--pf-warning` | `#fbbf24` | Yellow marker |
| `--pf-warning-bg` | `#422006` | |
| `--pf-warning-border` | `#a16207` | |
| `--pf-warning-fg` | `#fef08a` | |
| `--pf-error` | `#f87171` | Red marker |
| `--pf-error-bg` | `#3a0a0a` | |
| `--pf-error-border` | `#991b1b` | |
| `--pf-error-fg` | `#fecaca` | |
| `--pf-info` | `#38bdf8` | Info = primary cyan |
| `--pf-info-bg` | `#082f49` | |
| `--pf-info-border` | `#0369a1` | |
| `--pf-info-fg` | `#bae6fd` | |
| `--pf-status-online-bg` | `#052e16` | |
| `--pf-status-online-text` | `#86efac` | |
| `--pf-status-online-border` | `#166534` | |
| `--pf-status-offline-bg` | `#3a0a0a` | |
| `--pf-status-offline-text` | `#fca5a5` | |
| `--pf-status-offline-border` | `#7f1d1d` | |
| `--pf-status-printing-bg` | `#082f49` | |
| `--pf-status-printing-text` | `#7dd3fc` | |
| `--pf-status-printing-border` | `#0369a1` | |
| `--pf-status-paused-bg` | `#422006` | |
| `--pf-status-paused-text` | `#fcd34d` | |
| `--pf-status-paused-border` | `#a16207` | |
| `--pf-status-error-bg` | `#3a0a0a` | |
| `--pf-status-error-text` | `#fca5a5` | |
| `--pf-status-error-border` | `#991b1b` | |
| `--pf-status-idle-bg` | `#15293f` | |
| `--pf-status-idle-text` | `#9ec5e8` | |
| `--pf-status-idle-border` | `#2a4d7a` | |
| `--pf-focus-ring` | `rgba(56, 189, 248, 0.55)` | Cyan halo |
| `--pf-focus-ring-offset` | `#0c1a2e` | |
| `--pf-hover-overlay` | `rgba(56, 189, 248, 0.06)` | |
| `--pf-active-overlay` | `rgba(56, 189, 248, 0.12)` | |
| `--pf-selection-bg` | `#0284c7` | |
| `--pf-selection-text` | `#ffffff` | |
| `--pf-skeleton-bg` | `#15293f` | |
| `--pf-skeleton-bg-alt` | `#1a2f4a` | |
| `--pf-skeleton-accent` | `#2a4d7a` | |
| `--pf-glow-accent` | `0 0 14px rgba(56, 189, 248, 0.30)` | Cyan line glow |
| `--pf-glow-success` | `0 0 12px rgba(74, 222, 128, 0.20)` | |
| `--pf-glow-warning` | `0 0 12px rgba(251, 191, 36, 0.25)` | |
| `--pf-glow-error` | `0 0 12px rgba(248, 113, 113, 0.25)` | |
| `color-scheme` | `dark` | |

### Blueprint-Only Effects (opt-in)

- **Grid overlay** (opt-in via `.blueprint-grid` class) — `background-image` with a 24px cyan-line grid at 6% opacity, mimicking blueprint paper.
- **Dotted technical underlines** on links — `text-decoration: dotted underline`.

Like Matrix, all Blueprint effects respect `prefers-reduced-motion`.

---

## Component Patterns

Structural specifications for the canonical components. Visual values come from the tokens — these patterns describe **shape**, **rhythm**, and **states**, not pigment.

### Buttons

- **Variants**: `primary`, `secondary`, `danger`, `success`, `subtle`, `ghost`, `link`.
- **Sizes**: `sm = 28px` height, `md = 36px` height, `lg = 44px` height. `lg` is the minimum mobile touch target.
- **Radius**: `--pf-radius-sm` (4px).
- **Padding**: `sm = px-3`, `md = px-4`, `lg = px-6`.
- **Icon support**: `iconLeft`, `iconRight`, `iconCenter` (icon-only). Gap between icon and text is `8px`.
- **States**: idle / hover (subtle bg shift + `--pf-shadow-sm`) / active (no shift, depressed look) / focus-visible (2px ring `--pf-focus-ring` with 2px offset) / disabled (`opacity: 0.5`, no pointer events) / loading (replace icon with spinner, retain width).
- **Transition**: `transition-all duration-150 ease-out` on hover/active.

### Inputs / Selects / Textareas

- Height: `40px` standard, `32px` compact.
- Radius: `--pf-radius-sm` (4px).
- Padding: `px-3`.
- Border: `1px solid --pf-control-border`. Hover → `--pf-control-border-hover`. Focus → `--pf-control-border-focus` + 2px ring.
- Placeholder: `--pf-control-placeholder`, 70% opacity.
- Disabled: `--pf-control-disabled-bg`, `--pf-control-disabled-text`, no pointer.
- Invalid: `--pf-validation-error-border`, 2px error ring on focus.
- Data inputs (numbers, IPs, MACs) use `--pf-font-mono`.

### Cards

- Background: `--pf-card-bg`.
- Border: `1px solid --pf-border`.
- Radius: `--pf-radius-md` (6px).
- Padding: `p-4` compact, `p-6` comfortable.
- Shadow: `--pf-shadow-xs` at rest, `--pf-shadow-sm` on hover (only for interactive cards).
- Sub-regions: `Card.Header` (border-bottom), `Card.Body` (flex content), `Card.Footer` (border-top, justify-end actions).
- **No gradients on cards** by default. Backgrounds are flat color. (The previous gradient cards were inconsistent across themes.)

### Badges / Status Pills

- Height: `20px` (sm), `24px` (md).
- Radius: `--pf-radius-xs` (2px) for status pills, `--pf-radius-full` for tag chips.
- Padding: `px-2 py-0.5` sm, `px-3 py-1` md.
- Typography: `text-2xs`, weight 600, `uppercase`, `letter-spacing: 0.04em`.
- Always uses the `{bg, text, border}` triple for its semantic role.
- Icon prefix for status: `●` (online), `○` (offline), `▶` (printing), `❚❚` (paused), `!` (error). **Never color-only.**

### Modals

- Background: `--pf-modal-bg`.
- Radius: `--pf-radius-md`.
- Shadow: `--pf-shadow-lg`.
- Overlay: `rgba(0, 0, 0, 0.65)` with `backdrop-filter: blur(4px)`.
- Sizes: `sm = 400px`, `md = 560px`, `lg = 720px`, `xl = 960px`, `full = 95vw / 95vh`.
- Header: title left, close button (32×32, ghost variant) right. Border-bottom.
- Body: scrollable, `max-h-[70vh]`.
- Footer: right-aligned button row, border-top.
- Focus trap is mandatory. Initial focus to first interactive in body; `Escape` closes.

### Tables

- Row height: `40px` default.
- Header: `--pf-bg-2` background, `text-xs uppercase tracking-wide` typography, sortable columns show arrow on hover.
- Cells: `px-3 py-2`, `--pf-border-divider` between rows.
- Striped: alternating `--pf-bg-1` / `--pf-bg-2` (optional, theme-aware).
- Hover row: `--pf-hover-overlay`.
- Numeric columns right-aligned, use `--pf-font-mono`, `font-variant-numeric: tabular-nums`.
- Always use `<table>` with `<th scope="col">` and `<th scope="row">` for screen reader compatibility.

### Progress Bars

- Height: `8px` default, `4px` thin, `12px` thick.
- Radius: `--pf-radius-full`.
- Track: `--pf-bg-2`.
- Fill: semantic color (`--pf-accent` default, `--pf-success` complete, `--pf-warning` alert, `--pf-error` failed).
- Indeterminate: shimmer animation (1.2s linear infinite) — respects reduced motion.
- ARIA: `role="progressbar"`, `aria-valuenow`, `aria-valuemin`, `aria-valuemax`, `aria-label`.

### Tabs

- Active: underline `2px` in `--pf-accent`, text `--pf-text-primary`.
- Inactive: no underline, text `--pf-text-secondary`, hover → `--pf-text-primary`.
- Focus: 2px ring on the tab itself.
- Container: border-bottom `--pf-border`.

### Alerts / Banners

- Background: semantic `*-bg`, border-left `4px` in semantic `*` color, text in `*-fg`.
- Padding: `p-4`.
- Icon: leading 20×20 in semantic color.
- Dismissible alerts include a ghost close button at top-right.

---

## Layout Grid System

PrintFarmer uses a **persistent sidebar + content** layout. The grid is responsive but the sidebar is always present on `lg+`.

### Breakpoints (Tailwind v4 defaults — do not customize)

| Token | Width | Use |
|---|---|---|
| `sm` | ≥ 640px | Phones in landscape |
| `md` | ≥ 768px | Tablets portrait |
| `lg` | ≥ 1024px | Desktops, sidebar appears |
| `xl` | ≥ 1280px | Widescreen, multi-column dashboards |
| `2xl` | ≥ 1536px | Ultra-wide displays |

### App Shell

```text
┌──────────────────────────────────────────────────────────┐
│  Top bar (56px, --pf-sidebar-bg, optional on mobile)    │
├─────────┬────────────────────────────────────────────────┤
│         │                                                │
│ Sidebar │  Main content                                  │
│  (240   │  (max-w-screen-2xl, px-6 py-6, gap-6)         │
│   px)   │                                                │
│         │                                                │
└─────────┴────────────────────────────────────────────────┘
```

- **Sidebar**: `240px` fixed width on `lg+`, collapsible to `64px` (icon-only). Hidden behind a sheet on mobile, triggered by a hamburger in the top bar.
- **Main content area**: `max-w-screen-2xl` (1536px), centered, with `px-4 py-4` on mobile and `px-6 py-6` on `md+`.
- **Page header**: `PageTemplate` component — title (display font, `text-2xl`), optional subtitle (text-sm secondary), optional leading icon, optional trailing actions slot.
- **Section gaps**: `gap-6` (24px) between major sections in main content.

### Card Grids

- Default: `grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4`.
- Printer cards specifically: `lg:grid-cols-2 xl:grid-cols-3` (printers need more width for camera + status).
- Detail pages: `grid lg:grid-cols-[1fr_360px] gap-6` (main + right sidebar).

### Z-Index Scale

| Token | Value | Use |
|---|---|---|
| `--pf-z-base` | 0 | Default content |
| `--pf-z-sticky` | 10 | Sticky table headers |
| `--pf-z-sidebar` | 20 | Collapsed sidebar / mobile drawer |
| `--pf-z-dropdown` | 30 | Select menus, popovers |
| `--pf-z-overlay` | 40 | Modal backdrop |
| `--pf-z-modal` | 50 | Modal content |
| `--pf-z-toast` | 60 | Toast notifications |
| `--pf-z-tooltip` | 70 | Tooltips (above everything) |

---

## Motion & Animation

Industrial UIs are restrained. Motion communicates state change, not personality.

### Duration Tokens

| Token | Value | Use |
|---|---|---|
| `--pf-duration-fast` | `120ms` | Hover state changes, focus ring appearance |
| `--pf-duration-base` | `200ms` | Default transitions, button state, color shifts |
| `--pf-duration-slow` | `320ms` | Modal open/close, drawer slide, accordion expand |
| `--pf-duration-deliberate` | `500ms` | Toast enter/exit, page transitions (rare) |

### Easing Tokens

| Token | Value | Use |
|---|---|---|
| `--pf-ease-out` | `cubic-bezier(0.22, 1, 0.36, 1)` | Element entering (modals, toasts) |
| `--pf-ease-in` | `cubic-bezier(0.64, 0, 0.78, 0)` | Element leaving |
| `--pf-ease-inout` | `cubic-bezier(0.65, 0, 0.35, 1)` | Default for state changes |
| `--pf-ease-linear` | `linear` | Progress bars, spinners |

### What Animates

- ✅ Hover state changes (bg, border, shadow): `--pf-duration-fast`, `--pf-ease-out`
- ✅ Focus ring fade-in: `--pf-duration-fast`
- ✅ Modal/drawer enter+leave: `--pf-duration-slow`, `--pf-ease-out` / `--pf-ease-in`
- ✅ Toast enter+leave: `--pf-duration-deliberate`, `--pf-ease-out`
- ✅ Progress bar fill: animated continuously, `--pf-ease-linear`
- ✅ Skeleton shimmer: 1.1s pulse, infinite
- ✅ Spinner rotation: 0.8s linear infinite

### What Does NOT Animate

- ❌ Page transitions (instant)
- ❌ Tab switches (instant content swap)
- ❌ Bouncy springs, overshoot easings
- ❌ Parallax, mouse-tracking, hover-tilt effects
- ❌ Auto-playing carousels
- ❌ Long fade-ins on page load

### Reduced Motion

All animation must check `@media (prefers-reduced-motion: reduce)`:

```css
@media (prefers-reduced-motion: reduce) {
  *, ::before, ::after {
    animation-duration: 0.01ms !important;
    transition-duration: 0.01ms !important;
  }
}
```

Skeleton pulses degrade to static opacity. CRT scanlines and glow halos in the Matrix theme are removed entirely under reduced motion.

---

## Iconography

PrintFarmer uses **Material Design Icons (MDI)**, imported from `@/common/components/icons/MdiIcons`. MDI was chosen for its industrial neutrality, consistent stroke width, and exhaustive coverage (8000+ icons including 3D-printing-specific glyphs).

### Sizing

| Size | Pixel | Use |
|---|---|---|
| `xs` | 14px | Inline with `text-xs` |
| `sm` | 16px | Inline with `text-sm`, button icons (sm) |
| `md` | 20px | Default standalone, button icons (md/lg) |
| `lg` | 24px | Card titles, section headers |
| `xl` | 32px | Page headers, empty-state hero |
| `2xl` | 48px | Empty states, splash |

### Usage Rules

1. **Always render with `role="img"` and an `aria-label`** unless the icon is purely decorative (then `aria-hidden="true"`).
2. **Decorative icons** that accompany a text label must use `aria-hidden="true"` — the text already conveys the meaning.
3. **Icon-only buttons** must include an `aria-label` matching the visual purpose, and a `<title>` tooltip on hover.
4. **Stroke**: use the filled variant for status (online/offline indicators) and the outlined variant for actions (edit, delete, add).
5. **Color**: inherit from `currentColor`. Status icons may override with semantic colors (`text-pf-success`, `text-pf-error`).
6. **Never** mix MDI with another icon set in the same surface. If a glyph is missing, request its addition rather than reaching for Heroicons or FontAwesome.

---

## Accessibility Floor

Non-negotiable minimums. Every PR is reviewed against this list.

### Contrast (WCAG 2.2 AA)

- **Text ≥ 18.66px or ≥ 14px bold**: 3.0:1 minimum.
- **All other text**: 4.5:1 minimum. Aim for 7.0:1 (AAA) on primary body text.
- **UI components and graphics**: 3.0:1 minimum (icons, focus rings, borders, status pills).
- **Disabled controls are exempt** but should still be visually distinct.

### Focus

- **Every interactive element** must show a focus indicator. The default is a 2px ring in `--pf-focus-ring` with 2px offset against `--pf-focus-ring-offset`.
- Focus rings appear on `:focus-visible`, not `:focus`, so mouse users don't see them on click.
- Focus order follows DOM order. No `tabindex` greater than 0.
- Modal/dialog focus is trapped until closed. Initial focus → first interactive child. `Escape` closes.

### Touch Targets

- **Minimum 44 × 44 CSS pixels** for all interactive elements on mobile (iOS HIG, Android Material). Use the `lg` button size or add `min-h-[44px] min-w-[44px]` to icon buttons.
- Stacked controls: minimum `8px` vertical gap to prevent fat-finger errors.

### Screen Reader Support

- Use semantic HTML: `<button>`, `<a>`, `<table>`, `<th>`, `<nav>`, `<main>`, `<section>` over generic `<div>`.
- Form controls always have `<label>` (or `aria-label` / `aria-labelledby`).
- Form errors use `aria-invalid="true"` and `aria-describedby` pointing to the error message.
- Live regions (`aria-live="polite"` or `"assertive"`) for toast notifications and status updates that change after page load.
- Skip link (`Skip to main content`) as first focusable element in the page.

### Color Independence

- **Never use color as the only means** of conveying information. Status pills include an icon. Errors include text. Required fields include both an asterisk *and* `aria-required="true"`.

### Motion

- All animations respect `prefers-reduced-motion: reduce`.
- No flashing content faster than 3 Hz (seizure risk).
- Auto-playing animations longer than 5 seconds must have a pause control.

### Keyboard

- Every action achievable by mouse must be achievable by keyboard.
- Composite components (tabs, listboxes, comboboxes, menus) follow ARIA Authoring Practices for keyboard navigation (Arrow keys, Home, End, Enter, Escape).

---

## CSS Variable Contract

This is the **authoritative API** every theme file must implement. Phase 2 will generate a TypeScript type from this list and a lint rule enforcing that every theme defines every token.

### Typography

```text
--pf-font-sans                   /* Body, UI, navigation */
--pf-font-display                /* Headlines, page titles */
--pf-font-mono                   /* Data, timestamps, code */
```

### Spacing

```text
--pf-space-0, --pf-space-1, --pf-space-2, --pf-space-3, --pf-space-4,
--pf-space-5, --pf-space-6, --pf-space-8, --pf-space-10, --pf-space-12, --pf-space-16
```

### Radii

```text
--pf-radius-none, --pf-radius-xs, --pf-radius-sm,
--pf-radius-md, --pf-radius-lg, --pf-radius-full
```

### Shadows & Glows

```text
--pf-shadow-none, --pf-shadow-xs, --pf-shadow-sm,
--pf-shadow-md, --pf-shadow-lg, --pf-shadow-xl
--pf-glow-accent, --pf-glow-success, --pf-glow-warning, --pf-glow-error
```

### Motion

```text
--pf-duration-fast, --pf-duration-base, --pf-duration-slow, --pf-duration-deliberate
--pf-ease-out, --pf-ease-in, --pf-ease-inout, --pf-ease-linear
```

### Z-Index

```text
--pf-z-base, --pf-z-sticky, --pf-z-sidebar, --pf-z-dropdown,
--pf-z-overlay, --pf-z-modal, --pf-z-toast, --pf-z-tooltip
```

### Theme Metadata

```text
--pf-theme-name                  /* String literal, e.g. 'blueprint' */
color-scheme                     /* 'light' | 'dark' */
```

### Surfaces

```text
--pf-bg-0                        /* Page background */
--pf-bg-1                        /* Cards, primary surfaces */
--pf-bg-2                        /* Inset, hover surfaces */
--pf-panel                       /* Side panels */
--pf-card-bg                     /* Card surface */
--pf-sidebar-bg                  /* Navigation rail */
--pf-modal-bg                    /* Modal surface */
```

### Borders

```text
--pf-border                      /* Default 1px stroke */
--pf-border-strong               /* Emphasis stroke */
--pf-border-subtle               /* Hairline divider */
--pf-border-divider              /* Table row dividers */
```

### Text

```text
--pf-text-primary                /* Body text, ≥ 7:1 */
--pf-text-secondary              /* Default UI text, ≥ 4.5:1 */
--pf-text-tertiary               /* Helper text, ≥ 4.5:1 */
--pf-text-muted                  /* Captions, ≥ 3:1, non-essential only */
--pf-text-inverse                /* Text on bright accent surfaces */
--pf-text-on-accent              /* Text on accent button bg */
```

### Accent

```text
--pf-accent                      /* Primary brand color (text/icon use) */
--pf-accent-bg                   /* Accent button background */
--pf-accent-hover                /* Accent hover */
--pf-accent-fg                   /* Text on accent bg */
--pf-accent-2                    /* Secondary accent (links, complementary) */
```

### Semantic (each as a `{bg, border, fg, base}` group)

```text
--pf-success, --pf-success-bg, --pf-success-border, --pf-success-fg
--pf-warning, --pf-warning-bg, --pf-warning-border, --pf-warning-fg
--pf-error,   --pf-error-bg,   --pf-error-border,   --pf-error-fg
--pf-info,    --pf-info-bg,    --pf-info-border,    --pf-info-fg
```

### Status (operator-facing printer/job states)

```text
--pf-status-online-{bg,text,border}
--pf-status-offline-{bg,text,border}
--pf-status-printing-{bg,text,border}
--pf-status-paused-{bg,text,border}
--pf-status-error-{bg,text,border}
--pf-status-idle-{bg,text,border}
```

### Controls (Input, Select, Textarea)

```text
--pf-control-bg
--pf-control-border
--pf-control-border-hover
--pf-control-border-focus
--pf-control-text
--pf-control-placeholder
--pf-control-disabled-bg
--pf-control-disabled-text
```

### Buttons (each variant as a `{bg, hover, active, text, border}` group)

```text
--pf-button-primary-{bg,hover,active,text,border}
--pf-button-secondary-{bg,hover,active,text,border}
--pf-button-danger-{bg,hover,active,text,border}
--pf-button-success-{bg,hover,active,text,border}
```

### Validation

```text
--pf-validation-error-{bg,border,text}
--pf-validation-success-{bg,border,text}
--pf-validation-warning-{bg,border,text}
```

### Feedback

```text
--pf-focus-ring                  /* Color + alpha for ring */
--pf-focus-ring-offset           /* Solid bg color behind ring */
--pf-focus-ring-width            /* Typically 2px */
--pf-hover-overlay               /* Transparent overlay on hover */
--pf-active-overlay              /* Transparent overlay on active */
--pf-selection-bg                /* Text selection background */
--pf-selection-text              /* Text selection foreground */
```

### Skeleton / Loading

```text
--pf-skeleton-bg                 /* Base skeleton color */
--pf-skeleton-bg-alt             /* Alternating skeleton color */
--pf-skeleton-accent             /* Shimmer accent stripe */
```

### Domain-specific (preserve existing API)

```text
--pf-home-homed-bg               /* Axis homed indicator */
--pf-home-not-homed-bg           /* Axis not homed indicator */
```

### Total token count

**~140 CSS custom properties per theme.** Phase 2 will lint that every theme defines all of them and that no UI code references any color outside this contract.

---

## Migration Notes for Phase 2

The Phase 2 implementer should be aware of these deltas between the existing themes and this spec:

1. **Fonts change**. Inter and Bebas Neue are out; IBM Plex Sans, Space Grotesk, and JetBrains Mono are in. The Google Fonts `@import` in `index.css` must be updated and `--font-inter` / `--font-bebas` tokens replaced with `--pf-font-sans` / `--pf-font-display` / `--pf-font-mono`.
2. **Token rename**. Existing tokens like `--pf-text-light` (ambiguous) collapse into `--pf-text-secondary`. The migration map will be authored alongside Phase 2 to keep diffs surgical.
3. **Gradients deprecated**. The 14 `--pf-gradient-*` tokens are removed. Cards and buttons go to flat color. (They look inconsistent across themes anyway.) Where depth is needed, use `--pf-shadow-*` instead.
4. **New tokens**. `--pf-space-*`, `--pf-radius-*`, `--pf-shadow-*`, `--pf-duration-*`, `--pf-ease-*`, `--pf-z-*`, `--pf-glow-*`, `--pf-modal-bg`, status triples for `printing`/`paused`/`error`/`idle`, `--pf-text-inverse`, `--pf-text-on-accent`, `--pf-accent-fg`, `--pf-selection-*`, `--pf-validation-warning-*`, and the full `info` semantic group are all new.
5. **`forge` and `github-dark` themes**. Both are out of the supported set. Forge can stay as an undocumented "extra" theme during transition; `github-dark` is replaced by `printfarmer-dark` as the default and should be deleted.
6. **Default theme**. Currently the default (`:root:not([data-theme])`) is `github-dark`. The new default is `printfarmer-dark`.

---

## Appendix — Quick Reference Card

For day-to-day implementation. Pin this in your editor.

```text
SURFACE     bg-pf-bg-0 / bg-pf-bg-1 / bg-pf-bg-2 / bg-pf-card / bg-pf-modal
TEXT        text-pf-text-primary / -secondary / -tertiary / -muted
BORDER      border border-pf-border (-strong | -subtle)
ACCENT      text-pf-accent / bg-pf-accent-bg
RADIUS      rounded-xs (2) / rounded-sm (4) / rounded-md (6) / rounded-lg (8)
SHADOW      shadow-pf-xs / -sm / -md / -lg
SPACING     gap-2 / p-4 / px-6 (always 4px grid)
FOCUS       focus-visible:ring-2 focus-visible:ring-pf-focus
FONTS       font-pf-sans (default) / font-pf-display (headings) / font-pf-mono (data)
```

---

**End of spec.** This document is the source of truth for PrintFarmer UI. Phase 2 implements; Phase 3+ extends only with team approval and a corresponding update to this file.
