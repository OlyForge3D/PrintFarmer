/**
 * RouteErrorBoundary — chunk-load recovery + navigation-based reset (Hicks #3/#4/#5).
 *
 * These tests exercise the failure modes the previous implementation
 * could not recover from:
 *
 *   1. A rejected `React.lazy` import. `React.lazy` caches the rejected
 *      promise on the module registry, so simply calling
 *      `setState({ hasError: false })` re-throws the same rejection on
 *      the next render — "Try Again" was effectively a no-op. We now
 *      detect the chunk-load error and offer an explicit "Reload page"
 *      button that calls `window.location.reload()` (the only reliable
 *      way to recreate the registry). No implicit reload happens.
 *
 *   2. A Vite CSS-preload rejection ("Unable to preload CSS for
 *      <url>") thrown by Vite's importAnalysisBuild plugin when a
 *      lazy route's stylesheet 404s. This is functionally a
 *      chunk-load error (the module can't be recovered without a full
 *      reload) and MUST be classified accordingly.
 *
 *   3. A stuck error surviving navigation. Before the fix, the route
 *      boundary retained its `hasError=true` state across route
 *      transitions because the Outlet element type stayed the same.
 *      We now accept a `resetKey` prop (Layout passes
 *      `location.key`) and clear the error whenever it changes.
 *
 *   4. Same-pathname navigation (`?range=day` → `?range=week`, or a
 *      hash change like `#summary`). `location.pathname` would not
 *      change between these two entries, so a `resetKey={pathname}`
 *      wiring would leave the errored boundary stuck. Layout uses
 *      `location.key` (guaranteed unique per history entry), which we
 *      verify here through a real MemoryRouter.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { lazy, Suspense, useState } from 'react';
import type { ReactElement } from 'react';
import {
  MemoryRouter,
  Routes,
  Route,
  Link,
  useLocation,
} from 'react-router';
import { RouteErrorBoundary } from '../ErrorBoundary';
import { installConsoleErrorFilter } from '@/test/consoleFilter';

describe('RouteErrorBoundary', () => {
  // Precise console.error filter (Hicks #7). The prior blanket
  // suppression hid legitimate React warnings alongside the intended
  // componentDidCatch + "The above error occurred in" noise. We now
  // whitelist only the messages we know these tests produce and fail
  // on any unexpected log.
  const consoleFilter = installConsoleErrorFilter([
    /RouteErrorBoundary caught an error:/,
    /The above error occurred in the/,
    /Consider adding an error boundary/,
    /React will try to recreate this component tree/,
    // React 19 emits the raw thrown Error as an arg of a second
    // console.error call. We allow only the exact throw strings we
    // control below.
    /render blew up/,
    /Failed to fetch dynamically imported module/,
    /Loading chunk 42 failed/,
    /Unable to preload CSS/,
    /stubborn/,
    /crash on A/,
    /crash on B/,
    /same-key blows up/,
    /query-change-blows-up/,
    /hash-change-blows-up/,
  ]);
  afterEach(() => consoleFilter.flushUnexpectedErrors());

  it('shows the standard Try Again recovery UI for non-chunk errors and clears state when clicked', async () => {
    let shouldThrow = true;
    function Flaky() {
      if (shouldThrow) {
        throw new Error('render blew up');
      }
      return <div>recovered content</div>;
    }

    const { rerender } = render(
      <RouteErrorBoundary>
        <Flaky />
      </RouteErrorBoundary>
    );

    expect(screen.getByTestId('route-error-boundary')).toBeInTheDocument();
    expect(screen.getByTestId('route-error-try-again-button')).toBeInTheDocument();
    // Explicit reload button must NOT be present for non-chunk errors.
    expect(screen.queryByTestId('route-error-reload-button')).not.toBeInTheDocument();

    // Fix the underlying cause and click Try Again — the boundary must
    // clear its state so the next render succeeds.
    shouldThrow = false;
    const user = userEvent.setup();
    await user.click(screen.getByTestId('route-error-try-again-button'));
    rerender(
      <RouteErrorBoundary>
        <Flaky />
      </RouteErrorBoundary>
    );
    expect(screen.getByText('recovered content')).toBeInTheDocument();
    expect(screen.queryByTestId('route-error-boundary')).not.toBeInTheDocument();
  });

  it('detects a rejected React.lazy import as a chunk-load error and exposes an explicit Reload page action', async () => {
    // Reproduce the production shape of a Vite dynamic-import rejection.
    // React.lazy would cache this rejection on the module registry — a
    // plain state-clear "Try Again" cannot recover, so the boundary must
    // instead surface a full-reload action.
    const chunkError = new Error(
      'Failed to fetch dynamically imported module: /assets/PrinterMaintenancePage-abcd1234.js',
    );
    const RejectedLazy = lazy(() => Promise.reject(chunkError));

    render(
      <RouteErrorBoundary>
        <Suspense fallback={<div>loading…</div>}>
          <RejectedLazy />
        </Suspense>
      </RouteErrorBoundary>
    );

    // The boundary must catch the rejection. The lazy loader emits it
    // during commit; findByTestId waits for the microtask that resolves
    // the promise rejection through React's Suspense/error path.
    expect(await screen.findByTestId('route-error-boundary')).toBeInTheDocument();
    // Chunk-error branch: distinct headline, distinct action, no Try
    // Again button (which would just re-throw the cached rejection).
    expect(screen.getByText(/failed to load this page/i)).toBeInTheDocument();
    expect(screen.getByTestId('route-error-reload-button')).toBeInTheDocument();
    expect(screen.queryByTestId('route-error-try-again-button')).not.toBeInTheDocument();
  });

  it('classifies Vite "Unable to preload CSS" as a chunk-load error and offers Reload page (Hicks #4)', async () => {
    // Signature Vite emits from its importAnalysisBuild plugin when a
    // lazy route's stylesheet 404s. Without CSS-preload detection the
    // boundary would fall through to "Try Again", which cannot recover
    // (the same rejected promise would immediately re-throw). We must
    // classify this as a chunk-load error and expose Reload page.
    const cssError = new Error(
      'Unable to preload CSS for /assets/PrinterMaintenancePage.7f3a2b91.css',
    );
    const RejectedLazy = lazy(() => Promise.reject(cssError));

    render(
      <RouteErrorBoundary>
        <Suspense fallback={<div>loading…</div>}>
          <RejectedLazy />
        </Suspense>
      </RouteErrorBoundary>
    );

    expect(await screen.findByTestId('route-error-boundary')).toBeInTheDocument();
    expect(screen.getByText(/failed to load this page/i)).toBeInTheDocument();
    expect(screen.getByTestId('route-error-reload-button')).toBeInTheDocument();
    // Try Again would just re-throw the cached rejection — must not
    // be offered for a CSS preload failure.
    expect(screen.queryByTestId('route-error-try-again-button')).not.toBeInTheDocument();
  });

  it('invokes window.location.reload() exactly once when the Reload page action is clicked (no implicit reloads)', async () => {
    // Stub reload — we must never call it implicitly (would produce an
    // infinite loop). We only call it when the operator explicitly opts
    // in via the labelled action.
    //
    // jsdom's `window.location.reload` is a non-configurable property on
    // the Location prototype, so we redefine `window.location` as a whole
    // (which IS configurable on the Window) to inject the spy.
    const reloadSpy = vi.fn();
    const originalLocation = window.location;
    Object.defineProperty(window, 'location', {
      configurable: true,
      writable: true,
      value: { ...originalLocation, reload: reloadSpy },
    });

    try {
      const chunkError = new Error(
        'Failed to fetch dynamically imported module: /assets/AnalyticsHubPage-deadbeef.js',
      );
      const RejectedLazy = lazy(() => Promise.reject(chunkError));

      render(
        <RouteErrorBoundary>
          <Suspense fallback={<div>loading…</div>}>
            <RejectedLazy />
          </Suspense>
        </RouteErrorBoundary>
      );

      const btn = await screen.findByTestId('route-error-reload-button');

      // Nothing implicit — the boundary must NOT have called reload yet.
      expect(reloadSpy).not.toHaveBeenCalled();

      const user = userEvent.setup();
      await user.click(btn);
      expect(reloadSpy).toHaveBeenCalledTimes(1);
    } finally {
      Object.defineProperty(window, 'location', {
        configurable: true,
        writable: true,
        value: originalLocation,
      });
    }
  });

  it('recognises the standard webpack "Loading chunk NN failed" phrasing as a chunk-load error too', async () => {
    class ChunkLoadError extends Error {
      constructor(msg: string) {
        super(msg);
        this.name = 'ChunkLoadError';
      }
    }
    const err = new ChunkLoadError('Loading chunk 42 failed. (missing: /assets/foo.js)');

    function Boom(): ReactElement {
      throw err;
    }

    render(
      <RouteErrorBoundary>
        <Boom />
      </RouteErrorBoundary>
    );

    expect(screen.getByTestId('route-error-reload-button')).toBeInTheDocument();
    expect(screen.queryByTestId('route-error-try-again-button')).not.toBeInTheDocument();
  });

  it('resets the errored boundary when resetKey changes, so navigating to another route recovers automatically', async () => {
    // Simulates the Layout wiring: `resetKey={location.key}`. The
    // boundary catches an error on route A; the user navigates to route
    // B via the sidebar; `location.key` changes, the boundary must
    // clear and render B's children — the user must not need to click
    // any button.
    const throwOn = 'A';
    function RouteBody({ name }: { name: string }) {
      if (name === throwOn) {
        throw new Error(`crash on ${name}`);
      }
      return <div>content for {name}</div>;
    }

    function Harness(): ReactElement {
      const [route, setRoute] = useState('A');
      return (
        <>
          <button data-testid="go-b" onClick={() => setRoute('B')}>
            go B
          </button>
          <RouteErrorBoundary resetKey={route}>
            <RouteBody name={route} />
          </RouteErrorBoundary>
        </>
      );
    }

    render(<Harness />);

    // Route A crashes → boundary shows.
    expect(screen.getByTestId('route-error-boundary')).toBeInTheDocument();
    expect(screen.queryByText(/content for/i)).not.toBeInTheDocument();

    // Navigate to route B (resetKey changes) — no manual "Try Again"
    // click; the boundary must clear itself on the next commit.
    const user = userEvent.setup();
    await user.click(screen.getByTestId('go-b'));

    expect(screen.getByText('content for B')).toBeInTheDocument();
    expect(screen.queryByTestId('route-error-boundary')).not.toBeInTheDocument();
  });

  it('recovers via real router navigation between same-path/different-query using location.key (Hicks #5)', async () => {
    // Regression guard for the Layout wiring change from
    // `resetKey={location.pathname}` to `resetKey={location.key}`.
    // `location.pathname` does NOT change between `/reports?range=day`
    // and `/reports?range=week` — a resetKey based on pathname would
    // leave the errored boundary stuck. Only `location.key` (unique
    // per history entry) covers same-path/different-search navigation.
    //
    // The body decides deterministically from the search string
    // whether to throw: `?range=day` throws, `?range=week` renders.
    // A shared "throw once" counter would be re-tried by React 19
    // concurrent rendering (mutation on the first attempt flips the
    // flag before React actually catches — the retry sees no throw)
    // and emits "There was an error during concurrent rendering"
    // uncaught exceptions.
    function ReportsBody(): ReactElement {
      const location = useLocation();
      const params = new URLSearchParams(location.search);
      if (params.get('range') === 'day') {
        throw new Error('query-change-blows-up on day');
      }
      return <div data-testid="reports-body">reports {location.search}</div>;
    }

    function App(): ReactElement {
      const location = useLocation();
      return (
        <div>
          <Link to="/reports?range=week" data-testid="go-week">
            week
          </Link>
          <RouteErrorBoundary resetKey={location.key}>
            <Routes>
              <Route path="/reports" element={<ReportsBody />} />
            </Routes>
          </RouteErrorBoundary>
        </div>
      );
    }

    render(
      <MemoryRouter initialEntries={['/reports?range=day']}>
        <App />
      </MemoryRouter>
    );

    // Errored on `?range=day`.
    expect(screen.getByTestId('route-error-boundary')).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByTestId('go-week'));

    // Pathname is identical (`/reports`), but `location.key` changed —
    // boundary must clear and children must re-render with the new
    // query.
    expect(screen.getByTestId('reports-body')).toBeInTheDocument();
    expect(screen.getByTestId('reports-body').textContent).toContain('?range=week');
    expect(screen.queryByTestId('route-error-boundary')).not.toBeInTheDocument();
  });

  it('recovers via real router navigation between same-path/different-hash using location.key (Hicks #5)', async () => {
    // Hash-only navigation. `pathname` and `search` are unchanged, but
    // React Router creates a new history entry (and a new
    // `location.key`) for each hash. This exact pattern comes up on
    // in-page anchor navigation (e.g. `/reports#summary`) after a
    // render failure — the errored boundary must clear so the anchor
    // link becomes clickable.
    //
    // Deterministic decision: empty hash → throw; any hash → render.
    function DocsBody(): ReactElement {
      const location = useLocation();
      if (location.hash === '') {
        throw new Error('hash-change-blows-up on empty');
      }
      return <div data-testid="docs-body">docs {location.hash}</div>;
    }

    function App(): ReactElement {
      const location = useLocation();
      return (
        <div>
          <Link to="/docs#summary" data-testid="go-summary">
            summary
          </Link>
          <RouteErrorBoundary resetKey={location.key}>
            <Routes>
              <Route path="/docs" element={<DocsBody />} />
            </Routes>
          </RouteErrorBoundary>
        </div>
      );
    }

    render(
      <MemoryRouter initialEntries={['/docs']}>
        <App />
      </MemoryRouter>
    );

    expect(screen.getByTestId('route-error-boundary')).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByTestId('go-summary'));

    expect(screen.getByTestId('docs-body')).toBeInTheDocument();
    expect(screen.getByTestId('docs-body').textContent).toContain('#summary');
    expect(screen.queryByTestId('route-error-boundary')).not.toBeInTheDocument();
  });

  it('does NOT reset when resetKey stays the same even if children re-render (no accidental resets)', async () => {
    // Regression guard for a naive implementation that just clears state
    // on every componentDidUpdate. The reset must be gated on an actual
    // resetKey change; otherwise transient re-renders would appear to
    // recover from unrecoverable errors and mask real crashes.
    function Boom(): ReactElement {
      throw new Error('stubborn');
    }

    const { rerender } = render(
      <RouteErrorBoundary resetKey="same-key">
        <Boom />
      </RouteErrorBoundary>
    );
    expect(screen.getByTestId('route-error-boundary')).toBeInTheDocument();

    // Force a re-render with the SAME resetKey; the boundary must stay
    // errored (children still throw).
    rerender(
      <RouteErrorBoundary resetKey="same-key">
        <Boom />
      </RouteErrorBoundary>
    );
    expect(screen.getByTestId('route-error-boundary')).toBeInTheDocument();
  });
});
