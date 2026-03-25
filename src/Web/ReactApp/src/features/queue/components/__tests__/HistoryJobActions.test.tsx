import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@testing-library/jest-dom';
import type { HistoryJob } from '@/types/queue';
import HistoryJobTable from '../HistoryJobTable';
import HistoryJobCard from '../HistoryJobCard';

const createHistoryJob = (status: HistoryJob['status']): HistoryJob => ({
  id: `job-${status}`,
  name: `job-${status}.gcode`,
  printerName: 'Printer 1',
  status,
  completionPercentage: status === 'completed' ? 100 : status === 'cancelled' ? 42 : 12,
  startedAt: '2026-03-25T00:00:00.000Z',
  completedAt: '2026-03-25T00:30:00.000Z',
  durationSeconds: 1800,
  failureReason: status === 'failed' ? 'Hotend jam' : undefined,
});

describe('history rerun actions', () => {
  it('shows the rerun action for cancelled jobs in the table', () => {
    render(
      <HistoryJobTable
        jobs={[createHistoryJob('cancelled')]}
        onRerun={vi.fn()}
      />,
    );

    expect(screen.getByTitle('Rerun this job')).toBeInTheDocument();
  });

  it('does not show the rerun action for failed jobs in the table', () => {
    render(
      <HistoryJobTable
        jobs={[createHistoryJob('failed')]}
        onRerun={vi.fn()}
      />,
    );

    expect(screen.queryByTitle('Rerun this job')).not.toBeInTheDocument();
  });

  it('shows the rerun action for cancelled jobs in the card view', () => {
    render(
      <HistoryJobCard
        job={createHistoryJob('cancelled')}
        onRerun={vi.fn()}
      />,
    );

    expect(screen.getByText('↻ Rerun')).toBeInTheDocument();
  });

  it('does not show the rerun action for failed jobs in the card view', () => {
    render(
      <HistoryJobCard
        job={createHistoryJob('failed')}
        onRerun={vi.fn()}
      />,
    );

    expect(screen.queryByText('↻ Rerun')).not.toBeInTheDocument();
  });
});
