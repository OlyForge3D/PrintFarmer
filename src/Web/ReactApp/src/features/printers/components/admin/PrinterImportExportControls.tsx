import React from 'react';
import Button from '@/common/components/ui/Button';
import ImportExportModal from '@/features/printers/components/ImportExportModal';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useQueryClient } from '@tanstack/react-query';

export default function PrinterImportExportControls() {
  const auth = useAuth();
  const queryClient = useQueryClient();
  const [openImportExportModal, setOpenImportExportModal] = React.useState(false);

  if (!auth.hasPermission('printers', 'admin')) return null;

  // Control only opens the combined import/export modal. Modal handles the rest.

  return (
    <div className="flex items-center gap-2">
      <Button onClick={() => setOpenImportExportModal(true)} variant="secondary">Import / Export</Button>

      <ImportExportModal isOpen={openImportExportModal} onClose={() => setOpenImportExportModal(false)} onComplete={() => queryClient.invalidateQueries({ queryKey: ['printers'] })} />
    </div>
  );
}
