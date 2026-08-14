/**
 * Regression coverage for #1547: a fresh navigation to /auto-dispatch mounted
 * two consumers of the shared KEYS.globalStatus query — Layout's
 * useAllAutoDispatchStatuses and the (lazy-loaded) dashboard page's
 * useAutoDispatchGlobalStatus — close together but not perfectly
 * simultaneously (Layout mounts eagerly; the dashboard mounts once its route
 * chunk resolves). That stagger let each hook's own `shouldFetchOnMount`
 * decide independently to call the queryFn before the other had subscribed
 * to the same in-flight request, producing two real
 * GET /api/auto-dispatch/status calls a few hundred ms apart instead of one.
 *
 * This suite exercises the real hooks (useAutoDispatch.ts is NOT mocked) so
 * it proves the actual single-flight/shared-cache contract established by
 * PR #1507, per that PR's review recommendation to add a call-count-spy
 * integration test.
 */
import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import {
  useAllAutoDispatchStatuses,
  useAutoDispatchGlobalStatus,
  useSetAutoDispatchEnabled,
  __resetAutoDispatchGlobalStatusSingleFlightForTests,
} from '@/features/printers/hooks/useAutoDispatch';
import type { AutoDispatchGlobalStatus } from '@/types/api';

const apiTestState = vi.hoisted(() => ({
  getAutoDispatchStatus: vi.fn(),
  setAutoDispatchEnabled: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getAutoDispatchStatus: apiTestState.getAutoDispatchStatus,
    setAutoDispatchEnabled: apiTestState.setAutoDispatchEnabled,
  },
}));

vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    onAutoDispatchStateChanged: vi.fn().mockReturnValue(() => {}),
  },
}));

const emptyStatus: AutoDispatchGlobalStatus = {
  globalEnabled: true,
  printers: [],
} as unknown as AutoDispatchGlobalStatus;

/** Stand-in for Layout.tsx: mounts eagerly and calls useAllAutoDispatchStatuses. */
function LayoutLike() {
  useAllAutoDispatchStatuses();
  return <div>layout</div>;
}

/** Stand-in for AutoDispatchDashboardPage.tsx: calls useAutoDispatchGlobalStatus. */
function DashboardLike() {
  useAutoDispatchGlobalStatus();
  return <div>dashboard</div>;
}

/**
 * Mirrors the real App.tsx shape: Layout mounts immediately; the dashboard
 * route mounts after `dashboardDelayMs`, simulating the lazy-loaded chunk
 * (React.lazy + Suspense) resolving slightly after the initial commit.
 */
function ColdLoadTree({ dashboardDelayMs }: { dashboardDelayMs: number }) {
  const [showDashboard, setShowDashboard] = useState(false);
  useEffect(() => {
    const id = setTimeout(() => setShowDashboard(true), dashboardDelayMs);
    return () => clearTimeout(id);
  }, [dashboardDelayMs]);
  return (
    <div>
      <LayoutLike />
      {showDashboard && <DashboardLike />}
    </div>
  );
}

function MutatorLike() {
  const mutation = useSetAutoDispatchEnabled();
  return (
    <button
      type="button"
      onClick={() =>
        mutation.mutate({
          printerId: 'printer-1',
          enabled: false,
          dispatchStateETag: 'etag-state',
          printerETag: 'etag-printer',
        })
      }
    >
      toggle
    </button>
  );
}

