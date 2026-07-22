import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
}));

vi.mock('@/features/slicer/hooks/useSliceJobsRealtime', () => ({
  useSliceJobsRealtime: () => ({ isConnected: false }),
}));

vi.mock('@/features/slicer/components/SendToPrinterModal', () => ({
  SendToPrinterModal: () => null,
}));

vi.mock('@/features/slicer/components/GcodePreviewModal', () => ({
  GcodePreviewModal: ({ isOpen, jobId }: { isOpen: boolean; jobId: string }) =>
    isOpen ? <div data-testid="gcode-preview-modal">Preview: {jobId}</div> : null,
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

describe('SliceJobsPanel Preview button', () => {
  beforeEach(() => {
    mockGetMyJobs.mockReset();
  });

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
      },
    ]);

    renderPanel();

    const previewButton = await screen.findByRole('button', { name: /preview/i });
    expect(previewButton).toBeDefined();
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
      },
    ]);

    renderPanel();

    const previewButton = await screen.findByRole('button', { name: /preview/i });
    await user.click(previewButton);

    const modal = await screen.findByTestId('gcode-preview-modal');
    expect(modal.textContent).toContain('job-completed-2');
  });
});
