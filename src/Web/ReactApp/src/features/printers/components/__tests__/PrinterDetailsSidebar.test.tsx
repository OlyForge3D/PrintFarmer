import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { UseQueryOptions } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { PrinterBackend, type CommandResult, type Printer, type PrinterBackendCapabilitiesDto, type PrintJobObjectListDto } from '@/types/api';
import { PrinterDetailsSidebar } from '../PrinterDetailsSidebar';
import type { PrinterStatistics } from '@/types/maintenance';

const mockInvalidateQueries = vi.fn();
const mockSetQueryData = vi.fn();
const mockRefetch = vi.fn();
const mockPrintJobObjectsRefetch = vi.fn();
const mockExcludePrintJobObject = vi.fn();
let capturedStatisticsQueryOptions: UseQueryOptions<PrinterStatistics> | undefined;
let mockStatisticsData: PrinterStatistics | undefined;
let mockPrintJobObjectsData: PrintJobObjectListDto | undefined;

vi.mock('@tanstack/react-query', () => ({
  useQuery: (options: UseQueryOptions<PrinterStatistics>) => {
    if (Array.isArray(options.queryKey) && options.queryKey[0] === 'printerStatistics') {
      capturedStatisticsQueryOptions = options;
      return {
        data: mockStatisticsData,
        isLoading: false,
        refetch: mockRefetch,
      };
    }

    return {
      data: undefined,
      isLoading: false,
      refetch: mockRefetch,
    };
  },
  useQueryClient: () => ({
    invalidateQueries: mockInvalidateQueries,
    setQueryData: mockSetQueryData,
  }),
  useMutation: (options: {
    mutationFn: (name: string) => Promise<CommandResult>;
    onSuccess?: (result: CommandResult, name: string) => void | Promise<void>;
    onError?: (error: Error) => void;
  }) => ({
    isPending: false,
    mutate: (name: string) => {
      void options.mutationFn(name)
        .then((result) => options.onSuccess?.(result, name))
        .catch((error: Error) => options.onError?.(error));
    },
  }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  queryKeys: {
    printJobObjects: (printerId: string) => ['printers', printerId, 'printjob', 'objects'],
  },
  usePrinter: () => ({
    data: undefined,
    isLoading: false,
    refetch: mockRefetch,
  }),
  usePrinterDetails: () => ({ data: undefined }),
  usePrintJobObjects: () => ({
    data: mockPrintJobObjectsData,
    isLoading: false,
    isFetching: false,
    refetch: mockPrintJobObjectsRefetch,
  }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    excludePrintJobObject: (printerId: string, name: string) => mockExcludePrintJobObject(printerId, name),
  },
}));

vi.mock('@/common/hooks/usePrinterDisplay', () => ({
  usePrinterDisplay: (printer: Printer) => printer,
}));

vi.mock('@/common/hooks/useSpoolmanConfigured', () => ({
  useSpoolmanConfigured: () => ({ ready: false }),
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: () => ({ data: undefined }),
}));

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({
  PrinterHistoryModal: () => null,
}));

vi.mock('@/features/printers/components/PrinterFilesModal', () => ({
  PrinterFilesModal: () => null,
}));

vi.mock('@/features/printers/components/SpoolPickerModal', () => ({
  SpoolPickerModal: () => null,
}));

const printer: Printer = {
  id: 'printer-1',
  name: 'Printer Alpha',
  manufacturerName: 'Prusa',
  modelName: 'MK4',
  backend: PrinterBackend.PrusaLink,
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
};

function capabilities(overrides: Partial<PrinterBackendCapabilitiesDto> = {}): PrinterBackendCapabilitiesDto {
  return {
    printerId: printer.id,
    printerName: printer.name,
    backend: printer.backend,
    supportsCamera: true,
    supportsFileDownload: true,
    supportsFileList: true,
    supportsFileUpload: true,
    supportsStartPrint: true,
    supportsControlOperations: true,
    supportsFileMetadata: true,
    supportsMovement: true,
    supportsTemperatureControl: true,
    supportsPrinterInformation: true,
    supportsHistory: true,
    supportsFilamentControl: false,
    supportsObjectExclusion: false,
    ...overrides,
  };
}

