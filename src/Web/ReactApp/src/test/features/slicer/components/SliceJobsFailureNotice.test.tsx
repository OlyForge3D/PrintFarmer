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
  SliceFailureReason: {
    SlicingEngineRejectedModel: 'SlicingEngineRejectedModel',
    SlicerFailed: 'SlicerFailed',
  },
  getLayoutDegradationMessage: () => 'layout altered',
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

/**
 * The exact hint the backend sends for a `SlicingEngineRejectedModel` failure. It names the
 * "Auto-orient plate" control because that is what actually resolved every affected model when
 * issue #1811 was reproduced against the real OrcaSlicer CLI.
 */
const ENGINE_REJECTED_HINT =
  'The slicing engine could not slice this model. This most often happens when a model sits in ' +
  'an orientation the engine cannot handle — try the "Auto-orient plate" button on the plate ' +
  'controls in the slicer workspace (unlock the plate first if it is locked), then slice again. ' +
  "If it still fails, ask a farm admin to check the job's error detail.";

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

describe('SliceJobsPanel slice failure notice (#1811)', () => {
  beforeEach(() => {
    mockGetMyJobs.mockReset();
  });

  it('tells a normal operator why a failed job failed and what to try', async () => {
    // Before #1811 this operator saw only "Slicing failed." — errorDetail is admin-only, so there
    // was no route to the reason at all without container access.
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-failed-1',
        status: 'Failed',
        progressPercent: 49,
        queuedAt: '2026-08-21T14:24:18Z',
        startedAt: '2026-08-21T14:24:22Z',
        completedAt: '2026-08-21T14:24:28Z',
        errorMessage: 'Slicing failed.',
        errorDetail: null,
        failureReason: 'SlicingEngineRejectedModel',
        failureHint: ENGINE_REJECTED_HINT,
      },
    ]);

    renderPanel();

    const notice = await screen.findByRole('status');
    expect(notice.textContent).toContain('Auto-orient plate');
    expect(notice.textContent).toContain('most often');
  });

  it('shows no notice for a failed job the worker could not classify', async () => {
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-failed-2',
        status: 'Failed',
        progressPercent: 30,
        queuedAt: '2026-08-21T14:24:18Z',
        startedAt: '2026-08-21T14:24:22Z',
        completedAt: '2026-08-21T14:24:28Z',
        errorMessage: 'Slicing failed.',
      },
    ]);

    renderPanel();

    await screen.findByText(/job-fail/);
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('shows no failure notice for a job that completed successfully', async () => {
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-ok-1',
        status: 'Completed',
        progressPercent: 100,
        queuedAt: '2026-08-21T14:22:44Z',
        startedAt: '2026-08-21T14:22:49Z',
        completedAt: '2026-08-21T14:23:16Z',
        artifactsCount: 1,
        artifactsTotalBytes: 12394252,
      },
    ]);

    renderPanel();

    await screen.findByText(/job-ok/);
    expect(screen.queryByRole('status')).toBeNull();
  });
});
