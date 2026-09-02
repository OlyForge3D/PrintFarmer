import React from 'react';
import { act, render, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { GcodeHarvestStatus, type GcodeHarvestOperation } from '@/types/api';

// Regression coverage for issue #2395: HarvestPage's SignalR group
// membership must be reconciled by delta (join only newly-running ops,
// leave only ops that stopped running) rather than the old
// clear-and-rejoin-everything effect, which opened a leave/rejoin gap
// during which `harvestfileprogress` events were silently dropped, and
// must never re-fire a join/leave pair for an operation that stays
// running across a poll.

const signalRMocks = vi.hoisted(() => {
  // Mirrors the real `SignalRService` contract exactly: `onConnectionStateChange` only
  // notifies *future* transitions (it never replays current state to a new subscriber),
  // and `isConnected` reflects live connection state at read time. `HarvestPage` reads
  // `isConnected` synchronously right after subscribing specifically to cover the case
  // where the (singleton, shared) service is already connected before this page mounts -
  // defaulting `connected` to `true` here means the default lifecycle tests below exercise
  // exactly that "already connected at mount" path, matching the real-world common case.
  // Tests exercising the connect-race path explicitly set it to `false` first.
  let connected = true;
  const connectionStateCallbacks: Array<(connected: boolean) => void> = [];

  return {
    connect: vi.fn().mockResolvedValue(undefined),
    joinHarvestGroup: vi.fn().mockResolvedValue(undefined),
    leaveHarvestGroup: vi.fn().mockResolvedValue(undefined),
    onHarvestFileProgress: vi.fn(),
    onHarvestOperationProgress: vi.fn(),
    onHarvestUpdate: vi.fn(),
    onConnectionStateChange: vi.fn((callback: (connected: boolean) => void) => {
      connectionStateCallbacks.push(callback);
      return () => {
        const index = connectionStateCallbacks.indexOf(callback);
        if (index > -1) {
          connectionStateCallbacks.splice(index, 1);
        }
      };
    }),
    get isConnected() {
      return connected;
    },
    setAutoConnect: (value: boolean) => {
      connected = value;
    },
    emitConnectionState: (value: boolean) => {
      connected = value;
      connectionStateCallbacks.slice().forEach(callback => callback(value));
    },
  };
});

const apiMocks = vi.hoisted(() => ({
  getHarvestOperations: vi.fn(),
}));

vi.mock('@/services/harvest-signalr', () => ({
  signalRService: signalRMocks,
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getHarvestOperations: apiMocks.getHarvestOperations,
  },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => true }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [], isLoading: false }),
  useCancelHarvestOperation: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useRestartHarvestDiscovery: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock('@/features/gcode/components/harvest/HarvestWizardModal', () => ({
  HarvestWizardModal: () => null,
}));

vi.mock('@/features/gcode/components/harvest/HarvestOperationDetails', () => ({
  HarvestOperationDetails: () => null,
}));

import { HarvestPage } from '../HarvestPage';

function makeOp(overrides: Partial<GcodeHarvestOperation> & { id: string }): GcodeHarvestOperation {
  return {
    printerId: `printer-${overrides.id}`,
    printerName: `Printer ${overrides.id}`,
    status: GcodeHarvestStatus.Running,
    filesFound: 10,
    filesProcessed: 1,
    filesAdded: 1,
    filesSkipped: 0,
    filesErrored: 0,
    duplicatesSkipped: 0,
    totalSizeBytes: 0,
    startedAt: new Date().toISOString(),
    ...overrides,
  };
}

function renderHarvestPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/harvest']}>
        <HarvestPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('HarvestPage SignalR group membership lifecycle', () => {
  beforeEach(() => {
    signalRMocks.connect.mockClear();
    signalRMocks.joinHarvestGroup.mockClear();
    signalRMocks.leaveHarvestGroup.mockClear();
    signalRMocks.onHarvestFileProgress.mockReset().mockReturnValue(vi.fn());
    signalRMocks.onHarvestOperationProgress.mockReset().mockReturnValue(vi.fn());
    signalRMocks.onHarvestUpdate.mockReset().mockReturnValue(vi.fn());
    signalRMocks.onConnectionStateChange.mockClear();
    signalRMocks.setAutoConnect(true);
    apiMocks.getHarvestOperations.mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('joins the group for the initial running operation exactly once on mount (connection already established, the common singleton-service case)', async () => {
    apiMocks.getHarvestOperations.mockResolvedValue([makeOp({ id: 'op-1' })]);

    renderHarvestPage();

    await waitFor(() => expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(1));
    expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledWith('op-1');
    expect(signalRMocks.leaveHarvestGroup).not.toHaveBeenCalled();
    expect(signalRMocks.connect).toHaveBeenCalledTimes(1);
    expect(signalRMocks.onHarvestFileProgress).toHaveBeenCalledTimes(1);
    expect(signalRMocks.onHarvestOperationProgress).toHaveBeenCalledTimes(1);
    expect(signalRMocks.onHarvestUpdate).toHaveBeenCalledTimes(1);
  });

  it('does not re-join or leave when the running-operation set is unchanged across a poll', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    // A fresh object reference each poll (mirroring React Query's real
    // network responses) with the *same* running operation ID, so the old
    // clear-and-rejoin effect (keyed on array identity) would have
    // re-fired here even though nothing meaningfully changed.
    apiMocks.getHarvestOperations.mockImplementation(() =>
      Promise.resolve([makeOp({ id: 'op-1', filesProcessed: Math.floor(Math.random() * 10) })]),
    );

    renderHarvestPage();

    await waitFor(() => expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(1));

    // Advance past several 2s poll ticks with the same running-op set.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });

    expect(apiMocks.getHarvestOperations.mock.calls.length).toBeGreaterThanOrEqual(4);
    // No drop window: an unchanged running set must never re-fire join or leave.
    expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(1);
    expect(signalRMocks.leaveHarvestGroup).not.toHaveBeenCalled();
  });

  it('joins a newly-running operation without re-joining or leaving the still-running one', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    apiMocks.getHarvestOperations.mockResolvedValueOnce([makeOp({ id: 'op-1' })]);

    renderHarvestPage();

    await waitFor(() => expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(1));
    expect(signalRMocks.joinHarvestGroup).toHaveBeenNthCalledWith(1, 'op-1');

    apiMocks.getHarvestOperations.mockResolvedValue([
      makeOp({ id: 'op-1' }),
      makeOp({ id: 'op-2' }),
    ]);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2_000);
    });

    await waitFor(() => expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(2));
    expect(signalRMocks.joinHarvestGroup).toHaveBeenNthCalledWith(2, 'op-2');
    // op-1 never gets a leave/rejoin pair - it was running before and after.
    expect(signalRMocks.leaveHarvestGroup).not.toHaveBeenCalled();
  });

  it('leaves an operation that stopped running without affecting one that is still running', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    apiMocks.getHarvestOperations.mockResolvedValueOnce([
      makeOp({ id: 'op-1' }),
      makeOp({ id: 'op-2' }),
    ]);

    renderHarvestPage();

    await waitFor(() => expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(2));

    // op-2 completes; op-1 keeps running.
    apiMocks.getHarvestOperations.mockResolvedValue([
      makeOp({ id: 'op-1' }),
      makeOp({ id: 'op-2', status: GcodeHarvestStatus.Completed, completedAt: new Date().toISOString() }),
    ]);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2_000);
    });

    await waitFor(() => expect(signalRMocks.leaveHarvestGroup).toHaveBeenCalledTimes(1));
    expect(signalRMocks.leaveHarvestGroup).toHaveBeenCalledWith('op-2');
    // op-1 stayed running the whole time - it must still only have the
    // single initial join, i.e. no leave/rejoin pair was fired for it.
    expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(2);
  });

  it('leaves every remaining joined group exactly once on unmount and unsubscribes handlers', async () => {
    apiMocks.getHarvestOperations.mockResolvedValue([
      makeOp({ id: 'op-1' }),
      makeOp({ id: 'op-2' }),
    ]);

    const unsubscribeFileProgress = vi.fn();
    const unsubscribeOperationProgress = vi.fn();
    const unsubscribeUpdate = vi.fn();
    signalRMocks.onHarvestFileProgress.mockReturnValue(unsubscribeFileProgress);
    signalRMocks.onHarvestOperationProgress.mockReturnValue(unsubscribeOperationProgress);
    signalRMocks.onHarvestUpdate.mockReturnValue(unsubscribeUpdate);

    const { unmount } = renderHarvestPage();

    await waitFor(() => expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(2));

    unmount();

    expect(signalRMocks.leaveHarvestGroup).toHaveBeenCalledTimes(2);
    expect(signalRMocks.leaveHarvestGroup).toHaveBeenCalledWith('op-1');
    expect(signalRMocks.leaveHarvestGroup).toHaveBeenCalledWith('op-2');
    expect(unsubscribeFileProgress).toHaveBeenCalledTimes(1);
    expect(unsubscribeOperationProgress).toHaveBeenCalledTimes(1);
    expect(unsubscribeUpdate).toHaveBeenCalledTimes(1);
  });

  it('never registers duplicate event subscriptions across repeated polls', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    apiMocks.getHarvestOperations.mockImplementation(() =>
      Promise.resolve([makeOp({ id: 'op-1', filesProcessed: Math.floor(Math.random() * 10) })]),
    );

    renderHarvestPage();

    await waitFor(() => expect(signalRMocks.onHarvestFileProgress).toHaveBeenCalledTimes(1));

    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000);
    });

    // Subscriptions are registered by the mount-once effect only - it never
    // re-runs, so these must still be exactly one call each regardless of
    // how many polls occurred.
    expect(signalRMocks.onHarvestFileProgress).toHaveBeenCalledTimes(1);
    expect(signalRMocks.onHarvestOperationProgress).toHaveBeenCalledTimes(1);
    expect(signalRMocks.onHarvestUpdate).toHaveBeenCalledTimes(1);
    expect(signalRMocks.connect).toHaveBeenCalledTimes(1);
  });

  it('does not attempt (and later does not drop) a join before the connection is established', async () => {
    signalRMocks.setAutoConnect(false);
    apiMocks.getHarvestOperations.mockResolvedValue([makeOp({ id: 'op-1' })]);

    renderHarvestPage();

    // The running-op set resolves before the connection does. `connect()` is
    // fire-and-forget, and the underlying service silently no-ops a join attempted before
    // the connection reaches `Connected` - so the delta-reconciliation effect must not call
    // `joinHarvestGroup` yet, or that op would be marked "joined" locally while never
    // actually joining server-side, permanently losing its progress events.
    await waitFor(() => expect(apiMocks.getHarvestOperations).toHaveBeenCalled());
    await act(async () => {
      await Promise.resolve();
    });
    expect(signalRMocks.joinHarvestGroup).not.toHaveBeenCalled();

    // Connection finally establishes - the deferred join must now fire, not be lost.
    await act(async () => {
      signalRMocks.emitConnectionState(true);
    });

    await waitFor(() => expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(1));
    expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledWith('op-1');
  });

  it('rejoins every currently-running operation after a reconnect, without trusting stale local state', async () => {
    apiMocks.getHarvestOperations.mockResolvedValue([
      makeOp({ id: 'op-1' }),
      makeOp({ id: 'op-2' }),
    ]);

    renderHarvestPage();

    await waitFor(() => expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(2));
    signalRMocks.joinHarvestGroup.mockClear();
    signalRMocks.leaveHarvestGroup.mockClear();

    // Connection drops, then reconnects. Server-side group membership from the old
    // connection is gone, so both still-running operations must be rejoined - `joinedOpsRef`
    // cannot be trusted to reflect real server state across a reconnect.
    await act(async () => {
      signalRMocks.emitConnectionState(false);
    });
    await act(async () => {
      signalRMocks.emitConnectionState(true);
    });

    await waitFor(() => expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledTimes(2));
    expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledWith('op-1');
    expect(signalRMocks.joinHarvestGroup).toHaveBeenCalledWith('op-2');
  });
});
