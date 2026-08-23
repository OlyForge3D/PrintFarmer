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
      errorMessage: 'Slicing failed before the client subscribed.',
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
    expect(result.current.error).toBe('Slicing failed before the client subscribed.');
  });
});
