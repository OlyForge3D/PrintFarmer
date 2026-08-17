import '@testing-library/jest-dom';
import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PrinterBackend, type Printer, type SpoolInfo } from '@/types/api';

// Verifies the revision-guard resilience for the single-spool card controls:
//  (a) a printer object lacking `rowVersion` (compact list DTO) no longer
//      produces a silent no-op — the controls are disabled with an explanation;
//  (b) when the passed-in printer lacks a revision, the card recovers the
//      concurrency token from the fetched detail record and the mutation fires.

const usePrinterDetailsMock = vi.hoisted(() =>
  vi.fn(() => ({ data: undefined, isLoading: false }))
);
const useSpoolmanConfiguredMock = vi.hoisted(() => vi.fn(() => ({ ready: true })));
const setActiveSpoolMock = vi.hoisted(() => vi.fn());
const clearActiveSpoolMock = vi.hoisted(() => vi.fn());

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterDetails: usePrinterDetailsMock,
  usePrintJobObjects: () => ({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() }),
  queryKeys: {
    printJobObjects: (printerId: string) => ['printJobObjects', printerId],
    printerDetails: (printerId: string) => ['printers', printerId, 'details'],
  },
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: useSpoolmanConfiguredMock,
}));

vi.mock('@/services/maintenanceService', () => ({
  maintenanceService: { getPrinterStatistics: vi.fn() },
}));

vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn(), setQueryData: vi.fn() }),
  useQuery: () => ({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() }),
  useMutation: () => ({ mutate: vi.fn(), isPending: false }),
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: () => ({ data: null, isLoading: false }),
  useSetAutoDispatchEnabled: () => ({ mutateAsync: vi.fn() }),
}));
vi.mock('@/features/printers/hooks/useFailureDetectionAlert', () => ({
  useFailureDetectionAlert: () => ({ event: undefined, recentEvents: [] }),
}));
vi.mock('@/features/printers/hooks/usePrinterFailureDetectionStatus', () => ({
  usePrinterFailureDetectionStatus: () => ({ printerStatus: undefined, data: undefined, isLoading: false }),
}));
vi.mock('@/features/printers/hooks/useFailureDetectionPolling', () => ({
  useFailureDetectionPollingEnabled: () => false,
}));
vi.mock('@/features/filament-coverage/components/FilamentCoverageBreakdown', () => ({
  FilamentCoverageBreakdown: () => null,
}));
vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({ PrinterHistoryModal: () => null }));
vi.mock('@/features/printers/components/PrinterFilesModal', () => ({ PrinterFilesModal: () => null }));
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
vi.mock('@/features/printers/components/MaterialLoadout', () => ({
  MaterialLoadout: () => <div data-testid="material-loadout" />,
}));
vi.mock('@/features/printers/components/TemperatureControlSection', () => ({ TemperatureControlSection: () => null }));
vi.mock('@/features/printers/components/MovementControlSection', () => ({
  MovementControlSection: ({ rightContent }: { rightContent?: React.ReactNode }) => <div>{rightContent}</div>,
}));
vi.mock('@/features/printers/components/FilamentControlSection', () => ({ FilamentControlSection: () => null }));
vi.mock('@/features/printers/components/PrinterActionBar', () => ({ PrinterActionBar: () => null }));
vi.mock('@/features/printers/components/BedClearBanner', () => ({ BedClearBanner: () => null }));
vi.mock('@/features/printers/components/PrintProgressBar', () => ({ PrintProgressBar: () => null }));
vi.mock('@/features/printers/components/EstimatedCompletionBadge', () => ({ EstimatedCompletionBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionBadge', () => ({ FailureDetectionBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionMonitoringBadge', () => ({ FailureDetectionMonitoringBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionMonitoringSummary', () => ({ FailureDetectionMonitoringSummary: () => null }));
vi.mock('@/features/printers/components/OfflineTroubleshootingGuide', () => ({ OfflineTroubleshootingGuide: () => null }));
vi.mock('@/features/printers/components/PrinterCameraPreview', () => ({ PrinterCameraPreview: () => null }));
vi.mock('@/features/printers/components/ZOffsetCalibrationWizard', () => ({ ZOffsetCalibrationWizard: () => null }));
vi.mock('@/services/api', () => ({
  apiClient: {
    setActiveSpool: (...args: unknown[]) => setActiveSpoolMock(...args),
    clearActiveSpool: (...args: unknown[]) => clearActiveSpoolMock(...args),
  },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));

import { DetailedPrinterCard } from '../DetailedPrinterCard';

const activeSpool: SpoolInfo = { hasActiveSpool: true, activeSpoolId: 5, spoolName: 'Old' } as SpoolInfo;

function makePrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: 'printer-1',
    name: 'Printer 1',
    backend: PrinterBackend.Moonraker,
    backendUrl: 'http://printer-1.local',
    frontendUrl: 'http://printer-1.local',
    isOnline: true,
    isEnabled: true,
    isReachable: true,
    state: 'Idle',
    progress: 0,
    spoolInfo: activeSpool,
    ...overrides,
  } as Printer;
}

describe('DetailedPrinterCard single-spool revision guard', () => {
  beforeEach(() => {
    usePrinterDetailsMock.mockReset().mockReturnValue({ data: undefined, isLoading: false });
    useSpoolmanConfiguredMock.mockReset().mockReturnValue({ ready: true });
    setActiveSpoolMock.mockReset().mockResolvedValue('rev-2');
    clearActiveSpoolMock.mockReset().mockResolvedValue('rev-2');
  });

  it('keeps revision-blocked spool controls focusable with an accessible explanation and no activation', async () => {
    const user = userEvent.setup();
    // Printer sourced from the compact list DTO (no rowVersion) and no detail
    // record yet — the token is genuinely unavailable.
    render(<DetailedPrinterCard printer={makePrinter()} />);

    const change = screen.getByRole('button', { name: /Change spool/ });
    const eject = screen.getByRole('button', { name: /Eject spool/ });
    const explanation = 'Printer revision unavailable — refresh to manage spools';

    expect(change).not.toBeDisabled();
    expect(eject).not.toBeDisabled();
    expect(change).toHaveAttribute('aria-disabled', 'true');
    expect(eject).toHaveAttribute('aria-disabled', 'true');
    expect(change).toHaveAttribute('tabIndex', '0');
    expect(eject).toHaveAttribute('tabIndex', '0');
    expect(change).toHaveAttribute('title', explanation);
    expect(eject).toHaveAttribute('title', explanation);
    expect(change).toHaveAccessibleDescription(explanation);
    expect(eject).toHaveAccessibleDescription(explanation);

    change.focus();
    expect(change).toHaveFocus();
    eject.focus();
    expect(eject).toHaveFocus();

    await user.click(change);
    change.focus();
    await user.type(change, '{Enter}');
    await user.type(change, ' ');
    expect(screen.queryByTestId('spool-picker-select')).not.toBeInTheDocument();
    expect(setActiveSpoolMock).not.toHaveBeenCalled();

    await user.click(eject);
    eject.focus();
    await user.type(eject, '{Enter}');
    await user.type(eject, ' ');
    expect(clearActiveSpoolMock).not.toHaveBeenCalled();
  });

  it('suppresses keyboard activation on revision-blocked spool controls', async () => {
    const user = userEvent.setup();
    render(<DetailedPrinterCard printer={makePrinter()} />);

    const change = screen.getByRole('button', { name: /Change spool/ });
    const eject = screen.getByRole('button', { name: /Eject spool/ });

    change.focus();
    await user.type(change, '{Enter}');
    await user.type(change, ' ');
    expect(screen.queryByTestId('spool-picker-select')).not.toBeInTheDocument();
    expect(setActiveSpoolMock).not.toHaveBeenCalled();

    eject.focus();
    await user.type(eject, '{Enter}');
    await user.type(eject, ' ');
    expect(clearActiveSpoolMock).not.toHaveBeenCalled();
  });

  it('recovers the reviewed revision from the fetched detail record and issues the mutation', async () => {
    usePrinterDetailsMock.mockReturnValue({ data: { rowVersion: 'detail-rev-1' }, isLoading: false });
    // Printer prop still lacks rowVersion, mirroring the buggy list endpoint.
    render(<DetailedPrinterCard printer={makePrinter()} />);

    const change = screen.getByRole('button', { name: 'Change spool' });
    expect(change).toBeEnabled();

    fireEvent.click(change);
    fireEvent.click(await screen.findByTestId('spool-picker-select'));

    await waitFor(() =>
      expect(setActiveSpoolMock).toHaveBeenCalledWith('printer-1', 99, 'detail-rev-1'),
    );
  });
});
