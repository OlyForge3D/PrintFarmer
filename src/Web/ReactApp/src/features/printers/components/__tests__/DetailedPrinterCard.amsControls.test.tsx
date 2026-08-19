import '@testing-library/jest-dom';
import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PrinterBackend, type MmuStatus, type Printer } from '@/types/api';

// Regression coverage for #1699: the Detailed Print Card was missing the AMS
// (MmuControlBox) panel that PrinterDetailsSidebar renders whenever mmuStatus
// is present, so users could not load/unload/eject filament from the card.

const usePrinterDetailsMock = vi.hoisted(() =>
  vi.fn(() => ({ data: undefined, isLoading: false }))
);
const useSpoolmanConfiguredMock = vi.hoisted(() => vi.fn(() => ({ ready: false })));

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
vi.mock('@/features/printers/components/SpoolPickerModal', () => ({ SpoolPickerModal: () => null }));
vi.mock('@/features/printers/components/MaterialLoadout', () => ({
  MaterialLoadout: () => <div data-testid="material-loadout" />,
}));
vi.mock('@/features/printers/components/MmuControlBox', () => ({
  MmuControlBox: ({ printerId, isOnline }: { printerId: string; isOnline: boolean }) => (
    <div data-testid="ams-control-box" data-printer-id={printerId} data-online={String(isOnline)} />
  ),
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
  apiClient: {},
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() } }));

import { DetailedPrinterCard } from '../DetailedPrinterCard';

function makeMmuStatus(overrides: Partial<MmuStatus> = {}): MmuStatus {
  return {
    enabled: true,
    isHomed: true,
    activeTool: 0,
    activeGate: 0,
    numGates: 4,
    hasBypass: false,
    endlessSpool: false,
    clogDetection: false,
    gates: [],
    mmuType: 'HappyHare',
    ...overrides,
  } as MmuStatus;
}

function makePrinter(overrides: Partial<Printer> & { mmuStatus?: MmuStatus } = {}): Printer {
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

describe('DetailedPrinterCard AMS controls', () => {
  beforeEach(() => {
    usePrinterDetailsMock.mockReset().mockReturnValue({ data: undefined, isLoading: false });
    useSpoolmanConfiguredMock.mockReset().mockReturnValue({ ready: false });
  });

  it('renders the AMS control box when mmuStatus is present', () => {
    render(<DetailedPrinterCard printer={makePrinter({ mmuStatus: makeMmuStatus() })} />);

    const ams = screen.getByTestId('ams-control-box');
    expect(ams).toHaveAttribute('data-printer-id', 'printer-1');
    expect(ams).toHaveAttribute('data-online', 'true');
  });

  it('omits the AMS control box when mmuStatus is absent', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    expect(screen.queryByTestId('ams-control-box')).not.toBeInTheDocument();
  });

  it('omits the AMS control box for Snapmaker U1, which has its own UI', () => {
    render(
      <DetailedPrinterCard
        printer={makePrinter({ mmuStatus: makeMmuStatus({ mmuType: 'SnapmakerU1' }) })}
      />
    );

    expect(screen.queryByTestId('ams-control-box')).not.toBeInTheDocument();
  });
});
