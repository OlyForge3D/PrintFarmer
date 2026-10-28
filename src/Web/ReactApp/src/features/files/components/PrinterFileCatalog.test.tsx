import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PrinterFileCatalogEntry, PrinterFileCatalogPage, PrinterFileDeleteOperation, PrinterFileOperation, PrinterPrintMetadata } from '@/types/api';
import { PrinterFileCatalog } from '@/features/files/components/PrinterFileCatalog';

const mocks = vi.hoisted(() => ({
  catalog: vi.fn(), operations: vi.fn(), operation: vi.fn(), copy: vi.fn(), queue: vi.fn(),
  confirm: vi.fn(), cancel: vi.fn(), retry: vi.fn(), refresh: vi.fn(), enabled: vi.fn(),
  preview: vi.fn(), thumbnail: vi.fn(), download: vi.fn(), prepareDelete: vi.fn(), confirmDelete: vi.fn(),
  hasPermission: vi.fn(), toastError: vi.fn(), toastSuccess: vi.fn(),
}));
const media = vi.hoisted(() => ({ lg: false }));
const printerList = vi.hoisted(() => ({ value: [] as Array<{ id: string; name: string; isEnabled: boolean; isOnline: boolean }> }));
vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinterFileCatalog: mocks.catalog, getPrinterFileOperations: mocks.operations,
    getPrinterFileOperation: mocks.operation, copyIndexedPrinterFile: mocks.copy,
    queueIndexedPrinterFile: mocks.queue, confirmPrinterFileSnapshot: mocks.confirm,
    cancelPrinterFileOperation: mocks.cancel, retryPrinterFileOperation: mocks.retry,
    refreshPrinterFileIndex: mocks.refresh, setPrinterFileIndexEnabled: mocks.enabled,
    getPrinterFilePreview: mocks.preview, getPrinterFileLocationThumbnail: mocks.thumbnail,
    downloadPrinterFile: mocks.download,
    prepareIndexedPrinterFileDelete: mocks.prepareDelete, confirmPrinterFileDelete: mocks.confirmDelete,
  },
}));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: mocks.hasPermission }),
}));
vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({ data: printerList.value }),
}));
vi.mock('@/common/hooks/useMediaQuery', async (importOriginal) => ({
  ...await importOriginal<typeof import('@/common/hooks/useMediaQuery')>(),
  useIsLgBreakpoint: () => media.lg,
}));
vi.mock('sonner', () => ({
  toast: { error: mocks.toastError, success: mocks.toastSuccess, warning: vi.fn(), info: vi.fn() },
}));

const entry: PrinterFileCatalogEntry = {
  id: 'location', printerId: 'source', printerName: 'Source printer', storageRoot: 'gcodes',
  relativePath: 'Parts/part.gcode', displayName: 'part.gcode', sizeBytes: null, modifiedValue: null,
  observationRevision: 7, configurationGeneration: 1, availability: 'Available',
  lastSeenAtUtc: '2026-10-25T12:00:00Z', lastCompleteScanAtUtc: null, isStale: true,
  localCopies: [],
};
const catalog: PrinterFileCatalogPage = {
  items: [entry],
  total: 1, page: 1, pageSize: 25,
  printers: [{ id: 'source', name: 'Source printer', canManage: true, canCopy: true, canDelete: false, supportsCopy: true }],
  scans: [{
    printerId: 'source', enabled: false, outcome: 'Partial', reasonCode: 'time_limit',
    lastAttemptAtUtc: null, lastCompleteScanAtUtc: null, nextScanAtUtc: '2026-10-26T12:00:00Z',
    configurationGeneration: 1, isStale: true, isScanning: false,
  }],
  automaticDiscoveryEnabled: false,
};
const previewSource = { ...catalog.printers[0], supportsPreview: true, canPreview: true };
const captured: PrinterFileOperation = {
  id: 'operation', revision: 3, action: 'Queue', state: 'AwaitingSourceConfirmation',
  locationId: 'location', gcodeFileId: 'asset', contentSha256: 'actual-local-digest',
  sizeBytes: 100, verification: 'UnverifiedSnapshot', reasonCode: 'unverified_source_snapshot',
  cancellationRequested: false, destinationPrinterId: 'source', payloadSha256: 'enqueue-payload',
  jobIds: [],
};
const secondEntry: PrinterFileCatalogEntry = {
  ...entry, id: 'location2', relativePath: 'Parts/other.gcode', displayName: 'other.gcode', observationRevision: 9,
};
const bulkCatalog: PrinterFileCatalogPage = {
  ...catalog, items: [entry, secondEntry], total: 2,
  printers: [{ ...catalog.printers[0], canDelete: true }],
};
const preparedDelete: PrinterFileDeleteOperation = {
  id: 'delete-location', revision: 1, state: 'AwaitingDeleteConfirmation', locationId: entry.id,
  sourcePrinterId: entry.printerId, storageRoot: entry.storageRoot, relativePath: entry.relativePath,
  observationRevision: entry.observationRevision, configurationGeneration: entry.configurationGeneration,
  guarantee: 'CurrentExactPath', confirmationBinding: 'binding-location', currentPathRaceAccepted: false,
  deleteSentAtUtc: null, outcome: 'AwaitingConfirmation', reasonCode: null,
};

function LocationProbe() {
  return <output data-testid="url">{useLocation().search}</output>;
}

