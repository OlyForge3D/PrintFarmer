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

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ user: { id: 'user-1' }, hasRole: () => false }),
}));

vi.mock('@/features/slicer/components/SendToPrinterModal', () => ({
  SendToPrinterModal: () => null,
}));

vi.mock('@/features/slicer/components/GcodePreviewModal', () => ({
  GcodePreviewModal: () => null,
}));

// Failed job rows in the "workers > jobs" grid render via the explorer (table) view.
vi.mock('@/common/hooks/useViewModePreference', () => ({
  useViewModePreference: () => ({ viewMode: 'explorer', setViewMode: vi.fn() }),
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

describe('SliceJobsPanel failed job row keyboard expansion', () => {
  beforeEach(() => {
    mockGetMyJobs.mockReset();
    mockGetMyJobs.mockResolvedValue([
      {
        id: 'job-failed-keyboard-1',
        status: 'Failed',
        progressPercent: 0,
        queuedAt: '2026-05-31T09:00:00Z',
        errorMessage: 'Slicer crashed unexpectedly',
      },
    ]);
  });

  it('exposes the row as a focusable, collapsed control by default', async () => {
    renderPanel();

    const row = await screen.findByRole('row', { name: /job-fail/i });
    expect(row.getAttribute('tabindex')).toBe('0');
    expect(row.getAttribute('aria-expanded')).toBe('false');
  });

  it('expands the row details when Enter is pressed while focused', async () => {
    const user = userEvent.setup();
    renderPanel();

    const row = await screen.findByRole('row', { name: /job-fail/i });
    row.focus();
    expect(row).toHaveFocus();

    await user.keyboard('{Enter}');

    expect(row.getAttribute('aria-expanded')).toBe('true');
    expect(await screen.findByText('Slicer crashed unexpectedly')).toBeDefined();
  });

  it('toggles the row details when Space is pressed while focused', async () => {
    const user = userEvent.setup();
    renderPanel();

    const row = await screen.findByRole('row', { name: /job-fail/i });
    row.focus();

    await user.keyboard(' ');
    expect(row.getAttribute('aria-expanded')).toBe('true');

    await user.keyboard(' ');
    expect(row.getAttribute('aria-expanded')).toBe('false');
  });

  it('still expands the row when clicked with a pointer (no regression)', async () => {
    const user = userEvent.setup();
    renderPanel();

    const row = await screen.findByRole('row', { name: /job-fail/i });
    await user.click(row);

    expect(row.getAttribute('aria-expanded')).toBe('true');
    expect(await screen.findByText('Slicer crashed unexpectedly')).toBeDefined();
  });

  it('does not toggle the row when Enter is pressed on a nested action button', async () => {
    const user = userEvent.setup();
    renderPanel();

    const row = await screen.findByRole('row', { name: /job-fail/i });
    const retryButton = await screen.findByRole('button', { name: /retry job/i });

    retryButton.focus();
    await user.keyboard('{Enter}');

    expect(row.getAttribute('aria-expanded')).toBe('false');
  });
});
