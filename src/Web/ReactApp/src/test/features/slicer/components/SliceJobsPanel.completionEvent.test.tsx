import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SliceJobsPanel } from '@/features/slicer/components/SliceJobsPanel';
import type { SliceJobEvent } from '@/services/slicerHubService';

const realtime = vi.hoisted(() => {
  let handler: ((event: SliceJobEvent) => void) | null = null;
  return {
    get handler() { return handler; },
    set handler(value) { handler = value; },
    ensureConnected: vi.fn(async () => {}),
    joinUserGroup: vi.fn(async () => {}),
    leaveUserGroup: vi.fn(async () => {}),
    onUserJobEvent: vi.fn((callback: (event: SliceJobEvent) => void) => {
      handler = callback;
      return () => { handler = null; };
    }),
    isConnected: vi.fn(() => true),
    onReconnected: vi.fn(() => () => {}),
  };
});

const getMyJobs = vi.fn();
const artifactActions = vi.hoisted(() => ({
  download: vi.fn(),
  save: vi.fn(),
}));

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

vi.mock('@/services/slicerHubService', () => ({
  slicerHubService: realtime,
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ user: { id: 'user-1' } }),
}));

vi.mock('@/services/sliceJobService', async () => {
  const actual = await vi.importActual<typeof import('@/services/sliceJobService')>(
    '@/services/sliceJobService',
  );
  return {
    ...actual,
    sliceJobService: {
      getMyJobs: (...args: unknown[]) => getMyJobs(...args),
      cancelJob: vi.fn(),
      retryJob: vi.fn(),
      getEstimatedTimeRemaining: actual.sliceJobService.getEstimatedTimeRemaining,
      formatFilamentUsed: actual.sliceJobService.formatFilamentUsed,
      formatPrintTime: actual.sliceJobService.formatPrintTime,
      formatFileSize: actual.sliceJobService.formatFileSize,
    },
  };
});

vi.mock('@/features/slicer/components/SendToPrinterModal', () => ({
  SendToPrinterModal: () => null,
}));

vi.mock('@/features/slicer/components/GcodePreviewModal', () => ({
  GcodePreviewModal: () => null,
}));

vi.mock('@/features/slicer/utils/sliceArtifactActions', () => ({
  downloadGcodeArtifact: (...args: unknown[]) => artifactActions.download(...args),
  saveGcodeArtifactToLibrary: (...args: unknown[]) => artifactActions.save(...args),
}));

vi.mock('@/common/hooks/useViewModePreference', () => ({
  useViewModePreference: () => ({ viewMode: 'grid', setViewMode: vi.fn() }),
}));

describe('SliceJobsPanel live completion', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    realtime.handler = null;
    artifactActions.download.mockReset();
    artifactActions.save.mockReset();
    getMyJobs.mockResolvedValue([{
      id: 'job-1',
      status: 'Processing',
      progressPercent: 80,
      queuedAt: '2026-09-02T00:00:00Z',
    }]);
  });

  it('shows staged actions from completion and saves only after the explicit action', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <SliceJobsPanel />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await screen.findByText('Processing');
    await waitFor(() => expect(realtime.handler).not.toBeNull());
    expect(screen.queryByRole('button', { name: /preview/i })).not.toBeInTheDocument();

    act(() => {
      realtime.handler?.({
        eventType: 'JobCompleted',
        jobId: 'job-1',
        userId: 'user-1',
        status: 'Completed',
        progressPercent: 100,
        artifactsRoute: '/api/artifacts/job/job-1',
        timestamp: '2026-09-02T00:01:00Z',
      });
    });

    expect(await screen.findByRole('button', { name: /preview/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /download/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /save to library/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /print/i })).toBeInTheDocument();
    expect(getMyJobs).toHaveBeenCalledOnce();
    expect(artifactActions.save).not.toHaveBeenCalled();
    expect(invalidate).not.toHaveBeenCalledWith({ queryKey: ['file-browser'] });

    artifactActions.save.mockResolvedValue({
      createdNew: true,
      gcodeFileId: 'file-1',
    });
    await userEvent.click(screen.getByRole('button', { name: /save to library/i }));

    await waitFor(() => expect(artifactActions.save).toHaveBeenCalledOnce());
    expect(artifactActions.save).toHaveBeenCalledWith(
      '/api/artifacts/job/job-1',
      'job-1',
    );
    expect(
      invalidate.mock.calls.filter(([options]) =>
        (options as { queryKey?: unknown[] }).queryKey?.[0] === 'file-browser',
      ),
    ).toHaveLength(1);
  });
});
