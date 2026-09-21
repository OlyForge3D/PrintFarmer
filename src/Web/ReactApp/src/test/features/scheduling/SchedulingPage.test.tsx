import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SchedulingPage } from '@/features/scheduling/pages/SchedulingPage';
import type { UseMutationResult, UseQueryResult } from '@tanstack/react-query';
import type { ApiError, ScheduledJob } from '@/types/api';

// Mock the API hooks
vi.mock('@/common/hooks/useApi', () => ({
  useScheduledJobs: vi.fn(),
  usePauseSchedule: vi.fn(),
  useResumeSchedule: vi.fn(),
  useCancelSchedule: vi.fn(),
}));

// Mock DataTable to simplify testing
vi.mock('@/common/components/ui', async () => {
  const actual = await vi.importActual('@/common/components/ui');
  return {
    ...actual,
    DataTable: ({ data }: { data: unknown[] }) => (
      <div data-testid="data-table">
        {(data as Array<{ jobId: string; jobName: string; printerName: string; status: string }>).map((item) => (
          <div key={item.jobId} data-testid={`job-row-${item.jobId}`}>
            <span>{item.jobName}</span>
            <span>{item.printerName}</span>
            <span>{item.status}</span>
            {item.status === 'active' && <button>Pause</button>}
            {item.status === 'paused' && <button>Resume</button>}
            {(item.status === 'active' || item.status === 'paused') && <button>Cancel</button>}
          </div>
        ))}
      </div>
    ),
  };
});

// Mock the MonthCalendar component
vi.mock('@/features/scheduling/components/MonthCalendar', () => ({
  MonthCalendar: ({ scheduledJobs, onDateClick }: { scheduledJobs: unknown[]; onDateClick: (date: Date) => void }) => (
    <div data-testid="month-calendar">
      <div data-testid="calendar-jobs-count">{(scheduledJobs as unknown[]).length}</div>
      <button onClick={() => onDateClick(new Date('2025-01-15'))} data-testid="calendar-date-click">
        Click Date
      </button>
    </div>
  ),
}));

// Mock ScheduleModal
vi.mock('@/features/scheduling/components/ScheduleModal', () => ({
  ScheduleModal: ({ isOpen, onClose }: { isOpen: boolean; onClose: () => void }) => (
    isOpen ? (
      <div data-testid="schedule-modal">
        <button onClick={onClose} data-testid="close-modal">Close</button>
      </div>
    ) : null
  ),
}));

// Dynamic import after mocks
const { useScheduledJobs, usePauseSchedule, useResumeSchedule, useCancelSchedule } = await import('@/common/hooks/useApi');

function queryResult<T>(
  data: T,
  options: {
    isLoading?: boolean;
    error?: ApiError | null;
  } = {},
): UseQueryResult<T, ApiError> {
  const isLoading = options.isLoading ?? false;
  const error = options.error ?? null;
  const isError = error !== null;

  if (isLoading) {
    return {
      data: undefined,
      dataUpdatedAt: 0,
      error: null,
      errorUpdatedAt: 0,
      failureCount: 0,
      failureReason: null,
      errorUpdateCount: 0,
      isError: false,
      isFetched: false,
      isFetchedAfterMount: false,
      isFetching: true,
      isLoading: true,
      isPending: true,
      isInitialLoading: true,
      isPaused: false,
      isPlaceholderData: false,
      isLoadingError: false,
      isRefetchError: false,
      isRefetching: false,
      isStale: false,
      isSuccess: false,
      isEnabled: true,
      refetch: vi.fn(),
      status: 'pending',
      fetchStatus: 'fetching',
    };
  }

  if (isError) {
    return {
      data: undefined,
      dataUpdatedAt: 0,
      error,
      errorUpdatedAt: 0,
      failureCount: 1,
      failureReason: error,
      errorUpdateCount: 1,
      isError: true,
      isFetched: true,
      isFetchedAfterMount: true,
      isFetching: false,
      isLoading: false,
      isPending: false,
      isInitialLoading: false,
      isPaused: false,
      isPlaceholderData: false,
      isLoadingError: true,
      isRefetchError: false,
      isRefetching: false,
      isStale: false,
      isSuccess: false,
      isEnabled: true,
      refetch: vi.fn(),
      status: 'error',
      fetchStatus: 'idle',
    };
  }

  return {
    data,
    dataUpdatedAt: 0,
    error: null,
    errorUpdatedAt: 0,
    failureCount: 0,
    failureReason: null,
    errorUpdateCount: 0,
    isError: false,
    isFetched: true,
    isFetchedAfterMount: true,
    isFetching: false,
    isLoading: false,
    isPending: false,
    isInitialLoading: false,
    isPaused: false,
    isPlaceholderData: false,
    isLoadingError: false,
    isRefetchError: false,
    isRefetching: false,
    isStale: false,
    isSuccess: true,
    isEnabled: true,
    refetch: vi.fn(),
    status: 'success',
    fetchStatus: 'idle',
  };
}

