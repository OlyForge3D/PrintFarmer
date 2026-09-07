import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { createBrowserRouter, RouterProvider } from 'react-router';

type AppRouter = ReturnType<typeof createBrowserRouter>;

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

/**
 * Every router built but not yet claimed by a mounted provider.
 *
 * Building a router calls `initialize()`, which installs a `popstate` listener
 * that only `dispose()` removes — and `RouterProvider` never disposes. React
 * also invokes state initialisers speculatively under StrictMode and keeps only
 * one result, so construction cannot be assumed to happen once. Tracking each
 * instance is what makes it possible to dispose the ones React threw away;
 * otherwise they keep reacting to Back/Forward for the life of the page.
 */
const unclaimedRouters = new Set<AppRouter>();

function createAppRouter(): AppRouter {
  const router = createBrowserRouter([{ path: '*', element: <AppRouterChildren /> }]);
  unclaimedRouters.add(router);
  return router;
}

/** Dispose every instance except the one React actually kept. */
function disposeUnclaimedRouters(kept: AppRouter) {
  for (const router of unclaimedRouters) {
    if (router !== kept) router.dispose();
  }
  unclaimedRouters.clear();
}

export function AppRouterProvider({ children }: { children: ReactNode }) {
  const [router] = useState(createAppRouter);
  const disposePendingRef = useRef(false);

  useEffect(() => {
    // Any disposal scheduled by a previous cleanup is cancelled here: StrictMode
    // runs setup -> cleanup -> setup, and tearing the router down on that
    // simulated remount would leave the app holding one that no longer listens
    // to history. Only a cleanup with no setup behind it is a real unmount.
    disposePendingRef.current = false;
    disposeUnclaimedRouters(router);
    return () => {
      disposePendingRef.current = true;
      // A microtask, not a timer: it still lands after React's synchronous
      // effect flush, but cannot be stranded by a suite using fake timers.
      queueMicrotask(() => {
        if (!disposePendingRef.current) return;
        disposePendingRef.current = false;
        unclaimedRouters.delete(router);
        router.dispose();
      });
    };
  }, [router]);

  return (
    <AppRouterChildrenContext.Provider value={children}>
      <RouterProvider router={router} />
    </AppRouterChildrenContext.Provider>
  );
}
