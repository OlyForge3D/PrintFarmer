import { render } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import type { Printer } from '@/types/api';
import { PrinterHistoryModal } from '../PrinterHistoryModal';

// Regression coverage for #1589: PrinterHistoryModal is mounted unconditionally
// by the printer cards (visibility is controlled via the `isOpen` prop), so its
// history/totals queries must only be enabled while the modal is actually open
// and the printer is reachable. Without that gate, every printer card - including
// offline ones - fires `GET /api/printers/{id}/history` on render, and
// react-query's default retries turn a single 502 into four requests.
const usePrinterHistoryMock = vi.fn();
const usePrinterHistoryTotalsMock = vi.fn();

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterHistory: (...args: unknown[]) => usePrinterHistoryMock(...args),
  usePrinterHistoryTotals: (...args: unknown[]) => usePrinterHistoryTotalsMock(...args),
}));

const basePrinter: Printer = {
  id: 'printer-offline-1',
  name: 'Offline Printer',
  state: 'Offline',
  isOnline: false,
  isEnabled: true,
  hotendTemp: null,
  hotendTarget: null,
  bedTemp: null,
  bedTarget: null,
  homedAxes: null,
  printerBackend: 'Moonraker',
  url: 'http://offline-printer.local',
  apiKey: null,
  cameraUrl: null,
  thumbnailUrl: null,
  progress: null,
  printTime: null,
  estimatedTimeRemaining: null,
  currentFileName: null,
  isPrinting: false,
  isPaused: false,
  manufacturer: null,
  model: null,
  locationId: null,
  spoolId: null,
  spoolInfo: null,
} as Printer;

describe('PrinterHistoryModal query gating', () => {
  beforeEach(() => {
    usePrinterHistoryMock.mockReset();
    usePrinterHistoryTotalsMock.mockReset();
    usePrinterHistoryMock.mockReturnValue({ data: undefined, isLoading: false, error: null, refetch: vi.fn() });
    usePrinterHistoryTotalsMock.mockReturnValue({ data: undefined, isLoading: false });
  });

  it('does not enable history/totals queries when mounted closed for an offline printer', () => {
    render(
      <PrinterHistoryModal isOpen={false} onClose={vi.fn()} printer={basePrinter} />
    );

    expect(usePrinterHistoryMock).toHaveBeenCalledTimes(1);
    const [, , historyQueryOptions] = usePrinterHistoryMock.mock.calls[0] as [string, unknown, { enabled?: boolean }];
    expect(historyQueryOptions.enabled).toBe(false);

    expect(usePrinterHistoryTotalsMock).toHaveBeenCalledTimes(1);
    const [, totalsQueryOptions] = usePrinterHistoryTotalsMock.mock.calls[0] as [string, { enabled?: boolean }];
    expect(totalsQueryOptions.enabled).toBe(false);
  });

  it('does not enable history/totals queries for an offline printer even when isOpen is true', () => {
    // Defense-in-depth: canOpenHistory() already disables the "History" button
    // for offline printers, but the modal itself must not fetch for one either.
    render(
      <PrinterHistoryModal isOpen={true} onClose={vi.fn()} printer={basePrinter} />
    );

    const [, , historyQueryOptions] = usePrinterHistoryMock.mock.calls[0] as [string, unknown, { enabled?: boolean }];
    expect(historyQueryOptions.enabled).toBe(false);

    const [, totalsQueryOptions] = usePrinterHistoryTotalsMock.mock.calls[0] as [string, { enabled?: boolean }];
    expect(totalsQueryOptions.enabled).toBe(false);
  });

  it('enables history/totals queries when open for an online printer', () => {
    const onlinePrinter: Printer = { ...basePrinter, isOnline: true, state: 'Idle' };

    render(
      <PrinterHistoryModal isOpen={true} onClose={vi.fn()} printer={onlinePrinter} />
    );

    const [, , historyQueryOptions] = usePrinterHistoryMock.mock.calls[0] as [string, unknown, { enabled?: boolean }];
    expect(historyQueryOptions.enabled).toBe(true);

    const [, totalsQueryOptions] = usePrinterHistoryTotalsMock.mock.calls[0] as [string, { enabled?: boolean }];
    expect(totalsQueryOptions.enabled).toBe(true);
  });
});
