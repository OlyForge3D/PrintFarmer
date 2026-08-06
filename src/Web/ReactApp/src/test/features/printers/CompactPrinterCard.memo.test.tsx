import '@testing-library/jest-dom';
import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import {
  areCompactPrinterCardPropsEqual,
  type CompactPrinterCardMemoProps,
} from '@/features/printers/utils/compactPrinterCardMemo';
import { PrinterBackend, type MmuStatus, type Printer, type PrinterBackendCapabilitiesDto } from '@/types/api';
import type { PrinterQueueSummaryDto } from '@/types/api';

const progressBarRender = vi.hoisted(() => vi.fn());
const printerTagsFromFleetMock = vi.hoisted(() =>
  vi.fn(() => ({ data: [], isPending: false, isError: false, error: null }))
);
const queueSummaryFromFleetMock = vi.hoisted(() =>
  vi.fn(() => ({ data: undefined as PrinterQueueSummaryDto | undefined, isPending: false, isError: false, error: null }))
);
const failureDetectionPollingEnabledMock = vi.hoisted(() => vi.fn(() => false));
const usePrinterFailureDetectionStatusMock = vi.hoisted(() =>
  vi.fn(() => ({ printerStatus: undefined, data: undefined, isLoading: false }))
);

vi.mock('@tanstack/react-query', () => ({
  useQuery: () => ({ data: [], isLoading: false }),
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

vi.mock('@/features/printers/hooks/usePrinterTagsFleet', () => ({
  usePrinterTagsFromFleet: printerTagsFromFleetMock,
}));

vi.mock('@/features/printers/hooks/useQueueSummariesFleet', () => ({
  useQueueSummaryFromFleet: queueSummaryFromFleetMock,
}));

vi.mock('@/features/printers/hooks/useFailureDetectionPolling', () => ({
  useFailureDetectionPollingEnabled: failureDetectionPollingEnabledMock,
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
  usePrinterFailureDetectionStatus: usePrinterFailureDetectionStatusMock,
}));

vi.mock('@/features/printers/components/PrinterHistoryModal', () => ({
  PrinterHistoryModal: () => null,
}));

vi.mock('@/features/printers/components/PrinterFilesModal', () => ({
  PrinterFilesModal: () => null,
}));

vi.mock('@/features/printers/components/PrintProgressBar', () => ({
  PrintProgressBar: ({ progress, queueLabel }: { progress?: number; queueLabel?: string }) => {
    progressBarRender(progress);
    return (
      <div data-testid="print-progress">
        {progress ?? 0}
        {queueLabel && <span>{queueLabel}</span>}
      </div>
    );
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
    printerTagsFromFleetMock.mockClear();
    printerTagsFromFleetMock.mockReturnValue({ data: [], isPending: false, isError: false, error: null });
    queueSummaryFromFleetMock.mockClear();
    queueSummaryFromFleetMock.mockReturnValue({ data: undefined, isPending: false, isError: false, error: null });
    failureDetectionPollingEnabledMock.mockClear();
    failureDetectionPollingEnabledMock.mockReturnValue(false);
    usePrinterFailureDetectionStatusMock.mockClear();
    usePrinterFailureDetectionStatusMock.mockReturnValue({ printerStatus: undefined, data: undefined, isLoading: false });
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

  it('reads the shared queue-summary fleet query by printer id instead of polling its own queue', () => {
    render(
      <CompactPrinterCard
        printer={createPrinter({ state: 'Idle', isOnline: true })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    // #1146 item 9: no per-card `useJobQueue` call exists anymore — the card
    // selects its row from the one shared fleet query instead.
    expect(queueSummaryFromFleetMock).toHaveBeenCalledWith('printer-1');
  });

  it('does not render a queue label for an idle printer even if the fleet summary has one (prevents stale labels)', () => {
    // The fleet endpoint omits idle printers entirely, but this guards the
    // display-side predicate directly: even if a summary entry existed for
    // this printer id (e.g. queued-but-not-dispatched jobs while blocked on
    // a bed-clear confirmation), an Idle/non-printing card must never show a
    // stray position label — matching the pre-fleet UI exactly.
    queueSummaryFromFleetMock.mockReturnValue({
      data: { printerId: 'printer-1', queuedCount: 2, printingCount: 0, printingPosition: null },
      isPending: false,
      isError: false,
      error: null,
    });

    render(
      <CompactPrinterCard
        printer={createPrinter({ state: 'Idle', isOnline: true })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(screen.queryByText('1 of 2')).not.toBeInTheDocument();
  });

  it('renders the "X of Y" queue label for an active printer from the fleet summary', () => {
    queueSummaryFromFleetMock.mockReturnValue({
      data: { printerId: 'printer-1', queuedCount: 1, printingCount: 1, printingPosition: 1 },
      isPending: false,
      isError: false,
      error: null,
    });

    render(
      <CompactPrinterCard
        printer={createPrinter({ state: 'Printing', isOnline: true })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(queueSummaryFromFleetMock).toHaveBeenCalledWith('printer-1');
    expect(screen.getByText('1 of 2')).toBeInTheDocument();
  });

  it('does not render a queue label when only the currently-printing job is active (no queue depth)', () => {
    queueSummaryFromFleetMock.mockReturnValue({
      data: { printerId: 'printer-1', queuedCount: 0, printingCount: 1, printingPosition: 1 },
      isPending: false,
      isError: false,
      error: null,
    });

    render(
      <CompactPrinterCard
        printer={createPrinter({ state: 'Printing', isOnline: true })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(screen.queryByText('1 of 1')).not.toBeInTheDocument();
  });

  it('reads its tag list from the shared tags fleet query by printer id (#1146 item 1)', () => {
    printerTagsFromFleetMock.mockReturnValue({
      data: [{ id: 'tag-1', name: 'Production' }],
      isPending: false,
      isError: false,
      error: null,
    });

    render(
      <CompactPrinterCard
        printer={createPrinter()}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(printerTagsFromFleetMock).toHaveBeenCalledWith('printer-1');
    expect(screen.getByText('Production')).toBeInTheDocument();
  });

  it('renders no tag pills when the fleet query has no tags for this printer (empty-tag behavior)', () => {
    printerTagsFromFleetMock.mockReturnValue({ data: [], isPending: false, isError: false, error: null });

    render(
      <CompactPrinterCard
        printer={createPrinter()}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(screen.queryByText('Production')).not.toBeInTheDocument();
  });

  it('renders the "X of Y" queue label for a Paused printer, not just Printing (#1146 item 9 gating)', () => {
    // canShowQueueLabel = isOnline && (isPrinting || isPaused). Only the
    // Idle (false) and Printing (true) branches were previously exercised;
    // this proves the Paused branch of the OR is also wired correctly.
    queueSummaryFromFleetMock.mockReturnValue({
      data: { printerId: 'printer-1', queuedCount: 1, printingCount: 1, printingPosition: 1 },
      isPending: false,
      isError: false,
      error: null,
    });

    render(
      <CompactPrinterCard
        printer={createPrinter({ state: 'Paused', isOnline: true })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(screen.getByText('1 of 2')).toBeInTheDocument();
  });

  it('never renders a queue label for an offline printer even while its last-known state string says Printing', () => {
    // canShowQueueLabel requires isOnline; an offline printer must never
    // show a stray position label just because its stale `state` field
    // still reads "Printing" from before it dropped offline.
    queueSummaryFromFleetMock.mockReturnValue({
      data: { printerId: 'printer-1', queuedCount: 1, printingCount: 1, printingPosition: 1 },
      isPending: false,
      isError: false,
      error: null,
    });

    render(
      <CompactPrinterCard
        printer={createPrinter({ state: 'Printing', isOnline: false })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(screen.queryByText('1 of 2')).not.toBeInTheDocument();
  });

  it('threads the fleet-wide failure-detection polling gate through as this printer\'s own enabled flag (true)', () => {
    // #1146 item 3: the enabled flag passed to the shared status poll must
    // come from the fleet-level context/hook, not be recomputed per-card
    // from just this printer's own `obicoEnabled` field.
    failureDetectionPollingEnabledMock.mockReturnValue(true);

    render(
      <CompactPrinterCard
        printer={createPrinter({ obicoEnabled: false })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(usePrinterFailureDetectionStatusMock).toHaveBeenCalledWith('printer-1', true);
  });

  it('threads the fleet-wide failure-detection polling gate through as this printer\'s own enabled flag (false)', () => {
    failureDetectionPollingEnabledMock.mockReturnValue(false);

    render(
      <CompactPrinterCard
        printer={createPrinter({ obicoEnabled: true })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    // Even though *this* printer has obicoEnabled: true, the fleet-wide gate
    // (mocked false here) is what must govern the poll now — proves the
    // card no longer falls back to reading its own printer field.
    expect(usePrinterFailureDetectionStatusMock).toHaveBeenCalledWith('printer-1', false);
  });

  it('rerenders when only onEdit changes while all other props stay equal', () => {
    const onExpand = vi.fn();
    const { rerender } = render(
      <CompactPrinterCard
        printer={createPrinter({ progress: 7, state: 'Printing' })}
        onExpand={onExpand}
        onEdit={vi.fn()}
      />,
    );

    expect(progressBarRender).toHaveBeenCalledTimes(1);

    // New onEdit reference; printer (compared by shallowEqualPrinter) and onExpand
    // remain equal. The memoized component must rerender because onEdit changed.
    // Deleting `previous.onEdit === next.onEdit` from areCompactPrinterCardPropsEqual
    // causes the comparator to return true here, suppressing the rerender and
    // failing the assertion below.
    rerender(
      <CompactPrinterCard
        printer={createPrinter({ progress: 7, state: 'Printing' })}
        onExpand={onExpand}
        onEdit={vi.fn()}
      />,
    );

    expect(progressBarRender).toHaveBeenCalledTimes(2);
  });
});
