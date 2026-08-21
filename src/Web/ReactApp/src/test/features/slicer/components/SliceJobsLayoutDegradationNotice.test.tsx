import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { SliceJobsPanel } from '@/features/slicer/components/SliceJobsPanel';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

const mockGetMyJobs = vi.fn();

vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    getMyJobs: (...args: unknown[]) => mockGetMyJobs(...args),
    cancelJob: vi.fn(),
    retryJob: vi.fn(),
    getEstimatedTimeRemaining: () => null,
    formatFilamentUsed: (g: number) => `${g}g`,
    formatPrintTime: (s: number) => `${s}s`,
    formatFileSize: (b: number) => `${b}B`,
  },
  SliceJobStatus: {
    Queued: 'Queued',
    Processing: 'Processing',
    Completed: 'Completed',
    Failed: 'Failed',
    Cancelled: 'Cancelled',
  },
  LayoutDegradationReason: {
    LayoutNotEmbedded: 'LayoutNotEmbedded',
    SourcePlacementFallback: 'SourcePlacementFallback',
  },
  getLayoutDegradationMessage: (reason: string) =>
    reason === 'LayoutNotEmbedded'
      ? 'The requested model position could not be applied, so the print was auto-arranged instead.'
      : 'The requested model position was ignored in favor of the placement embedded in the source file.',
}));

vi.mock('@/features/slicer/hooks/useSliceJobsRealtime', () => ({
  useSliceJobsRealtime: () => ({ isConnected: false }),
}));

vi.mock('@/features/slicer/components/SendToPrinterModal', () => ({
  SendToPrinterModal: () => null,
}));

vi.mock('@/features/slicer/components/GcodePreviewModal', () => ({
  GcodePreviewModal: () => null,
}));

vi.mock('@/common/hooks/useViewModePreference', () => ({
  useViewModePreference: () => ({ viewMode: 'grid', setViewMode: vi.fn() }),
}));

function renderPanel() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <SliceJobsPanel />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('SliceJobsPanel layout degradation notice (#1800)', () => {
  beforeEach(() => {
    mockGetMyJobs.mockReset();
  });

  it('shows a non-fatal notice for a completed job whose layout was dropped', async () => {
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-degraded-1',
        status: 'Completed',
        progressPercent: 100,
        queuedAt: '2026-05-31T09:00:00Z',
        startedAt: '2026-05-31T09:01:00Z',
        completedAt: '2026-05-31T09:05:00Z',
        artifactsCount: 1,
        artifactsTotalBytes: 5000,
        layoutDegradation: 'LayoutNotEmbedded',
      },
    ]);

    renderPanel();

    const notice = await screen.findByRole('status');
    expect(notice.textContent).toContain('auto-arranged instead');

    // Must not be styled/read as a job failure.
    expect(screen.queryByText(/slicer crash/i)).toBeNull();
  });

  it('does not show a notice for a completed job with no layout degradation', async () => {
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-clean-1',
        status: 'Completed',
        progressPercent: 100,
        queuedAt: '2026-05-31T09:00:00Z',
        startedAt: '2026-05-31T09:01:00Z',
        completedAt: '2026-05-31T09:05:00Z',
        artifactsCount: 1,
        artifactsTotalBytes: 5000,
      },
    ]);

    renderPanel();

    await screen.findByText(/job-clea/);
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('does not show a notice for a non-completed job even if layoutDegradation is set', async () => {
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-processing-1',
        status: 'Processing',
        progressPercent: 50,
        queuedAt: '2026-05-31T09:00:00Z',
        startedAt: '2026-05-31T09:01:00Z',
        layoutDegradation: 'LayoutNotEmbedded',
      },
    ]);

    renderPanel();

    await screen.findByText(/job-proc/);
    expect(screen.queryByRole('status')).toBeNull();
  });
});
