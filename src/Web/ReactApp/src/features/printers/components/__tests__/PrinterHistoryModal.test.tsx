import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import type { HistoryJob, Printer } from '@/types/api';

const usePrinterHistory = vi.fn();
const usePrinterHistoryTotals = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterHistory: (...args: unknown[]) => usePrinterHistory(...args),
  usePrinterHistoryTotals: (...args: unknown[]) => usePrinterHistoryTotals(...args),
}));

const printer = {
  id: 'printer-1',
  name: 'Prusa MK4',
} as Printer;

function createJob(thumbnailUrl?: string): HistoryJob {
  return {
    jobId: 'job-1',
    filename: 'calibration.gcode',
    status: 'completed',
    startTime: 1_700_000_000,
    printDuration: 60,
    filamentUsed: 0,
    thumbnailUrl,
  } as HistoryJob;
}

function renderHistory(job: HistoryJob) {
  usePrinterHistory.mockReturnValue({
    data: { count: 1, jobs: [job] },
    isLoading: false,
    error: null,
    refetch: vi.fn(),
  });
  usePrinterHistoryTotals.mockReturnValue({
    data: null,
    isLoading: false,
  });

  render(
    <PrinterHistoryModal
      isOpen
      onClose={vi.fn()}
      printer={printer}
    />,
  );
}

describe('PrinterHistoryModal thumbnails', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders the established placeholder when a thumbnail is missing', () => {
    renderHistory(createJob());

    expect(
      screen.getByRole('img', { name: 'PrintFarmer logo placeholder' }),
    ).toBeInTheDocument();
  });

  it('replaces a failed thumbnail with the established placeholder', () => {
    renderHistory(createJob('/api/printers/printer-1/history/job-1/thumbnail'));

    fireEvent.error(
      screen.getByRole('img', { name: 'calibration.gcode thumbnail' }),
    );

    expect(
      screen.getByRole('img', { name: 'PrintFarmer logo placeholder' }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('img', { name: 'calibration.gcode thumbnail' }),
    ).not.toBeInTheDocument();
  });
});
