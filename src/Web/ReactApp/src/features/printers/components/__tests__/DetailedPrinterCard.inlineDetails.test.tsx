import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  PrinterBackend,
  type Printer,
  type PrintJobObjectDto,
  type PrintJobObjectListDto,
} from '@/types/api';

// Regression coverage for #1584: detailed print cards must show the same
// level of print detail previously only available behind the "Open details
// sidebar" action, and that action must be removed from the detailed card.

const usePrinterDetailsMock = vi.hoisted(() => vi.fn(() => ({ data: undefined, isLoading: false })));
const useSpoolmanConfiguredMock = vi.hoisted(() => vi.fn(() => ({ ready: true })));
interface PrintJobObjectsHookResult {
  data: PrintJobObjectListDto | undefined;
  isLoading: boolean;
  isFetching: boolean;
  refetch: () => void;
}

const usePrintJobObjectsMock = vi.hoisted(() =>
  vi.fn<() => PrintJobObjectsHookResult>(() => ({
    data: undefined,
    isLoading: false,
    isFetching: false,
    refetch: vi.fn(),
  }))
);
const useQueryMock = vi.hoisted(() => vi.fn());
const excludePrintJobObjectMock = vi.hoisted(() => vi.fn());
const getPrinterVersionInfoMock = vi.hoisted(() => vi.fn());
const setQueryDataMock = vi.hoisted(() => vi.fn());
const useAutoDispatchStatusMock = vi.hoisted(() => vi.fn(() => ({
  data: { enabled: false, dispatchStateETag: 'dispatch-etag', printerETag: 'printer-etag' },
  isLoading: false,
})));
const setAutoDispatchEnabledMock = vi.hoisted(() => vi.fn());
const autoDispatchMutation = vi.hoisted(() => ({ isPending: false }));

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
      mutateAsync: async (arg?: unknown) => {
        try {
          const data = await options.mutationFn(arg);
          await options.onSuccess?.(data, arg);
          return data;
        } catch (error) {
          await options.onError?.(error);
          throw error;
        }
      },
      isPending: false,
    })
  )
);

vi.mock('@tanstack/react-query', () => ({
  useIsMutating: () => 0,
  useQueryClient: () => ({ invalidateQueries: vi.fn(), setQueryData: setQueryDataMock }),
  useQuery: useQueryMock,
  useMutation: useMutationMock,
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: useAutoDispatchStatusMock,
  useSetAutoDispatchEnabled: () => ({
    mutateAsync: setAutoDispatchEnabledMock,
    isPending: autoDispatchMutation.isPending,
  }),
}));

