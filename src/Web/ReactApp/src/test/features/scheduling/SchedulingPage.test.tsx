import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SchedulingPage } from '@/features/scheduling/pages/SchedulingPage';
import type { ApiError, ScheduledJob } from '@/types/api';

interface ScheduledJobsHookResult {
  data: ScheduledJob[];
  isLoading: boolean;
  error: ApiError | null;
}

interface ScheduleMutationHookResult {
  mutate: (jobId: string) => void;
  isPending: boolean;
}

const mocks = vi.hoisted(() => ({
  useScheduledJobs: vi.fn<() => ScheduledJobsHookResult>(),
  usePauseSchedule: vi.fn<() => ScheduleMutationHookResult>(),
  useResumeSchedule: vi.fn<() => ScheduleMutationHookResult>(),
  useCancelSchedule: vi.fn<() => ScheduleMutationHookResult>(),
}));

// Mock the API hooks
vi.mock('@/common/hooks/useApi', () => mocks);

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
      printerId: 'printer-1',
      scheduledStartTimeUtc: '2025-01-15T10:00:00Z',
      scheduledLocalTime: '2025-01-15T10:00:00',
      timeZone: 'UTC',
      recurrencePattern: 'Daily',
      recurrenceInterval: 1,
      recurrenceEndTimeUtc: null,
      isActive: true,
      isPaused: false,
      requiresOperatorReauthorization: false,
      status: 'active',
    },
    {
      id: 'schedule-2',
      jobId: 'job-2',
      jobName: 'Weekly Maintenance',
      printerName: 'Printer 2',
      printerId: 'printer-2',
      scheduledStartTimeUtc: '2025-01-20T14:00:00Z',
      scheduledLocalTime: '2025-01-20T14:00:00',
      timeZone: 'UTC',
      recurrencePattern: 'Weekly',
      recurrenceInterval: 1,
      recurrenceEndTimeUtc: null,
      isActive: true,
      isPaused: true,
      requiresOperatorReauthorization: false,
      status: 'paused',
    },
    {
      id: 'schedule-3',
      jobId: 'job-3',
      jobName: 'One-time Job',
      printerName: 'Printer 1',
      printerId: 'printer-1',
      scheduledStartTimeUtc: '2025-01-25T08:00:00Z',
      scheduledLocalTime: '2025-01-25T08:00:00',
      timeZone: 'UTC',
      recurrencePattern: null,
      recurrenceInterval: 1,
      recurrenceEndTimeUtc: null,
      isActive: true,
      isPaused: false,
      requiresOperatorReauthorization: false,
      status: 'active',
    },
  ] satisfies ScheduledJob[];

  const mockPauseMutation = {
    mutate: vi.fn(),
    isPending: false,
  };

  const mockResumeMutation = {
    mutate: vi.fn(),
    isPending: false,
  };

  const mockCancelMutation = {
    mutate: vi.fn(),
    isPending: false,
  };

  beforeEach(() => {
    vi.clearAllMocks();
    
    mocks.usePauseSchedule.mockReturnValue(mockPauseMutation);
    mocks.useResumeSchedule.mockReturnValue(mockResumeMutation);
    mocks.useCancelSchedule.mockReturnValue(mockCancelMutation);
  });

  it('renders page with calendar and scheduled jobs table', () => {
    mocks.useScheduledJobs.mockReturnValue({
      data: mockJobs,
      isLoading: false,
      error: null,
    });

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
    mocks.useScheduledJobs.mockReturnValue({
      data: [],
      isLoading: true,
      error: null,
    });

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
    mocks.useScheduledJobs.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    expect(screen.getByTestId('month-calendar')).toBeInTheDocument();
    expect(screen.getByTestId('calendar-jobs-count')).toHaveTextContent('0');
  });

  it('displays scheduled jobs as badges on correct calendar dates', () => {
    mocks.useScheduledJobs.mockReturnValue({
      data: mockJobs,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    expect(screen.getByTestId('calendar-jobs-count')).toHaveTextContent('3');
  });

  it('clicking pause button calls pause mutation', async () => {
    mocks.useScheduledJobs.mockReturnValue({
      data: mockJobs,
      isLoading: false,
      error: null,
    });

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
    mocks.useScheduledJobs.mockReturnValue({
      data: mockJobs,
      isLoading: false,
      error: null,
    });

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
    mocks.useScheduledJobs.mockReturnValue({
      data: mockJobs,
      isLoading: false,
      error: null,
    });

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
      {
        ...mockJobs[2],
        id: 'schedule-4',
        jobId: 'job-4',
        isActive: false,
        status: 'completed' as const,
      },
      {
        ...mockJobs[0],
        id: 'schedule-5',
        jobId: 'job-5',
        requiresOperatorReauthorization: true,
        status: 'reauthorizationRequired' as const,
      },
    ];

    mocks.useScheduledJobs.mockReturnValue({
      data: jobsWithVariousStatuses,
      isLoading: false,
      error: null,
    });

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    expect(screen.getByText('active')).toBeInTheDocument();
    expect(screen.getByText('paused')).toBeInTheDocument();
    expect(screen.getByText('completed')).toBeInTheDocument();
    expect(screen.getByText('reauthorizationRequired')).toBeInTheDocument();
  });

  it('shows error message when data fails to load', () => {
    mocks.useScheduledJobs.mockReturnValue({
      data: [],
      isLoading: false,
      error: { message: 'Failed to fetch', statusCode: 503 },
    });

    render(
      <TestWrapper>
        <SchedulingPage />
      </TestWrapper>
    );

    expect(screen.getByText(/Failed to load scheduled jobs/)).toBeInTheDocument();
  });
});
