import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Printer } from '@/types/api';

const getPrinterHistory = vi.hoisted(() => vi.fn());
const getPrinterHistoryTotals = vi.hoisted(() => vi.fn());

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterHistory,
    getPrinterHistoryTotals,
    getPrinterHistoryThumbnail: vi.fn(),
  },
}));

import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';

const printer = {
  id: 'printer-1',
  name: 'Prusa MK4',
  // The modal also gates fetching on reachability (#1589), so an online
  // printer is required for the "opened" half of this test to fetch at all.
  isOnline: true,
} as Printer;

describe('PrinterHistoryModal data query gating', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getPrinterHistory.mockResolvedValue({ count: 0, jobs: [] });
    getPrinterHistoryTotals.mockResolvedValue({
      jobTotals: {
        totalJobs: 0,
        totalTime: 0,
        totalPrintTime: 0,
        totalFilamentUsed: 0,
        longestJob: 0,
        longestPrint: 0,
      },
    });
  });

  it('makes no history requests while closed and fetches both resources when opened', async () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });
    const { rerender } = render(
      <QueryClientProvider client={queryClient}>
        <PrinterHistoryModal
          isOpen={false}
          onClose={vi.fn()}
          printer={printer}
        />
      </QueryClientProvider>,
    );

    await waitFor(() => {
      expect(getPrinterHistory).not.toHaveBeenCalled();
      expect(getPrinterHistoryTotals).not.toHaveBeenCalled();
    });

    rerender(
      <QueryClientProvider client={queryClient}>
        <PrinterHistoryModal
          isOpen
          onClose={vi.fn()}
          printer={printer}
        />
      </QueryClientProvider>,
    );

    await waitFor(() => {
      expect(getPrinterHistory).toHaveBeenCalledTimes(1);
      expect(getPrinterHistoryTotals).toHaveBeenCalledTimes(1);
    });
    expect(getPrinterHistory).toHaveBeenCalledWith(
      'printer-1',
      { limit: 50, order: 'desc' },
    );
    expect(getPrinterHistoryTotals).toHaveBeenCalledWith('printer-1');

  });
});
