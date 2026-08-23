import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import type { SliceJobEvent } from '@/services/slicerHubService';

/*
 * Regression test for issue #1912: after the first slice job in a session,
 * subsequent jobs remained stuck at "Job queued" because the SignalR
 * 'slicejobevent' listener registered by useSliceJobProgress for the first
 * job was torn down while the connection object itself (and its live
 * event map) is a process-wide singleton shared across every job. This
 * test exercises the REAL slicerHubService (only the underlying
 * @microsoft/signalr transport is faked) so it reproduces the actual
 * connection.on/off bookkeeping instead of a simplified test double.
 */

const hubTestState = vi.hoisted(() => {
  const handlers = new Map<string, Set<(...args: unknown[]) => void>>();
  const connection = {
    on: vi.fn((methodName: string, callback: (...args: unknown[]) => void) => {
      if (!handlers.has(methodName)) handlers.set(methodName, new Set());
      handlers.get(methodName)!.add(callback);
    }),
    off: vi.fn((methodName: string, callback?: (...args: unknown[]) => void) => {
      if (!callback) {
        handlers.delete(methodName);
        return;
      }
      handlers.get(methodName)?.delete(callback);
    }),
    invoke: vi.fn(async () => undefined),
    onclose: vi.fn(),
    onreconnecting: vi.fn(),
    // Captured so tests can simulate the real SignalR reconnect lifecycle:
    // slicerHubService.start() passes this callback here, and firing it
    // drives slicerHubService's own onreconnected handler, which in turn
    // invokes every hook-registered onReconnected callback (i.e. this is
    // how a real automatic-reconnect event is simulated end-to-end).
    onreconnected: vi.fn(),
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    state: 'Connected',
  };
  const builder = {
    withUrl: vi.fn(),
    withAutomaticReconnect: vi.fn(),
    configureLogging: vi.fn(),
    build: vi.fn(),
  };
  builder.withUrl.mockReturnValue(builder);
  builder.withAutomaticReconnect.mockReturnValue(builder);
  builder.configureLogging.mockReturnValue(builder);
  builder.build.mockReturnValue(connection);

  const emit = (methodName: string, ...args: unknown[]) => {
    handlers.get(methodName)?.forEach(handler => handler(...args));
  };

  return { builder, connection, handlers, emit };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: function MockHubConnectionBuilder() {
    return hubTestState.builder;
  },
  HttpTransportType: { WebSockets: 1, ServerSentEvents: 2, LongPolling: 4 },
  HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
  LogLevel: { Information: 2 },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: vi.fn(() => 'http://localhost:5245/hubs/slicers'),
}));

vi.mock('@/common/auth/authenticatedSignalRSession', () => ({
  registerAuthenticatedSignalRTransport: vi.fn(),
}));

const mockGetJobStatus = vi.fn();

vi.mock('@/services/api', () => ({
  apiClient: {
    request: vi.fn(),
  },
}));

vi.mock('@/services/sliceJobService', async () => {
  const actual = await vi.importActual<typeof import('@/services/sliceJobService')>(
    '@/services/sliceJobService',
  );
  return {
    ...actual,
    sliceJobService: {
      ...actual.sliceJobService,
      getJobStatus: (...args: unknown[]) => mockGetJobStatus(...args),
    },
  };
});

let useSliceJobProgress:
  typeof import('@/features/slicer/hooks/useSliceJobProgress')['useSliceJobProgress'];
let slicerHubService: typeof import('@/services/slicerHubService')['slicerHubService'];

beforeAll(async () => {
  ({ useSliceJobProgress } = await import('@/features/slicer/hooks/useSliceJobProgress'));
  ({ slicerHubService } = await import('@/services/slicerHubService'));
}, 60_000);

function makeEvent(overrides: Partial<SliceJobEvent> = {}): SliceJobEvent {
  return {
    eventType: 'JobFailed',
    jobId: 'job-1',
    userId: 'user-1',
    status: 'Failed',
    errorMessage: 'Slicing failed.',
    timestamp: new Date().toISOString(),
    ...overrides,
  };
}