vi.mock('@/services/assetService', () => ({
  assetService: {
    getCoverImageUrl: (manufacturer: string, model: string) => `/covers/${manufacturer}/${model}.png`,
    getFallbackImageUrl: () => '/assets/printers/generic-corexy.svg',
  },
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
  TemperatureControlSection: ({ showHeading }: { showHeading?: boolean }) => (
    <div data-testid="temp-section" data-show-heading={String(showHeading !== false)} />
  ),
}));
vi.mock('@/features/printers/components/MovementControlSection', () => ({
  MovementControlSection: ({ rightContent }: { rightContent?: React.ReactNode }) => (
    <div data-testid="movement-section">{rightContent}</div>
  ),
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
  it('does not display the configured bed type in detailed cards', () => {
    render(<DetailedPrinterCard printer={makePrinter({
      bedTypeId: 'textured',
      bedTypeName: 'Textured PEI',
      bedTypeColor: '#abcdef',
    })} />);

    expect(screen.getByText('Printer 1')).toBeInTheDocument();
    expect(screen.queryByText('Textured PEI')).not.toBeInTheDocument();
    expect(screen.queryByTitle(/bed type/i)).not.toBeInTheDocument();
  });

  beforeEach(() => {
    useAutoDispatchStatusMock.mockReturnValue({
      data: { enabled: false, dispatchStateETag: 'dispatch-etag', printerETag: 'printer-etag' },
      isLoading: false,
    });
    setAutoDispatchEnabledMock.mockReset();
    setAutoDispatchEnabledMock.mockResolvedValue(undefined);
    autoDispatchMutation.isPending = false;
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

  it('keeps the Temps heading on the card', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    expect(screen.getByTestId('temp-section')).toHaveAttribute('data-show-heading', 'true');
  });

  it('renders the model cover and identity before progress, with a decorative image', () => {
    render(<DetailedPrinterCard printer={makePrinter({ manufacturerName: 'Prusa', modelName: 'MK4' })} />);

    const imageRow = screen.getByTestId('detailed-printer-image');
    const image = imageRow.querySelector('img');
    expect(image).toHaveAttribute('src', '/covers/Prusa/MK4.png');
    expect(image).toHaveAttribute('alt', '');
    expect(screen.getByText('MK4')).toBeInTheDocument();
    expect(screen.getByText('Prusa')).toBeInTheDocument();
    expect(imageRow.compareDocumentPosition(screen.getByTestId('print-progress')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('falls back after a cover error and retries when the printer model changes', () => {
    const { rerender } = render(
      <DetailedPrinterCard printer={makePrinter({ manufacturerName: 'Prusa', modelName: 'MK4' })} />,
    );
    const image = screen.getByTestId('detailed-printer-image').querySelector('img');
    if (!image) throw new Error('Printer image not rendered');
    fireEvent.error(image);
    expect(image).toHaveAttribute('src', '/assets/printers/generic-corexy.svg');

    rerender(<DetailedPrinterCard printer={makePrinter({ manufacturerName: 'Bambu Lab', modelName: 'P1S' })} />);
    expect(screen.getByTestId('detailed-printer-image').querySelector('img'))
      .toHaveAttribute('src', '/covers/Bambu Lab/P1S.png');
  });

  it('uses the generic printer image without a linked model', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);
    expect(screen.getByTestId('detailed-printer-image').querySelector('img'))
      .toHaveAttribute('src', '/assets/printers/generic-corexy.svg');
  });

  it.each([true, false])('matches the compact auto-dispatch visual and toggle contract when enabled=%s', async (enabled) => {
    useAutoDispatchStatusMock.mockReturnValue({
      data: { enabled, dispatchStateETag: 'dispatch-etag', printerETag: 'printer-etag' },
      isLoading: false,
    });
    render(<DetailedPrinterCard printer={makePrinter()} />);

    const button = screen.getByRole('button', { name: 'Toggle auto-dispatch for Printer 1' });
    expect(button).toHaveAttribute('aria-pressed', String(enabled));
    expect(button).toHaveAttribute('title', enabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled');
    expect(button).not.toHaveClass('bg-pf-accent-bg');
    expect(button).toHaveClass(enabled ? 'text-pf-accent' : 'text-pf-text-secondary');
    expect(button.querySelector('svg')).toHaveAttribute('fill', enabled ? 'currentColor' : 'none');

    fireEvent.click(button);
    await waitFor(() => expect(setAutoDispatchEnabledMock).toHaveBeenCalledWith({
      printerId: 'printer-1',
      enabled: !enabled,
      dispatchStateETag: 'dispatch-etag',
      printerETag: 'printer-etag',
    }));
  });

  it('disables auto-dispatch while a change is pending', () => {
    autoDispatchMutation.isPending = true;
    render(<DetailedPrinterCard printer={makePrinter()} />);

    const button = screen.getByRole('button', { name: 'Toggle auto-dispatch for Printer 1' });
    expect(button).toBeDisabled();
    fireEvent.click(button);
    expect(setAutoDispatchEnabledMock).not.toHaveBeenCalled();
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

  it('labels the firmware reading as unrecorded when no recorded identity is returned (#1656)', () => {
    render(<DetailedPrinterCard printer={makePrinter()} />);

    fireEvent.click(screen.getByText('Version'));

    expect(screen.getByText('No recorded firmware identity')).toBeInTheDocument();
    expect(screen.queryByText('Recorded firmware identity')).not.toBeInTheDocument();
  });

  it('labels the firmware reading as recorded when the version endpoint returns an identity (#1656)', () => {
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

    expect(screen.getByText('Recorded firmware identity')).toBeInTheDocument();
    expect(screen.queryByText('No recorded firmware identity')).not.toBeInTheDocument();
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
      data: {
        printerId: 'printer-1',
        objects: [makeObject({ name: 'part_1' }), makeObject({ name: 'part_2', isCurrent: true })],
      },
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

  it.each([
    ['Idle', true],
    ['Offline', false],
    ['Error', true],
    ['Complete', true],
    ['Printing', false],
    ['Paused', false],
  ])('omits the Objects section and inactive-print copy when the printer is %s (online: %s)', (state, isOnline) => {
    usePrintJobObjectsMock.mockReturnValue({
      data: { printerId: 'printer-1', objects: [makeObject({ name: 'stale_part' })] },
      isLoading: false,
      isFetching: false,
      refetch: vi.fn(),
    });

    render(
      <DetailedPrinterCard
        printer={makePrinter({ state, isOnline } as Partial<Printer>)}
        backendCapabilities={{ supportsObjectExclusion: true } as unknown as Parameters<typeof DetailedPrinterCard>[0]['backendCapabilities']}
      />
    );

    expect(screen.queryByText('Objects')).not.toBeInTheDocument();
    expect(screen.queryByText('stale_part')).not.toBeInTheDocument();
    expect(screen.queryByText(/Object skipping is available/)).not.toBeInTheDocument();
  });

  it('keeps the Objects section for a paused active print', () => {
    usePrintJobObjectsMock.mockReturnValue({
      data: { printerId: 'printer-1', objects: [makeObject({ name: 'part_1' })] },
      isLoading: false,
      isFetching: false,
      refetch: vi.fn(),
    });

    render(
      <DetailedPrinterCard
        printer={makePrinter({ state: 'Paused' } as Partial<Printer>)}
        backendCapabilities={{ supportsObjectExclusion: true } as unknown as Parameters<typeof DetailedPrinterCard>[0]['backendCapabilities']}
      />
    );

    expect(screen.getByText('Objects')).toBeInTheDocument();
    expect(screen.getByText('part_1')).toBeInTheDocument();
  });

  it('drops a pending skip confirmation when the active print ends', async () => {
    usePrintJobObjectsMock.mockReturnValue({
      data: { printerId: 'printer-1', objects: [makeObject({ name: 'part_1' })] },
      isLoading: false,
      isFetching: false,
      refetch: vi.fn(),
    });
    const capabilities = { supportsObjectExclusion: true } as unknown as Parameters<typeof DetailedPrinterCard>[0]['backendCapabilities'];
    const card = (state: string) => (
      <DetailedPrinterCard printer={makePrinter({ state } as Partial<Printer>)} backendCapabilities={capabilities} />
    );

    const { rerender } = render(card('Printing'));
    fireEvent.click(screen.getAllByRole('button', { name: /skip object/i })[0]);
    expect(screen.getByText('Skip print object?')).toBeInTheDocument();

    rerender(card('Complete'));
    await waitFor(() => expect(screen.queryByText('Skip print object?')).not.toBeInTheDocument());

    rerender(card('Printing'));
    expect(screen.queryByText('Skip print object?')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Skip object' })).not.toBeInTheDocument();
    expect(excludePrintJobObjectMock).not.toHaveBeenCalled();
  });

  // Regression coverage for #1698: the card's shared sections must appear in the same
  // relative order as PrinterDetailsSidebar (see printerDetailSectionOrder.ts):
  // Statistics, Version, Objects, Move, Temperature, Materials.
  it('renders shared sections in the same relative order as the sidebar (#1698)', () => {
    usePrintJobObjectsMock.mockReturnValue({
      data: { printerId: 'printer-1', objects: [makeObject({ name: 'part_1' })] },
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
