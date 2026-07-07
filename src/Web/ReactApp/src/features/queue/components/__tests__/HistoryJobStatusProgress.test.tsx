import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@testing-library/jest-dom';
import type { HistoryJob } from '@/types/queue';
import HistoryJobTable from '../HistoryJobTable';
import HistoryJobCard from '../HistoryJobCard';

const baseJob = (overrides: Partial<HistoryJob> = {}): HistoryJob => ({
  id: 'job-1',
  name: 'part.gcode',
  printerName: 'U1-2',
  status: 'completed',
  completionPercentage: 100,
  startedAt: '2026-03-25T00:00:00.000Z',
  completedAt: '2026-03-25T00:30:00.000Z',
  durationSeconds: 1800,
  ...overrides,
});

describe('history status badge progress (failed/cancelled jobs)', () => {
  it('appends the progress % to a failed job badge in the table', () => {
    render(
      <HistoryJobTable jobs={[baseJob({ status: 'failed', completionPercentage: 42 })]} onRerun={vi.fn()} />,
    );

    expect(screen.getByText('✗ Failed @ 42%')).toBeInTheDocument();
  });

  it('appends the progress % to a cancelled job badge in the table', () => {
    render(
      <HistoryJobTable jobs={[baseJob({ status: 'cancelled', completionPercentage: 63.7 })]} onRerun={vi.fn()} />,
    );

    // Rounded to the nearest whole percent.
    expect(screen.getByText('◯ Cancelled @ 64%')).toBeInTheDocument();
  });

  it('does not append progress for completed jobs', () => {
    render(<HistoryJobTable jobs={[baseJob({ status: 'completed', completionPercentage: 100 })]} onRerun={vi.fn()} />);

    expect(screen.getByText('✓ Completed')).toBeInTheDocument();
    expect(screen.queryByText(/@/)).not.toBeInTheDocument();
  });

  it('does not append progress when the percentage is 0 or 100', () => {
    render(
      <HistoryJobTable
        jobs={[
          baseJob({ id: 'a', status: 'failed', completionPercentage: 0 }),
          baseJob({ id: 'b', status: 'cancelled', completionPercentage: 100 }),
        ]}
        onRerun={vi.fn()}
      />,
    );

    expect(screen.getByText('✗ Failed')).toBeInTheDocument();
    expect(screen.getByText('◯ Cancelled')).toBeInTheDocument();
    expect(screen.queryByText(/@/)).not.toBeInTheDocument();
  });

  it('appends the progress % to a failed job badge in the card view', () => {
    render(
      <HistoryJobCard job={baseJob({ status: 'failed', completionPercentage: 42 })} onRerun={vi.fn()} />,
    );

    expect(screen.getByText('✗ Failed @ 42%')).toBeInTheDocument();
  });
});
