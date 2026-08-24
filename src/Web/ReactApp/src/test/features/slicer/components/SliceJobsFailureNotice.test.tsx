import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { SliceJobsPanel } from '@/features/slicer/components/SliceJobsPanel';
import { SlicerToolbar } from '@/features/slicer/components/viewer/SlicerToolbar';

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
 * The exact hint the backend sends for a `SlicingEngineRejectedModel` failure (issue #1811,
 * reworded for #1962). It names the "Auto-Orient" and "Lay Flat" model-tool-rail buttons —
 * the controls that actually resolved every affected model when issue #1811 was reproduced
 * against the real OrcaSlicer CLI. An earlier revision instead named a plate-level
 * "Auto-orient plate" control that never shipped in `SlicerToolbar` (#1962).
 */
const ENGINE_REJECTED_HINT =
  'The slicing engine could not slice this model. This most often happens when a model sits in ' +
  'an orientation the engine cannot handle — select the model and try the "Auto-Orient" or "Lay ' +
  'Flat" button in the model tools in the slicer workspace, then slice again. If it still fails, ' +
  "ask a farm admin to check the job's error detail.";

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
    expect(notice.textContent).toContain('Auto-Orient');
    expect(notice.textContent).toContain('most often');
  });

  it('names controls that actually exist in the rendered slicer toolbar (#1962)', async () => {
    // Ties the hint text to the real rendered button labels so the two cannot silently drift
    // apart again: this failed for #1962 because the hint named a "Auto-orient plate" control
    // that no toolbar button ever rendered.
    render(<SlicerToolbar hasSelection />);
    const autoOrientButton = screen.getByTitle('Auto-Orient');
    const layFlatButton = screen.getByTitle('Lay Flat (F)');

    expect(ENGINE_REJECTED_HINT).toContain(autoOrientButton.getAttribute('title')!);
    expect(ENGINE_REJECTED_HINT).toContain('Lay Flat');
    expect(layFlatButton).toBeInTheDocument();
    expect(ENGINE_REJECTED_HINT).not.toContain('Auto-orient plate');
    expect(ENGINE_REJECTED_HINT).not.toContain('plate controls');
    expect(ENGINE_REJECTED_HINT).not.toContain('unlock the plate');
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
