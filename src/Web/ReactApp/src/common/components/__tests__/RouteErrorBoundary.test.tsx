/**
 * RouteErrorBoundary — chunk-load recovery + navigation-based reset (Hicks #3).
 *
 * These tests exercise the two failure modes the previous implementation
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
 *   2. A stuck error surviving navigation. Before the fix, the route
 *      boundary retained its `hasError=true` state across route
 *      transitions because the Outlet element type stayed the same.
 *      We now accept a `resetKey` prop (Layout passes the pathname) and
 *      clear the error whenever it changes.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { lazy, Suspense, useState } from 'react';
import type { ReactElement } from 'react';
import { RouteErrorBoundary } from '../ErrorBoundary';

describe('RouteErrorBoundary', () => {
  // Suppress the noisy console.error from componentDidCatch. React itself
  // also warns; we filter both so the test output stays clean.
  const originalConsoleError = console.error;
  beforeEach(() => {
    console.error = vi.fn();
  });
  afterEach(() => {
    console.error = originalConsoleError;
  });

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
    // Simulates the Layout wiring: `resetKey={location.pathname}`. The
    // boundary catches an error on route A; the user navigates to route
    // B via the sidebar; the pathname changes, the boundary must clear
    // and render B's children — the user must not need to click any
    // button.
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
