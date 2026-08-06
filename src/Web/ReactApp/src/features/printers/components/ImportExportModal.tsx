import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import Tabs from '@/common/components/ui/Tabs';
import { FileUpload } from '@/common/components/ui/FileUpload';
import Button from '@/common/components/ui/Button';
import Select from '@/common/components/ui/Select';
import { ProgressBar } from '@/common/components/ui/ProgressBar';
import { toast } from 'sonner';
import { getHubUrl } from '@/common/utils/apiUrlHelpers';
import { printerHubService, PrinterImportProgress } from '@/services/printerHubService';
import { FileIcon } from '@/common/components/icons/MdiIcons';
import ImportProgressTable from './ImportProgressTable';
import { apiClient } from '@/services/api';
import { useQuery, useQueryClient } from '@tanstack/react-query';

export interface ImportExportModalProps {
  isOpen: boolean;
  onClose: () => void;
  onComplete?: () => void;
}

type ImportProgressItem = PrinterImportProgress;

export default function ImportExportModal({ isOpen, onClose, onComplete }: ImportExportModalProps) {
  const queryClient = useQueryClient();
  
  // Fetch printer count directly so it updates after import
  const { data: printers } = useQuery({
    queryKey: ['printers'],
    queryFn: () => apiClient.getPrinters(),
    enabled: isOpen, // Only fetch when modal is open
  });
  const printerCount = printers?.length ?? 0;

  const [fileName, setFileName] = React.useState('');
  const [totalCount, setTotalCount] = React.useState(0);
  const [isImporting, setIsImporting] = React.useState(false);
  const [progressItems, setProgressItems] = React.useState<ImportProgressItem[]>([]);
  const [isImportComplete, setIsImportComplete] = React.useState(false);

  const [exportFormat, setExportFormat] = React.useState<'json' | 'csv'>('json');
  const [exporting, setExporting] = React.useState(false);
  const [exportProgress, setExportProgress] = React.useState<number | null>(null);

  // State for confirmation modals
  const [showCloseConfirm, setShowCloseConfirm] = useState(false);
  const [showCancelConfirm, setShowCancelConfirm] = useState(false);

  // Tracks the *current* import's SignalR progress unsubscribe and
  // completion-poll interval in refs (not local closure consts) so every
  // exit path — natural completion, Cancel, Close Anyway, unmount, or a
  // route change while an import is in flight — can tear them down. #1146
  // item 10 made this modal conditionally mounted (unmounted entirely when
  // closed) instead of always-mounted-with-`isOpen`-toggling; a `setInterval`
  // and a SignalR subscription are plain browser/service resources, not
  // tied to React's lifecycle, so without this they would keep running (and
  // the SignalR callback would keep calling `setState` on an unmounted
  // component) after the modal itself is gone.
  const importUnsubscribeRef = useRef<(() => void) | undefined>(undefined);
  const importCompletionIntervalRef = useRef<ReturnType<typeof setInterval> | undefined>(undefined);

  // `startImport`'s async prelude (clone/parse the file, then — if needed —
  // establish the SignalR connection) can straddle an unmount, or a second
  // import starting before the first one's prelude has finished. #1146
  // re-review (Hicks) flagged that those continuations could resume after
  // unmount and still register a listener/interval or call setState, and
  // that the upload/start failure path didn't always dispose what it had
  // just created.
  //
  // `mountedRef` answers "is the component still here?"; `importOperationIdRef`
  // answers "is this the import the caller still cares about?" — every
  // `startImport` call mints a fresh id and captures it locally, and the
  // counter is also bumped by `doClose`/`handleImportComplete` so an
  // explicit Close/Cancel invalidates an in-flight prelude too, not only a
  // later import. Every await inside `startImport` re-checks
  // `isCurrentImport(operationId)` before registering a resource or
  // mutating state, so a stale continuation can neither resurrect a
  // closed/cancelled import nor attach on top of (or stomp on) a newer one.
  const mountedRef = useRef(true);
  const importOperationIdRef = useRef(0);

  const isCurrentImport = useCallback(
    (operationId: number) => mountedRef.current && importOperationIdRef.current === operationId,
    [],
  );

  const disposeImportWatchers = useCallback(() => {
    if (importCompletionIntervalRef.current !== undefined) {
      clearInterval(importCompletionIntervalRef.current);
      importCompletionIntervalRef.current = undefined;
    }
    if (importUnsubscribeRef.current) {
      importUnsubscribeRef.current();
      importUnsubscribeRef.current = undefined;
    }
  }, []);

  // Belt-and-suspenders: dispose on unmount / route change even if none of
  // the explicit close/cancel handlers below ran first.
  //
  // StrictMode (development) intentionally replays every mount as
  // setup -> cleanup -> setup, synchronously, specifically to prove that
  // setup can always undo whatever the preceding cleanup did — it is not
  // an occasional dev quirk, it happens on every mount. The bug Hicks
  // found: `useRef(true)` only *seeds* mountedRef the first time this
  // component instance is created; it does not run again on each effect
  // setup. The replay's cleanup set `mountedRef.current = false`, and
  // because setup here had no body of its own, nothing ever set it back
  // to `true` — so the *real*, still-mounted component was permanently
  // stuck with `mountedRef.current === false`, and every `startImport()`
  // afterward failed `isCurrentImport()` and silently bailed out before
  // registering anything. Setup must explicitly (re)assert `true` so the
  // replay's cleanup->setup round-trip restores exactly the state a fresh
  // mount starts with, the same way it would for a plain `useState`.
  //
  // Cleanup also invalidates the current operation generation (mirroring
  // doClose()/handleImportComplete()) and disposes any watchers, so a
  // still-in-flight startImport() prelude can't resurrect after this
  // effect tears down, and the listener/interval never outlive it. Both
  // are no-ops during the StrictMode replay itself: setup above does not
  // register anything by itself (only startImport(), triggered by a real
  // user file selection, does), and that replay runs synchronously with
  // no async gap for a file-select event to land in between setup and its
  // immediate cleanup — so replay can never invalidate a genuinely active
  // later operation or leak a resource it never created.
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      importOperationIdRef.current += 1;
      disposeImportWatchers();
    };
  }, [disposeImportWatchers]);

  const countPrintersInFile = async (f: File) => {
    try {
      // Clone the file using slice() to avoid consuming the original stream
      const clone = f.slice(0, f.size, f.type);
      const text = await clone.text();
      const ext = f.name.split('.').pop()?.toLowerCase();
      if (ext === 'json') {
        const data = JSON.parse(text);
        return Array.isArray(data) ? data.length : 0;
      }
      if (ext === 'csv') {
        const lines = text.split('\n').filter(l => l.trim());
        return Math.max(0, lines.length - 1);
      }
      return 0;
    } catch {
      return 0;
    }
  };

  const startImport = async (file: File) => {
    // Mint a new operation id and tear down whatever the previous attempt
    // (if any) had registered *before* awaiting anything: a later import
    // always invalidates an earlier one, synchronously, so a stale
    // continuation can never find its own resources still attached (or a
    // fresher operation's resources within reach) once it resumes.
    const operationId = ++importOperationIdRef.current;
    disposeImportWatchers();

    const count = await countPrintersInFile(file);
    // Unmounted, or superseded by a newer startImport() call, while parsing
    // the file: nothing has been registered yet at this checkpoint, so bail
    // without touching state or resources.
    if (!isCurrentImport(operationId)) return;
    if (count === 0) {
      toast.error('No printers found in file');
      return;
    }

    setFileName(file.name);
    setTotalCount(count);
    setIsImporting(true);
    setIsImportComplete(false);
    
    // Initialize all items as Pending
    const initialItems: ImportProgressItem[] = Array.from({ length: count }, (_, i) => ({
      index: i,
      name: `Printer ${i + 1}`,
      status: 'Pending'
    }));
    setProgressItems(initialItems);

    try {
      // CRITICAL: Establish SignalR connection BEFORE calling import endpoint
      // This ensures we're ready to receive events from the moment the backend starts broadcasting
      if (!printerHubService.isConnected()) {
        if (window.PrintFarmerDebug?.import) console.log('[Import] Connecting to SignalR hub...');
        await printerHubService.start(getHubUrl(''));
        // Unmounted, or superseded by a newer import, while connecting —
        // nothing has been registered yet, so just don't attach a
        // listener/interval for a dead import.
        if (!isCurrentImport(operationId)) return;
      }

      // Subscribe to events BEFORE triggering the import
      if (window.PrintFarmerDebug?.import) console.log('[Import] Subscribing to import progress events...');
      importUnsubscribeRef.current = printerHubService.onPrinterImportProgress((progress: PrinterImportProgress) => {
        if (window.PrintFarmerDebug?.import) console.log('[Import] Received progress update:', progress);

        setProgressItems(prevItems => {
          const newItems = [...prevItems];
          const index = progress.index;
          if (index >= 0 && index < newItems.length) {
            newItems[index] = {
              index: progress.index,
              name: progress.name || newItems[index].name,
              status: progress.status || 'Pending',
              id: progress.id,
              reason: progress.reason
            };
          }
          if (window.PrintFarmerDebug?.import) console.log('[Import] Updated progress items:', newItems);
          return newItems;
        });
      });

      // Setup completion monitor
      importCompletionIntervalRef.current = setInterval(() => {
        setProgressItems(prevItems => {
          if (prevItems.length > 0 && prevItems.every(item => item.status !== 'Pending')) {
            disposeImportWatchers();
            setIsImportComplete(true);
            // Refresh printer count so Export tab shows updated count
            queryClient.invalidateQueries({ queryKey: ['printers'] });
          }
          return prevItems;
        });
      }, 500);

      // NOW that SignalR is connected and listening, trigger the import
      const form = new FormData();
      form.append('file', file);
      try {
        await apiClient.uploadPrinterImport(form);
      } catch (err: unknown) {
        const error = err instanceof Error ? err : new Error('Unknown import error');
        throw error;
      }

    } catch (err) {
      console.error('Import start failed', err);
      if (!isCurrentImport(operationId)) {
        // Either unmounted (the effect above already disposed anything this
        // attempt had registered) or superseded by a newer import (which
        // took ownership of importUnsubscribeRef/importCompletionIntervalRef
        // — and already disposed this attempt's resources — when *it*
        // started). Disposing or updating state here would tear down a
        // live operation instead of this dead one's, so there is nothing
        // left for this stale continuation to do.
        return;
      }
      // Still mounted and current: this attempt's own listener/interval (if
      // it got that far) must not outlive its failed start — preserve the
      // existing UX (surface the error, return to the file picker).
      disposeImportWatchers();
      toast.error('Failed to start import');
      setIsImporting(false);
    }
  };

  const startExport = async () => {
    setExporting(true);
    setExportProgress(0);
    try {
      await apiClient.streamExportFile(undefined, exportFormat, undefined, (loaded, total) => {
        if (total && total > 0) setExportProgress(Math.round((loaded / total) * 100));
        else setExportProgress(null);
      });
      toast.success('Export started');
      onComplete?.();
    } catch (err) {
      console.error('Export failed', err);
      toast.error('Export failed');
    } finally {
      setExporting(false);
      setExportProgress(null);
    }
  };

  const handleFileSelect = (files: FileList | null) => {
    if (files && files.length > 0) {
      startImport(files[0]);
    }
  };

  const handleCloseModal = () => {
    if (isImporting && !isImportComplete) {
      setShowCloseConfirm(true);
      return;
    }
    doClose();
  };

  const doClose = () => {
    // Invalidate any in-flight startImport() prelude so it can't resurrect
    // the import after the user has explicitly closed the modal, even
    // during the brief window before the parent actually unmounts this
    // component (unmount itself is also guarded — see mountedRef above).
    importOperationIdRef.current += 1;
    disposeImportWatchers();
    setIsImporting(false);
    setProgressItems([]);
    setIsImportComplete(false);
    setShowCloseConfirm(false);
    onClose();
  };

  const handleImportComplete = () => {
    // Same reasoning as doClose(): both Cancel and natural completion must
    // invalidate a still-pending startImport() continuation too.
    importOperationIdRef.current += 1;
    disposeImportWatchers();
    setIsImporting(false);
    setProgressItems([]);
    setIsImportComplete(false);
    onComplete?.();
    onClose();
  };

  const handleCancelImport = () => {
    if (!isImportComplete) {
      setShowCancelConfirm(true);
      return;
    }
    handleImportComplete();
  };

  const confirmCancelImport = () => {
    setShowCancelConfirm(false);
    handleImportComplete();
  };

  return (
    <Modal isOpen={isOpen} onClose={handleCloseModal} title="Import / Export Printers" width="max-w-4xl">
      <div className="space-y-4">
        <Tabs defaultTab="import">
          <Tabs.List>
            <Tabs.Tab id="import">Import</Tabs.Tab>
            <Tabs.Tab id="export">Export</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="import">
              <div className="space-y-3">
                {!isImporting ? (
                  <div className="flex flex-col items-center gap-3 py-4">
                    <p className="text-sm text-pf-text-secondary">Select a JSON or CSV file to import printers</p>
                    <FileUpload onChange={handleFileSelect} accept=".json,.csv" buttonText="Select File to Import" buttonIcon={<FileIcon className="w-4 h-4" />} buttonVariant="primary" />
                  </div>
                ) : (
                  <ImportProgressTable
                    items={progressItems}
                    fileName={fileName}
                    totalCount={totalCount}
                    isComplete={isImportComplete}
                    onCancel={handleCancelImport}
                  />
                )}
              </div>
            </Tabs.Panel>

            <Tabs.Panel id="export">
              <div className="flex flex-col items-center gap-4 py-6">
                <p className="text-sm text-pf-text-secondary">
                  Export your printer configurations to back up or share with others
                </p>
                
                <div className="flex flex-col items-center gap-2">
                  <div className="flex items-center gap-3">
                    <label className="text-sm font-medium">Format:</label>
                    <Select value={exportFormat} onChange={(e: React.ChangeEvent<HTMLSelectElement>) => setExportFormat(e.target.value as 'json' | 'csv')}>
                      <option value="json">JSON</option>
                      <option value="csv">CSV</option>
                    </Select>
                  </div>
                  <p className="text-xs text-pf-text-tertiary max-w-sm text-center">
                    {exportFormat === 'json' 
                      ? 'JSON preserves all settings and is best for backups or importing into PrintFarmer'
                      : 'CSV is spreadsheet-friendly and easy to edit in Excel or Google Sheets'}
                  </p>
                </div>

                {printerCount > 0 ? (
                  <p className="text-sm text-pf-text-secondary">
                    {printerCount} printer{printerCount !== 1 ? 's' : ''} will be exported
                  </p>
                ) : (
                  <p className="text-sm text-pf-text-tertiary">No printers to export</p>
                )}

                <Button onClick={startExport} disabled={exporting || printerCount === 0} variant="primary">
                  {exporting ? 'Exporting…' : 'Export Printers'}
                </Button>

                {exportProgress !== null && (
                  <div className="w-full max-w-xs">
                    <ProgressBar
                      value={exportProgress ?? 0}
                      ariaLabel="Export progress"
                      showPercent={false}
                    />
                    <div className="text-xs text-pf-text-tertiary mt-1 text-center">{typeof exportProgress === 'number' ? `${exportProgress}%` : 'Downloading...'}</div>
                  </div>
                )}
              </div>
            </Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      </div>

      {/* Close during import confirmation */}
      <ConfirmationModal
        isOpen={showCloseConfirm}
        title="Close During Import?"
        message="Import is in progress. Are you sure you want to close? The import may continue in the background."
        confirmButtonText="Close Anyway"
        cancelButtonText="Continue Importing"
        isDangerous
        onConfirm={doClose}
        onCancel={() => setShowCloseConfirm(false)}
      />

      {/* Cancel import confirmation */}
      <ConfirmationModal
        isOpen={showCancelConfirm}
        title="Cancel Import?"
        message="Are you sure you want to cancel the import in progress?"
        confirmButtonText="Cancel Import"
        cancelButtonText="Continue"
        isDangerous
        onConfirm={confirmCancelImport}
        onCancel={() => setShowCancelConfirm(false)}
      />
    </Modal>
  );
}