// The API omits null properties (WhenWritingNull); mirror that wire shape exactly.
function omitted<T extends object>(value: T, ...keys: Array<keyof T>): T {
  const copy = { ...value };
  for (const key of keys) Reflect.deleteProperty(copy, key);
  return copy;
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

/** The table cell for `row` under the column whose header text is `header`. */
function cellUnder(header: string, row: HTMLElement) {
  const table = row.closest('table');
  if (!table) throw new Error('row is not inside a table');
  const headers = within(table).getAllByRole('columnheader').map(cell => cell.textContent);
  const index = headers.indexOf(header);
  if (index < 0) throw new Error(`no "${header}" column in ${headers.join(', ')}`);
  return within(row).getAllByRole('cell')[index];
}

async function findRow(name: string) {
  const button = await screen.findByRole('button', { name: `${name}, show details` });
  const row = button.closest('tr');
  if (!row) throw new Error(`${name} is not rendered as a table row`);
  return row;
}

/** Definition-list value for `term` inside a detail panel. */
function detailValue(panel: HTMLElement, term: string) {
  const definition = within(panel).getByText(term, { selector: 'dt' }).nextElementSibling;
  if (!(definition instanceof HTMLElement)) throw new Error(`no value for ${term}`);
  return definition;
}

// jsdom has no IntersectionObserver; this fake reports visibility only when a test says so.
const observers: FakeIntersectionObserver[] = [];
class FakeIntersectionObserver {
  readonly elements = new Set<Element>();
  constructor(readonly callback: IntersectionObserverCallback) { observers.push(this); }
  observe(element: Element) { this.elements.add(element); }
  unobserve(element: Element) { this.elements.delete(element); }
  disconnect() { this.elements.clear(); }
  takeRecords() { return []; }
}
function scrollIntoView(element: Element) {
  act(() => {
    for (const observer of [...observers]) {
      if (!observer.elements.has(element)) continue;
      observer.callback(
        [{ isIntersecting: true, target: element } as IntersectionObserverEntry],
        observer as unknown as IntersectionObserver,
      );
    }
  });
}

describe('Printer file catalog', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    window.localStorage.clear();
    media.lg = false;
    observers.length = 0;
    printerList.value = [
      { id: 'source', name: 'Source printer', isEnabled: true, isOnline: true },
      { id: 'other', name: 'Other printer', isEnabled: true, isOnline: true },
    ];
    mocks.hasPermission.mockReturnValue(true);
    mocks.catalog.mockResolvedValue(catalog);
    mocks.operations.mockResolvedValue([]);
    mocks.operation.mockResolvedValue(captured);
    mocks.copy.mockResolvedValue({ ...captured, action: 'Copy', state: 'Pending' });
    mocks.queue.mockResolvedValue({ ...captured, state: 'Pending' });
    mocks.confirm.mockResolvedValue({ ...captured, state: 'LocalReady' });
    mocks.download.mockResolvedValue(undefined);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    Reflect.deleteProperty(URL, 'createObjectURL');
    Reflect.deleteProperty(URL, 'revokeObjectURL');
  });

  it('lists observations in the compact 12px table with honest unknowns and no per-printer discovery controls', async () => {
    renderCatalog();
    const row = await findRow('part.gcode');
    const table = row.closest('table');
    expect(table).toHaveClass('text-xs');
    expect(within(table!).getAllByRole('columnheader').map(cell => cell.textContent))
      .toEqual(['', 'Preview', 'Name', 'Printer', 'Status', 'Size', 'Modified', 'Actions']);
    expect(within(table!).getByRole('checkbox', { name: 'Select all files on this page' })).toBeInTheDocument();
    expect(within(row).getByText('gcodes/Parts/part.gcode')).toBeInTheDocument();
    expect(cellUnder('Size', row)).toHaveTextContent(/^Unknown$/);
    expect(cellUnder('Modified', row)).toHaveTextContent(/^Unknown$/);
    expect(within(cellUnder('Status', row)).getAllByRole('listitem').map(item => item.textContent))
      .toEqual(['Available', 'Stale']);
    expect(screen.getByText(/Automatic discovery is globally disabled/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Discovery settings' })).toHaveAttribute(
      'href',
      '/admin/settings?scope=system&tab=general&sub=system&field=PrinterFileIndex.enabled',
    );
    // Per-printer discovery toggles live in Edit printer > Files; only the all-printers Refresh is here.
    expect(screen.getByRole('button', { name: 'Force refresh all online printers' })).toBeInTheDocument();
    expect(screen.queryByRole('checkbox', { name: /automatic discovery/i })).not.toBeInTheDocument();
    expect(mocks.catalog).toHaveBeenCalledWith({ printerId: undefined, search: undefined,
      availability: undefined, stale: undefined, page: 1, pageSize: 25 }, expect.any(AbortSignal));
    expect(mocks.refresh).not.toHaveBeenCalled();
    expect(mocks.enabled).not.toHaveBeenCalled();
    expect(mocks.copy).not.toHaveBeenCalled();
    expect(mocks.queue).not.toHaveBeenCalled();
    expect(mocks.confirm).not.toHaveBeenCalled();
  });

  describe('Force refresh all online printers', () => {
    const managed = (id: string, name: string, canManage = true) => ({ ...catalog.printers[0], id, name, canManage });

    it('sits right of the view toggle and force-scans only online printers the user manages', async () => {
      printerList.value = [
        { id: 'source', name: 'Source printer', isEnabled: true, isOnline: true },
        { id: 'other', name: 'Other printer', isEnabled: true, isOnline: false },
        { id: 'viewer', name: 'Viewer printer', isEnabled: true, isOnline: true },
      ];
      mocks.catalog.mockResolvedValue({ ...catalog, printers: [
        managed('source', 'Source printer'), managed('other', 'Other printer'), managed('viewer', 'Viewer printer', false),
      ] });
      mocks.refresh.mockResolvedValue(undefined);
      const user = userEvent.setup();
      const { queryClient } = renderCatalog();
      const invalidate = vi.spyOn(queryClient, 'invalidateQueries');
      const refresh = await screen.findByRole('button', { name: 'Force refresh all online printers' });
      expect(screen.getByTitle('Grid view').parentElement?.nextElementSibling).toBe(refresh);
      // Same size and padding as the adjacent toggle buttons, so both render at the same height.
      for (const sizing of ['text-sm', 'py-2', 'px-3']) {
        expect(screen.getByTitle('Grid view')).toHaveClass(sizing);
        expect(refresh).toHaveClass(sizing);
      }
      expect(refresh).not.toHaveClass('h-10');
      expect(refresh).toHaveAttribute('title', expect.stringMatching(/bypassing schedule and preview caches\. Requires idle printers/));
      expect(refresh.textContent).toBe('');
      expect(mocks.refresh).not.toHaveBeenCalled();

      await user.click(refresh);

      await waitFor(() => expect(mocks.toastSuccess).toHaveBeenCalledWith(
        'Force refresh completed for 1 printer. Previews reload on demand.'));
      expect(mocks.refresh.mock.calls).toEqual([['source', true]]);
      expect(mocks.toastError).not.toHaveBeenCalled();
      await waitFor(() => expect(invalidate).toHaveBeenCalledWith({ queryKey: ['printer-file-thumbnail'] }));
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['printer-file-preview'] });
      await waitFor(() => expect(mocks.catalog).toHaveBeenCalledTimes(2));
    });

    it.each([undefined, 1, 2, 4])('bounds parallel refreshes to capacity %s and continues past a named failure', async maxConcurrentIo => {
      const capacity = maxConcurrentIo ?? 2;
      const targets = Array.from({ length: capacity + 2 }, (_, index) => managed(`printer-${index}`, `Printer ${index}`));
      printerList.value = targets.map(printer => ({ ...printer, isEnabled: true, isOnline: true }));
      mocks.catalog.mockResolvedValue({ ...catalog, printers: targets, maxConcurrentIo });
      const pending: Array<{ resolve: () => void; reject: (error: unknown) => void }> = [];
      let active = 0;
      let peak = 0;
      mocks.refresh.mockImplementation(() => {
        peak = Math.max(peak, ++active);
        return new Promise<void>((resolve, reject) => pending.push({ resolve, reject }))
          .finally(() => { active--; });
      });
      const user = userEvent.setup();
      renderCatalog();
      const refresh = await screen.findByRole('button', { name: 'Force refresh all online printers' });
      await user.click(refresh);

      await waitFor(() => expect(mocks.refresh.mock.calls).toEqual(targets.slice(0, capacity).map(target => [target.id, true])));
      expect(active).toBe(capacity);
      expect(refresh).toHaveAttribute('aria-disabled', 'true');
      expect(mocks.toastSuccess).not.toHaveBeenCalled();
      await act(async () => pending[0].reject({ statusCode: 409, message: 'Request failed with status code 409',
        data: { code: 'waiting_for_confirmed_idle', reasonCode: 'waiting_for_confirmed_idle' } }));
      await waitFor(() => expect(mocks.refresh).toHaveBeenCalledTimes(capacity + 1));
      expect(active).toBe(capacity);
      for (let index = 1; index < targets.length; index++) {
        await waitFor(() => expect(pending[index]).toBeDefined());
        await act(async () => pending[index].resolve());
      }

      await waitFor(() => expect(mocks.toastError).toHaveBeenCalledWith(
        'Failed to force refresh: Printer 0: waiting for confirmed idle'));
      expect(mocks.refresh.mock.calls).toEqual(targets.map(target => [target.id, true]));
      expect(mocks.toastSuccess).toHaveBeenCalledWith(
        `Force refresh completed for ${targets.length - 1} printers. Previews reload on demand.`);
      expect(peak).toBe(capacity);
      expect(active).toBe(0);
      await waitFor(() => expect(refresh).not.toHaveAttribute('aria-disabled', 'true'));
    });

    it('blocks pointer and keyboard repeats until the scan and catalog reload finish', async () => {
      let complete!: () => void;
      let reload!: (page: PrinterFileCatalogPage) => void;
      mocks.refresh.mockReturnValue(new Promise<void>(resolve => { complete = resolve; }));
      mocks.catalog.mockResolvedValueOnce(catalog)
        .mockImplementationOnce(() => new Promise<PrinterFileCatalogPage>(resolve => { reload = resolve; }));
      const user = userEvent.setup();
      renderCatalog();
      const refresh = await screen.findByRole('button', { name: 'Force refresh all online printers' });
      await user.click(refresh);
      await waitFor(() => expect(mocks.refresh).toHaveBeenCalledTimes(1));
      expect(refresh.textContent).toBe('');
      expect(refresh).toHaveAttribute('aria-disabled', 'true');
      expect(refresh).toHaveAttribute('aria-busy', 'true');
      await user.click(refresh);
      await user.keyboard('{Enter} ');
      expect(mocks.refresh).toHaveBeenCalledTimes(1);

      await act(async () => complete());
      await waitFor(() => expect(mocks.catalog).toHaveBeenCalledTimes(2));
      expect(refresh).toHaveAttribute('aria-disabled', 'true');
      expect(refresh).toHaveAttribute('aria-busy', 'true');
      await user.click(refresh);
      await user.keyboard('{Enter} ');
      expect(mocks.refresh).toHaveBeenCalledTimes(1);

      await act(async () => reload(catalog));
      await waitFor(() => expect(refresh).not.toHaveAttribute('aria-disabled', 'true'));
      expect(refresh).toHaveAttribute('aria-busy', 'false');
    });

    it('stays disabled for a server-reported scan until a later status reports completion', async () => {
      mocks.catalog.mockResolvedValue({ ...catalog, scans: [{ ...catalog.scans[0], isScanning: true }] });
      const user = userEvent.setup();
      const { queryClient } = renderCatalog();
      const refresh = await screen.findByRole('button', { name: 'Force refresh all online printers' });
      expect(refresh).toHaveAttribute('aria-disabled', 'true');
      expect(refresh).toHaveAttribute('aria-busy', 'true');
      await user.click(refresh);
      expect(mocks.refresh).not.toHaveBeenCalled();

      mocks.catalog.mockResolvedValue(catalog);
      await act(async () => { await queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] }); });
      await waitFor(() => expect(refresh).not.toHaveAttribute('aria-disabled', 'true'));
      expect(refresh).toHaveAttribute('aria-busy', 'false');
    });

    it.each([0, 17, 1.5])('rejects an invalid server capacity of %s before any printer work', async maxConcurrentIo => {
      mocks.catalog.mockResolvedValue({ ...catalog, maxConcurrentIo });
      const user = userEvent.setup();
      renderCatalog();
      await user.click(await screen.findByRole('button', { name: 'Force refresh all online printers' }));
      await waitFor(() => expect(mocks.toastError).toHaveBeenCalledWith(
        'Failed to force refresh: The server returned an invalid printer-file I/O budget. Reload the catalog before retrying.'));
      expect(mocks.refresh).not.toHaveBeenCalled();
    });

    it('explains why it is unavailable when no printer the user manages is online', async () => {
      printerList.value = printerList.value.map(printer => ({ ...printer, isOnline: false }));
      const user = userEvent.setup();
      renderCatalog();
      const refresh = await screen.findByRole('button', { name: 'Force refresh all online printers' });
      expect(refresh).toHaveAttribute('aria-disabled', 'true');
      expect(refresh).toHaveAttribute('title', 'No online printers you can manage');

      await user.click(refresh);

      expect(mocks.refresh).not.toHaveBeenCalled();
      expect(mocks.toastSuccess).not.toHaveBeenCalled();
    });

    it('is hidden when the user manages none of the listed printers', async () => {
      mocks.catalog.mockResolvedValue({ ...catalog, printers: [managed('source', 'Source printer', false)] });
      renderCatalog();
      await findRow('part.gcode');
      expect(screen.queryByRole('button', { name: 'Force refresh all online printers' })).not.toBeInTheDocument();
    });
  });

  it.each(['table', 'grid'])('selects eligible files in the %s without opening details and exposes mixed select-all state', async viewMode => {
    const missing: PrinterFileCatalogEntry = {
      ...entry, id: 'missing', displayName: 'missing.gcode', availability: 'Missing',
    };
    mocks.catalog.mockResolvedValue({ ...bulkCatalog, items: [entry, secondEntry, missing], total: 3 });
    const user = userEvent.setup();
    renderCatalog();
    await findRow('part.gcode');
    if (viewMode === 'grid') await user.click(screen.getByRole('button', { name: 'Grid view' }));
    const selectAll = screen.getByRole('checkbox', { name: 'Select all files on this page' });
    const selectFirst = screen.getByRole('checkbox', { name: 'Select part.gcode' });
    const selectSecond = screen.getByRole('checkbox', { name: 'Select other.gcode' });
    const selectMissing = screen.getByRole('checkbox', { name: 'Select missing.gcode' });
    expect(selectMissing).toBeDisabled();
    expect(selectMissing).toHaveAttribute('title', 'No bulk action is available for this file');
    await user.click(selectFirst);
    expect(selectFirst).toBeChecked();
    expect(selectAll).toBePartiallyChecked();
    expect(selectSecond).not.toBeChecked();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await user.click(selectAll);
    expect(selectAll).toBeChecked();
    expect(selectSecond).toBeChecked();
    expect(selectMissing).not.toBeChecked();
    expect(screen.getByRole('group', { name: 'Selected files' })).toHaveTextContent('2 selected');
    await user.click(selectAll);
    expect(selectFirst).not.toBeChecked();
    expect(selectSecond).not.toBeChecked();
    expect(screen.queryByRole('group', { name: 'Selected files' })).not.toBeInTheDocument();
  });

  it.each(['filter', 'page'])('clears selection on a %s change and does not resurrect it on return', async change => {
    mocks.catalog.mockResolvedValue({ ...bulkCatalog, total: 30 });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('checkbox', { name: 'Select all files on this page' }));
    if (change === 'filter') await user.selectOptions(screen.getByRole('combobox', { name: 'Freshness' }), 'fresh');
    else await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByRole('checkbox', { name: 'Select part.gcode' })).not.toBeChecked();
    expect(screen.queryByRole('group', { name: 'Selected files' })).not.toBeInTheDocument();
    if (change === 'filter') await user.selectOptions(screen.getByRole('combobox', { name: 'Freshness' }), '');
    else await user.click(screen.getByRole('button', { name: 'Previous' }));
    expect(await screen.findByRole('checkbox', { name: 'Select part.gcode' })).not.toBeChecked();
  });

  it('drops selections that become unavailable on refresh, even if the file later returns', async () => {
    mocks.catalog.mockResolvedValue(bulkCatalog);
    const user = userEvent.setup();
    const { queryClient } = renderCatalog();
    await user.click(await screen.findByRole('checkbox', { name: 'Select part.gcode' }));
    mocks.catalog.mockResolvedValue({ ...bulkCatalog, items: [{ ...entry, availability: 'Missing' }, secondEntry] });
    await act(async () => { await queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] }); });
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Select part.gcode' })).toBeDisabled());
    expect(screen.queryByRole('group', { name: 'Selected files' })).not.toBeInTheDocument();
    mocks.catalog.mockResolvedValue(bulkCatalog);
    await act(async () => { await queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] }); });
    await waitFor(() => expect(screen.getByRole('checkbox', { name: 'Select part.gcode' })).toBeEnabled());
    expect(screen.getByRole('checkbox', { name: 'Select part.gcode' })).not.toBeChecked();
  });

  it('limits each bulk action to its permitted sources and never selects files with no action', async () => {
    mocks.hasPermission.mockImplementation((resource: string, action: string) => resource !== 'printer_files' || action !== 'delete');
    mocks.catalog.mockResolvedValue({
      ...bulkCatalog,
      items: [entry, { ...secondEntry, printerId: 'other' }, { ...entry, id: 'denied', displayName: 'denied.gcode', printerId: 'denied' }],
      total: 3,
      printers: [
        { ...bulkCatalog.printers[0], canDownload: false },
        { ...bulkCatalog.printers[0], id: 'other', canCopy: false, supportsDownload: true, canDownload: true },
        { ...bulkCatalog.printers[0], id: 'denied', canCopy: false, canDownload: false },
      ],
    });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('checkbox', { name: 'Select all files on this page' }));
    expect(screen.getByRole('checkbox', { name: 'Select denied.gcode' })).toBeDisabled();
    const toolbar = screen.getByRole('group', { name: 'Selected files' });
    expect(within(toolbar).getByRole('button', { name: 'Copy 1 of 2 selected to local library' })).toBeEnabled();
    expect(within(toolbar).getByRole('button', { name: 'Download 1 of 2 selected' })).toBeEnabled();
    expect(within(toolbar).getByRole('button', { name: 'Delete 0 of 2 selected from printer' })).toBeDisabled();
    await user.click(within(toolbar).getByRole('button', { name: 'Copy 1 of 2 selected to local library' }));
    const dialog = screen.getByRole('dialog', { name: 'Copy 1 file into local library' });
    expect(within(dialog).getByText('part.gcode')).toBeInTheDocument();
    expect(within(dialog).queryByText('other.gcode')).not.toBeInTheDocument();
  });

  it('bulk Copy uses each observation and a distinct intent, retrying only the failed file with its original key', async () => {
    mocks.catalog.mockResolvedValue(bulkCatalog);
    mocks.copy.mockRejectedValueOnce(new Error('Response lost'));
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('checkbox', { name: 'Select all files on this page' }));
    await user.click(screen.getByRole('button', { name: 'Copy 2 selected to local library' }));
    const dialog = screen.getByRole('dialog', { name: 'Copy 2 files into local library' });
    expect(mocks.copy).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole('button', { name: 'Copy 2 files locally' }));
    await within(dialog).findByText('1 requested, 1 failed, 0 stale');
    expect(mocks.copy.mock.calls).toEqual([
      ['location', 7, expect.any(String)],
      ['location2', 9, expect.any(String)],
    ]);
    const firstKey = mocks.copy.mock.calls[0][2];
    const secondKey = mocks.copy.mock.calls[1][2];
    expect(firstKey).toBeTruthy();
    expect(secondKey).toBeTruthy();
    expect(firstKey).not.toBe(secondKey);
    await user.click(within(dialog).getByRole('button', { name: 'Retry 1 failed file with the same intents' }));
    await within(dialog).findByText('2 requested, 0 failed, 0 stale');
    expect(mocks.copy).toHaveBeenCalledTimes(3);
    expect(mocks.copy.mock.calls[2]).toEqual(['location', 7, firstKey]);
    expect(mocks.queue).not.toHaveBeenCalled();
    expect(mocks.confirm).not.toHaveBeenCalled();
    expect(mocks.download).not.toHaveBeenCalled();
    expect(mocks.prepareDelete).not.toHaveBeenCalled();
  });

  it('does not retry stale bulk Copy observations and refreshes the catalog', async () => {
    mocks.catalog.mockResolvedValue(bulkCatalog);
    mocks.copy.mockRejectedValueOnce({ statusCode: 409, data: { reasonCode: 'stale_observation' } });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('checkbox', { name: 'Select all files on this page' }));
    await user.click(screen.getByRole('button', { name: 'Copy 2 selected to local library' }));
    const dialog = screen.getByRole('dialog', { name: 'Copy 2 files into local library' });
    await user.click(within(dialog).getByRole('button', { name: 'Copy 2 files locally' }));
    await within(dialog).findByText('1 requested, 0 failed, 1 stale');
    expect(within(dialog).queryByRole('button', { name: /Retry/ })).not.toBeInTheDocument();
    await waitFor(() => expect(mocks.catalog).toHaveBeenCalledTimes(2));
    expect(mocks.copy).toHaveBeenCalledTimes(2);
  });

  it('downloads selected files sequentially without copying and reports a stale failure by filename', async () => {
    mocks.catalog.mockResolvedValue(bulkCatalog);
    let finishFirst: (() => void) | undefined;
    mocks.download
      .mockImplementationOnce(() => new Promise<void>(resolve => { finishFirst = resolve; }))
      .mockRejectedValueOnce({ statusCode: 409, message: 'Conflict' });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('checkbox', { name: 'Select all files on this page' }));
    await user.click(screen.getByRole('button', { name: 'Download 2 selected' }));
    expect(mocks.download.mock.calls).toEqual([['location', 7, 'part.gcode']]);
    expect(screen.getByRole('button', { name: 'Download 2 selected' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Copy 2 selected to local library' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Delete 2 selected from printer' })).toBeDisabled();
    await act(async () => { finishFirst?.(); });
    await waitFor(() => expect(mocks.download.mock.calls).toEqual([
      ['location', 7, 'part.gcode'], ['location2', 9, 'other.gcode'],
    ]));
    await waitFor(() => expect(mocks.toastError).toHaveBeenCalledWith(
      'Downloaded 1 of 2 files.', { description: expect.stringMatching(/other\.gcode: The file changed on the printer/) },
    ));
    expect(mocks.catalog).toHaveBeenCalledTimes(2);
    expect(screen.getByRole('button', { name: 'Download 2 selected' })).toBeEnabled();
    expect(mocks.copy).not.toHaveBeenCalled();
    expect(mocks.queue).not.toHaveBeenCalled();
    expect(mocks.prepareDelete).not.toHaveBeenCalled();
  });

  it('bulk Delete needs both batch consents, binds each source separately and never resends an uncertain confirmation', async () => {
    mocks.catalog.mockResolvedValue(bulkCatalog);
    const conditionalDelete: PrinterFileDeleteOperation = {
      ...preparedDelete, id: 'delete-location2', locationId: secondEntry.id,
      relativePath: secondEntry.relativePath, observationRevision: secondEntry.observationRevision,
      guarantee: 'NativeConditional', confirmationBinding: 'binding-location2',
    };
    mocks.prepareDelete.mockResolvedValueOnce(preparedDelete).mockResolvedValueOnce(conditionalDelete);
    mocks.confirmDelete
      .mockResolvedValueOnce({ ...preparedDelete, state: 'Deleted', outcome: 'Acknowledged' })
      .mockRejectedValueOnce(new Error('Response lost'));
    const user = userEvent.setup();
    renderCatalog();
    await user.click(await screen.findByRole('checkbox', { name: 'Select all files on this page' }));
    await user.click(screen.getByRole('button', { name: 'Delete 2 selected from printer' }));
    const dialog = screen.getByRole('dialog', { name: 'Delete 2 files from printers' });
    const confirm = within(dialog).getByRole('button', { name: 'Delete 2 files' });
    expect(confirm).toBeDisabled();
    expect(within(dialog).getByText('gcodes/Parts/part.gcode')).toBeInTheDocument();
    expect(within(dialog).getByText('gcodes/Parts/other.gcode')).toBeInTheDocument();
    expect(mocks.prepareDelete).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole('checkbox', { name: /I accept irreversible deletion/ }));
    expect(confirm).toBeDisabled();
    expect(mocks.confirmDelete).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole('checkbox', { name: /I accept the current-path replacement race/ }));
    await user.click(confirm);
    await within(dialog).findByText('1 deleted, 0 failed, 0 stale, 1 outcome unknown');
    expect(mocks.prepareDelete.mock.calls).toEqual([
      ['location', 7, expect.any(String)], ['location2', 9, expect.any(String)],
    ]);
    expect(mocks.prepareDelete.mock.calls[0][2]).toBeTruthy();
    expect(mocks.prepareDelete.mock.calls[1][2]).toBeTruthy();
    expect(mocks.prepareDelete.mock.calls[0][2]).not.toBe(mocks.prepareDelete.mock.calls[1][2]);
    expect(mocks.confirmDelete.mock.calls).toEqual([
      ['delete-location', { confirmationBinding: 'binding-location', acceptIrreversibleDelete: true, acceptCurrentPathRace: true }],
      ['delete-location2', { confirmationBinding: 'binding-location2', acceptIrreversibleDelete: true, acceptCurrentPathRace: false }],
    ]);
    expect(within(dialog).getByText('Outcome unknown')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Show delete status for other.gcode' })).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: /Retry|Delete 2 files/ })).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Close' }));
    expect(mocks.confirmDelete).toHaveBeenCalledTimes(2);
    expect(mocks.copy).not.toHaveBeenCalled();
    expect(mocks.queue).not.toHaveBeenCalled();
  });

  it('renders wire-shaped observations whose null metadata was omitted by the API', async () => {
    const wireEntry = omitted(entry, 'sizeBytes', 'modifiedValue', 'lastCompleteScanAtUtc');
    const wireScan = omitted(catalog.scans[0], 'reasonCode', 'lastAttemptAtUtc', 'lastCompleteScanAtUtc');
    mocks.catalog.mockResolvedValue({ ...catalog, items: [wireEntry], scans: [wireScan] });
    const user = userEvent.setup();
    renderCatalog();
    const row = await findRow('part.gcode');
    expect(cellUnder('Size', row)).toHaveTextContent(/^Unknown$/);
    expect(cellUnder('Modified', row)).toHaveTextContent(/^Unknown$/);
    await user.click(within(row).getByRole('button', { name: 'part.gcode, show details' }));
    const panel = screen.getByRole('dialog', { name: 'part.gcode' });
    expect(detailValue(panel, 'Size')).toHaveTextContent(/^Unknown$/);
    expect(detailValue(panel, 'Modified')).toHaveTextContent(/^Unknown$/);
    expect(detailValue(panel, 'Last complete scan')).toHaveTextContent(/^Unknown$/);
  });

  it('shows provider modification times date-only with the full timestamp in the tooltip', async () => {
    const epochSeconds = 1_761_393_600;
    const modified = new Date(epochSeconds * 1000);
    mocks.catalog.mockResolvedValue({ ...catalog, items: [{ ...entry, modifiedValue: String(epochSeconds) }] });
    renderCatalog();
    const time = within(cellUnder('Modified', await findRow('part.gcode'))).getByText(modified.toLocaleDateString());
    expect(time.tagName).toBe('TIME');
    expect(time).toHaveAttribute('dateTime', modified.toISOString());
    expect(time).toHaveAttribute('title', modified.toLocaleString());
    expect(time.textContent).toBe(modified.toLocaleDateString());
  });

  it('refuses snapshot confirmation when the captured size was omitted', async () => {
    mocks.operation.mockResolvedValue(omitted(captured, 'sizeBytes'));
    const user = userEvent.setup();
    renderCatalog('/files?source=printers&operation=operation');
    await user.click(await screen.findByRole('checkbox', { name: 'I accept this captured snapshot for this enqueue intent' }));
    await user.click(screen.getByRole('button', { name: 'Confirm snapshot and queue' }));
    await waitFor(() => expect(mocks.operation).toHaveBeenCalled());
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
    await user.click(await screen.findByRole('button', { name: 'Copy to local library' }));
    expect(screen.getByRole('dialog', { name: 'Copy into local library' })).toBeInTheDocument();
    expect(mocks.copy).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Copy locally' }));
    await waitFor(() => expect(mocks.copy).toHaveBeenCalledWith('location', 7, expect.any(String)));
    expect(mocks.queue).not.toHaveBeenCalled();
    expect(mocks.confirm).not.toHaveBeenCalled();
    expect(mocks.download).not.toHaveBeenCalled();
  });

  it('offers Copy to local library and Download as distinct, named actions; Download is browser-only', async () => {
    const user = userEvent.setup();
    renderCatalog();
    const row = await findRow('part.gcode');
    const copy = within(row).getByRole('button', { name: 'Copy to local library' });
    const download = within(row).getByRole('button', { name: 'Download' });
    expect(copy).not.toBe(download);
    expect(copy).toHaveAttribute('title', 'Copy to local library');
    expect(download).toHaveAttribute('title', 'Download');
    await user.click(download);
    await waitFor(() => expect(mocks.download).toHaveBeenCalledWith('location', 7, 'part.gcode'));
    expect(mocks.download).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(mocks.copy).not.toHaveBeenCalled();
    expect(mocks.queue).not.toHaveBeenCalled();
    expect(mocks.confirm).not.toHaveBeenCalled();
  });

  it.each([
    ['unsupported', { supportsDownload: false, canDownload: true }],
    ['not permitted', { supportsDownload: true, canDownload: false }],
  ])('hides Download when the source reports it %s but keeps Copy', async (_label, capability) => {
    mocks.catalog.mockResolvedValue({ ...catalog, printers: [{ ...catalog.printers[0], ...capability }] });
    renderCatalog();
    const row = await findRow('part.gcode');
    expect(within(row).getByRole('button', { name: 'Copy to local library' })).toBeInTheDocument();
    expect(within(row).queryByRole('button', { name: 'Download' })).not.toBeInTheDocument();
  });

  it('reports a changed observation on Download and refreshes the listing instead of retrying', async () => {
    mocks.download.mockRejectedValue({ message: 'Conflict', statusCode: 409, isAxiosError: true });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(within(await findRow('part.gcode')).getByRole('button', { name: 'Download' }));
    await waitFor(() => expect(mocks.toastError).toHaveBeenCalledWith(expect.stringMatching(/changed/i)));
    await waitFor(() => expect(mocks.catalog).toHaveBeenCalledTimes(2));
    expect(mocks.download).toHaveBeenCalledTimes(1);
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

  it('opens details from the filename, names every icon action and restores focus on Escape', async () => {
    const user = userEvent.setup();
    renderCatalog();
    const name = within(await findRow('part.gcode')).getByRole('button', { name: 'part.gcode, show details' });
    await user.click(name);
    const panel = screen.getByRole('dialog', { name: 'part.gcode' });
    expect(detailValue(panel, 'Path')).toHaveTextContent('gcodes/Parts/part.gcode');
    expect(detailValue(panel, 'Source')).toHaveTextContent('Source printer');
    expect(detailValue(panel, 'Type')).toHaveTextContent('G-code file (GCODE)');
    // This source does not offer previews, so nothing is invented.
    expect(detailValue(panel, 'Print time')).toHaveTextContent(/^Unavailable$/);
    expect(detailValue(panel, 'Layer height')).toHaveTextContent(/^Unavailable$/);
    expect(within(panel).getByText('Preview unavailable')).toBeInTheDocument();
    const panelActions = within(within(panel).getByRole('group', { name: 'File actions' })).getAllByRole('button');
    expect(panelActions.map(button => button.getAttribute('aria-label')))
      .toEqual(['Copy to local library', 'Download', 'Queue']);
    // Icon-only, matching cards and rows: no visible text, tooltip carries the name.
    for (const button of panelActions) {
      expect(button.textContent).toBe('');
      expect(button).toHaveAttribute('title', button.getAttribute('aria-label'));
    }
    expect(mocks.preview).not.toHaveBeenCalled();
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'part.gcode' })).not.toBeInTheDocument());
    await waitFor(() => expect(name).toHaveFocus());
  });

  it('opens details when the table preview is clicked and returns focus to the filename', async () => {
    const user = userEvent.setup();
    renderCatalog();
    const row = await findRow('part.gcode');
    await user.click(within(row).getByTestId('printer-file-thumbnail'));
    expect(screen.getByRole('dialog', { name: 'part.gcode' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'part.gcode' })).not.toBeInTheDocument());
    await waitFor(() => expect(within(row).getByRole('button', { name: 'part.gcode, show details' })).toHaveFocus());
  });

  it('docks details beside a grid card on wide screens; Escape closes it only when no dialog is above it', async () => {
    media.lg = true;
    const user = userEvent.setup();
    renderCatalog();
    await findRow('part.gcode');
    await user.click(screen.getByRole('button', { name: 'Grid view' }));
    const card = screen.getByRole('group', { name: 'part.gcode' });
    expect(card.parentElement).toHaveClass('grid-cols-[repeat(auto-fill,minmax(9.5rem,1fr))]');
    await user.click(within(card).getByText(/Source printer/));
    const panel = screen.getByRole('complementary', { name: 'part.gcode' });
    expect(within(panel).getByRole('button', { name: 'Close details' })).toHaveFocus();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    await user.click(within(panel).getByRole('button', { name: 'Copy to local library' }));
    expect(screen.getByRole('dialog', { name: 'Copy into local library' })).toBeInTheDocument();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.getByRole('complementary', { name: 'part.gcode' })).toBeInTheDocument();

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Copy into local library' })).not.toBeInTheDocument());
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('complementary', { name: 'part.gcode' })).not.toBeInTheDocument();
    await waitFor(() => expect(within(card).getByRole('button', { name: 'part.gcode, show details' })).toHaveFocus());
    expect(mocks.copy).not.toHaveBeenCalled();
  });

  it('requests a preview only for items scrolled into view on sources that support and permit it', async () => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: vi.fn(() => 'blob:thumbnail') });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: vi.fn() });
    const hidden = { ...entry, id: 'location-2', relativePath: 'Parts/other.gcode', displayName: 'other.gcode', observationRevision: 3 };
    mocks.catalog.mockResolvedValue({ ...catalog, items: [entry, hidden], total: 2, printers: [previewSource] });
    mocks.preview.mockResolvedValue({
      locationId: 'location', observationRevision: 7, state: 'Available', hasImage: true,
      capturedAtUtc: '2026-10-25T12:00:00Z', printTimeSeconds: 5400, layerHeightMm: 0.2,
    });
    mocks.thumbnail.mockResolvedValue(new Blob(['png'], { type: 'image/png' }));
    renderCatalog();
    const row = await findRow('part.gcode');
    await findRow('other.gcode');
    expect(mocks.preview).not.toHaveBeenCalled();

    const thumbnail = within(row).getByTestId('printer-file-thumbnail');
    scrollIntoView(thumbnail);
    await waitFor(() => expect(mocks.thumbnail).toHaveBeenCalledWith('location', 7, null, expect.any(AbortSignal)));
    expect(mocks.preview).toHaveBeenCalledTimes(1);
    expect(mocks.preview).toHaveBeenCalledWith('location', 7, expect.any(AbortSignal));
    // Decorative row image (the filename names it); the blob is shown through an object URL.
    await waitFor(() => expect(thumbnail.querySelector('img')).toHaveAttribute('src', 'blob:thumbnail'));
    expect(mocks.preview).not.toHaveBeenCalledWith('location-2', expect.anything(), expect.anything());
  });

  it('shows preview metadata and capture date in the panel from the cached preview', async () => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    mocks.catalog.mockResolvedValue({ ...catalog, printers: [previewSource] });
    mocks.preview.mockResolvedValue({
      locationId: 'location', observationRevision: 7, state: 'Available', hasImage: false,
      capturedAtUtc: '2026-10-25T12:00:00Z', printTimeSeconds: 5400, layerHeightMm: 0.2,
    });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(within(await findRow('part.gcode')).getByRole('button', { name: 'part.gcode, show details' }));
    const panel = screen.getByRole('dialog', { name: 'part.gcode' });
    await waitFor(() => expect(detailValue(panel, 'Print time')).toHaveTextContent(/^1h 30m$/));
    expect(detailValue(panel, 'Layer height')).toHaveTextContent(/^0.2 mm$/);
    const capturedAt = new Date('2026-10-25T12:00:00Z');
    expect(within(panel).getByText(/Preview captured/)).toHaveTextContent(`Preview captured ${capturedAt.toLocaleDateString()}`);
    expect(within(panel).getByText('Preview unavailable')).toBeInTheDocument();
    expect(mocks.preview).toHaveBeenCalledTimes(1);
    expect(mocks.thumbnail).not.toHaveBeenCalled();
  });

  it('shows grouped print settings, the tool table and every other supplied native field, preserving the long name and short path', async () => {
    const metadata = {
      printerModel: 'COREONE', profile: '0.20mm QUALITY', printTimeSeconds: 5400,
      estimatedPrintTimeSeconds: 5460, estimatedPrintTimeNormalMode: '1h 31m',
      layerHeightMm: 0.2, maxLayerZMm: 80, fillDensity: '15%', brimWidthMm: 5,
      supportMaterial: '1', ironing: 0, bedTemperatureC: 60, bedTemperaturePerToolC: [60, 65],
      nozzleTemperatureC: 215, nozzleTemperaturePerToolC: [215, 220],
      nozzleDiameterMm: 0.4, nozzleDiameterPerToolMm: [0.4, 0.6],
      filamentType: 'PLA', filamentTypePerTool: ['PLA', 'PETG'],
      filamentUsedMm: 1200, filamentUsedPerToolMm: [800, 400],
      filamentUsedG: 4, filamentUsedPerToolG: [3, 1],
      filamentUsedCm3: 3, filamentUsedPerToolCm3: [2, 1],
      filamentCost: 0.12, filamentCostPerTool: [0.09, 0.03],
      materialName: 'Tough resin', requiredResinMl: 20, fadedLayers: 10,
      exposureTimeSeconds: 2, initialExposureTimeSeconds: 30,
      minExposureTimeSeconds: 1, maxExposureTimeSeconds: 5,
      minInitialExposureTimeSeconds: 20, maxInitialExposureTimeSeconds: 40,
      normalPercentPresent: true, normalLeftPresent: false,
      quietPercentPresent: false, quietLeftPresent: true, layerInfoPresent: true,
      layerCount: 400, infillPercentage: 15, infillPattern: 'gyroid',
      supportEnabled: true, supportType: 'tree', supportStyle: 'organic',
      slicerName: 'PrusaSlicer', slicerVersion: '2.9.0', filamentUsedGIsDerived: true,
      // Deliberately out of list order: rows follow the explicit source index.
      tools: [
        { index: 1, sourceId: 'T1', material: 'PETG', color: '#00ff00', filamentUsedG: 1 },
        { index: 0, sourceId: 'T0', material: 'PLA', color: '#ff0000', filamentUsedG: 3 },
      ],
    } satisfies Required<PrinterPrintMetadata>;
    const nativeEntry: PrinterFileCatalogEntry = {
      ...entry, storageRoot: 'usb', relativePath: 'PART~1.BGC',
      displayName: 'Long descriptive part with full settings.bgcode', sizeBytes: 4096,
      detailsFetchedAtUtc: '2026-10-27T12:00:00Z', metadata, metadataState: 'Ready',
    };
    mocks.catalog.mockResolvedValue({
      ...catalog, items: [nativeEntry],
      printers: [{ ...catalog.printers[0], supportsCopy: false, canCopy: false }],
    });
    const user = userEvent.setup();
    renderCatalog();
    const row = await findRow(nativeEntry.displayName);
    expect(cellUnder('Size', row)).toHaveTextContent(/^4 KB$/);
    await user.click(within(row).getByRole('button', { name: `${nativeEntry.displayName}, show details` }));
    const panel = screen.getByRole('dialog', { name: nativeEntry.displayName });
    expect(detailValue(panel, 'Path')).toHaveTextContent(/^usb\/PART~1.BGC$/);
    expect(detailValue(panel, 'Size')).toHaveTextContent(/^4 KB$/);
    expect(detailValue(panel, 'Print time')).toHaveTextContent(/^1h 30m$/);
    expect(detailValue(panel, 'Layer height')).toHaveTextContent(/^0.2 mm$/);
    expect(within(panel).getByText(/Saved in catalog/)).toHaveTextContent(
      `Saved in catalog ${new Date(nativeEntry.detailsFetchedAtUtc!).toLocaleDateString()}`);
    const section = within(panel).getByRole('region', { name: 'Print settings' });
    const settings = [
      ['Slicer', 'PrusaSlicer 2.9.0'], ['Sliced for', 'COREONE'], ['Layer count', '400'],
      ['Nozzle diameter', '0.4 mm'], ['Nozzle temperature', '215 \u00b0C'], ['Bed temperature', '60 \u00b0C'],
      ['Infill', '15%'], ['Infill pattern', 'gyroid'], ['Supports', 'Enabled'],
      ['Support type', 'tree'], ['Support style', 'organic'], ['Filament type', 'PLA'],
      ['Filament used', '4 g (sum of tools)'], ['Filament length', '1200 mm'],
    ];
    for (const [label, value] of settings) {
      expect(detailValue(section, label).textContent).toBe(value);
    }

    const table = within(section).getByRole('table', { name: 'Filament by tool' });
    expect(within(table).getAllByRole('columnheader').map(cell => cell.textContent)).toEqual(['Tool', 'Color', 'Material', 'Used']);
    const [, first, second] = within(table).getAllByRole('row');
    expect(within(first).getByRole('rowheader')).toHaveTextContent(/^Tool 1 \(T0\)$/);
    expect(within(first).getAllByRole('cell').map(cell => cell.textContent)).toEqual(['#FF0000', 'PLA', '3 g']);
    expect(within(first).getByTestId('tool-color-swatch')).toHaveStyle({ backgroundColor: '#FF0000' });
    expect(within(second).getByRole('rowheader')).toHaveTextContent(/^Tool 2 \(T1\)$/);
    expect(within(second).getAllByRole('cell').map(cell => cell.textContent)).toEqual(['#00FF00', 'PETG', '1 g']);

    const other = within(section).getByRole('region', { name: 'Other reported fields' });
    const expected = [
      ['Profile', '0.20mm QUALITY'],
      ['Reported print time', '5400 s'], ['Estimated print time', '5460 s'],
      ['Normal-mode estimate', '1h 31m'], ['Native layer height', '0.2 mm'],
      ['Maximum layer Z', '80 mm'], ['Brim width', '5 mm'],
      ['Support material', '1'], ['Ironing', '0'],
      ['Bed temperature per tool', 'Tool 1: 60 \u00b0C; Tool 2: 65 \u00b0C'],
      ['Nozzle temperature per tool', 'Tool 1: 215 \u00b0C; Tool 2: 220 \u00b0C'],
      ['Nozzle diameter per tool', 'Tool 1: 0.4 mm; Tool 2: 0.6 mm'],
      ['Filament type per tool', 'Tool 1: PLA; Tool 2: PETG'],
      ['Filament length per tool', 'Tool 1: 800 mm; Tool 2: 400 mm'],
      ['Filament mass per tool', 'Tool 1: 3 g; Tool 2: 1 g'],
      ['Filament volume', '3 cm\u00b3'], ['Filament volume per tool', 'Tool 1: 2 cm\u00b3; Tool 2: 1 cm\u00b3'],
      ['Filament cost', '0.12 (currency unspecified)'],
      ['Filament cost per tool', 'Tool 1: 0.09 (currency unspecified); Tool 2: 0.03 (currency unspecified)'],
      ['Material name', 'Tough resin'], ['Required resin', '20 mL'], ['Faded layers', '10'],
      ['Exposure time', '2 s'], ['Initial exposure time', '30 s'],
      ['Minimum exposure time', '1 s'], ['Maximum exposure time', '5 s'],
      ['Minimum initial exposure time', '20 s'], ['Maximum initial exposure time', '40 s'],
      ['Normal-mode progress reporting', 'Yes'], ['Normal-mode remaining-time reporting', 'No'],
      ['Quiet-mode progress reporting', 'No'], ['Quiet-mode remaining-time reporting', 'Yes'],
      ['Layer information present', 'Yes'],
    ];
    // 51 native fields: 14 grouped settings + tools table read 18 of them, the other 33 are listed once.
    expect(Object.keys(metadata)).toHaveLength(51);
    expect(expected).toHaveLength(33);
    expect(within(other).getAllByRole('term')).toHaveLength(33);
    expect(within(section).getAllByRole('term')).toHaveLength(settings.length + expected.length);
    for (const [label, value] of expected) {
      expect(detailValue(other, label).textContent).toBe(value);
    }
    expect(within(section).queryByText(/Collecting|earlier details|could not be read/)).not.toBeInTheDocument();
    expect(mocks.preview).not.toHaveBeenCalled();
    expect(mocks.thumbnail).not.toHaveBeenCalled();
    expect(within(panel).queryByRole('button', { name: 'Download' })).not.toBeInTheDocument();
    expect(within(panel).queryByRole('button', { name: 'Copy to local library' })).not.toBeInTheDocument();
  });

  it.each(['pending preview', 'failed thumbnail', 'missing file'])(
    'keeps saved metadata visible with a %s', async state => {
      vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
      mocks.catalog.mockResolvedValue({
        ...catalog, printers: [previewSource], items: [{
          ...entry, availability: state === 'missing file' ? 'Missing' : 'Available',
          detailsFetchedAtUtc: '2026-10-27T12:00:00Z',
          metadata: { printTimeSeconds: 5400, layerHeightMm: 0.2, filamentType: 'PLA' },
        }],
      });
      mocks.preview.mockReturnValue(state === 'pending preview' ? new Promise(() => {}) : Promise.resolve({
        locationId: entry.id, observationRevision: 7, state: 'Available', hasImage: true,
        metadata: { printTimeSeconds: 1, layerHeightMm: 1, filamentType: 'stale preview value' },
      }));
      mocks.thumbnail.mockRejectedValue(new Error('Thumbnail unavailable'));
      const user = userEvent.setup();
      const rendered = renderCatalog();
      await user.click(within(await findRow('part.gcode')).getByRole('button', { name: 'part.gcode, show details' }));
      const panel = screen.getByRole('dialog', { name: 'part.gcode' });
      expect(detailValue(panel, 'Print time')).toHaveTextContent(/^1h 30m$/);
      expect(detailValue(panel, 'Layer height')).toHaveTextContent(/^0.2 mm$/);
      expect(detailValue(panel, 'Filament type')).toHaveTextContent(/^PLA$/);
      if (state === 'pending preview') {
        expect(within(panel).getByText('Loading preview…')).toBeInTheDocument();
      } else if (state === 'failed thumbnail') {
        await waitFor(() => expect(mocks.thumbnail).toHaveBeenCalled());
        await waitFor(() => expect(within(panel).queryByText('Loading preview…')).not.toBeInTheDocument());
        expect(detailValue(panel, 'Filament type')).toHaveTextContent(/^PLA$/);
      } else {
        expect(mocks.preview).not.toHaveBeenCalled();
      }
      rendered.unmount();
      rendered.queryClient.clear();
    });

  it.each([undefined, null, {}])('treats a saved empty result (%j) as unknown, not stale preview metadata', async metadata => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    mocks.catalog.mockResolvedValue({
      ...catalog, printers: [previewSource], items: [{
        ...entry, detailsFetchedAtUtc: '2026-10-27T12:00:00Z', metadata,
      }],
    });
    mocks.preview.mockResolvedValue({
      locationId: entry.id, observationRevision: 7, state: 'Available', hasImage: false,
      sizeBytes: 999, printTimeSeconds: 600, layerHeightMm: 0.2,
      metadata: { printerModel: 'stale preview value', printTimeSeconds: 600, layerHeightMm: 0.2 },
    });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(within(await findRow('part.gcode')).getByRole('button', { name: 'part.gcode, show details' }));
    const panel = screen.getByRole('dialog', { name: 'part.gcode' });
    await waitFor(() => expect(mocks.preview).toHaveBeenCalled());
    await waitFor(() => expect(within(panel).queryByText('Loading preview…')).not.toBeInTheDocument());
    expect(detailValue(panel, 'Print time')).toHaveTextContent(/^Unknown$/);
    expect(detailValue(panel, 'Layer height')).toHaveTextContent(/^Unknown$/);
    expect(detailValue(panel, 'Size')).toHaveTextContent(/^Unknown$/);
    expect(within(panel).getByText('Unknown — no native metadata supplied.')).toBeInTheDocument();
    expect(within(panel).queryByText('stale preview value')).not.toBeInTheDocument();
  });

  it('preserves explicit zero and false rather than treating them as absent', async () => {
    mocks.catalog.mockResolvedValue({
      ...catalog, items: [{
        ...entry, sizeBytes: 0, detailsFetchedAtUtc: '2026-10-27T12:00:00Z',
        metadata: { printTimeSeconds: 0, layerHeightMm: 0, normalPercentPresent: false, filamentUsedPerToolG: [0, 3] },
      }],
    });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(within(await findRow('part.gcode')).getByRole('button', { name: 'part.gcode, show details' }));
    const panel = screen.getByRole('dialog', { name: 'part.gcode' });
    expect(detailValue(panel, 'Size')).toHaveTextContent(/^0 Bytes$/);
    expect(detailValue(panel, 'Print time')).toHaveTextContent(/^0 s$/);
    expect(detailValue(panel, 'Layer height')).toHaveTextContent(/^0 mm$/);
    expect(detailValue(panel, 'Normal-mode progress reporting')).toHaveTextContent(/^No$/);
    expect(detailValue(panel, 'Filament mass per tool')).toHaveTextContent(/^Tool 1: 0 g; Tool 2: 3 g$/);
    // Grouped settings the source did not report read as a dash ("Unknown" to screen readers), never a default.
    expect(detailValue(panel, 'Sliced for')).toHaveTextContent(/^—Unknown$/);
    expect(detailValue(panel, 'Supports')).toHaveTextContent(/^—Unknown$/);
  });

  it('uses native preview details before the first durable scan', async () => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    mocks.catalog.mockResolvedValue({ ...catalog, printers: [previewSource] });
    mocks.preview.mockResolvedValue({
      locationId: entry.id, observationRevision: 7, state: 'Available', hasImage: false,
      sizeBytes: 4096, metadata: { estimatedPrintTimeSeconds: 5400, layerHeightMm: 0.2, printerModel: 'COREONE' },
    });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(within(await findRow('part.gcode')).getByRole('button', { name: 'part.gcode, show details' }));
    const panel = screen.getByRole('dialog', { name: 'part.gcode' });
    await waitFor(() => expect(detailValue(panel, 'Print time')).toHaveTextContent(/^1h 30m$/));
    expect(detailValue(panel, 'Size')).toHaveTextContent(/^4 KB$/);
    expect(detailValue(panel, 'Layer height')).toHaveTextContent(/^0.2 mm$/);
    expect(detailValue(panel, 'Sliced for')).toHaveTextContent(/^COREONE$/);
    expect(within(panel).queryByText(/Saved in catalog/)).not.toBeInTheDocument();
  });

  it('orders tool rows by source index and shows unknown tool values as dashes, never invented defaults', async () => {
    mocks.catalog.mockResolvedValue({
      ...catalog, items: [{
        ...entry, detailsFetchedAtUtc: '2026-10-27T12:00:00Z', metadataState: 'Partial',
        // Wire shape: unknown values are omitted, not sent as zero or "disabled".
        metadata: {
          supportEnabled: false,
          tools: [
            { index: 2, material: 'TPU', color: 'red' },
            { index: 0, color: '#ff8800aa', filamentUsedG: 0 },
          ],
        },
      }],
    });
    const user = userEvent.setup();
    renderCatalog();
    await user.click(within(await findRow('part.gcode')).getByRole('button', { name: 'part.gcode, show details' }));
    const section = within(screen.getByRole('dialog', { name: 'part.gcode' })).getByRole('region', { name: 'Print settings' });
    expect(detailValue(section, 'Supports')).toHaveTextContent(/^Disabled$/);
    for (const label of ['Slicer', 'Layer count', 'Nozzle temperature', 'Infill', 'Filament used']) {
      expect(detailValue(section, label)).toHaveTextContent(/^—Unknown$/);
    }
    expect(within(section).queryByText('Support type', { selector: 'dt' })).not.toBeInTheDocument();
    expect(within(section).queryByRole('region', { name: 'Other reported fields' })).not.toBeInTheDocument();

    const table = within(section).getByRole('table', { name: 'Filament by tool' });
    const [, first, second] = within(table).getAllByRole('row');
    expect(within(first).getByRole('rowheader')).toHaveTextContent(/^Tool 1$/);
    expect(within(first).getAllByRole('cell').map(cell => cell.textContent)).toEqual(['#FF8800AA', '—Unknown', '0 g']);
    expect(within(second).getByRole('rowheader')).toHaveTextContent(/^Tool 3$/);
    // A non-literal color is unknown: no inline style, dashed placeholder swatch only.
    expect(within(second).getAllByRole('cell').map(cell => cell.textContent)).toEqual(['—Unknown', 'TPU', '—Unknown']);
    expect(within(second).getByTestId('tool-color-swatch')).not.toHaveAttribute('style');
    expect(within(second).getByTestId('tool-color-swatch')).toHaveClass('border-dashed');
  });

  it('shows calm "Collecting" states, not errors, while background capture is pending', async () => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    mocks.catalog.mockResolvedValue({
      ...catalog, printers: [previewSource],
      items: [{ ...entry, metadataState: 'Pending', thumbnailState: 'Pending' }],
    });
    mocks.preview.mockResolvedValue({
      locationId: entry.id, observationRevision: 7, state: 'Available', hasImage: false,
      capturedAtUtc: null, printTimeSeconds: null, layerHeightMm: null,
      metadataState: 'Pending', thumbnailState: 'Deferred', nextAttemptAtUtc: '2026-10-25T12:05:00Z',
    });
    const user = userEvent.setup();
    renderCatalog();
    const row = await findRow('part.gcode');
    scrollIntoView(within(row).getByTestId('printer-file-thumbnail'));
    expect(await within(row).findByRole('img', { name: 'Collecting preview…' })).toBeInTheDocument();
    expect(within(row).queryByRole('img', { name: /Preview unavailable/ })).not.toBeInTheDocument();

    await user.click(within(row).getByRole('button', { name: 'part.gcode, show details' }));
    const panel = screen.getByRole('dialog', { name: 'part.gcode' });
    expect(within(panel).getByText('Collecting preview…')).toBeInTheDocument();
    expect(within(panel).getByText('Collecting details…')).toHaveAttribute('role', 'status');
    expect(detailValue(panel, 'Print time')).toHaveTextContent(/^Collecting…$/);
    expect(detailValue(panel, 'Layer height')).toHaveTextContent(/^Collecting…$/);
    expect(within(panel).queryByText(/Preview unavailable|no native metadata|could not be read/)).not.toBeInTheDocument();
    // One shared, cached preview read for row + panel; no image is requested until one exists.
    expect(mocks.preview).toHaveBeenCalledTimes(1);
    expect(mocks.thumbnail).not.toHaveBeenCalled();
  });

  it('labels an older snapshot shown while a refresh is pending', async () => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: vi.fn(() => 'blob:old') });
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: vi.fn() });
    mocks.catalog.mockResolvedValue({
      ...catalog, printers: [previewSource], items: [{
        ...entry, detailsFetchedAtUtc: '2026-10-27T12:00:00Z', metadata: { filamentType: 'PLA' },
        metadataState: 'Ready', thumbnailState: 'Ready', thumbnailVersion: 'v1',
      }],
    });
    mocks.preview.mockResolvedValue({
      locationId: entry.id, observationRevision: 7, state: 'Available', hasImage: true,
      capturedAtUtc: '2026-10-25T12:00:00Z', metadataState: 'Ready', thumbnailState: 'Ready',
      thumbnailVersion: 'v1', isStale: true,
    });
    mocks.thumbnail.mockResolvedValue(new Blob(['png'], { type: 'image/png' }));
    const user = userEvent.setup();
    renderCatalog();
    await user.click(within(await findRow('part.gcode')).getByRole('button', { name: 'part.gcode, show details' }));
    const panel = screen.getByRole('dialog', { name: 'part.gcode' });
    expect(await within(panel).findByText('Showing earlier details while a refresh is pending.')).toHaveAttribute('role', 'status');
    expect(await within(panel).findByText('Showing an earlier preview while a refresh is pending.')).toBeInTheDocument();
    expect(within(panel).getByRole('img', { name: 'Preview of part.gcode' })).toHaveAttribute('src', 'blob:old');
    expect(detailValue(panel, 'Filament type')).toHaveTextContent(/^PLA$/);
    expect(within(panel).queryByText(/Collecting|Preview unavailable/)).not.toBeInTheDocument();
  });

  it('requests the versioned local thumbnail and re-reads it only when the polled catalog reports a new version', async () => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    const objectUrls = ['blob:v1', 'blob:v2'];
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, writable: true, value: vi.fn(() => objectUrls.shift()) });
    const revoke = vi.fn();
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, writable: true, value: revoke });
    const ready: PrinterFileCatalogEntry = { ...entry, metadataState: 'Ready', thumbnailState: 'Ready' };
    mocks.catalog.mockResolvedValue({ ...catalog, printers: [previewSource], items: [{ ...ready, thumbnailVersion: 'v1' }] });
    const previewFor = (version: string) => ({
      locationId: entry.id, observationRevision: 7, state: 'Available', hasImage: true,
      metadataState: 'Ready', thumbnailState: 'Ready', thumbnailVersion: version,
      thumbnailUrl: `/api/printer-files/locations/${entry.id}/thumbnail?observationRevision=7&version=${version}`,
    });
    mocks.preview.mockResolvedValue(previewFor('v1'));
    mocks.thumbnail.mockImplementation(async () => new Blob(['png'], { type: 'image/png' }));
    const { queryClient } = renderCatalog();
    const thumbnail = within(await findRow('part.gcode')).getByTestId('printer-file-thumbnail');
    scrollIntoView(thumbnail);
    await waitFor(() => expect(thumbnail.querySelector('img')).toHaveAttribute('src', 'blob:v1'));
    expect(mocks.thumbnail).toHaveBeenCalledTimes(1);
    expect(mocks.thumbnail).toHaveBeenCalledWith('location', 7, 'v1', expect.any(AbortSignal));

    // An unchanged catalog poll must not re-read the preview or the image.
    await act(async () => { await queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] }); });
    expect(mocks.preview).toHaveBeenCalledTimes(1);
    expect(mocks.thumbnail).toHaveBeenCalledTimes(1);

    // The background capture stored a new image: the next catalog poll carries the new version.
    mocks.catalog.mockResolvedValue({ ...catalog, printers: [previewSource], items: [{ ...ready, thumbnailVersion: 'v2' }] });
    mocks.preview.mockResolvedValue(previewFor('v2'));
    await act(async () => { await queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] }); });
    await waitFor(() => expect(mocks.thumbnail).toHaveBeenLastCalledWith('location', 7, 'v2', expect.any(AbortSignal)));
    await waitFor(() => expect(thumbnail.querySelector('img')).toHaveAttribute('src', 'blob:v2'));
    expect(mocks.preview).toHaveBeenCalledTimes(2);
    expect(mocks.thumbnail).toHaveBeenCalledTimes(2);
    expect(revoke).toHaveBeenCalledWith('blob:v1');
  });

  it.each([
    ['unsupported', { supportsPreview: false, canPreview: true }],
    ['not permitted', { supportsPreview: true, canPreview: false }],
  ])('never requests previews when the source reports them %s', async (_label, capability) => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    mocks.catalog.mockResolvedValue({ ...catalog, printers: [{ ...catalog.printers[0], ...capability }] });
    renderCatalog();
    const row = await findRow('part.gcode');
    scrollIntoView(within(row).getByTestId('printer-file-thumbnail'));
    await act(async () => { await Promise.resolve(); });
    expect(mocks.preview).not.toHaveBeenCalled();
    expect(mocks.thumbnail).not.toHaveBeenCalled();
  });

  it('shows an explicit unavailable fallback with the server reason when the printer is busy', async () => {
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    mocks.catalog.mockResolvedValue({ ...catalog, printers: [previewSource] });
    mocks.preview.mockResolvedValue({
      locationId: 'location', observationRevision: 7, state: 'Deferred', reasonCode: 'printer_busy', hasImage: false,
    });
    renderCatalog();
    const row = await findRow('part.gcode');
    scrollIntoView(within(row).getByTestId('printer-file-thumbnail'));
    expect(await within(row).findByRole('img', { name: 'Preview unavailable: printer busy' })).toBeInTheDocument();
    expect(mocks.thumbnail).not.toHaveBeenCalled();
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
    const row = await findRow('part.gcode');
    expect(within(row).getByText('gcodes/Parts/part.gcode')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Copy to local library' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Download' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Queue' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete from printer' })).not.toBeInTheDocument();
  });

  it('links to printer file index settings only for farm settings administrators', async () => {
    mocks.hasPermission.mockImplementation((resource: string, action: string) =>
      !(resource === 'system_settings' && action === 'admin'));
    renderCatalog();
    await findRow('part.gcode');
    expect(screen.queryByRole('link', { name: 'Discovery settings' })).not.toBeInTheDocument();
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