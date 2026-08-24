import '@testing-library/jest-dom';
import React from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
const taggingModalRenderMock = vi.hoisted(() => vi.fn());

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

const printerCoverageSummaryRenderMock = vi.hoisted(() => vi.fn());

vi.mock('@/features/filament-coverage/components/FilamentCoverageBadge', () => ({
  PrinterCoverageSummary: (props: Record<string, unknown>) => {
    printerCoverageSummaryRenderMock(props);
    return null;
  },
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
  TaggingModal: (props: Record<string, unknown>) => {
    taggingModalRenderMock(props);
    return null;
  },
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
    taggingModalRenderMock.mockClear();
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

  it('links Open in Browser to the printer frontend URL', async () => {
    const user = userEvent.setup();

    render(
      <CompactPrinterCard
        printer={createPrinter()}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'More options' }));

    expect(screen.getByRole('link', { name: /open in browser for printer printer 1 in new tab/i }))
      .toHaveAttribute('href', 'http://printer-1.local');
  });

  it('keeps the Open details sidebar action in compact mode', async () => {
    const user = userEvent.setup();
    const onExpand = vi.fn();

    render(
      <CompactPrinterCard
        printer={createPrinter()}
        onExpand={onExpand}
        onEdit={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Open details sidebar' }));

    expect(onExpand).toHaveBeenCalledWith('printer-1');
  });

  it('does not render an unsafe browser URL as a link', async () => {
    const user = userEvent.setup();

    render(
      <CompactPrinterCard
        printer={createPrinter({ frontendUrl: 'javascript:alert(1)' })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'More options' }));

    expect(screen.queryByRole('link', { name: /open in browser/i })).not.toBeInTheDocument();
    const fallbackButton = screen.getByRole('button', {
      name: /open in browser unavailable for printer printer 1: printer browser url is unavailable/i,
    });
    expect(fallbackButton).not.toBeDisabled();
    expect(fallbackButton).toHaveAttribute('aria-disabled', 'true');
  });

  it('disables Open in Browser with an explanatory tooltip for a TestEmulator internal-only host (#1546)', async () => {
    const user = userEvent.setup();

    render(
      <CompactPrinterCard
        printer={createPrinter({ frontendUrl: 'http://testemulator-11111111-1111-1111-1111-111111111111' })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'More options' }));

    expect(screen.queryByRole('link', { name: /open in browser/i })).not.toBeInTheDocument();
    const disabledButton = screen.getByRole('button', {
      name: /open in browser unavailable for printer printer 1: not available for simulated test printers/i,
    });
    expect(disabledButton).not.toBeDisabled();
    expect(disabledButton).toHaveAttribute('aria-disabled', 'true');
    expect(disabledButton).toHaveAttribute('title', 'Not available for simulated test printers');
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

  it('renders the "X of Y" queue label for an Idle printer when the authoritative fleet summary reports queue depth (Dallas item-9: no online/printing display gate)', () => {
    // Regression test: an earlier revision gated *display* behind
    // `isOnline && (isPrinting || isPaused)`, which suppressed this label for
    // an Idle printer that is legitimately queued (e.g. blocked on a
    // bed-clear confirmation, or simply not yet dispatched). The fleet
    // summary is itself the authoritative "does this printer have active
    // queue depth" signal — a printer absent from the fleet response already
    // renders no label (see the empty-tag-style test above), so no
    // additional online/printing gate is needed, matching
    // origin/development's pre-fleet behavior of showing the label for any
    // printer state as long as queue depth > 1.
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

    expect(screen.getByText('1 of 2')).toBeInTheDocument();
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
      data: [{
        id: 'tag-1',
        name: 'Production',
        color: '#ffff00',
        description: 'Production-ready printer',
      }],
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
    const tag = screen.getByText('Production').closest('[data-pf-radius="full"]');
    expect(tag).toHaveClass('bg-black/70', 'text-white');
    expect(tag).toHaveStyle({ borderColor: '#ffff00' });
    expect(tag).toHaveAttribute('title', 'Production-ready printer');
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

  it('threads the fleet tags-fleet pending flag into TaggingModal as tagsLoading (true) — this card renders TaggingModal unconditionally, so the modal must know when initialTags is not resolved yet', () => {
    printerTagsFromFleetMock.mockReturnValue({ data: [], isPending: true, isError: false, error: null });

    render(
      <CompactPrinterCard
        printer={createPrinter()}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(taggingModalRenderMock).toHaveBeenLastCalledWith(
      expect.objectContaining({ tagsLoading: true, initialTags: [] }),
    );
  });

  it('threads the fleet tags-fleet pending flag into TaggingModal as tagsLoading (false) once tags resolve', () => {
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

    expect(taggingModalRenderMock).toHaveBeenLastCalledWith(
      expect.objectContaining({
        tagsLoading: false,
        initialTags: [{ id: 'tag-1', name: 'Production' }],
      }),
    );
  });

  it('renders the "X of Y" queue label for a Paused printer, not just Printing', () => {
    // The label derives purely from the fleet summary's active-job total, so
    // it must render identically regardless of which non-idle printer state
    // the card is currently in.
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

  it('renders the "X of Y" queue label for an Offline printer with a stale last-known state string (authoritative summary overrides local staleness)', () => {
    // Regression test (Dallas item-9 / origin/development parity): the
    // display predicate must not reintroduce an `isOnline` (or any other
    // locally-cached printer field) check. The fleet queue-summary endpoint
    // is the authoritative source of truth for queue depth; a printer's own
    // `isOnline`/`state` fields can be momentarily stale (e.g. a dropped
    // SignalR connection) without the backend's queue state having changed,
    // so gating the label on those local fields would hide genuinely correct
    // information behind unrelated staleness.
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

    expect(screen.getByText('1 of 2')).toBeInTheDocument();
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

describe('CompactPrinterCard filament coverage online gating (#1684)', () => {
  beforeEach(() => {
    printerCoverageSummaryRenderMock.mockClear();
  });

  // Regression test for issue #1684: the Moonraker Offline card showed
  // "No spool loaded" alongside a green "Filament OK" check even though the
  // printer was unreachable. `PrinterCoverageSummary` now derives its
  // effective status from `isOnline`, but only if the card actually passes
  // that flag through — this pins the wiring so a future refactor can't
  // silently drop it again.
  it('passes isOnline=false through to PrinterCoverageSummary for an offline printer', () => {
    render(
      <CompactPrinterCard
        printer={createPrinter({ isOnline: false })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(printerCoverageSummaryRenderMock).toHaveBeenCalledWith(
      expect.objectContaining({ isOnline: false }),
    );
  });

  it('passes isOnline=true through to PrinterCoverageSummary for an online printer', () => {
    render(
      <CompactPrinterCard
        printer={createPrinter({ isOnline: true })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    expect(printerCoverageSummaryRenderMock).toHaveBeenCalledWith(
      expect.objectContaining({ isOnline: true }),
    );
  });
});

/**
 * #1584: the History action must be reachable for PrusaLink printers.
 *
 * `backendCapabilities` is fetched per-printer and is undefined on first paint, so
 * `getPrinterSupport` falls back to a client-side guess. That guess previously listed
 * only Moonraker and OctoPrint, which hid History for PrusaLink (and SDCP) until the
 * capability query resolved — and permanently whenever it never resolved.
 *
 * The fallback must mirror the server's own rule (`ISupportsHistory`, surfaced by
 * `PrinterBackendCapabilitiesService`), so these render with NO capabilities to pin the
 * pre-hydration behaviour specifically.
 */
describe('CompactPrinterCard history action before capabilities hydrate', () => {
  beforeEach(() => {
    printerTagsFromFleetMock.mockReturnValue({ data: [], isPending: false, isError: false, error: null });
    queueSummaryFromFleetMock.mockReturnValue({ data: undefined, isPending: false, isError: false, error: null });
    failureDetectionPollingEnabledMock.mockReturnValue(false);
    usePrinterFailureDetectionStatusMock.mockReturnValue({ printerStatus: undefined, data: undefined, isLoading: false });
  });

  it.each([
    ['PrusaLink', PrinterBackend.PrusaLink],
    ['Moonraker', PrinterBackend.Moonraker],
    ['OctoPrint', PrinterBackend.OctoPrint],
    ['SDCP', PrinterBackend.SDCP],
  ])('offers History for a history-capable %s printer with no capabilities loaded', async (_name, backend) => {
    const user = userEvent.setup();

    render(
      <CompactPrinterCard
        printer={createPrinter({ backend })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'More options' }));

    expect(screen.getByRole('button', { name: 'History' })).toBeEnabled();
  });

  it.each([
    ['FlashForge', PrinterBackend.FlashForge],
    ['Unknown', PrinterBackend.Unknown],
  ])('still hides History for a non-history %s backend before hydration', async (_name, backend) => {
    const user = userEvent.setup();

    render(
      <CompactPrinterCard
        printer={createPrinter({ backend })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'More options' }));

    expect(screen.queryByRole('button', { name: 'History' })).not.toBeInTheDocument();
  });

  it('lets the authoritative capability payload override the fallback', async () => {
    const user = userEvent.setup();

    render(
      <CompactPrinterCard
        printer={createPrinter({ backend: PrinterBackend.PrusaLink })}
        backendCapabilities={createCapabilities({ supportsHistory: false })}
        onExpand={vi.fn()}
        onEdit={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'More options' }));

    expect(screen.queryByRole('button', { name: 'History' })).not.toBeInTheDocument();
  });
});