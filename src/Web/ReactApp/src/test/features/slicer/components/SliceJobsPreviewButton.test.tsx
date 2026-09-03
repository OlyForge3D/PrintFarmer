import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { SliceJobsPanel } from '@/features/slicer/components/SliceJobsPanel';
import { toast } from 'sonner';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

const mockGetMyJobs = vi.fn();
const mockGetArtifactsByRoute = vi.fn();
const viewModeState = vi.hoisted(() => ({ value: 'grid' as 'grid' | 'explorer' }));

vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    getMyJobs: (...args: unknown[]) => mockGetMyJobs(...args),
    getArtifactsByRoute: (...args: unknown[]) => mockGetArtifactsByRoute(...args),
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
}));

vi.mock('@/features/slicer/hooks/useSliceJobsRealtime', () => ({
  useSliceJobsRealtime: () => ({ isConnected: false }),
}));

vi.mock('@/features/slicer/components/SendToPrinterModal', () => ({
  SendToPrinterModal: ({
    isOpen,
    jobId,
    artifactId,
  }: {
    isOpen: boolean;
    jobId: string;
    artifactId: string;
  }) => isOpen
    ? <div data-testid="print-modal">{jobId}:{artifactId}</div>
    : null,
}));

vi.mock('@/features/slicer/components/GcodePreviewModal', () => ({
  GcodePreviewModal: ({
    isOpen,
    artifactsRoute,
  }: {
    isOpen: boolean;
    artifactsRoute: string;
  }) => isOpen ? <div data-testid="gcode-preview-modal">Preview: {artifactsRoute}</div> : null,
}));

vi.mock('@/common/hooks/useViewModePreference', () => ({
  useViewModePreference: () => ({ viewMode: viewModeState.value, setViewMode: vi.fn() }),
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

describe('SliceJobsPanel Preview button', () => {
  beforeEach(() => {
    mockGetMyJobs.mockReset();
    mockGetArtifactsByRoute.mockReset();
    viewModeState.value = 'grid';
  });

  it.each(['grid', 'explorer'] as const)(
    'shows all staged-artifact actions for completed jobs in %s view',
    async (viewMode) => {
      viewModeState.value = viewMode;
      mockGetMyJobs.mockResolvedValue([
        {
          id: 'job-completed-actions',
          status: 'Completed',
          progressPercent: 100,
          queuedAt: '2026-05-31T09:00:00Z',
          completedAt: '2026-05-31T09:05:00Z',
          artifactsRoute: '/api/artifacts/job/job-completed-actions',
        },
      ]);
      mockGetArtifactsByRoute.mockResolvedValue([
        {
          id: 'newest-artifact',
          fileName: 'newest.gcode',
          createdAt: '2026-09-03T10:01:00Z',
          isPrimary: false,
        },
        {
          id: 'primary-artifact',
          fileName: 'primary.gcode',
          createdAt: '2026-09-03T10:00:00Z',
          isPrimary: true,
        },
      ]);

      renderPanel();

      expect(await screen.findByRole('button', { name: /preview/i })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /download/i })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /save to library/i })).toBeInTheDocument();
      await userEvent.click(screen.getByRole('button', { name: /print/i }));

      expect(await screen.findByTestId('print-modal')).toHaveTextContent(
        'job-completed-actions:primary-artifact',
      );
      expect(mockGetArtifactsByRoute).toHaveBeenCalledWith(
        '/api/artifacts/job/job-completed-actions',
      );
      expect(screen.getByTestId('print-modal')).not.toHaveTextContent('newest-artifact');
    },
  );

  it('shows Preview button for completed jobs', async () => {
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-completed-1',
        status: 'Completed',
        progressPercent: 100,
        queuedAt: '2026-05-31T09:00:00Z',
        startedAt: '2026-05-31T09:01:00Z',
        completedAt: '2026-05-31T09:05:00Z',
        artifactsCount: 1,
        artifactsTotalBytes: 5000,
        artifactsRoute: '/api/artifacts/job/job-completed-1',
      },
    ]);

    renderPanel();

    const previewButton = await screen.findByRole('button', { name: /preview/i });
    expect(previewButton).toBeDefined();
  });

  it('blocks Print and surfaces selection-required state when no primary is declared', async () => {
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-ambiguous',
        status: 'Completed',
        progressPercent: 100,
        queuedAt: '2026-05-31T09:00:00Z',
        completedAt: '2026-05-31T09:05:00Z',
        artifactsRoute: '/api/artifacts/job/job-ambiguous',
      },
    ]);
    mockGetArtifactsByRoute.mockResolvedValue([
      { id: 'artifact-1', fileName: 'first.gcode', isPrimary: false },
      { id: 'artifact-2', fileName: 'second.gcode', isPrimary: false },
    ]);

    renderPanel();
    await userEvent.click(await screen.findByRole('button', { name: /print/i }));

    expect(toast.error).toHaveBeenCalledWith(
      expect.stringMatching(/did not declare exactly one valid primary artifact/i),
    );
    expect(screen.queryByTestId('print-modal')).not.toBeInTheDocument();
  });

  it('does not show Preview button for in-progress jobs', async () => {
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-processing-1',
        status: 'Processing',
        progressPercent: 50,
        queuedAt: '2026-05-31T09:00:00Z',
        startedAt: '2026-05-31T09:01:00Z',
      },
    ]);

    renderPanel();

    // Wait for data to render (job ID appears in the card)
    await screen.findByText(/job-proc/);

    const previewButtons = screen.queryAllByRole('button', { name: /preview/i });
    expect(previewButtons).toHaveLength(0);
  });

  it('does not show Preview button for failed jobs', async () => {
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-failed-1',
        status: 'Failed',
        progressPercent: 0,
        queuedAt: '2026-05-31T09:00:00Z',
        errorMessage: 'Slicer crash',
      },
    ]);

    renderPanel();

    await screen.findByText(/job-fail/);

    const previewButtons = screen.queryAllByRole('button', { name: /preview/i });
    expect(previewButtons).toHaveLength(0);
  });

  it('opens preview modal when Preview button is clicked', async () => {
    const user = userEvent.setup();
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-completed-2',
        status: 'Completed',
        progressPercent: 100,
        queuedAt: '2026-05-31T09:00:00Z',
        startedAt: '2026-05-31T09:01:00Z',
        completedAt: '2026-05-31T09:05:00Z',
        artifactsCount: 1,
        artifactsTotalBytes: 8000,
        artifactsRoute: '/api/artifacts/job/job-completed-2',
      },
    ]);

    renderPanel();

    const previewButton = await screen.findByRole('button', { name: /preview/i });
    await user.click(previewButton);

    const modal = await screen.findByTestId('gcode-preview-modal');
    expect(modal.textContent).toContain('/api/artifacts/job/job-completed-2');
  });
});
