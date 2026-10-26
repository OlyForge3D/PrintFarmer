import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PrinterFileCatalogPage, PrinterFileOperation } from '@/types/api';
import { PrinterFileCatalog } from '@/features/files/components/PrinterFileCatalog';

const mocks = vi.hoisted(() => ({
  catalog: vi.fn(), operations: vi.fn(), operation: vi.fn(), copy: vi.fn(), queue: vi.fn(),
  confirm: vi.fn(), cancel: vi.fn(), retry: vi.fn(), refresh: vi.fn(), enabled: vi.fn(),
  hasPermission: vi.fn(),
}));
vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterFileCatalog: mocks.catalog, getPrinterFileOperations: mocks.operations,
    getPrinterFileOperation: mocks.operation, copyIndexedPrinterFile: mocks.copy,
    queueIndexedPrinterFile: mocks.queue, confirmPrinterFileSnapshot: mocks.confirm,
    cancelPrinterFileOperation: mocks.cancel, retryPrinterFileOperation: mocks.retry,
    refreshPrinterFileIndex: mocks.refresh, setPrinterFileIndexEnabled: mocks.enabled,
  },
}));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: mocks.hasPermission }),
}));
vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: [
    { id: 'source', name: 'Source printer', isEnabled: true },
    { id: 'other', name: 'Other printer', isEnabled: true },
  ] }),
}));

const catalog: PrinterFileCatalogPage = {
  items: [{
    id: 'location', printerId: 'source', printerName: 'Source printer', storageRoot: 'gcodes',
    relativePath: 'Parts/part.gcode', displayName: 'part.gcode', sizeBytes: null, modifiedValue: null,
    observationRevision: 7, configurationGeneration: 1, availability: 'Available',
    lastSeenAtUtc: '2026-10-25T12:00:00Z', lastCompleteScanAtUtc: null, isStale: true,
    localCopies: [],
  }],
  total: 1, page: 1, pageSize: 25,
  printers: [{ id: 'source', name: 'Source printer', canManage: true, canCopy: true, canDelete: false, supportsCopy: true }],
  scans: [{
    printerId: 'source', enabled: false, outcome: 'Partial', reasonCode: 'request_limit',
    lastAttemptAtUtc: null, lastCompleteScanAtUtc: null, nextScanAtUtc: '2026-10-26T12:00:00Z',
    configurationGeneration: 1, isStale: true, isScanning: false,
  }],
  automaticDiscoveryEnabled: false,
};
const captured: PrinterFileOperation = {
  id: 'operation', revision: 3, action: 'Queue', state: 'AwaitingSourceConfirmation',
  locationId: 'location', gcodeFileId: 'asset', contentSha256: 'actual-local-digest',
  sizeBytes: 100, verification: 'UnverifiedSnapshot', reasonCode: 'unverified_source_snapshot',
  cancellationRequested: false, destinationPrinterId: 'source', payloadSha256: 'enqueue-payload',
  jobIds: [],
};

function LocationProbe() {
  return <output data-testid="url">{useLocation().search}</output>;
}

function renderCatalog(url = '/files?source=printers') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const result = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>
        <PrinterFileCatalog /><LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { ...result, queryClient };
}

