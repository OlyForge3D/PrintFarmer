import React from 'react';
import Button from '@/common/components/ui/Button';
import { useAuth } from '@/features/auth/hooks/useAuth';
import type { Printer } from '@/types/api';

interface Props {
  selectedIds: string[];
  printersById: Record<string, Printer>;
  onDelete: (printers: Printer[]) => void;
  onBulkSetMaintenance: (printers: Printer[], inMaintenance: boolean) => void;
}

export default function PrinterBulkControls({ selectedIds, printersById, onDelete, onBulkSetMaintenance }: Props) {
  const auth = useAuth();

  if (!selectedIds || selectedIds.length === 0) return null;

  const selectedPrinters = selectedIds.map(id => printersById[id]).filter(Boolean);

  return (
    <div className="flex items-center gap-2">
      {auth.hasPermission('printers', 'delete') && (
        <Button variant="danger" size="sm" onClick={() => onDelete(selectedPrinters)}>
          Delete {selectedIds.length} Selected
        </Button>
      )}

      <Button size="sm" onClick={() => onBulkSetMaintenance(selectedPrinters, true)}>
        Mark as In Maintenance
      </Button>

      <Button size="sm" onClick={() => onBulkSetMaintenance(selectedPrinters, false)}>
        Remove Maintenance
      </Button>
    </div>
  );
}
