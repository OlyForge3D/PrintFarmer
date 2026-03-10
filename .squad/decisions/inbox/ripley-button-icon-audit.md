# Decision: Button Icon Prop Convention Enforcement

**Author:** Ripley (Frontend Dev)
**Date:** 2025-07-17
**Status:** Proposed

## Context

Audited all ~805 `<Button>` instances across the React codebase. Found **25 true violations** where icons are rendered as inline children alongside text instead of using the `iconLeft`/`iconRight` props.

## Key Findings

- 25 violations across 15 files (most in admin pages, slicer, gcode, and webhooks features)
- Most common pattern: `<Button><Icon className="mr-2" />Text</Button>` — manual spacing hack
- 4 instances use manual loading icon conditionals instead of the `loading` prop
- Button component already provides `gap-2` via `inline-flex items-center gap-2`, making manual `flex items-center gap-2` className additions redundant

## Decision

1. All icon+text Buttons must use `iconLeft` or `iconRight` props — never inline icon children alongside text
2. Use the `loading` prop for loading states instead of conditional `<LoadingIcon>` children
3. Icon-only buttons (no text) may use inline icon children or `iconCenter`
4. `variant="unstyled"` buttons with complex card-like layouts are exempt

## Impact

- Full report at: `src/Web/ReactApp/BUTTON_AUDIT.md`
- Fixes improve consistency, reduce redundant CSS classes, and ensure proper icon wrapping/spacing via the Button component's built-in handling
