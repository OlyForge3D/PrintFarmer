import '@testing-library/jest-dom';
import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import {
  areCompactPrinterCardPropsEqual,
  type CompactPrinterCardMemoProps,
} from '@/features/printers/utils/compactPrinterCardMemo';
import { PrinterBackend, type MmuStatus, type Printer, type PrinterBackendCapabilitiesDto } from '@/types/api';

const progressBarRender = vi.hoisted(() => vi.fn());

vi.mock('@tanstack/react-query', () => ({
  useQuery: () => ({ data: [], isLoading: false }),
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  useJobQueue: () => ({ data: [], isLoading: false }),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getObjectTags: vi.fn().mockResolvedValue([]),
  },
}));

vi.mock('@/features/printers/hooks/useAutoDispatch', () => ({
  useAutoDispatchStatus: () => ({ data: null, isLoading: false }),
  useSetAutoDispatchEnabled: () => ({ mutateAsync: vi.fn() }),
}));

vi.mock('@/features/filament-coverage/hooks', () => ({
  usePrinterCoverageFromFleet: () => ({ data: undefined, isLoading: false }),
}));

vi.mock('@/features/filament-coverage/components/FilamentCoverageBadge', () => ({
  PrinterCoverageSummary: () => null,
}));

vi.mock('@/features/printers/hooks/useFailureDetectionAlert', () => ({
  useFailureDetectionAlert: () => ({ event: undefined, recentEvents: [] }),
}));

vi.mock('@/features/printers/hooks/usePrinterFailureDetectionStatus', () => ({
  usePrinterFailureDetectionStatus: () => ({ printerStatus: undefined, data: undefined, isLoading: false }),
}));

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({
  PrinterHistoryModal: () => null,
}));

vi.mock('@/features/printers/components/PrinterFilesModal', () => ({
  PrinterFilesModal: () => null,
}));

vi.mock('@/features/printers/components/PrintProgressBar', () => ({
  PrintProgressBar: ({ progress }: { progress?: number }) => {
    progressBarRender(progress);
    return <div data-testid="print-progress">{progress ?? 0}</div>;
  },
}));

vi.mock('@/features/printers/components/FailureDetectionBadge', () => ({
  FailureDetectionBadge: () => null,
}));

vi.mock('@/features/printers/components/FailureDetectionMonitoringBadge', () => ({
  FailureDetectionMonitoringBadge: () => null,
}));

vi.mock('@/features/printers/components/FailureDetectionMonitoringSummary', () => ({
  FailureDetectionMonitoringSummary: () => null,
}));

vi.mock('@/features/printers/components/OfflineTroubleshootingGuide', () => ({
  OfflineTroubleshootingGuide: () => null,
}));

vi.mock('@/features/printers/components/PrinterCameraPreview', () => ({
  PrinterCameraPreview: () => <div data-testid="camera-preview" />,
}));

vi.mock('@/features/printers/components/EstimatedCompletionBadge', () => ({
  EstimatedCompletionBadge: () => null,
}));

vi.mock('@/features/printers/components/BedClearBanner', () => ({
  BedClearBanner: () => null,
}));

vi.mock('@/components/TaggingModal', () => ({
  TaggingModal: () => null,
}));

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  },
}));

import { CompactPrinterCard } from '@/features/printers/components/CompactPrinterCard';

