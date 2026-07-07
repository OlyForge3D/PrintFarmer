import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import '@testing-library/jest-dom';
import type { HistoryJob } from '@/types/queue';
import type { PrintJobToolheadUsage } from '@/types/api';
import HistoryJobTable from '../HistoryJobTable';
import HistoryJobCard from '../HistoryJobCard';

const baseJob = (overrides: Partial<HistoryJob> = {}): HistoryJob => ({
  id: 'job-1',
  name: 'seeded.gcode',
  printerName: 'U1-2',
  status: 'completed',
  completionPercentage: 100,
  startedAt: '2026-03-25T00:00:00.000Z',
  completedAt: '2026-03-25T00:30:00.000Z',
  durationSeconds: 1800,
  ...overrides,
});

const toolheadUsage = (overrides: Partial<PrintJobToolheadUsage> = {}): PrintJobToolheadUsage => ({
  id: 'tu-1',
  printJobId: 'job-1',
  toolheadIndex: 0,
  filamentUsageGrams: 87.5,
  filamentName: 'PETG Black',
  filamentColor: '#000000',
  materialCostUsd: 2.19,
  ...overrides,
});

describe('history filament/cost fallback (seeded jobs without toolhead usages)', () => {
  it('shows aggregate actualFilamentUsageGrams in the table when there are no toolhead usages', () => {
    render(
      <HistoryJobTable
        jobs={[baseJob({ actualFilamentUsageGrams: 156.8, materialCostUsd: 3.14, totalCostUsd: 4.5, costIsEstimated: false })]}
        onRerun={vi.fn()}
      />,
    );

    expect(screen.getByText('156.8g')).toBeInTheDocument();
    expect(screen.getByText('$3.14')).toBeInTheDocument();
  });

  it('marks seeded-history cost as estimated (est badge, no misleading minus) in the table', () => {
    render(
      <HistoryJobTable
        jobs={[baseJob({ actualFilamentUsageGrams: 156.8, materialCostUsd: 3.14, totalCostUsd: 4.5, costIsEstimated: true })]}
        onRerun={vi.fn()}
      />,
    );

    // Cost is shown as a plain positive value plus an "est" marker — never with a
    // leading "~" that users mistake for a negative sign.
    expect(screen.getByText('$3.14')).toBeInTheDocument();
    expect(screen.queryByText('~$3.14')).not.toBeInTheDocument();
    expect(screen.getByText('est')).toBeInTheDocument();
  });

  it('prefers per-toolhead usage over the aggregate fallback in the table', () => {
    render(
      <HistoryJobTable
        jobs={[
          baseJob({
            toolheadUsages: [toolheadUsage()],
            actualFilamentUsageGrams: 999,
            materialCostUsd: 999,
          }),
        ]}
        onRerun={vi.fn()}
      />,
    );

    expect(screen.getByText('87.5g')).toBeInTheDocument();
    expect(screen.getByText('$2.19')).toBeInTheDocument();
    expect(screen.queryByText('999.0g')).not.toBeInTheDocument();
  });

  it('renders a dash when no filament or cost data is available', () => {
    render(<HistoryJobTable jobs={[baseJob()]} onRerun={vi.fn()} />);

    // Both filament and cost cells fall back to the muted em dash.
    expect(screen.getAllByText('—').length).toBeGreaterThanOrEqual(2);
  });

  it('shows the aggregate filament/cost block in the card view when there are no toolhead usages', () => {
    render(
      <HistoryJobCard
        job={baseJob({ actualFilamentUsageGrams: 181.6, materialCostUsd: 3.63, costIsEstimated: true })}
        onRerun={vi.fn()}
      />,
    );

    expect(screen.getByText('181.6g')).toBeInTheDocument();
    expect(screen.getByText('$3.63 (est.)')).toBeInTheDocument();
  });

  it('shows estimated filament as a fallback so an estimated cost always has a visible basis', () => {
    render(
      <HistoryJobTable
        jobs={[baseJob({ estimatedFilamentUsageGrams: 42.5, materialCostUsd: 1.06, costIsEstimated: true })]}
        onRerun={vi.fn()}
      />,
    );

    // No actual usage, but the slicer estimate is shown (marked "est") next to the cost.
    expect(screen.getByText('42.5g')).toBeInTheDocument();
    expect(screen.getByText('$1.06')).toBeInTheDocument();
  });

  it('renders the material type column', () => {
    render(
      <HistoryJobTable
        jobs={[baseJob({ materialType: 'PETG;PETG;PETG', actualFilamentUsageGrams: 100 })]}
        onRerun={vi.fn()}
      />,
    );

    // Duplicate material tokens collapse to a single compact label.
    expect(screen.getByText('PETG')).toBeInTheDocument();
  });
});
