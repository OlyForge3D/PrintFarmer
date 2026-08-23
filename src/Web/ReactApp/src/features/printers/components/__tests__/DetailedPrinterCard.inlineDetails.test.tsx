import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
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
const excludePrintJobObjectMock = vi.hoisted(() => vi.fn());
const getPrinterVersionInfoMock = vi.hoisted(() => vi.fn());
const setQueryDataMock = vi.hoisted(() => vi.fn());

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

// The real useMutation actually invokes mutationFn/onSuccess/onError, so this fake
// preserves that contract instead of stubbing `mutate` to a bare spy — otherwise a
// regression like #1651 (a mutation quietly writing to the wrong cache key, or never
// calling the API with the right options) would not be caught by any test using it.
const useMutationMock = vi.hoisted(() =>
  vi.fn(
    (options: {
      mutationFn: (arg?: unknown) => unknown;
      onSuccess?: (data: unknown, arg?: unknown) => void;
      onError?: (error: unknown) => void;
    }) => ({
      mutate: (arg?: unknown) => {
        const result = options.mutationFn(arg);
        Promise.resolve(result).then(
          (data) => options.onSuccess?.(data, arg),
          (error) => options.onError?.(error)
        );
      },
      isPending: false,
    })
  )
);

vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => ({ invalidateQueries: vi.fn(), setQueryData: setQueryDataMock }),
  useQuery: useQueryMock,
  useMutation: useMutationMock,
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
vi.mock('@/features/printers/components/CalibrationSetupPrompt', () => ({ CalibrationSetupPrompt: () => null }));
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
    excludePrintJobObjectMock.mockClear();
    excludePrintJobObjectMock.mockResolvedValue({ success: true });
    getPrinterVersionInfoMock.mockClear();
    getPrinterVersionInfoMock.mockResolvedValue({
      firmwareVersion: '9.9.9',
      backendVersion: '9.9.9',
      apiVersion: '9.9.9',
      supported: true,
      message: '',
    });
    setQueryDataMock.mockClear();

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

  it('labels the firmware reading as live-only (not used for calibration) when no recorded identity is returned (#1656)', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    fireEvent.click(screen.getByText('Version'));

    expect(screen.getByText('Live reading only — not used for calibration eligibility')).toBeInTheDocument();
    expect(screen.queryByText('Recorded — used for calibration eligibility')).not.toBeInTheDocument();
  });

  it('labels the firmware reading as the recorded/calibration-eligible identity when the version endpoint returns one (#1656)', () => {
    useQueryMock.mockImplementation(({ queryKey, enabled }: { queryKey: unknown[]; enabled?: boolean }) => {
      if (queryKey[0] === 'printerVersion') {
        return enabled
          ? {
              data: {
                firmwareVersion: '1.2.3',
                backendVersion: '4.5.6',
                apiVersion: '7.8.9',
                supported: true,
                message: '',
                recordedFirmwareIdentity: {
                  family: 'Klipper',
                  gcodeDialect: 'Klipper',
                  detectionSource: 'printer',
                  version: '1.2.3',
                  detectionVersion: 'moonraker-printer-info-v1',
                  detectionConfidence: 1,
                  detectedAtUtc: '2024-01-01T00:00:00.000Z',
                  verified: false,
                },
              },
              isLoading: false,
              isFetching: false,
              refetch: vi.fn(),
            }
          : { data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() };
      }
      return { data: undefined, isLoading: false, isFetching: false, refetch: vi.fn() };
    });

    render(<DetailedPrinterCard printer={makePrinter()} />);

    fireEvent.click(screen.getByText('Version'));

    expect(screen.getByText('Recorded — used for calibration eligibility')).toBeInTheDocument();
    expect(screen.queryByText('Live reading only — not used for calibration eligibility')).not.toBeInTheDocument();
  });

  // Regression coverage for #1651: after a transient Klippy fault clears, the explicit
  // "Refresh version info" button must force-refresh instead of re-reading whatever is
  // still cached, and the recovered result must land back in the same React Query cache
  // entry the Version section reads from.
  it('force-refreshes version info and writes the result into the printerVersion query cache on click', async () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    fireEvent.click(screen.getByText('Version'));
    fireEvent.click(screen.getByRole('button', { name: /refresh version info/i }));

    expect(getPrinterVersionInfoMock).toHaveBeenCalledWith('printer-1', { forceRefresh: true });

    await waitFor(() =>
      expect(setQueryDataMock).toHaveBeenCalledWith(
        ['printerVersion', 'printer-1'],
        expect.objectContaining({ firmwareVersion: '9.9.9', backendVersion: '9.9.9', apiVersion: '9.9.9' })
      )
    );
  });

  it('renders the Print Objects section with a Skip action when object exclusion is supported and a print is active', async () => {
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

    await waitFor(() => expect(excludePrintJobObjectMock).toHaveBeenCalledWith('printer-1', 'part_1'));
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

  // Regression coverage for #1698: the card's shared sections must appear in the same
  // relative order as PrinterDetailsSidebar (see printerDetailSectionOrder.ts):
  // Statistics, Version, Objects, Move, Temperature, Materials.
  it('renders shared sections in the same relative order as the sidebar (#1698)', () => {
    usePrintJobObjectsMock.mockReturnValue({
      data: { objects: [makeObject({ name: 'part_1' })] },
      isLoading: false,
      isFetching: false,
      refetch: vi.fn(),
    });

    render(
      <DetailedPrinterCard
        printer={makePrinter()}
        backendCapabilities={{ supportsObjectExclusion: true } as unknown as Parameters<typeof DetailedPrinterCard>[0]['backendCapabilities']}
      />
    );

    const precedes = (a: Element, b: Element) =>
      Boolean(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);

    const statistics = screen.getByText('Statistics');
    const version = screen.getByText('Version');
    const objects = screen.getByText('Objects');
    const move = screen.getByTestId('movement-section');
    const temperature = screen.getByTestId('temp-section');
    // No mmuStatus is set up on this printer, so materialLoadout resolves to
    // null and the single-spool fallback card ("Spool") renders instead of
    // MaterialLoadout — both occupy the same "materials" slot in the order.
    const spool = screen.getByText('Spool');

    expect(precedes(statistics, version)).toBe(true);
    expect(precedes(version, objects)).toBe(true);
    expect(precedes(objects, move)).toBe(true);
    expect(precedes(move, temperature)).toBe(true);
    expect(precedes(temperature, spool)).toBe(true);
  });
});
