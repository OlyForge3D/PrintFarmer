import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import type { SliceJobEvent } from '@/services/slicerHubService';
import type { SliceJobStatusResponse } from '@/services/sliceJobService';

/* ── Hoisted mock state ── */

const mockState = vi.hoisted(() => {
  let userJobHandler: ((event: SliceJobEvent) => void) | null = null;
  let connected = false;

  return {
    get userJobHandler() { return userJobHandler; },
    set userJobHandler(v) { userJobHandler = v; },
    get connected() { return connected; },
    set connected(v: boolean) { connected = v; },
    ensureConnected: vi.fn(async () => { connected = true; }),
    joinUserGroup: vi.fn(async () => {}),
    leaveUserGroup: vi.fn(async () => {}),
    onUserJobEvent: vi.fn((callback: (event: SliceJobEvent) => void) => {
      userJobHandler = callback;
      return () => { userJobHandler = null; };
    }),
    isConnected: vi.fn(() => connected),
    onReconnected: vi.fn(() => () => {}),
  };
});

vi.mock('@/services/slicerHubService', () => ({
  slicerHubService: {
    ensureConnected: mockState.ensureConnected,
    joinUserGroup: mockState.joinUserGroup,
    leaveUserGroup: mockState.leaveUserGroup,
    onUserJobEvent: mockState.onUserJobEvent,
    isConnected: mockState.isConnected,
    onReconnected: mockState.onReconnected,
  },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ user: { id: 'user-123' } }),
}));

let useSliceJobsRealtime:
  typeof import('@/features/slicer/hooks/useSliceJobsRealtime')['useSliceJobsRealtime'];

beforeAll(async () => {
  ({ useSliceJobsRealtime } = await import('@/features/slicer/hooks/useSliceJobsRealtime'));
}, 60_000);

function makeJobResponse(overrides: Partial<SliceJobStatusResponse> = {}): SliceJobStatusResponse {
  return {
    id: 'job-1',
    status: 'Processing',
    progressPercent: 20,
    queuedAt: new Date().toISOString(),
    ...overrides,
  };
}

function makeEvent(overrides: Partial<SliceJobEvent> = {}): SliceJobEvent {
  return {
    eventType: 'JobProgress',
    jobId: 'job-1',
    userId: 'user-123',
    status: 'Processing',
    timestamp: new Date().toISOString(),
    ...overrides,
  };
}

function createWrapper() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return {
    queryClient,
    wrapper: ({ children }: { children: React.ReactNode }) =>
      React.createElement(QueryClientProvider, { client: queryClient }, children),
  };
}

describe('useSliceJobsRealtime', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockState.userJobHandler = null;
    mockState.connected = false;
  });

  it('joins user group on mount', async () => {
    const { wrapper } = createWrapper();

    renderHook(() => useSliceJobsRealtime(), { wrapper });

    await waitFor(() => {
      expect(mockState.ensureConnected).toHaveBeenCalled();
    });
    expect(mockState.joinUserGroup).toHaveBeenCalledWith('user-123');
    expect(mockState.onUserJobEvent).toHaveBeenCalled();
  });

  it('updates cache when a progress event arrives', async () => {
    const { queryClient, wrapper } = createWrapper();
    const existingJobs = [makeJobResponse({ id: 'job-1', progressPercent: 10 })];
    queryClient.setQueryData(['slice-jobs'], existingJobs);

    renderHook(() => useSliceJobsRealtime(), { wrapper });

    await waitFor(() => {
      expect(mockState.userJobHandler).toBeTruthy();
    });

    act(() => {
      mockState.userJobHandler!(makeEvent({
        jobId: 'job-1',
        progressPercent: 55,
        progressMessage: 'Layer 28/50',
      }));
    });

    const cached = queryClient.getQueryData<SliceJobStatusResponse[]>(['slice-jobs']);
    expect(cached).toHaveLength(1);
    expect(cached![0].progressPercent).toBe(55);
    expect(cached![0].progressMessage).toBe('Layer 28/50');
  });

  it('updates cache on job completion', async () => {
    const { queryClient, wrapper } = createWrapper();
    queryClient.setQueryData(['slice-jobs'], [
      makeJobResponse({ id: 'job-1', status: 'Processing', progressPercent: 80 }),
    ]);

    renderHook(() => useSliceJobsRealtime(), { wrapper });

    await waitFor(() => {
      expect(mockState.userJobHandler).toBeTruthy();
    });

    act(() => {
      mockState.userJobHandler!(makeEvent({
        eventType: 'JobCompleted',
        jobId: 'job-1',
        status: 'Completed',
        progressPercent: 100,
        artifactsRoute: '/api/artifacts/job/job-1',
      }));
    });

    const cached = queryClient.getQueryData<SliceJobStatusResponse[]>(['slice-jobs']);
    expect(cached![0].status).toBe('Completed');
    expect(cached![0].artifactsRoute).toBe('/api/artifacts/job/job-1');
  });

  it('invalidates query for unknown job IDs', async () => {
    const { queryClient, wrapper } = createWrapper();
    queryClient.setQueryData(['slice-jobs'], [makeJobResponse({ id: 'job-1' })]);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    renderHook(() => useSliceJobsRealtime(), { wrapper });

    await waitFor(() => {
      expect(mockState.userJobHandler).toBeTruthy();
    });

    act(() => {
      mockState.userJobHandler!(makeEvent({ jobId: 'unknown-job' }));
    });

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['slice-jobs'] });
  });

  it('leaves user group on unmount', async () => {
    const { wrapper } = createWrapper();

    const { unmount } = renderHook(() => useSliceJobsRealtime(), { wrapper });

    await waitFor(() => {
      expect(mockState.joinUserGroup).toHaveBeenCalled();
    });

    unmount();

    expect(mockState.leaveUserGroup).toHaveBeenCalledWith('user-123');
  });

  it('clears stale failure diagnostics (including errorDetail) once a job is no longer failed', async () => {
    // Regression coverage: a job's admin-only errorDetail must not linger from a previous failed
    // attempt once the job has been retried/requeued and is reported as non-failed again.
    const { queryClient, wrapper } = createWrapper();
    queryClient.setQueryData(['slice-jobs'], [
      makeJobResponse({
        id: 'job-1',
        status: 'Failed',
        failureReason: 'SlicingEngineRejectedModel',
        failureHint: 'Try Auto-Orient.',
        errorDetail: "OrcaSlicer exited with code 1: failed to resolve profile '/data/x.json'",
      }),
    ]);

    renderHook(() => useSliceJobsRealtime(), { wrapper });

    await waitFor(() => {
      expect(mockState.userJobHandler).toBeTruthy();
    });

    act(() => {
      mockState.userJobHandler!(makeEvent({
        eventType: 'JobProgress',
        jobId: 'job-1',
        status: 'Processing',
        progressPercent: 5,
      }));
    });

    const cached = queryClient.getQueryData<SliceJobStatusResponse[]>(['slice-jobs']);
    expect(cached![0].status).toBe('Processing');
    expect(cached![0].failureReason).toBeNull();
    expect(cached![0].failureHint).toBeNull();
    expect(cached![0].errorDetail).toBeNull();
  });

  it('does not connect when disabled', () => {
    const { wrapper } = createWrapper();

    renderHook(() => useSliceJobsRealtime({ enabled: false }), { wrapper });

    expect(mockState.ensureConnected).not.toHaveBeenCalled();
    expect(mockState.joinUserGroup).not.toHaveBeenCalled();
  });
});
