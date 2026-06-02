# Decision: Theme-specific Body Fonts for PrintFarmer Themes

**Author:** Ripley (Frontend Dev)
**Date:** 2026-06-02T09:00:01-07:00
**Requested by:** Jeff Papiez
**Status:** Pending team review
**Scope:** React frontend theming (`src/Web/ReactApp/`)

## Summary

Assigned a distinct body font to each supported theme so the visual identity changes with the selected theme instead of only changing colors. The implementation follows the existing Matrix pattern: each theme owns a `body[data-theme="X"], html[data-theme="X"] body` override in its theme stylesheet, while font assets load centrally from `index.html`.

## Key Decisions

### 1. Keep font ownership inside each theme file

Each theme now declares its own body font override in the matching CSS file instead of introducing new shared font tokens or runtime logic. This keeps the change surgical and mirrors the precedent already established by `matrix.css`.

### 2. Choose fonts by theme personality

| Theme | Font | Why |
|---|---|---|
| Dark | **Inter** | Clean, neutral, technical default for mission-control UI |
| Light | **Nunito Sans** | Friendly, readable daylight mode without feeling decorative |
| Blueprint | **DM Mono** | Drafting-style mono that reinforces schematic/CAD vibes |
| RatOS | **Rajdhani** | Geometric, tech-forward letterforms that fit neon firmware styling |
| Voron | **Chakra Petch** | Sharp, industrial shapes that feel aggressive but still readable as body copy |
| Farm | **Merriweather Sans** | Warm, humanist sans that softens the harvest palette |
| Matrix | **JetBrains Mono** | Existing mono override remains unchanged |

### 3. Load theme fonts alongside base fonts in `index.html`

All selected Google Fonts are loaded through the existing Google Fonts `<link>` in `src/Web/ReactApp/index.html`. Centralizing imports avoids scattered CSS `@import` usage and preserves the current font-loading pattern.

## What This Decision Affects

- Theme identity now includes typography, not just palette
- `index.html` carries additional Google Font families for theme switching
- Each theme stylesheet contains a body font override block matching the Matrix selector pattern

## What This Decision Does Not Affect

- No component-level typography tokens or Tailwind font utilities changed
- No backend or mobile changes
- No theme-selection logic changed

## Files

- `src/Web/ReactApp/index.html`
- `src/Web/ReactApp/src/design-system/themes/dark.css`
- `src/Web/ReactApp/src/design-system/themes/light.css`
- `src/Web/ReactApp/src/design-system/themes/blueprint.css`
- `src/Web/ReactApp/src/design-system/themes/ratos.css`
- `src/Web/ReactApp/src/design-system/themes/voron.css`
- `src/Web/ReactApp/src/design-system/themes/farm.css`
- `.squad/agents/ripley/history.md`
- `.squad/decisions/inbox/ripley-theme-fonts.md`
