import React, { Suspense } from 'react';
import Button from '@/common/components/ui/Button';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useQueryClient } from '@tanstack/react-query';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import type { ImportExportModalProps } from '@/features/printers/components/ImportExportModal';

// Interaction-only: this modal is never needed until an admin clicks
// "Import / Export", so it's lazy-loaded out of the printers page bundle
// (#1146 item 10).
const ImportExportModal = lazyWithPreload<ImportExportModalProps, React.FC<ImportExportModalProps>>(
  () => import('@/features/printers/components/ImportExportModal').then(m => ({ default: m.default }))
);

export default function PrinterImportExportControls() {
  const auth = useAuth();
  const queryClient = useQueryClient();
  const [openImportExportModal, setOpenImportExportModal] = React.useState(false);

  if (!auth.hasPermission('printers', 'admin')) return null;

  // Control only opens the combined import/export modal. Modal handles the rest.

  return (
    <div className="flex items-center gap-2">
      <Button
        onClick={() => setOpenImportExportModal(true)}
        onMouseEnter={() => ImportExportModal.preload()}
        onFocus={() => ImportExportModal.preload()}
        variant="secondary"
      >
        Import / Export
      </Button>

      {openImportExportModal && (
        <Suspense fallback={null}>
          <ImportExportModal
            isOpen={openImportExportModal}
            onClose={() => setOpenImportExportModal(false)}
            onComplete={() => queryClient.invalidateQueries({ queryKey: ['printers'] })}
          />
        </Suspense>
      )}
    </div>
  );
}
