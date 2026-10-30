import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { useIsMutating, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import clsx from 'clsx';
import { Alert, Button, Checkbox, FormField, Input, Select, Spinner, Toolbar, ToolbarField } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { FileBrowserViewModeToggle } from '@/common/components/FileBrowserViewModeToggle';
import { CopyLocalIcon, DeleteIcon, DownloadIcon, PlayIcon } from '@/common/components/icons/MdiIcons';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { usePrinters } from '@/common/hooks/useApi';
import { useViewModePreference } from '@/common/hooks/useViewModePreference';
import { apiClient } from '@/services/api';
import { generateUUID } from '@/utils/uuid';
import type { PrinterFileCatalogEntry } from '@/types/api';
import { PrinterFileDeleteControl, type PrinterFileDeleteIntent } from '@/features/files/components/PrinterFileDeleteControl';
import { PrinterFileBulkActions } from '@/features/files/components/PrinterFileBulkActions';
import { CompactFileCard } from '@/features/files/components/CompactFileCard';
import { FileActionIconButtons, type FileAction } from '@/features/files/components/FileActions';
import { FileNameButton } from '@/features/files/components/FileNameButton';
import { PrinterFileStatusGlyphs } from '@/features/files/components/FileGlyphs';
import { PrinterFileDetails, PrinterFileModified } from '@/features/files/components/PrinterFileDetails';
import { PrinterFileThumbnail } from '@/features/files/components/PrinterFileThumbnail';
import { useFileDetailSelection } from '@/features/files/hooks/useFileDetailSelection';
import { canDownloadSource } from '@/features/files/hooks/usePrinterFilePreview';
import { formatSizeLabel } from '@/features/files/utils/fileDisplay';
import {
  describePrinterFileFailure,
  hasBulkCapability,
  printerFileBulkCapabilities,
} from '@/features/files/utils/printerFileBulk';
import {
  COMPACT_FILE_GRID_CLASS,
  COMPACT_FILE_TABLE_CELL_CLASS,
  COMPACT_FILE_TABLE_CLASS,
  COMPACT_FILE_TABLE_FRAME_CLASS,
  FILE_TABLE_HEAD_CLASS,
  FILE_TABLE_ROW_CLASS,
  FILE_TABLE_HEADER_CELL_CLASS,
  FILE_TABLE_PREVIEW_CELL_CLASS,
} from '@/features/fileBrowser/fileBrowserStyles';
import { RefreshButton, SearchField } from '@/common/components/ui/toolbar';

const CATALOG_POLL_MS = 30_000;
const OPERATION_POLL_MS = 2_000;
const PAGE_SIZE = 25;
const ACTIVE_STATES = new Set(['Pending', 'Copying', 'Validating', 'LocalReady', 'Enqueueing']);
const VIEW_MODE_STORAGE_KEY = 'printfarmer-printer-files-viewmode';
const ACTION_ICON_CLASS = 'h-4 w-4';

interface Intent {
  entry: PrinterFileCatalogEntry;
  action: 'Copy' | 'Queue';
  key: string;
}

/** Bulk selection, scoped to the filters and page it was made on. */
interface Selection {
  scope: string;
  ids: ReadonlySet<string>;
}

const NO_IDS: ReadonlySet<string> = new Set();
const NO_BULK_ACTION_TITLE = 'No bulk action is available for this file';
const REFRESH_ALL_LABEL = 'Force refresh all online printers';
const REFRESH_ALL_TITLE = 'Scan online printers you manage now, bypassing schedule and preview caches. Requires idle printers; waits up to two minutes for I/O capacity, then up to two minutes per scan.';
const REFRESH_ALL_UNAVAILABLE_TITLE = 'No online printers you can manage';

interface RefreshTarget {
  id: string;
  name: string;
}

function byteCount(value?: number | null) {
  return value == null ? 'Unknown size' : `${value.toLocaleString()} bytes`;
}

export function PrinterFileCatalog() {
  const { hasPermission } = useAuth();
  const [params, setParams] = useSearchParams();
  const queryClient = useQueryClient();
  // Shared across mounts so a remounted control stays disabled while any force refresh is still running.
  const discoveryPending = useIsMutating({
    mutationKey: ['printer-file-discovery'],
    predicate: mutation => (mutation.state.variables as { kind?: string } | undefined)?.kind !== 'enabled',
  }) > 0;
  const [intent, setIntent] = useState<Intent | null>(null);
  const [deleteIntent, setDeleteIntent] = useState<PrinterFileDeleteIntent | null>(null);
  const [destination, setDestination] = useState('');
  const [copies, setCopies] = useState('1');
  const [acceptedSnapshot, setAcceptedSnapshot] = useState(false);
  const printerId = params.get('printer') ?? '';
  const search = params.get('remoteSearch') ?? '';
  const requestedPage = Number(params.get('remotePage') ?? '1');
  const page = Number.isSafeInteger(requestedPage) && requestedPage > 0 ? requestedPage : 1;
  const operationId = params.get('operation') ?? '';
  const canRead = hasPermission('printer_files', 'read');
  const { data: destinations = [] } = usePrinters({ enabled: canRead });
  const canQueue = hasPermission('queue', 'write');
  const canDeleteFiles = hasPermission('printer_files', 'delete');
  const { viewMode, setViewMode } = useViewModePreference(VIEW_MODE_STORAGE_KEY);
  const details = useFileDetailSelection<string>();
  // Filters and paging change what is listed, so any change starts a fresh selection.
  const selectionScope = JSON.stringify([printerId, search, page]);
  const [selection, setSelection] = useState<Selection>(() => ({ scope: selectionScope, ids: NO_IDS }));
  if (selection.scope !== selectionScope) setSelection({ scope: selectionScope, ids: NO_IDS });

  const changeFilter = (key: string, value: string) => {
    setParams(previous => {
      const next = new URLSearchParams(previous);
      if (value) next.set(key, value);
      else next.delete(key);
      if (key !== 'remotePage' && key !== 'operation') next.delete('remotePage');
      return next;
    });
  };
  const catalog = useQuery({
    queryKey: ['printer-file-catalog', printerId, search, page],
    queryFn: ({ signal }) => apiClient.getPrinterFileCatalog({
      printerId: printerId || undefined, search: search || undefined, page, pageSize: PAGE_SIZE,
    }, signal),
    enabled: canRead,
    refetchInterval: query => query.state.data?.scans.some(scan => scan.isScanning)
      ? OPERATION_POLL_MS : CATALOG_POLL_MS,
  });
  const history = useQuery({
    queryKey: ['printer-file-operations'],
    queryFn: ({ signal }) => apiClient.getPrinterFileOperations(signal),
    enabled: canRead,
    refetchInterval: CATALOG_POLL_MS,
  });
  const progress = useQuery({
    queryKey: ['printer-file-operation', operationId],
    queryFn: ({ signal }) => apiClient.getPrinterFileOperation(operationId, signal),
    enabled: canRead && !!operationId,
    refetchInterval: query => query.state.data && ACTIVE_STATES.has(query.state.data.state)
      ? OPERATION_POLL_MS : false,
  });
  const operation = progress.data;

  const invalidate = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] }),
      queryClient.invalidateQueries({ queryKey: ['printer-file-operations'] }),
      queryClient.invalidateQueries({ queryKey: ['printer-file-operation', operationId] }),
      queryClient.invalidateQueries({ queryKey: ['gcode-files'] }),
    ]);
  };
  const command = useMutation({
    mutationFn: async () => {
      if (!intent) throw new Error('Select a file first.');
      if (intent.action === 'Copy') {
        return apiClient.copyIndexedPrinterFile(intent.entry.id, intent.entry.observationRevision, intent.key);
      }
      const count = Number(copies);
      if (!destination || !Number.isSafeInteger(count) || count < 1 || count > 1000) {
        throw new Error('Select a destination and between 1 and 1,000 copies.');
      }
      return apiClient.queueIndexedPrinterFile(intent.entry.id, intent.entry.observationRevision,
        destination, { copies: count }, intent.key);
    },
    onSuccess: async result => {
      changeFilter('operation', result.id);
      setAcceptedSnapshot(false);
      setIntent(null);
      toast.info(result.action === 'Copy' ? 'Copy requested. No job will be created.' : 'Local staging requested.');
      await invalidate();
    },
    // The dialog retains this intent/key after a lost response. Retry never invents another command.
    onError: (error: Error) => toast.error(`Failed to start: ${error.message}`),
  });
  const operationMutation = useMutation({
    mutationFn: async (action: 'confirm' | 'decline' | 'retry' | 'cancel') => {
      if (!operation) throw new Error('Reload the operation first.');
      if (action === 'confirm' || action === 'decline') {
        if (!operation.gcodeFileId || !operation.contentSha256 || operation.sizeBytes == null) {
          throw new Error('The captured-content binding is unavailable.');
        }
        return apiClient.confirmPrinterFileSnapshot(operation.id, {
          gcodeFileId: operation.gcodeFileId, contentSha256: operation.contentSha256,
          sizeBytes: operation.sizeBytes, payloadSha256: operation.payloadSha256, accept: action === 'confirm',
        });
      }
      return action === 'cancel' ? apiClient.cancelPrinterFileOperation(operation.id)
        : apiClient.retryPrinterFileOperation(operation.id, operation.action === 'Queue' && !!operation.gcodeFileId);
    },
    onSuccess: async () => {
      setAcceptedSnapshot(false);
      await invalidate();
    },
    onError: (error: Error) => toast.error(`Failed to update operation: ${error.message}`),
  });
  // Browser-only download: streams the printer's bytes to this computer. It never creates a
  // local-library asset, an operation, or a queue job (that is "Copy to local library").
  const download = useMutation({
    mutationFn: (entry: PrinterFileCatalogEntry) =>
      apiClient.downloadPrinterFile(entry.id, entry.observationRevision, entry.displayName),
    onError: async (error: unknown) => {
      const failure = describePrinterFileFailure(error, { blobResponse: true });
      toast.error(`Failed to download: ${failure.message}`);
      if (failure.stale) {
        await queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] });
      }
    },
  });
  const refreshAll = useMutation({
    mutationKey: ['printer-file-discovery'],
    mutationFn: async (targets: RefreshTarget[]) => {
      const concurrency = catalog.data?.maxConcurrentIo ?? 2;
      if (!Number.isInteger(concurrency) || concurrency < 1 || concurrency > 16) {
        throw new Error('The server returned an invalid printer-file I/O budget. Reload the catalog before retrying.');
      }
      const failures: (string | undefined)[] = new Array(targets.length);
      let next = 0;
      await Promise.all(Array.from({ length: Math.min(concurrency, targets.length) }, async () => {
        while (next < targets.length) {
          const index = next++;
          const target = targets[index];
          try {
            await apiClient.refreshPrinterFileIndex(target.id, true);
          } catch (error) {
            failures[index] = `${target.name}: ${describePrinterFileFailure(error).message}`;
          }
        }
      }));
      return failures.filter((failure): failure is string => failure !== undefined);
    },
    onSuccess: async (failures, targets) => {
      const completed = targets.length - failures.length;
      if (completed > 0) {
        toast.success(`Force refresh completed for ${completed} printer${completed === 1 ? '' : 's'}. Previews reload on demand.`);
      }
      if (failures.length > 0) toast.error(`Failed to force refresh: ${failures.join('; ')}`);
      // Refresh revisions before invalidating old preview keys.
      await queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] });
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['printer-file-preview'] }),
        queryClient.invalidateQueries({ queryKey: ['printer-file-thumbnail'] }),
      ]);
    },
    onError: (error: Error) => toast.error(`Failed to force refresh: ${error.message}`),
  });
  const selectIntent = (entry: PrinterFileCatalogEntry, action: Intent['action']) => {
    setIntent({ entry, action, key: generateUUID() });
    setDestination(entry.printerId);
    setCopies('1');
    command.reset();
  };

  if (!canRead) return <Alert>You do not have permission to view printer-file observations.</Alert>;

  const sourceFor = (entry: PrinterFileCatalogEntry) =>
    catalog.data?.printers.find(printer => printer.id === entry.printerId);
  const onlinePrinterIds = new Set(destinations.filter(printer => printer.isOnline).map(printer => printer.id));
  const manageableSources = catalog.data?.printers.filter(printer => printer.canManage) ?? [];
  const refreshTargets: RefreshTarget[] = manageableSources
    .filter(printer => onlinePrinterIds.has(printer.id))
    .map(({ id, name }) => ({ id, name }));
  // Background leases do not block requests; the API waits for exclusive I/O capacity.
  const refreshBusy = refreshAll.isPending || discoveryPending;
  const entryActions = (entry: PrinterFileCatalogEntry): FileAction[] => {
    const source = sourceFor(entry);
    if (!source) return [];
    const available = entry.availability === 'Available';
    const actions: FileAction[] = [];
    if (source.canCopy) {
      actions.push({ id: 'copy', label: 'Copy to local library', icon: <CopyLocalIcon className={ACTION_ICON_CLASS} />,
        disabled: !available, onClick: () => selectIntent(entry, 'Copy') });
    }
    if (available && canDownloadSource(source)) {
      actions.push({ id: 'download', label: 'Download', icon: <DownloadIcon className={ACTION_ICON_CLASS} />,
        disabled: download.isPending && download.variables?.id === entry.id, onClick: () => download.mutate(entry) });
    }
    if (source.canCopy && canQueue) {
      actions.push({ id: 'queue', label: 'Queue', icon: <PlayIcon className={ACTION_ICON_CLASS} />, tone: 'primary',
        disabled: !available, onClick: () => selectIntent(entry, 'Queue') });
    }
    if (source.canDelete) {
      actions.push({ id: 'delete', label: 'Delete from printer', icon: <DeleteIcon className={ACTION_ICON_CLASS} />, tone: 'danger',
        disabled: !available, onClick: () => setDeleteIntent({ entry, key: generateUUID() }) });
    }
    return actions;
  };
  const selectedEntry = details.selected
    ? catalog.data?.items.find(entry => entry.id === details.selected) ?? null : null;

  const items = catalog.data?.items ?? [];
  const capabilitiesFor = (entry: PrinterFileCatalogEntry) =>
    printerFileBulkCapabilities(entry, sourceFor(entry), canDeleteFiles);
  // Only listed files that support at least one bulk action can be selected.
  const selectableIds = items.filter(entry => hasBulkCapability(capabilitiesFor(entry))).map(entry => entry.id);
  const selectedIds = selection.scope === selectionScope ? selection.ids : NO_IDS;
  const selectedEntries = items.filter(entry => selectedIds.has(entry.id) && selectableIds.includes(entry.id));
  // Drop ids that are no longer listed (or no longer eligible) so they never come back selected.
  if (catalog.data && selectedIds.size !== selectedEntries.length) {
    setSelection({ scope: selectionScope, ids: new Set(selectedEntries.map(entry => entry.id)) });
  }
  const writeSelection = (ids: Iterable<string>) => setSelection({ scope: selectionScope, ids: new Set(ids) });
  const toggleSelected = (id: string) => {
    const next = new Set(selectedEntries.map(entry => entry.id));
    if (next.has(id)) next.delete(id);
    else next.add(id);
    writeSelection(next);
  };
  const allSelected = selectableIds.length > 0 && selectedEntries.length === selectableIds.length;
  const someSelected = selectedEntries.length > 0 && !allSelected;
  const toggleAll = () => writeSelection(allSelected ? [] : selectableIds);
  const selectAllCheckbox = (label?: string) => (
    <Checkbox
      aria-label={label ? undefined : 'Select all files on this page'}
      label={label}
      checked={allSelected}
      indeterminate={someSelected}
      disabled={selectableIds.length === 0}
      onChange={toggleAll}
    />
  );
  const selectionFor = (entry: PrinterFileCatalogEntry) => {
    const selectable = selectableIds.includes(entry.id);
    return {
      checked: selectable && selectedIds.has(entry.id),
      onToggle: () => toggleSelected(entry.id),
      disabled: !selectable,
      title: selectable ? undefined : NO_BULK_ACTION_TITLE,
    };
  };
  const showDeleteOperation = (id: string) => setParams(previous => {
    const next = new URLSearchParams(previous);
    next.set('deleteOperation', id);
    return next;
  });

  return (
    <section aria-label="On printers" className="space-y-4">
      {catalog.data?.automaticDiscoveryEnabled === false && (
        <Alert variant="info">Automatic discovery is globally disabled. Force refresh, Copy, and Queue remain available as separate actions.</Alert>
      )}
      <Toolbar aria-label="Printer file controls" labelled>
        <ToolbarField label="Search" htmlFor="catalog-search" className="min-w-0 flex-1">
          <SearchField
            id="catalog-search"
            label="Search"
            placeholder="Search filenames or paths"
            value={search}
            maxLength={200}
            onChange={event => changeFilter('remoteSearch', event.target.value)}
            onClear={() => changeFilter('remoteSearch', '')}
          />
        </ToolbarField>
        <Toolbar.Group>
          <ToolbarField label="Printer" htmlFor="catalog-printer" className="w-full sm:w-64">
            <Select id="catalog-printer" data-role="field" className="pf-control" value={printerId} onChange={event => changeFilter('printer', event.target.value)}>
            <option value="">All</option>
            {catalog.data?.printers.map(printer => <option key={printer.id} value={printer.id}>{printer.name}</option>)}
            </Select>
          </ToolbarField>
        </Toolbar.Group>
        <Toolbar.Spacer />
        <Toolbar.Group className="pf-toolbar__group--nowrap">
          {catalog.data && viewMode === 'grid' && selectAllCheckbox('Select all files on this page')}
          <FileBrowserViewModeToggle viewMode={viewMode} onViewModeChange={setViewMode} />
          {manageableSources.length > 0 && (
            <RefreshButton
              type="button"
              variant="secondary"
              aria-label={REFRESH_ALL_LABEL}
              title={refreshTargets.length > 0 ? REFRESH_ALL_TITLE : REFRESH_ALL_UNAVAILABLE_TITLE}
              explainedDisabled
              disabled={refreshTargets.length === 0 || refreshBusy}
              busy={refreshBusy}
              onClick={() => refreshAll.mutate(refreshTargets)}
            />
          )}
        </Toolbar.Group>
      </Toolbar>
      {catalog.isLoading && <div role="status"><Spinner /> Loading observations</div>}
      {catalog.error && <Alert variant="error">Failed to load observations: {catalog.error.message}</Alert>}
      <PrinterFileBulkActions
        selected={selectedEntries.map(entry => ({ entry, capabilities: capabilitiesFor(entry) }))}
        onClearSelection={() => writeSelection([])}
        onShowOperation={id => changeFilter('operation', id)}
        onShowDeleteOperation={showDeleteOperation}
      />
      {catalog.data && (
        <>
          <div className="flex items-start gap-4">
            <div className="min-w-0 flex-1">
              {catalog.data.items.length === 0 ? (
                <p className="rounded-lg border border-pf-border p-5 text-pf-text-secondary">No matching observations. Only a complete scan establishes an empty source; never-scanned or failed sources are not proof of absence.</p>
              ) : viewMode === 'grid' ? (
                <div role="region" className={COMPACT_FILE_GRID_CLASS} aria-label="Files on printers">
                  {catalog.data.items.map(entry => {
                    const source = sourceFor(entry);
                    return (
                      <div key={entry.id} role="group" aria-label={entry.displayName}>
                        <CompactFileCard
                          name={entry.displayName}
                          thumbnail={<PrinterFileThumbnail entry={entry} source={source} size="card" />}
                          status={<PrinterFileStatusGlyphs entry={entry} />}
                          meta={<span title={entry.printerName}>{formatSizeLabel(entry.sizeBytes)} · {entry.printerName}</span>}
                          actions={entryActions(entry)}
                          selection={selectionFor(entry)}
                          isActive={details.selected === entry.id}
                          onOpen={invoker => details.open(entry.id, invoker)}
                        />
                      </div>
                    );
                  })}
                </div>
              ) : (
                <div className={COMPACT_FILE_TABLE_FRAME_CLASS}>
                  <table className={COMPACT_FILE_TABLE_CLASS} aria-label="Files on printers">
                    <caption className="sr-only">Printer-resident file observations. Missing metadata does not establish owned content.</caption>
                    <thead className={FILE_TABLE_HEAD_CLASS}><tr>
                      <th scope="col" className={clsx(FILE_TABLE_HEADER_CELL_CLASS, 'w-10')}>{selectAllCheckbox()}</th>
                      <th scope="col" className={clsx(FILE_TABLE_HEADER_CELL_CLASS, FILE_TABLE_PREVIEW_CELL_CLASS)}>Preview</th>
                      <th scope="col" className={FILE_TABLE_HEADER_CELL_CLASS}>Name</th>
                      <th scope="col" className={FILE_TABLE_HEADER_CELL_CLASS}>Printer</th>
                      <th scope="col" className={clsx(FILE_TABLE_HEADER_CELL_CLASS, 'text-right')}>Size</th>
                      <th scope="col" className={FILE_TABLE_HEADER_CELL_CLASS}>Modified</th>
                      <th scope="col" className={clsx(FILE_TABLE_HEADER_CELL_CLASS, 'text-right')}>Actions</th>
                    </tr></thead>
                    <tbody>
                      {catalog.data.items.map(entry => {
                        const path = `${entry.storageRoot}/${entry.relativePath}`;
                        const isActive = details.selected === entry.id;
                        const rowSelection = selectionFor(entry);
                        return (
                          <tr key={entry.id} className={clsx(FILE_TABLE_ROW_CLASS, isActive || rowSelection.checked ? 'bg-pf-accent-bg/5' : 'hover:bg-pf-bg-2')}>
                            <td className={clsx(COMPACT_FILE_TABLE_CELL_CLASS, 'w-10')}>
                              <Checkbox
                                aria-label={`Select ${entry.displayName}`}
                                checked={rowSelection.checked}
                                disabled={rowSelection.disabled}
                                title={rowSelection.title}
                                onChange={rowSelection.onToggle}
                              />
                            </td>
                            {/* Mouse shortcut matching the card view; keyboard users open details from the name button. */}
                            <td
                              className={clsx(COMPACT_FILE_TABLE_CELL_CLASS, FILE_TABLE_PREVIEW_CELL_CLASS, 'cursor-pointer')}
                              onClick={event => details.open(
                                entry.id,
                                event.currentTarget.closest('tr')?.querySelector<HTMLElement>('[data-file-details-trigger]') ?? null,
                              )}
                            >
                              <PrinterFileThumbnail entry={entry} source={sourceFor(entry)} size="row" />
                            </td>
                            <td className={clsx(COMPACT_FILE_TABLE_CELL_CLASS, 'max-w-xs')}>
                              <FileNameButton name={entry.displayName} onOpen={invoker => details.open(entry.id, invoker)} />
                              <div className="truncate font-mono text-pf-text-secondary" title={path}>{path}</div>
                            </td>
                            <td className={COMPACT_FILE_TABLE_CELL_CLASS}>{entry.printerName}</td>
                            <td className={clsx(COMPACT_FILE_TABLE_CELL_CLASS, 'whitespace-nowrap text-right')}>{formatSizeLabel(entry.sizeBytes)}</td>
                            <td className={clsx(COMPACT_FILE_TABLE_CELL_CLASS, 'whitespace-nowrap')}><PrinterFileModified value={entry.modifiedValue} /></td>
                            <td className={clsx(COMPACT_FILE_TABLE_CELL_CLASS, 'text-right')}>
                              <div className="flex justify-end gap-1"><FileActionIconButtons actions={entryActions(entry)} dense /></div>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
            {selectedEntry && (
              <PrinterFileDetails
                key={selectedEntry.id}
                entry={selectedEntry}
                source={sourceFor(selectedEntry)}
                actions={entryActions(selectedEntry)}
                onClose={details.close}
              />
            )}
          </div>
          <div className="flex items-center justify-between gap-3">
            <p className="text-sm text-pf-text-secondary">{catalog.data.total} observations · Page {page}</p>
            <div className="flex gap-2">
              <Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => changeFilter('remotePage', String(page - 1))}>Previous</Button>
              <Button size="sm" variant="secondary" disabled={page * PAGE_SIZE >= catalog.data.total} onClick={() => changeFilter('remotePage', String(page + 1))}>Next</Button>
            </div>
          </div>
        </>
      )}
      {history.error && <Alert variant="error">Failed to load recent operations: {history.error.message}</Alert>}
      {!!history.data?.length && (
        <FormField label="Resume a recent Copy or Queue operation" htmlFor="catalog-operation">
          <Select id="catalog-operation" value={operationId} onChange={event => {
            setAcceptedSnapshot(false);
            changeFilter('operation', event.target.value);
          }}>
            <option value="">Select an operation</option>
            {history.data.map(item => <option key={item.id} value={item.id}>{item.action} · {item.state} · {item.id}</option>)}
          </Select>
        </FormField>
      )}
      {progress.error && <Alert variant="error">Failed to load operation: {progress.error.message}</Alert>}
      <PrinterFileDeleteControl intent={deleteIntent} onClose={() => setDeleteIntent(null)}
        canDelete={hasPermission('printer_files', 'delete')} printers={catalog.data?.printers ?? []} />
      {operation && (
        <section className="space-y-3 rounded-sm border border-pf-border bg-pf-bg-1 p-4" aria-labelledby="catalog-progress-heading">
          <h3 id="catalog-progress-heading" className="font-semibold">{operation.action} operation</h3>
          <p role="status" aria-live="polite">{operation.state}{operation.reasonCode ? ` · ${operation.reasonCode.replaceAll('_', ' ')}` : ''}</p>
          <p className="break-all font-mono text-xs">{operation.id}</p>
          {operation.gcodeFileId && <p>Owned local asset: <Link className="text-pf-accent underline" to={`/files?type=gcode&file=${encodeURIComponent(operation.gcodeFileId)}`}>{operation.gcodeFileId}</Link> · {byteCount(operation.sizeBytes)}</p>}
          {operation.contentSha256 && <p className="break-all font-mono text-xs">SHA-256: {operation.contentSha256}</p>}
          {operation.destinationPrinterId && <p>Destination: {destinations.find(printer => printer.id === operation.destinationPrinterId)?.name ?? operation.destinationPrinterId}</p>}
          {operation.state === 'AwaitingSourceConfirmation' && (
            <Alert variant="warning" title="Confirm this captured snapshot">
              <p>The printer cannot prove a version-bound download. Local integrity and destination safety checks passed, but another client may have changed the remote file during capture. Queue only the exact local bytes and destination shown above.</p>
              <p className="mt-1 break-all font-mono text-xs">Enqueue intent: {operation.payloadSha256}</p>
              <div className="mt-3 space-y-3">
                <Checkbox label="I accept this captured snapshot for this enqueue intent" checked={acceptedSnapshot} onChange={event => setAcceptedSnapshot(event.target.checked)} />
                <div className="flex flex-wrap gap-2">
                  <Button disabled={!acceptedSnapshot || operationMutation.isPending} onClick={() => operationMutation.mutate('confirm')}>Confirm snapshot and queue</Button>
                  <Button variant="secondary" disabled={operationMutation.isPending} onClick={() => operationMutation.mutate('decline')}>Decline · keep local copy</Button>
                </div>
              </div>
            </Alert>
          )}
          {operation.state === 'Queued' && <p>Committed job: {(operation.jobIds ?? []).map(id => <Link key={id} to="/printQueue" className="mr-2 text-pf-accent underline">{id}</Link>)}. Cancelling a transfer cannot cancel an admitted job; use the queue.</p>}
          {operation.state === 'Cancelled' && operation.gcodeFileId && <p>No job was admitted by this cancelled intent. The local copy is retained.</p>}
          <div className="flex flex-wrap gap-2">
            {(operation.state === 'Failed' || operation.state === 'Blocked') && <Button variant="secondary" loading={operationMutation.isPending} onClick={() => operationMutation.mutate('retry')}>Retry same operation</Button>}
            {(ACTIVE_STATES.has(operation.state) || operation.state === 'AwaitingSourceConfirmation') && <Button variant="secondary" disabled={operationMutation.isPending} onClick={() => operationMutation.mutate('cancel')}>Cancel before admission</Button>}
          </div>
        </section>
      )}
      <Modal isOpen={!!intent} onClose={() => { if (!command.isPending) setIntent(null); }}
        title={intent?.action === 'Queue' ? 'Stage locally and queue' : 'Copy into local library'} size="lg"
        footer={<>
          <Button variant="secondary" disabled={command.isPending} onClick={() => setIntent(null)}>Cancel</Button>
          <Button loading={command.isPending} onClick={() => command.mutate()}>{command.isError ? 'Retry same intent' : intent?.action === 'Queue' ? 'Stage locally' : 'Copy locally'}</Button>
        </>}>
        {intent && (
          <div className="space-y-4">
            <p className="font-semibold">{intent.entry.printerName}</p>
            <p className="break-all font-mono">{intent.entry.storageRoot}/{intent.entry.relativePath}</p>
            <p>Observation {intent.entry.observationRevision} · {byteCount(intent.entry.sizeBytes)}</p>
            <Alert>{intent.action === 'Copy' ? 'This creates or reuses an owned local asset. It never queues or starts a print.' : 'This stages local bytes even when the destination is the source printer. Compatibility and safety checks still apply. Unverified source snapshots require a separate confirmation after capture. Queueing does not bypass the Ready gate.'}</Alert>
            {intent.action === 'Queue' && (
              <>
                <FormField label="Destination printer" htmlFor="catalog-destination" required>
                  <Select id="catalog-destination" value={destination} onChange={event => setDestination(event.target.value)} disabled={command.isError}>
                    <option value="">Select a printer</option>
                    {destinations.filter(printer => printer.isEnabled).map(printer => <option key={printer.id} value={printer.id}>{printer.name}</option>)}
                  </Select>
                </FormField>
                <FormField label="Copies" htmlFor="catalog-copies" required>
                  <Input id="catalog-copies" type="number" min={1} max={1000} value={copies} disabled={command.isError} onChange={event => setCopies(event.target.value)} />
                </FormField>
              </>
            )}
            {command.error && <Alert variant="error">{command.error.message}. The server may already have accepted this intent. Retry the same intent or resume its history before creating another one; another intent can deliberately create another job.</Alert>}
          </div>
        )}
      </Modal>
    </section>
  );
}
