# Directive: No Inline CSS Styles in React Components

**Date:** 2026-03-18
**Source:** User directive
**Priority:** High

## Rule

When adding or modifying React UI components, **never use inline CSS styles** (`style={{ ... }}` or `style={variable}`). All styling must use **Tailwind CSS utility classes** exclusively.

## Rationale

- Inline styles bypass Tailwind's design token system (`pf-*` tokens), breaking visual consistency
- Inline styles can't be overridden by Tailwind's responsive/dark-mode variants
- Inline styles increase bundle size and reduce cacheability compared to atomic CSS classes
- Microsoft Edge Tools and linters flag `no-inline-styles` as a warning

## Exception

The **only** acceptable use of inline styles is for truly dynamic values that cannot be expressed as Tailwind classes — for example, a color hex code from an API response (e.g., spool color `#FF5733`). In these cases:
- Add a code comment explaining why inline style is necessary
- Keep the inline style to the absolute minimum (e.g., only `backgroundColor`)

## Examples

```tsx
// ❌ BAD: inline style for static layout
<div style={{ padding: '16px', marginTop: '8px' }}>

// ✅ GOOD: Tailwind utility classes
<div className="p-4 mt-2">

// ❌ BAD: inline style for a known color
<span style={{ color: 'red' }}>Error</span>

// ✅ GOOD: Tailwind token
<span className="text-pf-error">Error</span>

// ✅ ACCEPTABLE: dynamic API-driven color (with comment)
{/* Dynamic spool color from Spoolman API — can't use Tailwind class */}
<span style={{ backgroundColor: printer.spoolInfo.colorHex }} />
```
