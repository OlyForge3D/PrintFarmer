import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SchedulingPage } from '../SchedulingPage';
import type { ScheduledJob } from '@/types/api';

const mocks = vi.hoisted(() => ({
  getJobExecutions: vi.fn(),
  jobs: [] as ScheduledJob[],
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
  } satisfies ScheduledJob,
}));

vi.mock('@/common/hooks/useApi', () => ({
  useScheduledJobs: () => ({
    data: mocks.jobs,
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
    mocks.jobs = [mocks.scheduledJob];
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

  it.each([
    ['Job Name', ['Alpha', 'Bravo', 'Zulu']],
    ['Printer', ['Alpha', 'Bravo', 'Zulu']],
    ['Scheduled Time', ['Alpha', 'Bravo', 'Zulu']],
    ['Recurrence', ['Alpha', 'Bravo', 'Zulu']],
    ['Status', ['Alpha', 'Bravo', 'Zulu']],
  ])('sorts %s by raw values and restores the supplied order', async (header, ascending) => {
    mocks.jobs = [
      { ...mocks.scheduledJob, id: 'high', jobId: 'high', jobName: 'Zulu', printerName: 'Zulu', scheduledStartTimeUtc: '2026-11-01T00:00:00Z', recurrencePattern: null, status: 'paused' },
      { ...mocks.scheduledJob, id: 'low', jobId: 'low', jobName: 'Alpha', printerName: 'Alpha', scheduledStartTimeUtc: '2026-02-01T00:00:00Z', recurrencePattern: 'Daily', recurrenceInterval: 2, status: 'active' },
      { ...mocks.scheduledJob, id: 'middle', jobId: 'middle', jobName: 'Bravo', printerName: 'Bravo', scheduledStartTimeUtc: '2026-10-01T00:00:00Z', recurrencePattern: 'Daily', recurrenceInterval: 10, status: 'completed' },
    ];
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={queryClient}><SchedulingPage /></QueryClientProvider>);
    const table = screen.getByRole('table');
    const rowNames = () => within(table).getAllByRole('row').slice(1)
      .map(row => within(row).getAllByRole('cell')[0].textContent);
    const user = userEvent.setup();
    const button = within(table).getByRole('button', { name: header });
    expect(rowNames()).toEqual(['Zulu', 'Alpha', 'Bravo']);
    await user.click(button);
    expect(rowNames()).toEqual(ascending);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'ascending');
    await user.click(button);
    expect(rowNames()).toEqual([...ascending].reverse());
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'descending');
    await user.click(button);
    expect(rowNames()).toEqual(['Zulu', 'Alpha', 'Bravo']);
    expect(button.closest('th')).toHaveAttribute('aria-sort', 'none');
    expect(within(table).getByRole('columnheader', { name: 'Actions' })).not.toHaveAttribute('aria-sort');
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