describe('auto-dispatch cold-load status request dedup (#1547)', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    vi.clearAllMocks();
    __resetAutoDispatchGlobalStatusSingleFlightForTests();
    apiTestState.getAutoDispatchStatus.mockResolvedValue(emptyStatus);
    apiTestState.setAutoDispatchEnabled.mockResolvedValue(undefined);
    queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
  });

  afterEach(() => {
    queryClient.clear();
    __resetAutoDispatchGlobalStatusSingleFlightForTests();
    vi.useRealTimers();
  });

  it('fires exactly one status request when Layout and the dashboard mount together on cold load', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });

    // Give the request real (simulated) network latency so Layout's fetch is
    // still genuinely in flight when the dashboard mounts 368ms later —
    // otherwise this test can pass even without the fix, because an
    // instant-resolving mock never creates the overlap window the bug
    // depends on (see PR review discussion on #1547).
    apiTestState.getAutoDispatchStatus.mockImplementation(
      () => new Promise((resolve) => setTimeout(() => resolve(emptyStatus), 500))
    );

    render(
      <QueryClientProvider client={queryClient}>
        <ColdLoadTree dashboardDelayMs={368} />
      </QueryClientProvider>
    );

    // Advance to just past the dashboard's staggered mount (368ms) while the
    // first request (500ms) is still unresolved — this is the exact overlap
    // window in which the reported duplicate fetch occurred.
    await act(async () => {
      vi.advanceTimersByTime(400);
    });
    expect(apiTestState.getAutoDispatchStatus).toHaveBeenCalledTimes(1);

    // Let the in-flight request resolve and settle.
    await act(async () => {
      vi.advanceTimersByTime(200);
    });

    await waitFor(() => {
      expect(apiTestState.getAutoDispatchStatus).toHaveBeenCalledTimes(1);
    });
  });

  it('fires exactly one status request per configured polling interval while idle with both consumers mounted', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });

    render(
      <QueryClientProvider client={queryClient}>
        <div>
          <LayoutLike />
          <DashboardLike />
        </div>
      </QueryClientProvider>
    );

    await waitFor(() => {
      expect(apiTestState.getAutoDispatchStatus).toHaveBeenCalledTimes(1);
    });

    // Steady-state polling interval is 10s; advance through three intervals
    // and confirm each one produces exactly one additional request, not two
    // (one per consumer).
    for (let interval = 1; interval <= 3; interval += 1) {
      await act(async () => {
        vi.advanceTimersByTime(10_000);
      });
      await waitFor(() => {
        expect(apiTestState.getAutoDispatchStatus).toHaveBeenCalledTimes(1 + interval);
      });
    }
  });

  it('still refetches on explicit mutation invalidation', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const { getByRole } = render(
      <QueryClientProvider client={queryClient}>
        <div>
          <LayoutLike />
          <DashboardLike />
          <MutatorLike />
        </div>
      </QueryClientProvider>
    );

    await waitFor(() => {
      expect(apiTestState.getAutoDispatchStatus).toHaveBeenCalledTimes(1);
    });

    const callsBeforeMutation = apiTestState.getAutoDispatchStatus.mock.calls.length;

    await act(async () => {
      getByRole('button', { name: 'toggle' }).click();
      await vi.runOnlyPendingTimersAsync();
    });

    await waitFor(() => {
      expect(apiTestState.setAutoDispatchEnabled).toHaveBeenCalledTimes(1);
    });

    await waitFor(() => {
      expect(apiTestState.getAutoDispatchStatus.mock.calls.length).toBeGreaterThan(
        callsBeforeMutation
      );
    });
  });

  it('forces a fresh status request on mutation success even while a background poll is still in flight', async () => {
    // Reproduces the scenario the single-flight guard could otherwise make
    // worse: a background poll fetch is genuinely still pending (its
    // response hasn't arrived yet) at the exact moment a mutation succeeds
    // and asks for a guaranteed-fresh refetch. The mutation's refetch must
    // not be silently satisfied by the stale, still-pending poll response —
    // it must issue its own new request.
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const pendingResolvers: Array<(value: AutoDispatchGlobalStatus) => void> = [];
    apiTestState.getAutoDispatchStatus.mockImplementation(
      () =>
        new Promise<AutoDispatchGlobalStatus>((resolve) => {
          pendingResolvers.push(resolve);
        })
    );

    render(
      <QueryClientProvider client={queryClient}>
        <div>
          <LayoutLike />
          <DashboardLike />
          <MutatorLike />
        </div>
      </QueryClientProvider>
    );

    // Resolve the cold-load fetch (call #1) so the query settles into idle.
    await act(async () => {
      pendingResolvers.shift()?.(emptyStatus);
    });
    await waitFor(() => {
      expect(apiTestState.getAutoDispatchStatus).toHaveBeenCalledTimes(1);
    });

    // Advance to the next poll tick — this starts a second request (call #2)
    // that we deliberately leave unresolved, simulating a slow in-flight
    // background poll.
    await act(async () => {
      vi.advanceTimersByTime(10_000);
    });
    await waitFor(() => {
      expect(apiTestState.getAutoDispatchStatus).toHaveBeenCalledTimes(2);
    });
    expect(pendingResolvers).toHaveLength(1);

    // Trigger the mutation while call #2 is still pending. Its onSuccess
    // resets the single-flight guard before invalidating/refetching, so this
    // must produce a brand-new call #3 rather than reusing call #2's promise.
    await act(async () => {
      screen.getByRole('button', { name: 'toggle' }).click();
      await vi.runOnlyPendingTimersAsync();
    });

    await waitFor(() => {
      expect(apiTestState.setAutoDispatchEnabled).toHaveBeenCalledTimes(1);
    });
    await waitFor(() => {
      expect(apiTestState.getAutoDispatchStatus.mock.calls.length).toBeGreaterThanOrEqual(3);
    });

    // Clean up the still-pending promises so the test doesn't leak timers.
    pendingResolvers.splice(0).forEach((resolve) => resolve(emptyStatus));
    await act(async () => {
      await Promise.resolve();
    });
  });
});
