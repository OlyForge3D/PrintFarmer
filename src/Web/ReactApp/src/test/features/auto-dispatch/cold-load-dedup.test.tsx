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
import { act, render, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import {
  useAllAutoDispatchStatuses,
  useAutoDispatchGlobalStatus,
  useSetAutoDispatchEnabled,
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
    apiTestState.getAutoDispatchStatus.mockResolvedValue(emptyStatus);
    apiTestState.setAutoDispatchEnabled.mockResolvedValue(undefined);
    queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
  });

  afterEach(() => {
    queryClient.clear();
    vi.useRealTimers();
  });

  it('fires exactly one status request when Layout and the dashboard mount together on cold load', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });

    render(
      <QueryClientProvider client={queryClient}>
        <ColdLoadTree dashboardDelayMs={368} />
      </QueryClientProvider>
    );

    // Advance past the simulated lazy-chunk stagger that mounts the dashboard.
    await act(async () => {
      vi.advanceTimersByTime(500);
    });

    await waitFor(() => {
      expect(apiTestState.getAutoDispatchStatus).toHaveBeenCalledTimes(1);
    });

    // Give any pending microtasks/effects a chance to run, then assert the
    // count is still exactly one (no trailing duplicate fetch).
    await act(async () => {
      await Promise.resolve();
    });
    expect(apiTestState.getAutoDispatchStatus).toHaveBeenCalledTimes(1);
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
});
