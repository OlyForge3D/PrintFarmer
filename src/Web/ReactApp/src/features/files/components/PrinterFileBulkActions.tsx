import { useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import clsx from 'clsx';
import { Alert, Button, Checkbox } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { CopyLocalIcon, DeleteIcon, DownloadIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { generateUUID } from '@/utils/uuid';
import type { PrinterFileCatalogEntry, PrinterFileDeleteOperation } from '@/types/api';
import { describeReasonCode } from '@/features/files/utils/fileDisplay';
import { describePrinterFileFailure, type PrinterFileBulkCapabilities } from '@/features/files/utils/printerFileBulk';

export interface SelectedPrinterFile {
  entry: PrinterFileCatalogEntry;
  capabilities: PrinterFileBulkCapabilities;
}

interface PrinterFileBulkActionsProps {
  /** Selected, currently listed files that support at least one bulk action. */
  selected: SelectedPrinterFile[];
  onClearSelection: () => void;
  /** Opens a Copy operation in the catalog's resumable progress section. */
  onShowOperation: (operationId: string) => void;
  /** Opens a delete operation's recorded status in the single-file delete control. */
  onShowDeleteOperation: (operationId: string) => void;
}

type BatchKind = 'copy' | 'delete';
type ItemStatus = 'pending' | 'working' | 'requested' | 'deleted' | 'stale' | 'failed' | 'unknown';

interface BatchItem {
  entry: PrinterFileCatalogEntry;
  /** This file's own idempotency key, kept so a retry replays the same intent. */
  key: string;
  status: ItemStatus;
  message?: string;
  operationId?: string;
}

interface Batch {
  kind: BatchKind;
  phase: 'review' | 'running' | 'done';
  items: BatchItem[];
}

type ItemResult = Pick<BatchItem, 'status' | 'message' | 'operationId'>;

const ICON_CLASS = 'h-4 w-4';
const DANGER_CLASS = 'text-pf-error hover:bg-pf-error/10 hover:text-pf-error';
const UNKNOWN_OUTCOME = 'The request may have reached the printer. Check its status; do not resend.';
const STATUS_LABEL: Record<ItemStatus, string> = {
  pending: 'Waiting',
  working: 'In progress',
  requested: 'Copy requested',
  deleted: 'Deleted',
  stale: 'Stale',
  failed: 'Failed',
  unknown: 'Outcome unknown',
};
const STATUS_CLASS: Partial<Record<ItemStatus, string>> = {
  requested: 'text-pf-success',
  deleted: 'text-pf-success',
  stale: 'text-pf-warning',
  failed: 'text-pf-error',
  unknown: 'text-pf-warning',
};

function plural(count: number, noun: string) {
  return `${count} ${noun}${count === 1 ? '' : 's'}`;
}

/** "3 selected", or "2 of 3 selected" when only some selected files support the action. */
function selectionCount(eligible: number, selected: number) {
  return eligible === selected ? `${eligible} selected` : `${eligible} of ${selected} selected`;
}

function countOf(batch: Batch, status: ItemStatus) {
  return batch.items.filter(item => item.status === status).length;
}

function summarize(batch: Batch) {
  const succeeded = batch.kind === 'copy' ? `${countOf(batch, 'requested')} requested` : `${countOf(batch, 'deleted')} deleted`;
  const parts = [succeeded, `${countOf(batch, 'failed')} failed`, `${countOf(batch, 'stale')} stale`];
  const unknown = countOf(batch, 'unknown');
  if (unknown > 0) parts.push(`${unknown} outcome unknown`);
  return parts.join(', ');
}

/** One Copy intent for one exact observation, through the same command as a single Copy. */
async function copyOne(item: BatchItem): Promise<ItemResult> {
  try {
    const operation = await apiClient.copyIndexedPrinterFile(item.entry.id, item.entry.observationRevision, item.key);
    return { status: 'requested', operationId: operation.id };
  } catch (error) {
    const failure = describePrinterFileFailure(error);
    return { status: failure.stale ? 'stale' : 'failed', message: failure.message };
  }
}

/**
 * Prepares then confirms one exact observation with the same request shapes as the single-file
 * delete control. The batch acknowledgements stand in for its per-file checkboxes only.
 */
async function deleteOne(item: BatchItem): Promise<ItemResult> {
  let prepared: PrinterFileDeleteOperation;
  try {
    prepared = await apiClient.prepareIndexedPrinterFileDelete(item.entry.id, item.entry.observationRevision, item.key);
  } catch (error) {
    const failure = describePrinterFileFailure(error);
    return { status: failure.stale ? 'stale' : 'failed', message: failure.message };
  }
  if (prepared.state !== 'AwaitingDeleteConfirmation' || prepared.guarantee === 'Unsupported') {
    return {
      status: 'failed',
      operationId: prepared.id,
      message: describeReasonCode(prepared.reasonCode) ?? `This file cannot be deleted (${prepared.state}).`,
    };
  }
  try {
    const result = await apiClient.confirmPrinterFileDelete(prepared.id, {
      confirmationBinding: prepared.confirmationBinding,
      acceptIrreversibleDelete: true,
      // Sent only where the backend cannot match a file version, exactly like the single flow.
      acceptCurrentPathRace: prepared.guarantee === 'CurrentExactPath',
    });
    if (result.state === 'Deleted' && result.outcome === 'Acknowledged') {
      return { status: 'deleted', operationId: result.id };
    }
    if (result.state === 'Blocked' || result.outcome === 'Blocked') {
      return { status: 'failed', operationId: result.id, message: describeReasonCode(result.reasonCode) ?? 'Blocked.' };
    }
    return { status: 'unknown', operationId: result.id, message: UNKNOWN_OUTCOME };
  } catch (error) {
    // A failed confirmation is never resent: the request may already have reached the printer.
    return { status: 'unknown', operationId: prepared.id, message: `${describePrinterFileFailure(error).message}. ${UNKNOWN_OUTCOME}` };
  }
}

function BulkActionButton({ label, count, icon, danger = false, disabled = false, onClick }: {
  label: string;
  count: number;
  icon: ReactNode;
  danger?: boolean;
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <Button
      type="button"
      size="sm"
      variant="secondary"
      aria-label={label}
      title={label}
      className={clsx(danger && DANGER_CLASS)}
      disabled={disabled || count === 0}
      iconLeft={icon}
      onClick={onClick}
    >
      <span className="tabular-nums">{count}</span>
    </Button>
  );
}

function BatchFileList({ batch, onShowOperation }: { batch: Batch; onShowOperation: (item: BatchItem) => void }) {
  return (
    <div
      role="region"
      aria-label="Files in this batch"
      // Scrollable, so it must be reachable by keyboard.
      tabIndex={0}
      className="max-h-64 overflow-y-auto rounded-sm border border-pf-border focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent"
    >
      <ul className="divide-y divide-pf-border text-xs">
        {batch.items.map(item => (
          <li key={item.entry.id} className="flex items-start justify-between gap-3 px-3 py-2">
            <div className="min-w-0 space-y-0.5">
              <p className="break-all font-medium text-pf-text-primary">{item.entry.displayName}</p>
              <p className="text-pf-text-secondary">{item.entry.printerName}</p>
              <p className="break-all font-mono text-pf-text-secondary">{item.entry.storageRoot}/{item.entry.relativePath}</p>
              {item.message && <p className="text-pf-text-secondary">{item.message}</p>}
            </div>
            <div className="flex shrink-0 flex-col items-end gap-1">
              {item.status !== 'pending' && (
                <span className={clsx('font-medium', STATUS_CLASS[item.status])}>{STATUS_LABEL[item.status]}</span>
              )}
              {item.operationId && batch.phase === 'done' && (batch.kind === 'copy' || item.status !== 'deleted') && (
                <Button
                  type="button"
                  size="sm"
                  variant="subtle"
                  aria-label={`${batch.kind === 'copy' ? 'Show progress' : 'Show delete status'} for ${item.entry.displayName}`}
                  onClick={() => onShowOperation(item)}
                >
                  {batch.kind === 'copy' ? 'Show progress' : 'Show status'}
                </Button>
              )}
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}

/**
 * Bulk Copy to local library, Download and Delete from printer for selected printer files.
 * Every file keeps its own command, idempotency key and observation revision; nothing is
 * widened into a batch request, and every server-side guard still applies per file.
 */
export function PrinterFileBulkActions({
  selected, onClearSelection, onShowOperation, onShowDeleteOperation,
}: PrinterFileBulkActionsProps) {
  const queryClient = useQueryClient();
  const [batch, setBatch] = useState<Batch | null>(null);
  const [acceptIrreversible, setAcceptIrreversible] = useState(false);
  const [acceptRace, setAcceptRace] = useState(false);
  const [downloadStatus, setDownloadStatus] = useState('');

  const copyable = selected.filter(item => item.capabilities.copy).map(item => item.entry);
  const downloadable = selected.filter(item => item.capabilities.download).map(item => item.entry);
  const deletable = selected.filter(item => item.capabilities.delete).map(item => item.entry);
  const running = batch?.phase === 'running';
  const downloading = downloadStatus !== '';

  // The dialog lists, and acts on, the files exactly as they were listed when it opened.
  const openBatch = (kind: BatchKind, entries: PrinterFileCatalogEntry[]) => {
    setAcceptIrreversible(false);
    setAcceptRace(false);
    setBatch({ kind, phase: 'review', items: entries.map(entry => ({ entry, key: generateUUID(), status: 'pending' })) });
  };
  const closeBatch = () => {
    if (!running) setBatch(null);
  };
  const updateItem = (id: string, result: ItemResult) => setBatch(current => current && {
    ...current,
    items: current.items.map(item => item.entry.id === id
      ? { ...item, message: undefined, operationId: undefined, ...result } : item),
  });

  const runBatch = async (kind: BatchKind, items: BatchItem[]) => {
    setBatch(current => current && { ...current, phase: 'running' });
    // Sequential: one command at a time per printer, each with its own key and revision.
    for (const item of items) {
      updateItem(item.entry.id, { status: 'working' });
      updateItem(item.entry.id, kind === 'copy' ? await copyOne(item) : await deleteOne(item));
    }
    setBatch(current => current && { ...current, phase: 'done' });
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] }),
      ...(kind === 'copy'
        ? [queryClient.invalidateQueries({ queryKey: ['printer-file-operations'] }),
          queryClient.invalidateQueries({ queryKey: ['gcode-files'] })]
        : [queryClient.invalidateQueries({ queryKey: ['printer-file-delete'] })]),
    ]);
  };

  // Transfer one file at a time; the browser may still require multiple-download permission.
  const runDownloads = async (entries: PrinterFileCatalogEntry[]) => {
    const failures: string[] = [];
    let stale = false;
    for (const [index, entry] of entries.entries()) {
      setDownloadStatus(`Downloading ${index + 1} of ${entries.length}: ${entry.displayName}`);
      try {
        await apiClient.downloadPrinterFile(entry.id, entry.observationRevision, entry.displayName);
      } catch (error) {
        const failure = describePrinterFileFailure(error, { blobResponse: true });
        stale ||= failure.stale;
        failures.push(`${entry.displayName}: ${failure.message}`);
      }
    }
    setDownloadStatus('');
    if (stale) await queryClient.invalidateQueries({ queryKey: ['printer-file-catalog'] });
    const downloaded = entries.length - failures.length;
    if (failures.length === 0) toast.success(`Downloaded ${plural(downloaded, 'file')}.`);
    else toast.error(`Downloaded ${downloaded} of ${plural(entries.length, 'file')}.`, { description: failures.join('\n') });
  };

  const showOperation = (item: BatchItem) => {
    if (!batch || !item.operationId) return;
    const show = batch.kind === 'copy' ? onShowOperation : onShowDeleteOperation;
    setBatch(null);
    show(item.operationId);
  };

  const total = batch?.items.length ?? 0;
  const working = batch?.items.find(item => item.status === 'working');
  const progress = working ? `Working on ${working.entry.displayName}`
    : batch?.phase === 'done' ? summarize(batch) : '';
  const retryable = batch?.kind === 'copy' ? batch.items.filter(item => item.status === 'failed') : [];

  return (
    <>
      {selected.length > 0 && (
        <div role="group" aria-label="Selected files" className="flex flex-wrap items-center gap-2 rounded-lg border border-pf-accent/40 bg-pf-bg-1 px-3 py-2">
          <span className="text-xs font-medium text-pf-text-primary">{selected.length} selected</span>
          <BulkActionButton
            label={`Copy ${selectionCount(copyable.length, selected.length)} to local library`}
            count={copyable.length}
            icon={<CopyLocalIcon className={ICON_CLASS} />}
            disabled={running || downloading}
            onClick={() => openBatch('copy', copyable)}
          />
          <BulkActionButton
            label={`Download ${selectionCount(downloadable.length, selected.length)}`}
            count={downloadable.length}
            icon={<DownloadIcon className={ICON_CLASS} />}
            disabled={running || downloading}
            onClick={() => { void runDownloads(downloadable); }}
          />
          <BulkActionButton
            label={`Delete ${selectionCount(deletable.length, selected.length)} from printer`}
            count={deletable.length}
            icon={<DeleteIcon className={ICON_CLASS} />}
            danger
            disabled={running || downloading}
            onClick={() => openBatch('delete', deletable)}
          />
          <Button type="button" size="sm" variant="subtle" onClick={onClearSelection}>Clear selection</Button>
          <span role="status" aria-live="polite" className="text-xs text-pf-text-secondary">{downloadStatus}</span>
        </div>
      )}
      <Modal
        isOpen={batch?.kind === 'copy'}
        onClose={closeBatch}
        closeOnEscape={!running}
        title={`Copy ${plural(total, 'file')} into local library`}
        size="lg"
        footer={batch?.phase === 'done' ? (
          <>
            {retryable.length > 0 && (
              <Button type="button" variant="secondary" onClick={() => { void runBatch('copy', retryable); }}>
                Retry {plural(retryable.length, 'failed file')} with the same intents
              </Button>
            )}
            <Button type="button" onClick={closeBatch}>Close</Button>
          </>
        ) : (
          <>
            <Button type="button" variant="secondary" disabled={running} onClick={closeBatch}>Cancel</Button>
            <Button type="button" loading={running} onClick={() => { if (batch) void runBatch('copy', batch.items); }}>
              Copy {plural(total, 'file')} locally
            </Button>
          </>
        )}
      >
        {batch?.kind === 'copy' && (
          <div className="space-y-4">
            <Alert>Each file becomes its own Copy operation that creates or reuses an owned local asset. Nothing is queued or printed.</Alert>
            <BatchFileList batch={batch} onShowOperation={showOperation} />
            <p role="status" aria-live="polite" className="text-sm text-pf-text-primary">{progress}</p>
            {countOf(batch, 'stale') > 0 && batch.phase === 'done' && (
              <Alert variant="warning">Stale files changed on the printer after they were listed. The list was refreshed; select them again to copy the current version.</Alert>
            )}
          </div>
        )}
      </Modal>
      <Modal
        isOpen={batch?.kind === 'delete'}
        onClose={closeBatch}
        closeOnEscape={!running}
        title={`Delete ${plural(total, 'file')} from printers`}
        size="lg"
        footer={batch?.phase === 'done' ? (
          <Button type="button" onClick={closeBatch}>Close</Button>
        ) : (
          <>
            <Button type="button" variant="secondary" disabled={running} onClick={closeBatch}>Keep files</Button>
            <Button
              type="button"
              variant="danger"
              loading={running}
              disabled={!acceptIrreversible || !acceptRace}
              onClick={() => { if (batch) void runBatch('delete', batch.items); }}
            >
              Delete {plural(total, 'file')}
            </Button>
          </>
        )}
      >
        {batch?.kind === 'delete' && (
          <div className="space-y-4">
            <Alert variant="warning">Remote deletion is irreversible. No backup is created automatically. Local library copies, other source locations, provenance and accepted jobs are retained.</Alert>
            <p className="text-sm text-pf-text-secondary">Each file is deleted separately at its exact printer and path, bound to the observation listed here. Files that changed since they were listed are skipped. The server still requires current permission, Manage access and a confirmed idle printer before each delete.</p>
            <BatchFileList batch={batch} onShowOperation={showOperation} />
            <p role="status" aria-live="polite" className="text-sm text-pf-text-primary">{progress}</p>
            {batch.phase !== 'done' && (
              <div className="space-y-3">
                <Alert variant="warning">Some printers cannot atomically match a file version. Another client could replace a file after the last check; Delete would remove the file currently at that exact path.</Alert>
                <Checkbox
                  label={`I accept irreversible deletion of these ${plural(total, 'exact source')} without an automatic backup`}
                  checked={acceptIrreversible}
                  disabled={running}
                  onChange={event => setAcceptIrreversible(event.target.checked)}
                />
                <Checkbox
                  label={`I accept the current-path replacement race for these ${plural(total, 'delete')}`}
                  checked={acceptRace}
                  disabled={running}
                  onChange={event => setAcceptRace(event.target.checked)}
                />
              </div>
            )}
          </div>
        )}
      </Modal>
    </>
  );
}