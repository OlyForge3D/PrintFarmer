import { render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MonthCalendar } from '@/features/scheduling/components/MonthCalendar';
import type { ScheduledJob } from '@/types/api';

describe('MonthCalendar schedule timezone', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-15T12:00:00Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('places a cross-midnight UTC instant on its reviewed local day', () => {
    const job: ScheduledJob = {
      id: 'schedule-1',
      jobId: 'job-1',
      jobName: 'Night print',
      printerName: 'Printer',
      printerId: 'printer-1',
      scheduledStartTimeUtc: '2026-06-02T01:30:00Z',
      scheduledLocalTime: '2026-06-01T21:30:00',
      timeZone: 'America/New_York',
      recurrenceInterval: 1,
      isActive: true,
      isPaused: false,
      requiresOperatorReauthorization: false,
      status: 'active',
    };

    render(<MonthCalendar scheduledJobs={[job]} onDateClick={vi.fn()} />);

    const dayOne = screen.getByRole('button', { name: /1.*Night print/i });
    expect(within(dayOne).getByText('Night print')).toBeInTheDocument();
    expect(within(dayOne).getByText(/21:30 America\/New_York/))
      .toBeInTheDocument();
    const dayTwo = screen.getByRole('button', { name: /^2$/ });
    expect(within(dayTwo).queryByText('Night print')).not.toBeInTheDocument();
  });
});
