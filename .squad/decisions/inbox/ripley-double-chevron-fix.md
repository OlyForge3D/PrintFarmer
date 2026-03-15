# Decision: Fix double chevron on select dropdowns

**Author:** Ripley (Frontend)
**Date:** 2026-03-14
**Status:** Implemented

## Context

Global CSS in `controls.css` styles all `<select>` elements with `appearance: none` and a `background-image` SVG chevron. Components that add their own custom chevron overlay (e.g., `<ChevronDownIcon>`) produce two visible chevrons.

## Decision

Add Tailwind `bg-none` (`background-image: none`) to any `<select>` element that has a custom chevron overlay rendered alongside it. This suppresses the global CSS chevron while keeping the component's own icon.

## Affected Files

- `Select.tsx` (core UI component — most widely used)
- `ThemeToggle.tsx` (dropdown variant)
- `SettingRow.tsx` (OrcaSlicer SelectControl)

## Team Impact

**Any future component** that wraps a raw `<select>` and adds a custom dropdown arrow MUST include `bg-none` in the select's className. Alternatively, use the `Select` component from `@/common/components/ui` which now handles this correctly.
