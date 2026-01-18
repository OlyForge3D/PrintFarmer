import React, { useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import Tabs from '@/common/components/ui/Tabs';
import { FileUpload } from '@/common/components/ui/FileUpload';
import Button from '@/common/components/ui/Button';
import Select from '@/common/components/ui/Select';
import { toast } from 'sonner';
import { getHubUrl } from '@/common/utils/apiUrlHelpers';
import { printerHubService, PrinterImportProgress } from '@/services/printerHubService';
import ImportProgressTable from './ImportProgressTable';
import { apiClient } from '@/services/api';

interface ImportExportModalProps {
  isOpen: boolean;
  onClose: () => void;
  onComplete?: () => void;
}

type ImportProgressItem = PrinterImportProgress;

export default function ImportExportModal({ isOpen, onClose, onComplete }: ImportExportModalProps) {
  const [selectedFile, setSelectedFile] = React.useState<File | null>(null);
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

  const countPrintersInFile = async (f: File) => {
    try {
      const text = await f.text();
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

  const startImport = async () => {
    if (!selectedFile) return toast.error('Select a file to import');
    const count = await countPrintersInFile(selectedFile);
    if (count === 0) return toast.error('No printers found in file');
    
    setFileName(selectedFile.name);
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
      }

      // Subscribe to events BEFORE triggering the import
      if (window.PrintFarmerDebug?.import) console.log('[Import] Subscribing to import progress events...');
      const unsubscribe = printerHubService.onPrinterImportProgress((progress: PrinterImportProgress) => {
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
      const checkCompletion = setInterval(() => {
        setProgressItems(prevItems => {
          if (prevItems.length > 0 && prevItems.every(item => item.status !== 'Pending')) {
            clearInterval(checkCompletion);
            unsubscribe();
            setIsImportComplete(true);
          }
          return prevItems;
        });
      }, 500);

      // NOW that SignalR is connected and listening, trigger the import
      const form = new FormData();
      form.append('file', selectedFile);
      try {
        await apiClient.uploadPrinterImport(form);
      } catch (err: unknown) {
        const error = err instanceof Error ? err : new Error('Unknown import error');
        throw error;
      }

    } catch (err) {
      console.error('Import start failed', err);
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

  const handleCloseModal = () => {
    if (isImporting && !isImportComplete) {
      setShowCloseConfirm(true);
      return;
    }
    doClose();
  };

  const doClose = () => {
    setSelectedFile(null);
    setIsImporting(false);
    setProgressItems([]);
    setIsImportComplete(false);
    setShowCloseConfirm(false);
    onClose();
  };

  const handleImportComplete = () => {
    setSelectedFile(null);
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
                  <>
                    <FileUpload onChange={(files) => setSelectedFile(files && files.length > 0 ? files[0] : null)} accept=".json,.csv" buttonText={selectedFile ? `Selected: ${selectedFile.name}` : 'Select file'} buttonVariant="primary" />
                    <div className="flex justify-end gap-2">
                      <Button onClick={startImport} disabled={!selectedFile} size="sm">Start Import</Button>
                    </div>
                  </>
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
              <div className="space-y-3">
                <div className="flex items-center gap-3">
                  <label className="text-sm">Format:</label>
                  <Select value={exportFormat} onChange={(e: React.ChangeEvent<HTMLSelectElement>) => setExportFormat(e.target.value as 'json' | 'csv')}>
                    <option value="json">JSON</option>
                    <option value="csv">CSV</option>
                  </Select>
                </div>
                <div className="flex justify-end gap-2">
                  <Button onClick={startExport} disabled={exporting} size="sm">{exporting ? 'Exporting…' : 'Export Printers'}</Button>
                </div>
                {exportProgress !== null && (
                  <div className="mt-2">
                    <div className="w-full bg-pf-bg-2 rounded-full h-2">
                      <div className="bg-pf-accent h-2 rounded-full" style={{ width: `${Math.max(0, Math.min(100, exportProgress ?? 0))}%` }} />
                    </div>
                    <div className="text-xs text-pf-text-tertiary mt-1">{typeof exportProgress === 'number' ? `${exportProgress}%` : 'Downloading...'}</div>
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
