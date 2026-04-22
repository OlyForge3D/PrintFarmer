# Design Decision: OrcaSlicer Section SVG Icons

**Author:** Newt (Designer)  
**Bead:** PFarm1-98f1  
**Date:** 2025-07-15

## Summary

All 118 OrcaSlicer section/tab SVG icons are present in `src/Web/ReactApp/public/icons/orca/` and verified against `orcaSettingsMetadata.json`. An `index.json` manifest was created for programmatic access. Hardcoded colors were converted to CSS custom properties with fallbacks.

## Icon Inventory

- **75** icons referenced directly in metadata tabs/sections
- **115** icons listed in the metadata `icons` key
- **118** total unique SVGs on disk (superset covers both)
- **0** missing icons

## Color Theming

All 118 SVGs use a consistent two-tone color scheme from OrcaSlicer:

| Role | Original Color | CSS Variable | Usage |
|---|---|---|---|
| Structural | `#949494` (gray) | `--orca-icon-secondary` | Borders, outlines, dial marks |
| Accent | `#009688` (teal) | `--orca-icon-accent` | Highlighted elements, primary paths |

Colors were converted from hardcoded hex values to `var(--orca-icon-secondary, #949494)` and `var(--orca-icon-accent, #009688)` in inline `style` attributes. Fallback values preserve the original OrcaSlicer appearance.

**Theming behavior depends on how SVGs are loaded:**
- `<img src="...">` — Isolated context; fallback values used (original colors, works on dark backgrounds)
- Inline SVG / `dangerouslySetInnerHTML` — Parent CSS variables override; full theme control

Both colors have sufficient contrast on dark backgrounds (#1a1a2e or similar), so the fallback path is dark-theme safe.

## ViewBox Sizes

SVGs have three viewBox sizes. All are square, so they scale uniformly:

| viewBox | Count |
|---|---|
| `0 0 18 18` | 62 |
| `0 0 24 24` | 31 |
| `0 0 16 16` | 25 |

**Decision: Not normalized.** Since all viewBoxes are square, the rendering container controls display size. Modifying coordinate spaces risks distorting the hand-crafted paths. The `index.json` includes viewBox metadata so consumers can handle sizing if needed.

## Files Created/Modified

- **Modified:** 118 SVG files (color → CSS variable conversion)
- **Created:** `src/Web/ReactApp/public/icons/orca/index.json` (icon manifest)
