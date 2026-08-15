import '@testing-library/jest-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterInlineDetails } from '@/features/printers/components/PrinterInlineDetails';
import { apiClient } from '@/services/api';
import { maintenanceService } from '@/services/maintenanceService';
import {
  PrinterBackend,
  type Printer,
  type PrinterBackendCapabilitiesDto,
} from '@/types/api';

const usePrintJobObjectsMock = vi.hoisted(() => vi.fn());

vi.mock('@/common/hooks/useApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/common/hooks/useApi')>();
  return {
    ...actual,
    usePrintJobObjects: usePrintJobObjectsMock,
  };
});

const printer = {
  id: 'printer-1',
  name: 'Printer 1',
  backend: PrinterBackend.PrusaLink,
  state: 'Idle',
  isOnline: true,
  isEnabled: true,
} as Printer;

function capabilities(
  overrides: Partial<PrinterBackendCapabilitiesDto> = {},
): PrinterBackendCapabilitiesDto {
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

function renderDetails(backendCapabilities = capabilities()) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <PrinterInlineDetails
        printerId={printer.id}
        printer={printer}
        backendCapabilities={backendCapabilities}
      />
    </QueryClientProvider>,
  );
}

describe('PrinterInlineDetails', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    usePrintJobObjectsMock.mockReturnValue({
      data: undefined,
      isLoading: false,
      isFetching: false,
      refetch: vi.fn(),
    });
    vi.spyOn(maintenanceService, 'getPrinterStatistics').mockResolvedValue({
      totalPrintHours: 0,
      totalFilamentUsedMeters: 0,
      totalFilamentUsedGrams: 0,
      totalJobsCompleted: 0,
      totalJobsFailed: 0,
      successRate: 0,
      averagePrintTimeHours: 0,
      lastSyncTime: null,
      createdAt: '',
      updatedAt: '',
    });
  });

  it('keeps Version collapsed and its upstream query disabled until expansion', async () => {
    const versionSpy = vi.spyOn(apiClient, 'getPrinterVersionInfo').mockResolvedValue({
      firmwareVersion: '6.0.0',
      backendVersion: '0.8.0',
      apiVersion: '1.0.0',
      supported: true,
    });

    renderDetails();

    expect(versionSpy).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /version/i }));

    await waitFor(() => expect(versionSpy).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('6.0.0')).toBeInTheDocument();
  });

  it('composes only supported informational sections without duplicate card controls', () => {
    vi.spyOn(apiClient, 'getPrinterVersionInfo').mockResolvedValue({
      supported: true,
    });

    renderDetails(capabilities({ supportsObjectExclusion: false }));

    expect(screen.getByRole('region', { name: 'Printer 1 details' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /statistics/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /version/i })).toBeInTheDocument();
    expect(screen.queryByText('Objects')).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Materials' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /print|pause|cancel|move|temperature/i }))
      .not.toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