describe('useSliceJobProgress across consecutive jobs (issue #1912)', () => {
  beforeEach(async () => {
    // Tear down the previous test's connection first so start() below
    // actually rebuilds it (and re-registers onreconnected) rather than
    // short-circuiting with "connection already exists" — otherwise the
    // just-cleared mock.calls on `onreconnected` would never be
    // repopulated, and tests that capture the reconnect callback would see
    // it as unregistered.
    await slicerHubService.stop();
    vi.clearAllMocks();
    hubTestState.handlers.clear();
    hubTestState.connection.state = 'Connected';
    mockGetJobStatus.mockResolvedValue({
      id: 'job-x',
      status: 'Queued',
      progressPercent: 0,
      queuedAt: new Date().toISOString(),
    });
    await slicerHubService.start();
  });

  it('receives failure events for a second job started right after the first job failed', async () => {
    const { result, rerender } = renderHook(
      ({ jobId }: { jobId: string | null }) => useSliceJobProgress(jobId),
      { initialProps: { jobId: 'job-1' as string | null } },
    );

    await waitFor(() => {
      expect(hubTestState.connection.on).toHaveBeenCalledWith('slicejobevent', expect.any(Function));
    });

    act(() => {
      hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-1', status: 'Failed' }));
    });

    expect(result.current.status).toBe('Failed');

    // Second job starts immediately (submittedJobId goes job-1 -> job-2 directly,
    // as NewSliceJobPage does in submitMutation.onSuccess), without ever passing
    // through null.
    rerender({ jobId: 'job-2' });

    await waitFor(() => {
      expect(hubTestState.connection.on).toHaveBeenCalledWith('slicejobevent', expect.any(Function));
    });

    // A handler for 'slicejobevent' must still be registered on the shared
    // connection after job-1's handler was torn down.
    expect(hubTestState.handlers.get('slicejobevent')?.size).toBeGreaterThan(0);

    act(() => {
      hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-2', status: 'Failed' }));
    });

    expect(result.current.status).toBe('Failed');
  });

  it('receives failure events for a third job in the same session', async () => {
    const { result, rerender } = renderHook(
      ({ jobId }: { jobId: string | null }) => useSliceJobProgress(jobId),
      { initialProps: { jobId: 'job-1' as string | null } },
    );

    await waitFor(() => {
      expect(hubTestState.connection.on).toHaveBeenCalledWith('slicejobevent', expect.any(Function));
    });
    act(() => {
      hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-1', status: 'Failed' }));
    });
    expect(result.current.status).toBe('Failed');

    rerender({ jobId: 'job-2' });
    await waitFor(() => {
      expect(hubTestState.handlers.get('slicejobevent')?.size).toBeGreaterThan(0);
    });
    act(() => {
      hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-2', status: 'Failed' }));
    });
    expect(result.current.status).toBe('Failed');

    rerender({ jobId: 'job-3' });
    await waitFor(() => {
      expect(hubTestState.handlers.get('slicejobevent')?.size).toBeGreaterThan(0);
    });
    act(() => {
      hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-3', status: 'Failed' }));
    });
    expect(result.current.status).toBe('Failed');
  });

  it('recovers a job whose live "Failed" event was broadcast and missed before the client subscribed (root cause of #1912)', async () => {
    // This is the actual failure mode reported in the issue: the job fails
    // (or is already terminal) so fast on the server that its 'slicejobevent'
    // broadcast happens before/while useSliceJobProgress's subscription RPC
    // is still in flight. SignalR never replays missed events to a
    // late-joining group member, so a purely event-driven hook would be
    // stuck at "Job queued" forever. No 'slicejobevent' is ever emitted for
    // job-2 here — the only way the hook can learn the job failed is via the
    // REST reconciliation fetch performed after subscribing.
    const { result, rerender } = renderHook(
      ({ jobId }: { jobId: string | null }) => useSliceJobProgress(jobId),
      { initialProps: { jobId: 'job-1' as string | null } },
    );

    await waitFor(() => {
      expect(hubTestState.connection.on).toHaveBeenCalledWith('slicejobevent', expect.any(Function));
    });
    act(() => {
      hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-1', status: 'Failed' }));
    });
    expect(result.current.status).toBe('Failed');

    mockGetJobStatus.mockResolvedValue({
      id: 'job-2',
      status: 'Failed',
      progressPercent: 0,
      queuedAt: new Date().toISOString(),
      // The public status endpoint only ever returns the generic message
      // (SliceJobController.MapToPublicStatusResponse); it never echoes
      // job-specific detail to non-admin callers.
      errorMessage: 'Slicing failed.',
    });

    rerender({ jobId: 'job-2' });

    // No 'slicejobevent' is ever emitted for job-2 — the hook must still
    // reach the correct terminal state via REST reconciliation.
    await waitFor(() => {
      expect(mockGetJobStatus).toHaveBeenCalledWith('job-2');
    });
    await waitFor(() => {
      expect(result.current.status).toBe('Failed');
    });
    expect(result.current.error).toBe('Slicing failed.');
  });

  it('recovers a job whose terminal event was missed while the connection was reconnecting', async () => {
    // Distinct from the "never connected yet" race above: here the job was
    // already being tracked live (status = 'Queued' from an earlier event),
    // the connection drops, the job fails on the server while disconnected
    // — so the 'slicejobevent' broadcast is never received — and only then
    // does the client reconnect. A guard keyed off "has state.status ever
    // been set" would stay permanently satisfied from the earlier 'Queued'
    // event and never re-check the server, leaving the UI stuck exactly as
    // in issue #1912. This proves reconciliation runs again on every
    // reconnect, not just once per job.
    const { result } = renderHook(() => useSliceJobProgress('job-2'));

    await waitFor(() => {
      expect(hubTestState.connection.on).toHaveBeenCalledWith('slicejobevent', expect.any(Function));
    });

    act(() => {
      hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-2', status: 'Queued', errorMessage: undefined }));
    });
    expect(result.current.status).toBe('Queued');

    // Simulate the connection dropping and SignalR's automatic reconnect
    // firing. slicerHubService registers its own onreconnected handler with
    // the underlying connection in start(); invoking it here drives the
    // exact same path a real reconnect would (which then calls every
    // hook-registered onReconnected callback, i.e. the hook's resubscribe).
    const reconnectedHandler = hubTestState.connection.onreconnected.mock.calls[0]?.[0] as
      | ((connectionId: string | undefined) => Promise<void>)
      | undefined;
    expect(reconnectedHandler).toBeDefined();

    mockGetJobStatus.mockResolvedValue({
      id: 'job-2',
      status: 'Failed',
      progressPercent: 0,
      queuedAt: new Date().toISOString(),
      errorMessage: 'Slicing failed.',
    });

    await act(async () => {
      await reconnectedHandler!('new-connection-id');
    });

    await waitFor(() => {
      expect(result.current.status).toBe('Failed');
    });
    expect(result.current.error).toBe('Slicing failed.');
  });

  it('never lets a stale REST reconciliation response overwrite a live event that already arrived', async () => {
    // The REST fetch and the live event race independently; the fix must
    // guarantee the live event always wins even if the REST response
    // resolves afterwards with older/different data.
    let resolveGetJobStatus!: (value: {
      id: string;
      status: string;
      progressPercent: number;
      queuedAt: string;
    }) => void;
    mockGetJobStatus.mockImplementation(
      () =>
        new Promise(resolve => {
          resolveGetJobStatus = resolve;
        }),
    );

    const { result } = renderHook(() => useSliceJobProgress('job-2'));

    await waitFor(() => {
      expect(hubTestState.connection.on).toHaveBeenCalledWith('slicejobevent', expect.any(Function));
    });
    await waitFor(() => {
      expect(mockGetJobStatus).toHaveBeenCalledWith('job-2');
    });

    // The live event arrives first, while the REST call is still pending.
    act(() => {
      hubTestState.emit('slicejobevent', makeEvent({ jobId: 'job-2', status: 'Failed' }));
    });
    expect(result.current.status).toBe('Failed');

    // The REST response resolves afterwards with stale, non-terminal data —
    // it must be ignored now that a live event has been observed.
    act(() => {
      resolveGetJobStatus({
        id: 'job-2',
        status: 'Queued',
        progressPercent: 0,
        queuedAt: new Date().toISOString(),
      });
    });

    await Promise.resolve();
    expect(result.current.status).toBe('Failed');
  });
});