describe('Printer file catalog', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mocks.hasPermission.mockReturnValue(true);
    mocks.catalog.mockResolvedValue(catalog);
    mocks.operations.mockResolvedValue([]);
    mocks.operation.mockResolvedValue(captured);
    mocks.copy.mockResolvedValue({ ...captured, action: 'Copy', state: 'Pending' });
    mocks.queue.mockResolvedValue({ ...captured, state: 'Pending' });
    mocks.confirm.mockResolvedValue({ ...captured, state: 'LocalReady' });
  });
  afterEach(() => { vi.useRealTimers(); });

  it('browses metadata with unknown values and honest partial/disabled/stale states', async () => {
    renderCatalog();
    expect(await screen.findByText('gcodes/Parts/part.gcode')).toBeInTheDocument();
    expect(screen.getByText('Unknown size')).toBeInTheDocument();
    expect(screen.getByText('Modified: Unknown')).toBeInTheDocument();
    expect(screen.getByText('Partial')).toBeInTheDocument();
    expect(screen.getByText(/Automatic discovery is globally disabled/)).toBeInTheDocument();
    expect(screen.getByText(/Discovery disabled/)).toBeInTheDocument();
    expect(mocks.catalog).toHaveBeenCalledWith({ printerId: undefined, search: undefined,
      availability: undefined, stale: undefined, page: 1, pageSize: 25 }, expect.any(AbortSignal));
    expect(mocks.copy).not.toHaveBeenCalled();
    expect(mocks.queue).not.toHaveBeenCalled();
    expect(mocks.confirm).not.toHaveBeenCalled();
  });

  it('keeps filters in the canonical URL and sends them to authorized server search', async () => {
    renderCatalog('/files?source=printers&printer=source&remoteSearch=part&availability=Missing&freshness=stale&remotePage=2');
    await waitFor(() => expect(mocks.catalog).toHaveBeenCalledWith({
      printerId: 'source', search: 'part', availability: 'Missing', stale: true, page: 2, pageSize: 25,
    }, expect.any(AbortSignal)));
  });

  it('Copy posts one explicit durable intent and never queues', async () => {
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('button', { name: 'Copy locally' }));
    expect(screen.getByRole('dialog', { name: 'Copy into local library' })).toBeInTheDocument();
    expect(mocks.copy).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Copy locally' }));
    await waitFor(() => expect(mocks.copy).toHaveBeenCalledWith('location', 7, expect.any(String)));
    expect(mocks.queue).not.toHaveBeenCalled();
    expect(mocks.confirm).not.toHaveBeenCalled();
  });

  it.each(['source', 'other'])('Queue stages locally for destination %s without implicit snapshot consent', async destination => {
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('button', { name: 'Queue' }));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Destination printer' }), destination);
    await user.click(screen.getByRole('button', { name: 'Stage locally' }));
    await waitFor(() => expect(mocks.queue).toHaveBeenCalledWith(
      'location', 7, destination, { copies: 1 }, expect.any(String)));
    expect(mocks.copy).not.toHaveBeenCalled();
    expect(mocks.confirm).not.toHaveBeenCalled();
  });

  it('a resumed snapshot requires a fresh explicit checkbox and exact content-bound decision', async () => {
    const user = userEvent.setup();
    renderCatalog('/files?source=printers&operation=operation');
    const confirm = await screen.findByRole('button', { name: 'Confirm snapshot and queue' });
    expect(confirm).toBeDisabled();
    expect(screen.getByText('SHA-256: actual-local-digest')).toBeInTheDocument();
    expect(mocks.confirm).not.toHaveBeenCalled();
    await user.click(screen.getByRole('checkbox', { name: 'I accept this captured snapshot for this enqueue intent' }));
    await user.click(confirm);
    await waitFor(() => expect(mocks.confirm).toHaveBeenCalledWith('operation', {
      gcodeFileId: 'asset', contentSha256: 'actual-local-digest', sizeBytes: 100,
      payloadSha256: 'enqueue-payload', accept: true,
    }));
    expect(mocks.queue).not.toHaveBeenCalled();
    expect(mocks.copy).not.toHaveBeenCalled();
  });

  it('decline is distinct from delete/cancel and identifies retained local content', async () => {
    const user = userEvent.setup();
    renderCatalog('/files?source=printers&operation=operation');
    await user.click(await screen.findByRole('button', { name: 'Decline · keep local copy' }));
    await waitFor(() => expect(mocks.confirm).toHaveBeenCalledWith('operation', expect.objectContaining({ accept: false })));
    expect(mocks.cancel).not.toHaveBeenCalled();
    expect(mocks.queue).not.toHaveBeenCalled();
  });

  it('blocked unsafe content cannot be confirmed away and retries admission without transfer', async () => {
    mocks.operation.mockResolvedValue({ ...captured, state: 'Blocked', reasonCode: 'gcode_motion_outside_build_volume' });
    mocks.retry.mockResolvedValue({ ...captured, state: 'Blocked' });
    const user = userEvent.setup();
    renderCatalog('/files?source=printers&operation=operation');
    await user.click(await screen.findByRole('button', { name: 'Retry same operation' }));
    await waitFor(() => expect(mocks.retry).toHaveBeenCalledWith('operation', true));
    expect(screen.queryByRole('button', { name: 'Confirm snapshot and queue' })).not.toBeInTheDocument();
    expect(mocks.copy).not.toHaveBeenCalled();
    expect(mocks.queue).not.toHaveBeenCalled();
  });

  it('a lost create response replays the same key rather than manufacturing a second job intent', async () => {
    mocks.queue.mockRejectedValueOnce(new Error('Response lost')).mockResolvedValueOnce({ ...captured, state: 'Pending' });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('button', { name: 'Queue' }));
    await user.click(screen.getByRole('button', { name: 'Stage locally' }));
    await user.click(await screen.findByRole('button', { name: 'Retry same intent' }));
    await waitFor(() => expect(mocks.queue).toHaveBeenCalledTimes(2));
    expect(mocks.queue.mock.calls[0]).toEqual(mocks.queue.mock.calls[1]);
  });

  it('does not expose mutation controls when the server denies source authority', async () => {
    mocks.catalog.mockResolvedValue({ ...catalog, printers: [{ ...catalog.printers[0], canCopy: false, canManage: false }] });
    renderCatalog();
    await screen.findByText('gcodes/Parts/part.gcode');
    expect(screen.queryByRole('button', { name: 'Copy locally' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Queue' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Request refresh' })).not.toBeInTheDocument();
  });

  it('never fetches without catalog permission', () => {
    mocks.hasPermission.mockReturnValue(false);
    renderCatalog('/files?source=printers&operation=operation');
    expect(mocks.catalog).not.toHaveBeenCalled();
    expect(mocks.operations).not.toHaveBeenCalled();
    expect(mocks.operation).not.toHaveBeenCalled();
  });

  it('polls only while mounted and never auto-confirms after reconnect/refetch', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const view = renderCatalog('/files?source=printers&operation=operation');
    await waitFor(() => expect(mocks.catalog).toHaveBeenCalledTimes(1));
    await act(async () => { await vi.advanceTimersByTimeAsync(31_000); });
    expect(mocks.catalog).toHaveBeenCalledTimes(2);
    await act(async () => { await view.queryClient.invalidateQueries({ queryKey: ['printer-file-operation'] }); });
    expect(mocks.confirm).not.toHaveBeenCalled();
    view.unmount();
    const count = mocks.catalog.mock.calls.length;
    const operationCount = mocks.operation.mock.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(61_000); });
    expect(mocks.catalog).toHaveBeenCalledTimes(count);
    expect(mocks.operation).toHaveBeenCalledTimes(operationCount);
    expect(mocks.queue).not.toHaveBeenCalled();
    view.queryClient.clear();
  });
});