function createPrinter(overrides: Partial<Printer> = {}): Printer {
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

function createProps(
  printer: Printer,
  overrides: Partial<CompactPrinterCardMemoProps> = {},
): CompactPrinterCardMemoProps {
  return {
    printer,
    onExpand: vi.fn(),
    onEdit: vi.fn(),
    ...overrides,
  };
}

function createCapabilities(
  overrides: Partial<PrinterBackendCapabilitiesDto> = {},
): PrinterBackendCapabilitiesDto {
  return {
    printerId: 'printer-1',
    printerName: 'Printer 1',
    backend: PrinterBackend.Moonraker,
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
    supportsFilamentControl: true,
    ...overrides,
  };
}

describe('CompactPrinterCard memoization', () => {
  beforeEach(() => {
    progressBarRender.mockClear();
  });

  it('skips rendering when parent recreates unchanged printer props with stable callbacks', () => {
    const onExpand = vi.fn();
    const onEdit = vi.fn();
    const previous = createProps(createPrinter(), { onExpand, onEdit });
    const next = createProps(createPrinter(), { onExpand, onEdit });

    expect(areCompactPrinterCardPropsEqual(previous, next)).toBe(true);
  });

  it('renders when callbacks change', () => {
    const previous = createProps(createPrinter(), { onExpand: vi.fn(), onEdit: vi.fn() });
    const next = createProps(createPrinter(), { onExpand: vi.fn(), onEdit: previous.onEdit });

    expect(areCompactPrinterCardPropsEqual(previous, next)).toBe(false);
  });

  it('renders when optional own-key membership changes even when values are undefined', () => {
    const onExpand = vi.fn();
    const onEdit = vi.fn();
    const previous = createProps(createPrinter({ jobName: undefined }), { onExpand, onEdit });
    const next = createProps(createPrinter({ fileName: undefined }), { onExpand, onEdit });

    expect(areCompactPrinterCardPropsEqual(previous, next)).toBe(false);
  });

  it('renders when backend capabilities change', () => {
    const onExpand = vi.fn();
    const previousCapabilities = createCapabilities();
    const nextCapabilities = createCapabilities({ supportsHistory: false });
    const previous = createProps(createPrinter(), { onExpand, backendCapabilities: previousCapabilities });
    const next = createProps(createPrinter(), { onExpand, onEdit: previous.onEdit, backendCapabilities: nextCapabilities });

    expect(areCompactPrinterCardPropsEqual(previous, next)).toBe(false);
  });

  it('renders when nested printer references change', () => {
    const onExpand = vi.fn();
    const onEdit = vi.fn();
    const previousSpoolInfo = { hasActiveSpool: true, material: 'PLA' };
    const nextSpoolInfo = { hasActiveSpool: true, material: 'PLA' };
    const previousMmuStatus = { gates: [] } as unknown as MmuStatus;
    const nextMmuStatus = { gates: [] } as unknown as MmuStatus;
    const previous = createProps(
      createPrinter({ spoolInfo: previousSpoolInfo, mmuStatus: previousMmuStatus } as Partial<Printer>),
      { onExpand, onEdit },
    );
    const next = createProps(
      createPrinter({ spoolInfo: nextSpoolInfo, mmuStatus: nextMmuStatus } as Partial<Printer>),
      { onExpand, onEdit },
    );

    expect(areCompactPrinterCardPropsEqual(previous, next)).toBe(false);
  });

  it('renders when live printer status changes', () => {
    const onExpand = vi.fn();
    const onEdit = vi.fn();
    const previous = createProps(createPrinter({ progress: 10, state: 'Printing' }), { onExpand, onEdit });
    const next = createProps(createPrinter({ progress: 11, state: 'Printing' }), { onExpand, onEdit });

    expect(areCompactPrinterCardPropsEqual(previous, next)).toBe(false);
  });

  it('wires the comparator into the exported memoized component', () => {
    const onExpand = vi.fn();
    const onEdit = vi.fn();
    const { rerender } = render(
      <CompactPrinterCard
        printer={createPrinter({ progress: 10, state: 'Printing' })}
        onExpand={onExpand}
        onEdit={onEdit}
      />,
    );

    expect(screen.getByTestId('print-progress')).toHaveTextContent('10');
    expect(progressBarRender).toHaveBeenCalledTimes(1);

    rerender(
      <CompactPrinterCard
        printer={createPrinter({ progress: 10, state: 'Printing' })}
        onExpand={onExpand}
        onEdit={onEdit}
      />,
    );

    expect(progressBarRender).toHaveBeenCalledTimes(1);

    rerender(
      <CompactPrinterCard
        printer={createPrinter({ progress: 11, state: 'Printing' })}
        onExpand={onExpand}
        onEdit={onEdit}
      />,
    );

    expect(screen.getByTestId('print-progress')).toHaveTextContent('11');
    expect(progressBarRender).toHaveBeenCalledTimes(2);
  });
});
