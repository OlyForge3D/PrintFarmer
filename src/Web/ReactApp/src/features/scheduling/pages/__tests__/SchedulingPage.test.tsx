import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SchedulingPage } from '../SchedulingPage';

const mocks = vi.hoisted(() => ({
  getJobExecutions: vi.fn(),
  scheduledJob: {
    id: 'schedule-1',
    jobId: 'root-job-1',
    jobName: 'DST validation cube',
    printerName: 'Printer One',
    printerId: 'printer-1',
    scheduledStartTimeUtc: '2026-03-08T07:30:00Z',
    scheduledLocalTime: '2026-03-08T03:30:00',
    timeZone: 'America/New_York',
    recurrencePattern: 'Daily',
    recurrenceInterval: 1,
    recurrenceEndTimeUtc: null,
    isActive: true,
    isPaused: false,
    requiresOperatorReauthorization: false,
    status: 'active',
  },
}));

vi.mock('@/common/hooks/useApi', () => ({
  useScheduledJobs: () => ({
    data: [mocks.scheduledJob],
    isLoading: false,
    error: null,
  }),
  usePauseSchedule: () => ({ mutate: vi.fn(), isPending: false }),
  useResumeSchedule: () => ({ mutate: vi.fn(), isPending: false }),
  useCancelSchedule: () => ({ mutate: vi.fn(), isPending: false }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getJobExecutions: mocks.getJobExecutions,
  },
}));

vi.mock('../../components/ScheduleModal', () => ({
  ScheduleModal: () => null,
}));

describe('SchedulingPage', () => {
  beforeEach(() => {
    mocks.getJobExecutions.mockReset();
    mocks.getJobExecutions.mockResolvedValue([
      {
        id: 'execution-1',
        occurrenceJobId: 'occurrence-1',
        dispatchAttemptId: 'attempt-1',
        scheduledExecutionTime: '2026-03-09T07:30:00Z',
        status: 'Completed',
        message: 'The backend confirmed the scheduled start.',
      },
    ]);
  });

  it('renders reviewed wall time and execution history in the schedule timezone', async () => {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    });
    render(
      <QueryClientProvider client={queryClient}>
        <SchedulingPage />
      </QueryClientProvider>
    );

    expect(
      screen.getByText(/Mar 8, 2026, 3:30 AM \(America\/New_York\)/)
    ).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'History' }));

    expect(await screen.findByText('Completed')).toBeInTheDocument();
    expect(mocks.getJobExecutions).toHaveBeenCalledWith('root-job-1');
    expect(
      screen.getByText(/Mar 9, 2026, 3:30 AM \(America\/New_York\)/)
    ).toBeInTheDocument();
  });
});