describe('PrinterDetailsSidebar', () => {
  beforeEach(() => {
    capturedStatisticsQueryOptions = undefined;
    mockStatisticsData = undefined;
    mockPrintJobObjectsData = undefined;
    mockInvalidateQueries.mockClear();
    mockRefetch.mockClear();
    mockPrintJobObjectsRefetch.mockClear();
    mockSetQueryData.mockClear();
    mockExcludePrintJobObject.mockReset();
    mockExcludePrintJobObject.mockResolvedValue({ success: true, message: 'Object skipped' });
  });

  it('bounds content layout on desktop and lets the inner region scroll', () => {
    const { container } = render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="content"
      />
    );

    const shell = container.firstElementChild;
    expect(shell).toHaveClass('w-full', 'max-w-sm', 'overflow-hidden', 'flex', 'flex-col');
    expect(shell).toHaveClass('lg:max-h-[calc(100dvh-5rem)]', 'lg:min-h-0');

    const scrollRegion = shell?.querySelector('.overflow-y-auto');
    expect(scrollRegion).toHaveClass('flex-1', 'min-h-0', 'overflow-y-auto');
  });

  it('keeps the drawer layout height contract unchanged', () => {
    const { container } = render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="panel"
      />
    );

    const shell = container.firstElementChild;
    expect(shell).toHaveClass('w-[calc(100%-1.5rem)]', 'h-[calc(100%-1.5rem)]', 'shrink-0');
    expect(shell).not.toHaveClass('lg:max-h-[calc(100dvh-5rem)]');
  });

  it('does not retry printer statistics query on client errors', () => {
    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="panel"
      />
    );

    expect(capturedStatisticsQueryOptions?.retry).toBeTypeOf('function');
    const retry = capturedStatisticsQueryOptions!.retry as (failureCount: number, error: unknown) => boolean;
    expect(retry(0, { statusCode: 404 })).toBe(false);
    expect(retry(0, { response: { status: 404 } })).toBe(false);
    expect(retry(0, { statusCode: 500 })).toBe(true);
    expect(retry(2, { statusCode: 500 })).toBe(false);
  });

  it('renders never-synced statistics with an em dash last sync', () => {
    mockStatisticsData = {
      id: printer.id,
      printerId: printer.id,
      totalPrintHours: 0,
      totalJobsCompleted: 0,
      totalJobsFailed: 0,
      totalFilamentUsedGrams: 0,
      totalFilamentUsedMeters: 0,
      lastSyncTime: '0001-01-01T00:00:00',
      createdAt: '0001-01-01T00:00:00',
      updatedAt: '0001-01-01T00:00:00',
    };

    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={printer}
        onClose={vi.fn()}
        layout="panel"
      />
    );

    fireEvent.click(screen.getByText('Statistics'));

    const lastSyncTerm = screen.getByText('Last sync');
    const lastSyncRow = lastSyncTerm.closest('div');

    expect(lastSyncRow).not.toBeNull();
    expect(within(lastSyncRow!).getByText('—')).toBeInTheDocument();
  });

  it('hides object skip controls when backend capability is false', () => {
    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={{ ...printer, state: 'Printing' }}
        backendCapabilities={capabilities({ supportsObjectExclusion: false })}
        onClose={vi.fn()}
        layout="panel"
      />
    );

    expect(screen.queryByText('Objects')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Skip object cube')).not.toBeInTheDocument();
  });

  it('calls the skip object mutation after confirmation', async () => {
    mockPrintJobObjectsData = {
      printerId: printer.id,
      jobName: 'plate.gcode',
      objects: [
        { name: 'cube', isExcluded: false, isCurrent: true },
      ],
    };

    render(
      <PrinterDetailsSidebar
        printerId={printer.id}
        printer={{ ...printer, backend: PrinterBackend.Moonraker, state: 'Printing' }}
        backendCapabilities={capabilities({
          backend: PrinterBackend.Moonraker,
          supportsObjectExclusion: true,
        })}
        onClose={vi.fn()}
        layout="panel"
      />
    );

    fireEvent.click(screen.getByLabelText('Skip object cube'));
    fireEvent.click(screen.getByRole('button', { name: 'Skip object' }));

    await waitFor(() => {
      expect(mockExcludePrintJobObject).toHaveBeenCalledWith(printer.id, 'cube');
    });

    expect(mockSetQueryData).toHaveBeenCalledWith(
      ['printers', printer.id, 'printjob', 'objects'],
      expect.any(Function)
    );
    const updateCache = mockSetQueryData.mock.calls[0][1] as (old: PrintJobObjectListDto) => PrintJobObjectListDto;
    expect(updateCache(mockPrintJobObjectsData!).objects[0]).toMatchObject({
      name: 'cube',
      isExcluded: true,
      isCurrent: false,
    });
  });
});
