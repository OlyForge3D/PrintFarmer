import React from 'react';
import Button from '@/common/components/ui/Button';
import { FileUpload } from '@/common/components/ui/FileUpload';
import ImportProgressModal from '@/features/printers/components/ImportProgressModal';
import { useAuth } from '@/features/auth/hooks/useAuth';

export default function PrinterImportControls() {
  const [file, setFile] = React.useState<File | null>(null);
  const [openProgress, setOpenProgress] = React.useState(false);
  const auth = useAuth();

  if (!auth.hasPermission('printers', 'admin')) return null;

  function handleUpload() {
    if (!file) return;
    setOpenProgress(true);
  }

  return (
    <div className="flex items-center gap-2">
      <FileUpload
        onChange={(f) => setFile(f ?? null)}
        accept=".json,.csv"
        label="Import printers"
        ariaLabel="Import printers file"
      />
      <Button onClick={handleUpload} disabled={!file} size="sm">
        Start Import
      </Button>
      <ImportProgressModal open={openProgress} onClose={() => setOpenProgress(false)} file={file} />
    </div>
  );
}
