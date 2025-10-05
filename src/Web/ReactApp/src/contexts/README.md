Fast-refresh pattern for React Contexts
=====================================

Keep context files minimal and focused so they play nicely with React Fast Refresh. Follow these guidelines:

- Context files (e.g., ThemeContext.tsx, AuthContext.tsx)
  - Export only the React context object(s) and the Provider component(s).
  - Avoid exporting custom hooks or helper functions from the same file. Exported hooks can break the react-refresh rule that only components should be exported from files that render JSX.

- Hooks and helpers (e.g., ThemeHooks.ts, AuthHooks.ts)
  - Place custom hooks, helpers, and any non-JSX logic in separate files.
  - Export hooks from these files and import them where needed (components, pages, tests).

- Tests
  - When rendering components that consume context or react-query hooks in tests, wrap them with the required providers (AuthProvider, QueryClientProvider, etc.).

- Rationale
  - The react-refresh ESLint rules (and the underlying hot-reload mechanism) assume files exporting components may be reloaded. Mixing non-component exports that capture state or hooks in the same module can cause stale closures or ESLint rule violations such as react-refresh/only-export-components.

```markdown
Fast-refresh pattern for React Contexts
=====================================

Keep context files minimal and focused so they play nicely with React Fast Refresh. Follow these guidelines:

- Context files (e.g., ThemeContext.tsx, AuthContext.tsx)
  - Export only the React context object(s) and the Provider component(s).
  - Avoid exporting custom hooks or helper functions from the same file. Exported hooks can break the react-refresh rule that only components should be exported from files that render JSX.

- Hooks and helpers (e.g., ThemeHooks.ts, AuthHooks.ts)
  - Place custom hooks, helpers, and any non-JSX logic in separate files.
  - Export hooks from these files and import them where needed (components, pages, tests).

- Tests
  - When rendering components that consume context or react-query hooks in tests, wrap them with the required providers (AuthProvider, QueryClientProvider, etc.).

- Rationale
  - The react-refresh ESLint rules (and the underlying hot-reload mechanism) assume files exporting components may be reloaded. Mixing non-component exports that capture state or hooks in the same module can cause stale closures or ESLint rule violations such as react-refresh/only-export-components.

- Example
  - Keep `src/contexts/AuthContext.tsx` to:
    - export AuthContext and AuthProvider
  - Put `useAuth` in `src/contexts/AuthHooks.ts` and import `useAuth` in your components.

Small, consistent separation reduces lint noise and avoids subtle fast-refresh bugs.

If you'd like, I can include a tiny automated test template that shows the required provider wrappers for components that use Auth and react-query.

```

---

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
