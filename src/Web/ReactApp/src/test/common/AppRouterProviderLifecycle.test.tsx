import { StrictMode } from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

import { AppRouterProvider } from '../../common/router/AppRouterProvider';

/**
 * Building a data router calls `initialize()`, which installs a `popstate`
 * listener that only `dispose()` removes — and `RouterProvider` never disposes.
 * Without an explicit lifecycle every remount (tests, HMR, StrictMode's
 * speculative double-invoke of state initialisers) strands another live router
 * that keeps reacting to Back/Forward. See issue 2525.
 */
describe('AppRouterProvider router lifecycle', () => {
  const nativeAdd = window.addEventListener.bind(window);
  const nativeRemove = window.removeEventListener.bind(window);
  let added = 0;
  let removed = 0;

  beforeEach(() => {
    added = 0;
    removed = 0;
    // Spies are installed per test, not at describe scope: the suite restores
    // mocks between tests, which would silently unhook a shared spy and leave
    // the counters reading zero.
    vi.spyOn(window, 'addEventListener').mockImplementation(
      (...args: Parameters<typeof window.addEventListener>) => {
        if (args[0] === 'popstate') added += 1;
        nativeAdd(...args);
      },
    );
    vi.spyOn(window, 'removeEventListener').mockImplementation(
      (...args: Parameters<typeof window.removeEventListener>) => {
        if (args[0] === 'popstate') removed += 1;
        nativeRemove(...args);
      },
    );
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  /** Let the deferred disposal microtask run. */
  const flushDisposal = () => new Promise<void>((resolve) => queueMicrotask(resolve));

  it('releases every history listener it installs across repeated mounts', async () => {
    for (let i = 0; i < 3; i += 1) {
      const view = render(
        <AppRouterProvider>
          <div>router child</div>
        </AppRouterProvider>,
      );
      expect(screen.getByText('router child')).toBeInTheDocument();
      view.unmount();
      await flushDisposal();
    }

    expect(added).toBeGreaterThan(0);
    expect(removed).toBe(added);
  });

  it('leaves no router listening after a StrictMode mount and unmount', async () => {
    const view = render(
      <StrictMode>
        <AppRouterProvider>
          <div>strict child</div>
        </AppRouterProvider>
      </StrictMode>,
    );
    expect(screen.getByText('strict child')).toBeInTheDocument();
    view.unmount();
    await flushDisposal();

    expect(added).toBeGreaterThan(0);
    expect(removed).toBe(added);
  });

  it('keeps the router listening across a StrictMode simulated remount', async () => {
    render(
      <StrictMode>
        <AppRouterProvider>
          <div>strict child</div>
        </AppRouterProvider>
      </StrictMode>,
    );
    await flushDisposal();

    // Exactly one router must survive the double-invoked mount, still listening.
    expect(added - removed).toBe(1);
  });
});
