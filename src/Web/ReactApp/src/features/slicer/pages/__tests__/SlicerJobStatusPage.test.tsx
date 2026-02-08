import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { BrowserRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SlicerJobStatusPage } from '../SlicerJobStatusPage';

// Mock the API client
vi.mock('@/services/api', () => ({
  apiClient: {
    getSlicerJobStatus: vi.fn(),
  },
}));

describe('SlicerJobStatusPage', () => {
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

  it('should render page title', () => {
    renderWithProviders(<SlicerJobStatusPage />);

    expect(screen.getByText('Slicer Job Status')).toBeInTheDocument();
  });

  it('should render subtitle', () => {
    renderWithProviders(<SlicerJobStatusPage />);

    expect(screen.getByText(/Query a job to view scheduling and retry metadata/i)).toBeInTheDocument();
  });

  it('should have job ID input field', () => {
    renderWithProviders(<SlicerJobStatusPage />);

    const input = screen.getByPlaceholderText('Enter job GUID');
    expect(input).toBeInTheDocument();
  });

  it('should have fetch button', () => {
    renderWithProviders(<SlicerJobStatusPage />);

    const fetchButton = screen.getByText('Fetch');
    expect(fetchButton).toBeInTheDocument();
  });

  it('should disable fetch button when job ID is empty', () => {
    renderWithProviders(<SlicerJobStatusPage />);

    const fetchButton = screen.getByRole('button', { name: /Fetch/i });
    expect(fetchButton).toBeDisabled();
  });

  it('should enable fetch button when job ID is entered', () => {
    renderWithProviders(<SlicerJobStatusPage />);

    const input = screen.getByPlaceholderText('Enter job GUID');
    fireEvent.change(input, { target: { value: 'job-123' } });

    const fetchButton = screen.getByText('Fetch');
    expect(fetchButton).not.toBeDisabled();
  });

  it('should fetch job status when button is clicked', async () => {
    const { apiClient } = await import('@/services/api');
    const mockStatus = {
      id: 'job-123',
      status: 'completed',
      progress: 100,
      createdAt: '2024-01-01T00:00:00Z',
    };

    vi.mocked(apiClient.getSlicerJobStatus).mockResolvedValue(mockStatus as never);

    renderWithProviders(<SlicerJobStatusPage />);

    const input = screen.getByPlaceholderText('Enter job GUID');
    fireEvent.change(input, { target: { value: 'job-123' } });

    const fetchButton = screen.getByText('Fetch');
    fireEvent.click(fetchButton);

    await waitFor(() => {
      expect(apiClient.getSlicerJobStatus).toHaveBeenCalledWith('job-123');
    });
  });

  it('should display loading state when fetching', async () => {
    const { apiClient } = await import('@/services/api');
    vi.mocked(apiClient.getSlicerJobStatus).mockImplementation(() => new Promise(() => {}));

    renderWithProviders(<SlicerJobStatusPage />);

    const input = screen.getByPlaceholderText('Enter job GUID');
    fireEvent.change(input, { target: { value: 'job-123' } });

    const fetchButton = screen.getByText('Fetch');
    fireEvent.click(fetchButton);

    await waitFor(() => {
      expect(screen.getByText('Loading...')).toBeInTheDocument();
    });
  });

  it('should display error when job not found', async () => {
    const { apiClient } = await import('@/services/api');
    const error = new Error('Not Found');
    vi.mocked(apiClient.getSlicerJobStatus).mockRejectedValue(error);

    renderWithProviders(<SlicerJobStatusPage />);

    const input = screen.getByPlaceholderText('Enter job GUID');
    fireEvent.change(input, { target: { value: 'non-existent' } });

    const fetchButton = screen.getByText('Fetch');
    fireEvent.click(fetchButton);

    await waitFor(() => {
      expect(screen.getByText('Job not found')).toBeInTheDocument();
    });
  });

  it('should display job status details when loaded', async () => {
    const { apiClient } = await import('@/services/api');
    const mockStatus = {
      id: 'job-123',
      status: 'completed',
      progress: 100,
      retryCount: 0,
      createdAt: '2024-01-01T00:00:00Z',
    };

    vi.mocked(apiClient.getSlicerJobStatus).mockResolvedValue(mockStatus as never);

    renderWithProviders(<SlicerJobStatusPage />);

    const input = screen.getByPlaceholderText('Enter job GUID');
    fireEvent.change(input, { target: { value: 'job-123' } });

    const fetchButton = screen.getByText('Fetch');
    fireEvent.click(fetchButton);

    await waitFor(() => {
      expect(screen.getByText('Status:')).toBeInTheDocument();
      expect(screen.getByText('completed')).toBeInTheDocument();
    });
  });

  it('should clear previous status when fetching new job', async () => {
    const { apiClient } = await import('@/services/api');
    
    // First job
    vi.mocked(apiClient.getSlicerJobStatus).mockResolvedValueOnce({
      id: 'job-1',
      status: 'completed',
      progress: 100,
    } as never);

    renderWithProviders(<SlicerJobStatusPage />);

    const input = screen.getByPlaceholderText('Enter job GUID');
    fireEvent.change(input, { target: { value: 'job-1' } });
    fireEvent.click(screen.getByText('Fetch'));

    await waitFor(() => {
      expect(screen.getByText('completed')).toBeInTheDocument();
    });

    // Second job
    vi.mocked(apiClient.getSlicerJobStatus).mockResolvedValueOnce({
      id: 'job-2',
      status: 'pending',
      progress: 0,
    } as never);

    fireEvent.change(input, { target: { value: 'job-2' } });
    fireEvent.click(screen.getByText('Fetch'));

    await waitFor(() => {
      expect(apiClient.getSlicerJobStatus).toHaveBeenCalledWith('job-2');
    });
  });
});
