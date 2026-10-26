import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Alert, Badge, Button, Checkbox, FormField, Input, Select, Spinner, Toggle } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { usePrinters } from '@/common/hooks/useApi';
import { apiClient } from '@/services/api';
import { generateUUID } from '@/utils/uuid';
import type { PrinterFileAvailability, PrinterFileCatalogEntry } from '@/types/api';
import { PrinterFileDeleteControl, type PrinterFileDeleteIntent } from '@/features/files/components/PrinterFileDeleteControl';

const CATALOG_POLL_MS = 30_000;
const OPERATION_POLL_MS = 2_000;
const PAGE_SIZE = 25;
const ACTIVE_STATES = new Set(['Pending', 'Copying', 'Validating', 'LocalReady', 'Enqueueing']);

interface Intent {
  entry: PrinterFileCatalogEntry;
  action: 'Copy' | 'Queue';
  key: string;
}

function observedAt(value?: string | null) {
  return value ? new Date(value).toLocaleString() : 'Unknown';
}

function byteCount(value?: number | null) {
  return value == null ? 'Unknown size' : `${value.toLocaleString()} bytes`;
}

export function PrinterFileCatalog() {
  const { hasPermission } = useAuth();
  const [params, setParams] = useSearchParams();
  const queryClient = useQueryClient();
  const [intent, setIntent] = useState<Intent | null>(null);
  const [deleteIntent, setDeleteIntent] = useState<PrinterFileDeleteIntent | null>(null);
  const [destination, setDestination] = useState('');
  const [copies, setCopies] = useState('1');
  const [acceptedSnapshot, setAcceptedSnapshot] = useState(false);
  const printerId = params.get('printer') ?? '';
  const search = params.get('remoteSearch') ?? '';
  const requestedPage = Number(params.get('remotePage') ?? '1');
  const page = Number.isSafeInteger(requestedPage) && requestedPage > 0 ? requestedPage : 1;
  const rawAvailability = params.get('availability');
  const availability: PrinterFileAvailability | undefined =
    rawAvailability === 'Available' || rawAvailability === 'Missing' || rawAvailability === 'Deleted'
      ? rawAvailability : undefined;
  const freshness = params.get('freshness') ?? '';
  const operationId = params.get('operation') ?? '';
  const canRead = hasPermission('printer_files', 'read');
  const { data: destinations = [] } = usePrinters({ enabled: canRead });
  const canQueue = hasPermission('queue', 'write');

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
    queryKey: ['printer-file-catalog', printerId, search, availability, freshness, page],
    queryFn: ({ signal }) => apiClient.getPrinterFileCatalog({
      printerId: printerId || undefined, search: search || undefined, availability,
      stale: freshness ? freshness === 'stale' : undefined, page, pageSize: PAGE_SIZE,
    }, signal),
    enabled: canRead,
    refetchInterval: CATALOG_POLL_MS,
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
  const scan = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled?: boolean }) =>
      enabled === undefined ? apiClient.refreshPrinterFileIndex(id) : apiClient.setPrinterFileIndexEnabled(id, enabled),
    onSuccess: async () => {
      toast.success('Discovery request saved. It still requires an idle printer.');
      await invalidate();
    },
    onError: (error: Error) => toast.error(`Failed to update discovery: ${error.message}`),
  });
  const selectIntent = (entry: PrinterFileCatalogEntry, action: Intent['action']) => {
    setIntent({ entry, action, key: generateUUID() });
    setDestination(entry.printerId);
    setCopies('1');
    command.reset();
  };

  if (!canRead) return <Alert>You do not have permission to view printer-file observations.</Alert>;

  return (
    <section aria-labelledby="printer-catalog-heading" className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3 border-b border-pf-border pb-3">
        <div>
          <h2 id="printer-catalog-heading" className="text-lg font-semibold text-pf-text-primary">On printers</h2>
          <p className="text-sm text-pf-text-secondary">Metadata observations, not owned library files. Browsing never downloads G-code.</p>
        </div>
        {hasPermission('settings', 'read') && <Link to="/admin/settings?q=printer%20file" className="text-sm text-pf-accent underline">Discovery settings</Link>}
      </div>
      {catalog.data?.automaticDiscoveryEnabled === false && (
        <Alert variant="info">Automatic discovery is globally disabled. Explicit Copy and Queue remain separate actions.</Alert>
      )}
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <FormField label="Printer" htmlFor="catalog-printer">
          <Select id="catalog-printer" value={printerId} onChange={event => changeFilter('printer', event.target.value)}>
            <option value="">All authorized printers</option>
            {catalog.data?.printers.map(printer => <option key={printer.id} value={printer.id}>{printer.name}</option>)}
          </Select>
        </FormField>
        <FormField label="Path search" htmlFor="catalog-search">
          <Input id="catalog-search" value={search} maxLength={200} onChange={event => changeFilter('remoteSearch', event.target.value)} />
        </FormField>
        <FormField label="Availability" htmlFor="catalog-availability">
          <Select id="catalog-availability" value={availability ?? ''} onChange={event => changeFilter('availability', event.target.value)}>
            <option value="">All observations</option><option value="Available">Available</option>
            <option value="Missing">Missing</option><option value="Deleted">Deleted</option>
          </Select>
        </FormField>
        <FormField label="Freshness" htmlFor="catalog-freshness">
          <Select id="catalog-freshness" value={freshness} onChange={event => changeFilter('freshness', event.target.value)}>
            <option value="">Any freshness</option><option value="fresh">Recently seen</option><option value="stale">Stale</option>
          </Select>
        </FormField>
      </div>
      {catalog.isLoading && <div role="status"><Spinner /> Loading observations</div>}
      {catalog.error && <Alert variant="error">Failed to load observations: {catalog.error.message}</Alert>}
      {catalog.data && (
        <>
          <div className="grid gap-2 md:grid-cols-2">
            {catalog.data.printers.filter(printer => !printerId || printer.id === printerId).map(printer => {
              const status = catalog.data.scans.find(item => item.printerId === printer.id);
              return (
                <div key={printer.id} className="rounded-sm border border-pf-border bg-pf-bg-1 p-3 text-sm">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <strong>{printer.name}</strong>
                    <Badge variant={status?.isStale ? 'warning' : 'default'}>
                      {status?.isScanning ? 'Scanning / file I/O active' : status?.outcome ?? 'Never scanned'}
                    </Badge>
                  </div>
                  <p className="mt-1 text-pf-text-secondary">
                    Last complete: {observedAt(status?.lastCompleteScanAtUtc ?? null)}
                    {status?.isStale ? ' · Stale' : ''}{status && !status.enabled ? ' · Discovery disabled' : ''}
                  </p>
                  {status?.reasonCode && <p className="break-words">{status.reasonCode.replaceAll('_', ' ')}</p>}
                  {!printer.supportsCopy && <p>Listing only: authenticated local staging is not supported by this backend.</p>}
                  {status?.nextScanAtUtc && <p className="text-pf-text-secondary">Next eligible scan: {observedAt(status.nextScanAtUtc)}</p>}
                  {printer.canManage && (
                    <div className="mt-2 flex flex-wrap items-center gap-3">
                      <Button size="sm" variant="secondary" disabled={scan.isPending} onClick={() => scan.mutate({ id: printer.id })}>Request refresh</Button>
                      <Toggle label={`Automatic discovery for ${printer.name}`} checked={status?.enabled ?? true}
                        disabled={scan.isPending} onChange={event => scan.mutate({ id: printer.id, enabled: event.target.checked })} />
                    </div>
                  )}
                </div>
              );
            })}
          </div>
          <div className="overflow-x-auto rounded-sm border border-pf-border">
            <table className="w-full text-left text-sm">
              <caption className="sr-only">Printer-resident file observations. Missing metadata does not establish owned content.</caption>
              <thead className="bg-pf-bg-1 text-pf-text-secondary"><tr>
                <th scope="col" className="p-3">Source / exact path</th><th scope="col" className="p-3">Observation</th>
                <th scope="col" className="p-3">Captured local copies</th><th scope="col" className="p-3">Actions</th>
              </tr></thead>
              <tbody>
                {catalog.data.items.map(entry => {
                  const source = catalog.data.printers.find(printer => printer.id === entry.printerId);
                  return (
                    <tr key={entry.id} className="border-t border-pf-border align-top">
                      <th scope="row" className="max-w-md p-3 font-normal">
                        <strong>{entry.printerName}</strong><div className="break-all font-mono">{entry.storageRoot}/{entry.relativePath}</div>
                      </th>
                      <td className="p-3">
                        <Badge variant={entry.isStale || entry.availability !== 'Available' ? 'warning' : 'default'}>{entry.availability}{entry.isStale ? ' · Stale' : ''}</Badge>
                        <p className="mt-1">{byteCount(entry.sizeBytes)}</p><p>Modified: {entry.modifiedValue ?? 'Unknown'}</p>
                        <p className="text-pf-text-secondary">Last seen: {observedAt(entry.lastSeenAtUtc)}</p>
                        <p className="text-pf-text-secondary">Complete scan: {observedAt(entry.lastCompleteScanAtUtc)}</p>
                      </td>
                      <td className="p-3">
                        {entry.localCopies.length === 0 ? 'None captured' : entry.localCopies.map(local => (
                          <div key={`${local.gcodeFileId}-${local.capturedAtUtc}`} className="mb-2">
                            <Link to={`/files?type=gcode&file=${encodeURIComponent(local.gcodeFileId)}`} className="text-pf-accent underline">Local copy</Link>
                            <p>{observedAt(local.capturedAtUtc)} · {local.verification === 'UnverifiedSnapshot' ? 'Captured snapshot' : local.verification}</p>
                          </div>
                        ))}
                        {entry.localCopies.length > 0 && <p className="text-pf-text-secondary">Not proof of current remote byte equality.</p>}
                      </td>
                      <td className="p-3">
                        <div className="flex flex-wrap gap-2">
                          {source?.canCopy && <Button size="sm" variant="secondary" disabled={entry.availability !== 'Available'} onClick={() => selectIntent(entry, 'Copy')}>Copy locally</Button>}
                          {source?.canCopy && canQueue && <Button size="sm" disabled={entry.availability !== 'Available'} onClick={() => selectIntent(entry, 'Queue')}>Queue</Button>}
                          {source?.canDelete && <Button size="sm" variant="danger" disabled={entry.availability !== 'Available'}
                            onClick={() => setDeleteIntent({ entry, key: generateUUID() })}>Delete from printer</Button>}
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {catalog.data.items.length === 0 && <p className="p-5 text-pf-text-secondary">No matching observations. Only a complete scan establishes an empty source; never-scanned or failed sources are not proof of absence.</p>}
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
