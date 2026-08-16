import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { PrinterBackend, type Printer, type PrintJobObjectDto } from '@/types/api';

// Regression coverage for #1584: detailed print cards must show the same
// level of print detail previously only available behind the "Open details
// sidebar" action, and that action must be removed from the detailed card.

const usePrinterDetailsMock = vi.hoisted(() => vi.fn(() => ({ data: undefined, isLoading: false })));
const useSpoolmanConfiguredMock = vi.hoisted(() => vi.fn(() => ({ ready: true })));
const usePrintJobObjectsMock = vi.hoisted(() =>
  vi.fn(() => ({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() }))
);
const useQueryMock = vi.hoisted(() => vi.fn());
const excludeObjectMutateMock = vi.hoisted(() => vi.fn());
const excludePrintJobObjectMock = vi.hoisted(() => vi.fn());
const getPrinterVersionInfoMock = vi.hoisted(() => vi.fn());

vi.mock('@/common/hooks/useApi', () => ({
  usePrinterDetails: usePrinterDetailsMock,
  usePrintJobObjects: usePrintJobObjectsMock,
  queryKeys: { printJobObjects: (printerId: string) => ['printJobObjects', printerId] },
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: useSpoolmanConfiguredMock,
}));

vi.mock('@/services/maintenanceService', () => ({
  maintenanceService: { getPrinterStatistics: vi.fn() },
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterVersionInfo: getPrinterVersionInfoMock,
    excludePrintJobObject: excludePrintJobObjectMock,
  },
}));

vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn(), setQueryData: vi.fn() }),
  useQuery: useQueryMock,
  useMutation: () => ({ mutate: excludeObjectMutateMock, isPending: false }),
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
    state: 'Printing',
    progress: 42,
    ...overrides,
  } as Printer;
}

function makeObject(overrides: Partial<PrintJobObjectDto> = {}): PrintJobObjectDto {
  return {
    name: 'part_1',
    isCurrent: false,
    isExcluded: false,
    ...overrides,
  } as PrintJobObjectDto;
}

describe('DetailedPrinterCard inline details (#1584)', () => {
  beforeEach(() => {
    usePrinterDetailsMock.mockClear();
    usePrinterDetailsMock.mockReturnValue({ data: undefined, isLoading: false });
    useSpoolmanConfiguredMock.mockReturnValue({ ready: true });
    usePrintJobObjectsMock.mockReturnValue({ data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() });
    excludeObjectMutateMock.mockClear();
    excludePrintJobObjectMock.mockClear();
    getPrinterVersionInfoMock.mockClear();

    useQueryMock.mockImplementation(({ queryKey, enabled }: { queryKey: unknown[]; enabled?: boolean }) => {
      if (queryKey[0] === 'printerStatistics') {
        return enabled
          ? {
              data: {
                totalPrintHours: 12.5,
                totalFilamentUsedGrams: 2500,
                totalJobsCompleted: 10,
                totalJobsFailed: 1,
                lastSyncTime: '2024-01-01T00:00:00.000Z',
              },
              isLoading: false,
              isFetching: false,
              refetch: vi.fn(),
            }
          : { data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() };
      }
      if (queryKey[0] === 'printerVersion') {
        return enabled
          ? {
              data: {
                firmwareVersion: '1.2.3',
                backendVersion: '4.5.6',
                apiVersion: '7.8.9',
                supported: true,
                message: '',
              },
              isLoading: false,
              isFetching: false,
              refetch: vi.fn(),
            }
          : { data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() };
      }
      return { data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() };
    });
  });

  it('does not render an Open details sidebar action', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    expect(screen.queryByRole('button', { name: /open details sidebar/i })).not.toBeInTheDocument();
    expect(screen.queryByTitle(/open details sidebar/i)).not.toBeInTheDocument();
  });

  it('renders a Statistics section that expands to show the same fields as the sidebar', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    expect(screen.getByText('Statistics')).toBeInTheDocument();
    // Collapsed by default: no stats requested/rendered yet.
    expect(screen.queryByText('12.5h')).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('Statistics'));

    expect(screen.getByText('12.5h')).toBeInTheDocument();
    expect(screen.getByText('2.50kg')).toBeInTheDocument();
    expect(screen.getByText('10')).toBeInTheDocument();
  });

  it('renders a Version section that expands to show firmware/backend/API version info', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    expect(screen.getByText('Version')).toBeInTheDocument();
    expect(screen.queryByText('1.2.3')).not.toBeInTheDocument();

    fireEvent.click(screen.getByText('Version'));

    expect(screen.getByText('1.2.3')).toBeInTheDocument();
    expect(screen.getByText('4.5.6')).toBeInTheDocument();
    expect(screen.getByText('7.8.9')).toBeInTheDocument();
  });

  it('renders the Print Objects section with a Skip action when object exclusion is supported and a print is active', () => {
    usePrintJobObjectsMock.mockReturnValue({
      data: { objects: [makeObject({ name: 'part_1' }), makeObject({ name: 'part_2', isCurrent: true })] },
      isLoading: false,
      isFetching: false,
      refetch: vi.fn(),
    });

    const printer = makePrinter({ state: 'Printing' } as Partial<Printer>);

    render(
      <DetailedPrinterCard
        printer={printer}
        backendCapabilities={{ supportsObjectExclusion: true } as unknown as Parameters<typeof DetailedPrinterCard>[0]['backendCapabilities']}
      />
    );

    expect(screen.getByText('Objects')).toBeInTheDocument();
    expect(screen.getByText('part_1')).toBeInTheDocument();
    expect(screen.getByText('part_2')).toBeInTheDocument();

    const skipButtons = screen.getAllByRole('button', { name: /skip object/i });
    fireEvent.click(skipButtons[0]);

    expect(screen.getByText('Skip print object?')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Skip object' }));

    expect(excludeObjectMutateMock).toHaveBeenCalledWith('part_1');
  });

  it('does not render the Print Objects section when the backend does not support object exclusion', () => {
    render(
      <DetailedPrinterCard
        printer={makePrinter()}
        backendCapabilities={{ supportsObjectExclusion: false } as unknown as Parameters<typeof DetailedPrinterCard>[0]['backendCapabilities']}
      />
    );

    expect(screen.queryByText('Objects')).not.toBeInTheDocument();
  });
});