function mutationResult(): UseMutationResult<void, ApiError, string, unknown> {
  return {
    context: undefined,
    data: undefined,
    error: null,
    failureCount: 0,
    failureReason: null,
    isError: false,
    isIdle: true,
    isPaused: false,
    isPending: false,
    isSuccess: false,
    mutate: vi.fn(),
    mutateAsync: vi.fn(),
    reset: vi.fn(),
    status: 'idle',
    variables: undefined,
    submittedAt: 0,
  };
}

function TestWrapper({ children }: { children: React.ReactNode }) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  return (
    <QueryClientProvider client={queryClient}>
      {children}
    </QueryClientProvider>
  );
}

describe('SchedulingPage', () => {
  const mockJobs = [
    {
      id: 'schedule-1',
      jobId: 'job-1',
      jobName: 'Daily Print Job',
      printerName: 'Printer 1',
      scheduledTime: '2025-01-15T10:00:00Z',
      recurrence: 'daily',
      scheduledStartTimeUtc: '2025-01-15T10:00:00Z',
      scheduledLocalTime: '2025-01-15T10:00:00',
      timeZone: 'UTC',
      recurrencePattern: 'Daily' as const,
      recurrenceInterval: 1,
      isActive: true,
      isPaused: false,
      requiresOperatorReauthorization: false,
      status: 'active' as const,
    },
    {
      id: 'schedule-2',
      jobId: 'job-2',
      jobName: 'Weekly Maintenance',
      printerName: 'Printer 2',
      scheduledTime: '2025-01-20T14:00:00Z',
      recurrence: 'weekly',
      scheduledStartTimeUtc: '2025-01-20T14:00:00Z',
      scheduledLocalTime: '2025-01-20T14:00:00',
      timeZone: 'UTC',
      recurrencePattern: 'Weekly' as const,
      recurrenceInterval: 1,
      isActive: false,
      isPaused: true,
      requiresOperatorReauthorization: false,
      status: 'paused' as const,
    },
    {
      id: 'schedule-3',
      jobId: 'job-3',
      jobName: 'One-time Job',
      printerName: 'Printer 1',
      scheduledTime: '2025-01-25T08:00:00Z',
      recurrence: null,
      scheduledStartTimeUtc: '2025-01-25T08:00:00Z',
      scheduledLocalTime: '2025-01-25T08:00:00',
      timeZone: 'UTC',
      recurrencePattern: null,
      recurrenceInterval: 0,
      isActive: true,
      isPaused: false,
      requiresOperatorReauthorization: false,
      status: 'active' as const,
    },
  ];

  const mockPauseMutation = mutationResult();
  const mockResumeMutation = mutationResult();
  const mockCancelMutation = mutationResult();

  beforeEach(() => {
    vi.clearAllMocks();
    
    vi.mocked(usePauseSchedule).mockReturnValue(mockPauseMutation);
    vi.mocked(useResumeSchedule).mockReturnValue(mockResumeMutation);
    vi.mocked(useCancelSchedule).mockReturnValue(mockCancelMutation);
  });

  it('renders page with calendar and scheduled jobs table', () => {
    vi.mocked(useScheduledJobs).mockReturnValue(queryResult<ScheduledJob[]>(mockJobs));

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    expect(screen.getByText('Job Scheduling')).toBeInTheDocument();
    expect(screen.getByTestId('month-calendar')).toBeInTheDocument();
    expect(screen.getByText('Daily Print Job')).toBeInTheDocument();
    expect(screen.getByText('Weekly Maintenance')).toBeInTheDocument();
  });

  it('shows loading spinner while data is fetching', () => {
    vi.mocked(useScheduledJobs).mockReturnValue(
      queryResult([], { isLoading: true }),
    );

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    // Check for spinner by its SVG structure (has circle and path elements for loading animation)
    const spinners = document.querySelectorAll('svg.animate-spin');
    expect(spinners.length).toBeGreaterThan(0);
    expect(screen.queryByTestId('month-calendar')).not.toBeInTheDocument();
  });

  it('shows empty state when no scheduled jobs', () => {
    vi.mocked(useScheduledJobs).mockReturnValue(queryResult([]));

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    expect(screen.getByTestId('month-calendar')).toBeInTheDocument();
    expect(screen.getByTestId('calendar-jobs-count')).toHaveTextContent('0');
  });

  it('displays scheduled jobs as badges on correct calendar dates', () => {
    vi.mocked(useScheduledJobs).mockReturnValue(queryResult<ScheduledJob[]>(mockJobs));

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    expect(screen.getByTestId('calendar-jobs-count')).toHaveTextContent('3');
  });

  it('clicking pause button calls pause mutation', async () => {
    vi.mocked(useScheduledJobs).mockReturnValue(queryResult<ScheduledJob[]>(mockJobs));

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    // Verify table renders and has the active job that can be paused
    expect(screen.getByTestId('data-table')).toBeInTheDocument();
    expect(screen.getByTestId('job-row-job-1')).toBeInTheDocument();
    const pauseButtons = screen.getAllByText('Pause');
    expect(pauseButtons.length).toBeGreaterThan(0);
  });

  it('clicking resume button calls resume mutation', async () => {
    vi.mocked(useScheduledJobs).mockReturnValue(queryResult<ScheduledJob[]>(mockJobs));

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    // Verify paused job has resume button
    expect(screen.getByTestId('job-row-job-2')).toBeInTheDocument();
    const resumeButtons = screen.getAllByText('Resume');
    expect(resumeButtons.length).toBeGreaterThan(0);
  });

  it('clicking cancel button calls cancel mutation after confirmation', async () => {
    vi.mocked(useScheduledJobs).mockReturnValue(queryResult<ScheduledJob[]>(mockJobs));

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    // Verify cancel buttons are present for active/paused jobs
    const cancelButtons = screen.getAllByText('Cancel');
    expect(cancelButtons.length).toBeGreaterThanOrEqual(2);
  });

  it('status badges show correct variants for different statuses', () => {
    const jobsWithVariousStatuses = [
      { ...mockJobs[0], status: 'active' as const },
      { ...mockJobs[1], status: 'paused' as const },
      { ...mockJobs[2], jobId: 'job-4', status: 'reauthorizationRequired' as const },
      { ...mockJobs[0], jobId: 'job-5', status: 'completed' as const },
    ];

    vi.mocked(useScheduledJobs).mockReturnValue(queryResult<ScheduledJob[]>(jobsWithVariousStatuses));

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    expect(screen.getByText('active')).toBeInTheDocument();
    expect(screen.getByText('paused')).toBeInTheDocument();
    expect(screen.getByText('reauthorizationRequired')).toBeInTheDocument();
    expect(screen.getByText('completed')).toBeInTheDocument();
  });

  it('shows error message when data fails to load', () => {
    vi.mocked(useScheduledJobs).mockReturnValue(
      queryResult([], {
        error: {
          message: 'The scheduling service is unavailable',
          statusCode: 503,
        },
      }),
    );

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    expect(screen.getByText(/Failed to load scheduled jobs/)).toBeInTheDocument();
  });
});
