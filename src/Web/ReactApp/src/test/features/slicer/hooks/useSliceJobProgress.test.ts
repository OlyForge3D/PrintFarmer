import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import type { SliceJobEvent } from '@/services/slicerHubService';

/* ── Hoisted mock state (accessible to vi.mock factories) ── */

const mockState = vi.hoisted(() => {
  const jobEventHandlers = new Map<string, (event: SliceJobEvent) => void>();
  let connected = false;

  return {
    jobEventHandlers,
    get connected() { return connected; },
    set connected(v: boolean) { connected = v; },
    ensureConnected: vi.fn(async () => { connected = true; }),
    subscribeToJob: vi.fn(async () => {}),
    unsubscribeFromJob: vi.fn(async () => {}),
    onJobEvent: vi.fn((jobId: string, callback: (event: SliceJobEvent) => void) => {
      const key = `SliceJob_${jobId}`;
      jobEventHandlers.set(key, callback);
      return () => { jobEventHandlers.delete(key); };
    }),
    isConnected: vi.fn(() => connected),
    onReconnected: vi.fn(() => () => {}),
  };
});

vi.mock('@/services/slicerHubService', () => ({
  slicerHubService: {
    ensureConnected: mockState.ensureConnected,
    subscribeToJob: mockState.subscribeToJob,
    unsubscribeFromJob: mockState.unsubscribeFromJob,
    onJobEvent: mockState.onJobEvent,
    isConnected: mockState.isConnected,
    onReconnected: mockState.onReconnected,
  },
}));

function emitJobEvent(jobId: string, event: SliceJobEvent) {
  const handler = mockState.jobEventHandlers.get(`SliceJob_${jobId}`);
  handler?.(event);
}

function makeEvent(overrides: Partial<SliceJobEvent> = {}): SliceJobEvent {
  return {
    eventType: 'JobProgress',
    jobId: 'job-1',
    userId: 'user-1',
    status: 'Processing',
    timestamp: new Date().toISOString(),
    ...overrides,
  };
}

describe('useSliceJobProgress', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockState.jobEventHandlers.clear();
    mockState.connected = false;
  });

  it('returns initial state when jobId is null', async () => {
    const { useSliceJobProgress } = await import('@/features/slicer/hooks/useSliceJobProgress');
    const { result } = renderHook(() => useSliceJobProgress(null));

    expect(result.current.progressPercent).toBe(0);
    expect(result.current.status).toBeNull();
    expect(result.current.isConnected).toBe(false);
  });

  it('connects and subscribes when given a jobId', async () => {
    const { useSliceJobProgress } = await import('@/features/slicer/hooks/useSliceJobProgress');
    renderHook(() => useSliceJobProgress('job-1'));

    await waitFor(() => {
      expect(mockState.ensureConnected).toHaveBeenCalled();
    });
    expect(mockState.subscribeToJob).toHaveBeenCalledWith('job-1');
    expect(mockState.onJobEvent).toHaveBeenCalledWith('job-1', expect.any(Function));
  });

  it('updates state on progress events', async () => {
    const { useSliceJobProgress } = await import('@/features/slicer/hooks/useSliceJobProgress');
    const { result } = renderHook(() => useSliceJobProgress('job-1'));

    await waitFor(() => {
      expect(mockState.onJobEvent).toHaveBeenCalled();
    });

    act(() => {
      emitJobEvent('job-1', makeEvent({
        progressPercent: 42,
        progressMessage: 'Slicing layer 21/50',
      }));
    });

    expect(result.current.progressPercent).toBe(42);
    expect(result.current.progressMessage).toBe('Slicing layer 21/50');
    expect(result.current.status).toBe('Processing');
  });

  it('updates state on completion', async () => {
    const { useSliceJobProgress } = await import('@/features/slicer/hooks/useSliceJobProgress');
    const { result } = renderHook(() => useSliceJobProgress('job-1'));

    await waitFor(() => {
      expect(mockState.onJobEvent).toHaveBeenCalled();
    });

    act(() => {
      emitJobEvent('job-1', makeEvent({
        eventType: 'JobCompleted',
        status: 'Completed',
        progressPercent: 100,
        resultFileUrl: '/artifacts/job-1/output.gcode',
        estimatedPrintTimeSeconds: 3600,
        filamentUsedGrams: 25.5,
      }));
    });

    expect(result.current.status).toBe('Completed');
    expect(result.current.progressPercent).toBe(100);
    expect(result.current.resultFileUrl).toBe('/artifacts/job-1/output.gcode');
    expect(result.current.estimatedPrintTimeSeconds).toBe(3600);
    expect(result.current.filamentUsedGrams).toBe(25.5);
  });

  it('updates state on failure', async () => {
    const { useSliceJobProgress } = await import('@/features/slicer/hooks/useSliceJobProgress');
    const { result } = renderHook(() => useSliceJobProgress('job-1'));

    await waitFor(() => {
      expect(mockState.onJobEvent).toHaveBeenCalled();
    });

    act(() => {
      emitJobEvent('job-1', makeEvent({
        eventType: 'JobFailed',
        status: 'Failed',
        errorMessage: 'Slicer crashed',
      }));
    });

    expect(result.current.status).toBe('Failed');
    expect(result.current.error).toBe('Slicer crashed');
  });

  it('unsubscribes on unmount', async () => {
    const { useSliceJobProgress } = await import('@/features/slicer/hooks/useSliceJobProgress');
    const { unmount } = renderHook(() => useSliceJobProgress('job-1'));

    await waitFor(() => {
      expect(mockState.subscribeToJob).toHaveBeenCalled();
    });

    unmount();

    expect(mockState.unsubscribeFromJob).toHaveBeenCalledWith('job-1');
    expect(mockState.jobEventHandlers.has('SliceJob_job-1')).toBe(false);
  });

  it('resets state when jobId changes to null', async () => {
    const { useSliceJobProgress } = await import('@/features/slicer/hooks/useSliceJobProgress');
    const { result, rerender } = renderHook(
      ({ jobId }: { jobId: string | null }) => useSliceJobProgress(jobId),
      { initialProps: { jobId: 'job-1' as string | null } },
    );

    await waitFor(() => {
      expect(mockState.subscribeToJob).toHaveBeenCalled();
    });

    act(() => {
      emitJobEvent('job-1', makeEvent({ progressPercent: 50 }));
    });

    expect(result.current.progressPercent).toBe(50);

    rerender({ jobId: null });

    expect(result.current.progressPercent).toBe(0);
    expect(result.current.status).toBeNull();
  });
});
