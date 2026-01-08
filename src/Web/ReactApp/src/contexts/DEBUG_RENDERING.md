Debug rendering and renderUnknown (developer note)

This short doc explains how to safely render untyped payloads in the UI for developer/debug purposes.

Why
- Rendering `unknown` or raw objects directly inside JSX can cause TypeScript build-time errors (unknown not assignable to ReactNode) and can leak internal data to end users.

What to use
- Use `src/utils/renderUnknown.tsx` to safely render unknown values.
  - null/undefined -> null
  - React element -> returned as-is
  - primitive -> string
  - object/array -> pretty-printed JSON inside a `<pre>` with overflow handling

Guidelines
- Gate debug UI behind `window.PrintFarmerDebug.*` to avoid showing debug information to normal users.
- Prefer small debug payloads (timestamps, ids, or minimal metadata) when storing things on `window`. If you must store objects, consider stringifying them defensively on write.
- Example:

```tsx
{window.PrintFarmerDebug?.printerCardDisplay && (
  <div className="text-xs text-pf-text-tertiary">{renderUnknown({ printer, realtimeStatus })}</div>
)}
```

Cleanup
- Remove temporary no-op imports like `void renderUnknown;` once actual `renderUnknown` usages are in place.

Testing
- Add unit tests for `renderUnknown` to cover primitives, objects/arrays, null/undefined, and React elements to prevent regressions.

If you'd like, I can also add an ESLint rule or codemod to flag `JSON.stringify` inside JSX so these patterns are easier to find and fix.
