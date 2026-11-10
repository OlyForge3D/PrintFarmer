import Button from '@/common/components/ui/Button';
import { useAuth } from '@/features/auth/hooks/useAuth';
import type { Printer } from '@/types/api';
import { usePrinterMonitoring } from '@/features/printers/hooks/usePrinterMonitoring';

interface Props {
  selectedIds: string[];
  printersById: Record<string, Printer>;
  onDelete: (printers: Printer[]) => void;
  onBulkSetMaintenance: (printers: Printer[], inMaintenance: boolean) => void;
}

export default function PrinterBulkControls({ selectedIds, printersById, onDelete, onBulkSetMaintenance }: Props) {
  const auth = useAuth();
  const monitoring = usePrinterMonitoring();

  if (!selectedIds || selectedIds.length === 0) return null;

  const selectedPrinters = selectedIds.map(id => printersById[id]).filter(Boolean);

  return (
    <div className="flex flex-wrap items-center gap-2">
      {auth.hasPermission('printers', 'admin') && (
        <>
          <Button size="sm" variant="secondary" disabled={monitoring.isPending}
            onClick={() => monitoring.mutate({ printers: selectedPrinters, expectedOffline: true })}>
            Mark expected offline
          </Button>
          <Button size="sm" variant="secondary" disabled={monitoring.isPending}
            onClick={() => monitoring.mutate({ printers: selectedPrinters, expectedOffline: false })}>
            Expect online
          </Button>
        </>
      )}
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
