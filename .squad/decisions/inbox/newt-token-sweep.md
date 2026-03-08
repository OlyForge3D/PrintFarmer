# Design Token Sweep Decision

**Author:** Newt (Designer, Industrial UI)
**Date:** 2026-03-11
**Status:** IMPLEMENTED

## Decision

All React component files now use `pf-*` design tokens exclusively for colors. Raw Tailwind color classes (`gray-*`, `red-*`, `blue-*`, etc.) are no longer used in UI chrome code.

## Exception: colorFamilies.ts

`src/Web/ReactApp/src/common/utils/colorFamilies.ts` retains 12 raw Tailwind color classes (`bg-red-500`, `bg-blue-500`, etc.) because these represent **literal filament material colors** for spool swatches, not UI chrome. They should NOT use design tokens.

## Exception: bg-black Overlays

`bg-black/50`, `bg-black/60`, `bg-black/75` used for modal backdrop dimming are kept as-is. True black with opacity is the universal overlay convention.

## Going Forward

- Any new UI code must use `pf-*` tokens — never raw Tailwind color classes
- If new status colors are needed beyond error/success/warning/accent, add them as new `pf-*` tokens in `theme.css` and `tailwind.config.js`
- Consider adding an ESLint rule (`local/pf-no-raw-colors`) to enforce this automatically
