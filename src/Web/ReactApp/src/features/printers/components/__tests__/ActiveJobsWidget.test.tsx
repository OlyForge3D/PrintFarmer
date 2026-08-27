import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ActiveJobsWidget } from '../ActiveJobsWidget';

// Regression test for issue #2101: the dashboard widget summed all active jobs
// (Queued + Assigned + Printing + Paused) but labeled the total "in queue", even
// though /printQueue's own stats correctly separate queued/printing/paused.

vi.mock('@/common/hooks/useApi', () => ({
  useJobQueue: vi.fn(),
}));

import { useJobQueue } from '@/common/hooks/useApi';

describe('ActiveJobsWidget', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });
    vi.clearAllMocks();
  });

  const renderWidget = (props = {}) => {
    return render(
      <MemoryRouter>
        <QueryClientProvider client={queryClient}>
          <ActiveJobsWidget {...props} />
        </QueryClientProvider>
      </MemoryRouter>
    );
  };

  it('labels a mix of Printing and Paused jobs (0 Queued) as active jobs, not queued', () => {
    // Matches the reported repro: /printQueue shows 0 queued, 1 printing, 1 paused,
    // yet the dashboard previously said "2 jobs in queue".
    vi.mocked(useJobQueue).mockReturnValue({
      data: [
        {
          job: { id: '1', status: 'Printing', queuePosition: 0 },
          gcodeFile: null,
          assignedPrinter: null,
        },
        {
          job: { id: '2', status: 'Paused', queuePosition: 0 },
          gcodeFile: null,
          assignedPrinter: null,
        },
      ],
    } as unknown as ReturnType<typeof useJobQueue>);

    renderWidget();

    expect(screen.getByText('2 active jobs')).toBeInTheDocument();
    expect(screen.queryByText(/in queue/i)).not.toBeInTheDocument();
  });

  it('singularizes the label for a single active job', () => {
    vi.mocked(useJobQueue).mockReturnValue({
      data: [
        {
          job: { id: '1', status: 'Printing', queuePosition: 0 },
          gcodeFile: null,
          assignedPrinter: null,
        },
      ],
    } as unknown as ReturnType<typeof useJobQueue>);

    renderWidget();

    expect(screen.getByText('1 active job')).toBeInTheDocument();
  });

  it('shows "No Active Jobs" empty state when the queue is empty', () => {
    vi.mocked(useJobQueue).mockReturnValue({
      data: [],
    } as unknown as ReturnType<typeof useJobQueue>);

    renderWidget();

    expect(screen.getByText('No active jobs')).toBeInTheDocument();
    expect(screen.getByText('No Active Jobs')).toBeInTheDocument();
    expect(screen.queryByText(/in queue/i)).not.toBeInTheDocument();
  });
});
