import { createContext, useContext, useState, type ReactNode } from 'react';
import { createBrowserRouter, RouterProvider } from 'react-router';

/**
 * Owns the application's router composition.
 *
 * Extracted from `App` so tests can exercise the exact composition the app ships
 * instead of standing in a `MemoryRouter`/`createMemoryRouter` — a stand-in
 * silently changes which react-router capabilities are available, which is how
 * the dirty-draft navigation guard shipped inert (issue 2525).
 *
 * This is a data router because `useBlocker` — the mechanism that guards unsaved
 * settings drafts against navbar links and browser Back/Forward — only works
 * under one. The app's declarative `<Routes>` tree is hosted unchanged as a
 * descendant of the single splat route below, so route definitions, lazy
 * boundaries, and `useNavigate`/`<Link>` semantics are all untouched; the only
 * thing that changes is that a data-router context now exists above them.
 */
const AppRouterChildrenContext = createContext<ReactNode>(null);

/**
 * Renders whatever `AppRouterProvider` was last given.
 *
 * The router is built once, so its route definition cannot hold the `children`
 * element directly — that would freeze the app subtree at its first render and
 * silently swallow every update from above the provider. Reading the children
 * from context instead keeps the route element stable while the subtree it
 * renders stays live.
 */
function AppRouterChildren() {
  return <>{useContext(AppRouterChildrenContext)}</>;
}

export function AppRouterProvider({ children }: { children: ReactNode }) {
  // Created once per mount: a module-scope router would leak history state
  // between tests and survive HMR with a stale element tree.
  const [router] = useState(() => createBrowserRouter([{ path: '*', element: <AppRouterChildren /> }]));
  return (
    <AppRouterChildrenContext.Provider value={children}>
      <RouterProvider router={router} />
    </AppRouterChildrenContext.Provider>
  );
}
