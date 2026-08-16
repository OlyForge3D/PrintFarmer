import '@testing-library/jest-dom';
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { PrinterBackend, type Printer } from '@/types/api';
import { PrinterDetailsSidebar } from '../PrinterDetailsSidebar';

// The sidebar receives its `printer` prop from the compact list query, which can
// omit `rowVersion`. These tests prove the spool controls resolve the reviewed
// revision from the fetched detail record instead of dead-ending on a click.

const setActiveSpoolMock = vi.hoisted(() => vi.fn());
const clearActiveSpoolMock = vi.hoisted(() => vi.fn());
const usePrinterDetailsMock = vi.hoisted(() => vi.fn(() => ({ data: undefined })));
const useSpoolmanConfiguredMock = vi.hoisted(() => vi.fn(() => ({ ready: true })));

vi.mock('@tanstack/react-query', () => ({
  useQuery: () => ({ data: undefined, isLoading: false, refetch: vi.fn() }),
  useQueryClient: () => ({ invalidateQueries: vi.fn(), setQueryData: vi.fn() }),
  useMutation: () => ({ isPending: false, mutate: vi.fn() }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  queryKeys: {
    printJobObjects: (printerId: string) => ['printers', printerId, 'printjob', 'objects'],
  },
  usePrinter: () => ({ data: undefined, isLoading: false, refetch: vi.fn() }),
  usePrinterDetails: usePrinterDetailsMock,
  usePrintJobObjects: () => ({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    setActiveSpool: (...args: unknown[]) => setActiveSpoolMock(...args),
    clearActiveSpool: (...args: unknown[]) => clearActiveSpoolMock(...args),
    excludePrintJobObject: vi.fn(),
  },
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplay: (printer: Printer) => printer,
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: useSpoolmanConfiguredMock,
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: () => ({ data: undefined }),
}));

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({ PrinterHistoryModal: () => null }));
vi.mock('@/features/printers/components/PrinterFilesModal', () => ({ PrinterFilesModal: () => null }));
vi.mock('@/features/printers/components/CalibrationSetupModal', () => ({ CalibrationSetupModal: () => null }));
vi.mock('@/features/filament-coverage/components/FilamentCoverageBreakdown', () => ({ FilamentCoverageBreakdown: () => null }));
vi.mock('@/features/fallback-groups/components/FallbackGroupsPanel', () => ({ FallbackGroupsPanel: () => null }));
vi.mock('@/features/printers/components/MaterialLoadout', () => ({ MaterialLoadout: () => null }));
vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: ({
    isOpen,
    onSelect,
  }: {
    isOpen: boolean;
    onSelect: (spoolId: number, spool: { id: number; name: string; material: string }) => void;
  }) =>
    isOpen ? (
      <button
        type="button"
        data-testid="spool-picker-select"
        onClick={() => onSelect(99, { id: 99, name: 'Charcoal Black', material: 'PLA' })}
      >
        pick
      </button>
    ) : null,
}));

function makePrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: 'printer-1',
    name: 'Printer Alpha',
    backend: PrinterBackend.Moonraker,
    isOnline: true,
    isEnabled: true,
    state: 'Idle',
    hotendTemp: 25,
    hotendTarget: 0,
    bedTemp: 23,
    bedTarget: 0,
    x: 0,
    y: 0,
    z: 0,
    spoolInfo: { hasActiveSpool: true, activeSpoolId: 5 },
    ...overrides,
  } as Printer;
}

describe('PrinterDetailsSidebar spool revision fallback', () => {
  beforeEach(() => {
    setActiveSpoolMock.mockReset().mockResolvedValue('rev-2');
    clearActiveSpoolMock.mockReset().mockResolvedValue('rev-2');
    usePrinterDetailsMock.mockReset().mockReturnValue({ data: undefined });
    useSpoolmanConfiguredMock.mockReset().mockReturnValue({ ready: true });
  });

  it('resolves the reviewed revision from the fetched detail record when the prop lacks one', async () => {
    usePrinterDetailsMock.mockReturnValue({ data: { rowVersion: 'detail-rev-1' } });

    render(
      <PrinterDetailsSidebar printerId="printer-1" printer={makePrinter()} onClose={vi.fn()} layout="content" />,
    );

    const change = screen.getByRole('button', { name: 'Change spool' });
    expect(change).toBeEnabled();

    fireEvent.click(change);
    fireEvent.click(await screen.findByTestId('spool-picker-select'));

    await waitFor(() =>
      expect(setActiveSpoolMock).toHaveBeenCalledWith('printer-1', 99, 'detail-rev-1'),
    );
  });

  it('disables the spool controls with an explanation when no revision can be resolved', () => {
    render(
      <PrinterDetailsSidebar printerId="printer-1" printer={makePrinter()} onClose={vi.fn()} layout="content" />,
    );

    const change = screen.getByRole('button', { name: /Change spool/ });
    const eject = screen.getByRole('button', { name: /Eject spool/ });

    expect(change).toBeDisabled();
    expect(eject).toBeDisabled();
    expect(change).toHaveAttribute('title', 'Printer revision unavailable — refresh to manage spools');

    fireEvent.click(change);
    expect(screen.queryByTestId('spool-picker-select')).not.toBeInTheDocument();
    expect(setActiveSpoolMock).not.toHaveBeenCalled();
  });

  it('uses the prop revision directly once the list DTO carries one (post-backend-fix)', async () => {
    render(
      <PrinterDetailsSidebar
        printerId="printer-1"
        printer={makePrinter({ rowVersion: 'list-rev-1' })}
        onClose={vi.fn()}
        layout="content"
      />,
    );

    const change = screen.getByRole('button', { name: 'Change spool' });
    fireEvent.click(change);
    fireEvent.click(await screen.findByTestId('spool-picker-select'));

    await waitFor(() =>
      expect(setActiveSpoolMock).toHaveBeenCalledWith('printer-1', 99, 'list-rev-1'),
    );
  });
});
