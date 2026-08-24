import '@testing-library/jest-dom';
import React from 'react';
import { render } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PrinterBackend, type Printer } from '@/types/api';

const useSpoolmanConfiguredMock = vi.hoisted(() => vi.fn(() => ({ ready: true })));
const failureDetectionPollingEnabledMock = vi.hoisted(() => vi.fn(() => false));
const usePrinterFailureDetectionStatusMock = vi.hoisted(() =>
  vi.fn(() => ({ printerStatus: undefined, data: undefined, isLoading: false }))
);

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterDetails: () => ({ data: undefined, isLoading: false }),
  usePrintJobObjects: () => ({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() }),
  queryKeys: { printJobObjects: (printerId: string) => ['printJobObjects', printerId] },
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
  usePrinterFailureDetectionStatus: usePrinterFailureDetectionStatusMock,
}));

vi.mock('@/features/printers/hooks/useFailureDetectionPolling', () => ({
  useFailureDetectionPollingEnabled: failureDetectionPollingEnabledMock,
}));

vi.mock('@/features/filament-coverage/components/FilamentCoverageBreakdown', () => ({
  FilamentCoverageBreakdown: () => null,
}));

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({ PrinterHistoryModal: () => null }));
vi.mock('@/features/printers/components/PrinterFilesModal', () => ({ PrinterFilesModal: () => null }));
vi.mock('@/features/printers/components/SpoolPickerModal', () => ({ SpoolPickerModal: () => null }));
vi.mock('@/features/printers/components/MaterialLoadout', () => ({
  MaterialLoadout: () => <div data-testid="material-loadout" />,
}));
vi.mock('@/features/printers/components/TemperatureControlSection', () => ({
  TemperatureControlSection: () => <div data-testid="temp-section" />,
}));
vi.mock('@/features/printers/components/MovementControlSection', () => ({
  MovementControlSection: ({ rightContent }: { rightContent?: React.ReactNode }) => (
    <div data-testid="movement-section">{rightContent}</div>
  ),
}));
vi.mock('@/features/printers/components/FilamentControlSection', () => ({
  FilamentControlSection: () => <div data-testid="filament-section" />,
}));
vi.mock('@/features/printers/components/PrinterActionBar', () => ({ PrinterActionBar: () => <div data-testid="action-bar" /> }));
vi.mock('@/features/printers/components/BedClearBanner', () => ({ BedClearBanner: () => null }));
vi.mock('@/features/printers/components/PrintProgressBar', () => ({ PrintProgressBar: () => <div data-testid="print-progress" /> }));
vi.mock('@/features/printers/components/EstimatedCompletionBadge', () => ({ EstimatedCompletionBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionBadge', () => ({ FailureDetectionBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionMonitoringBadge', () => ({ FailureDetectionMonitoringBadge: () => null }));
vi.mock('@/features/printers/components/FailureDetectionMonitoringSummary', () => ({ FailureDetectionMonitoringSummary: () => null }));
vi.mock('@/features/printers/components/OfflineTroubleshootingGuide', () => ({ OfflineTroubleshootingGuide: () => null }));
vi.mock('@/features/printers/components/PrinterCameraPreview', () => ({ PrinterCameraPreview: () => <div data-testid="camera-preview" /> }));
vi.mock('@/features/printers/components/ZOffsetCalibrationWizard', () => ({
  ZOffsetCalibrationWizard: () => <div data-testid="zoffset-wizard" />,
}));
vi.mock('@/services/api', () => ({ apiClient: {} }));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));

import { DetailedPrinterCard } from '../DetailedPrinterCard';

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
    ...overrides,
  } as Printer;
}

/**
 * Regression coverage for #1146 item 3's other half: `useFailureDetectionAlert.test.tsx`
 * proves the SignalR alert store is hoisted to one subscription/timer per grid, and
 * `useFailureDetectionPolling.test.tsx` proves the fleet-wide gate hook/context works in
 * isolation. Neither proves that `DetailedPrinterCard` actually *reads* that shared gate
 * instead of quietly falling back to its own `printer.obicoEnabled` field the way it did
 * before this PR — that wiring is what this file covers.
 */
describe('DetailedPrinterCard failure-detection polling gate wiring (#1146 item 3)', () => {
  beforeEach(() => {
    useSpoolmanConfiguredMock.mockReturnValue({ ready: true });
    failureDetectionPollingEnabledMock.mockClear();
    failureDetectionPollingEnabledMock.mockReturnValue(false);
    usePrinterFailureDetectionStatusMock.mockClear();
    usePrinterFailureDetectionStatusMock.mockReturnValue({ printerStatus: undefined, data: undefined, isLoading: false });
  });

  it('passes the fleet-wide gate value (true) through as this printer\'s enabled flag, ignoring its own obicoEnabled', () => {
    failureDetectionPollingEnabledMock.mockReturnValue(true);

    render(<DetailedPrinterCard printer={makePrinter({ obicoEnabled: false })} />);

    expect(usePrinterFailureDetectionStatusMock).toHaveBeenCalledWith('printer-1', true);
  });

  it('passes the fleet-wide gate value (false) through even when this printer\'s own obicoEnabled is true', () => {
    failureDetectionPollingEnabledMock.mockReturnValue(false);

    render(<DetailedPrinterCard printer={makePrinter({ obicoEnabled: true })} />);

    // Proves the card no longer computes `!!printer.obicoEnabled` itself —
    // only the shared fleet-level decision governs the poll.
    expect(usePrinterFailureDetectionStatusMock).toHaveBeenCalledWith('printer-1', false);
  });
});
