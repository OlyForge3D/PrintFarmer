import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { BrowserRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobQueueDashboardPage } from '../JobQueueDashboardPage';

// Mock the slice job service
vi.mock('@/services/sliceJobService', () => ({
  sliceJobService: {
    getMyJobs: vi.fn().mockResolvedValue([]),
    getQueue: vi.fn().mockResolvedValue([]),
    cancelJob: vi.fn().mockResolvedValue(undefined),
    getStatusColor: vi.fn().mockReturnValue('bg-blue-100 text-blue-800'),
    getStatusText: vi.fn().mockReturnValue('Queued'),
  },
  SliceJobStatus: {
    Queued: 'Queued',
    Slicing: 'Slicing',
    Completed: 'Completed',
    Failed: 'Failed',
    Cancelled: 'Cancelled',
  },
}));

describe('JobQueueDashboardPage', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });
    vi.clearAllMocks();
  });

  const renderWithProviders = (ui: React.ReactElement) => {
    return render(
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          {ui}
        </BrowserRouter>
      </QueryClientProvider>
    );
  };

  it('should render page title', async () => {
    renderWithProviders(<JobQueueDashboardPage />);

    await waitFor(() => {
      expect(screen.getByText(/Slice Job Queue/i)).toBeInTheDocument();
    });
  });

  it('should load jobs on mount', async () => {
    const { sliceJobService } = await import('@/services/sliceJobService');
    
    renderWithProviders(<JobQueueDashboardPage />);

    await waitFor(() => {
      expect(sliceJobService.getMyJobs).toHaveBeenCalled();
    });
  });

  it('should render loading state', () => {
    renderWithProviders(<JobQueueDashboardPage />);

    expect(screen.getByText(/Loading jobs/i)).toBeInTheDocument();
  });

  it('should render with empty jobs list', async () => {
    const { sliceJobService } = await import('@/services/sliceJobService');
    vi.mocked(sliceJobService.getMyJobs).mockResolvedValue([]);

    renderWithProviders(<JobQueueDashboardPage />);

    await waitFor(() => {
      expect(screen.queryByText(/Loading jobs/i)).not.toBeInTheDocument();
    });
  });

  it('should render with jobs data', async () => {
    const mockJobs = [
      {
        id: 'job-1',
        modelName: 'Test Model',
        status: 'Queued' as never,
        progress: 0,
        createdAt: '2024-01-01T00:00:00Z',
      },
    ];

    const { sliceJobService } = await import('@/services/sliceJobService');
    vi.mocked(sliceJobService.getMyJobs).mockResolvedValue(mockJobs as never);

    renderWithProviders(<JobQueueDashboardPage />);

    await waitFor(() => {
      expect(sliceJobService.getMyJobs).toHaveBeenCalled();
    });
  });

  it('should display error when job loading fails', async () => {
    const { sliceJobService } = await import('@/services/sliceJobService');
    vi.mocked(sliceJobService.getMyJobs).mockRejectedValue(new Error('Failed to load jobs'));

    renderWithProviders(<JobQueueDashboardPage />);

    await waitFor(() => {
      expect(screen.getByText(/Failed to load jobs/i)).toBeInTheDocument();
    });
  });

  it('should have refresh button', async () => {
    renderWithProviders(<JobQueueDashboardPage />);

    await waitFor(() => {
      expect(screen.getByText('Refresh')).toBeInTheDocument();
    });
  });

  it('should have new job button', async () => {
    renderWithProviders(<JobQueueDashboardPage />);

    await waitFor(() => {
      expect(screen.getByText('New Job')).toBeInTheDocument();
    });
  });

  it('should have view full queue button', async () => {
    renderWithProviders(<JobQueueDashboardPage />);

    await waitFor(() => {
      expect(screen.getByText('View Full Queue')).toBeInTheDocument();
    });
  });

  it('should display subtitle', async () => {
    renderWithProviders(<JobQueueDashboardPage />);

    await waitFor(() => {
      expect(screen.getByText(/Your slice jobs and progress/i)).toBeInTheDocument();
    });
  });
});
