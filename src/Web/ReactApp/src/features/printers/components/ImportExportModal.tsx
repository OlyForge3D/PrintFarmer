import React from 'react';
import { Modal } from '@/common/components/modals/Modal';
import Tabs from '@/common/components/ui/Tabs';
import { FileUpload } from '@/common/components/ui/FileUpload';
import Button from '@/common/components/ui/Button';
import Select from '@/common/components/ui/Select';
import { toast } from 'sonner';
import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';
import ImportProgressModal from '@/features/printers/components/ImportProgressModal';
import { apiClient } from '@/services/api';

interface ImportExportModalProps {
  isOpen: boolean;
  onClose: () => void;
  onComplete?: () => void;
}

export default function ImportExportModal({ isOpen, onClose, onComplete }: ImportExportModalProps) {
  const [selectedFile, setSelectedFile] = React.useState<File | null>(null);
  const [openImportProgress, setOpenImportProgress] = React.useState(false);
  const [fileName, setFileName] = React.useState('');
  const [totalCount, setTotalCount] = React.useState(0);

  const [exportFormat, setExportFormat] = React.useState<'json' | 'csv'>('json');
  const [exporting, setExporting] = React.useState(false);
  const [exportProgress, setExportProgress] = React.useState<number | null>(null);

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

    const form = new FormData();
    form.append('file', selectedFile);
    try {
      const resp = await fetch(`${getApiBaseUrl()}/printers/import`, {
        method: 'POST',
        headers: getAuthHeaders(),
        body: form
      });
      if (!resp.ok) {
        const t = await resp.text().catch(() => 'Unknown');
        throw new Error(t || `HTTP ${resp.status}`);
      }
      setOpenImportProgress(true);
    } catch (err) {
      console.error('Import start failed', err);
      toast.error('Failed to start import');
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

  return (
    <>
      <Modal isOpen={isOpen} onClose={onClose} title="Import / Export Printers" width="max-w-3xl">
        <div className="space-y-4">
          <Tabs defaultTab="import">
            <Tabs.List>
              <Tabs.Tab id="import">Import</Tabs.Tab>
              <Tabs.Tab id="export">Export</Tabs.Tab>
            </Tabs.List>
            <Tabs.Panels>
              <Tabs.Panel id="import">
              <div className="space-y-3">
                <FileUpload onChange={(files) => setSelectedFile(files && files.length > 0 ? files[0] : null)} accept=".json,.csv" buttonText={selectedFile ? `Selected: ${selectedFile.name}` : 'Select file'} buttonVariant="primary" />
                <div className="flex justify-end gap-2">
                  <Button onClick={startImport} disabled={!selectedFile} size="sm">Start Import</Button>
                </div>
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
      </Modal>

      <ImportProgressModal isOpen={openImportProgress} onClose={() => { setOpenImportProgress(false); onComplete?.(); }} fileName={fileName} totalCount={totalCount} />
    </>
  );
}
