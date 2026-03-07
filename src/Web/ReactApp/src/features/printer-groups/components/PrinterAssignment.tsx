import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Badge, Select, FormField } from '@/common/components/ui';
import { PlusIcon, DeleteIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { useState } from 'react';
import { usePrinters } from '@/common/hooks/useApi';
import type { PrinterGroupPrinter } from '@/types/api';
import { PrinterBackend } from '@/types/api';

interface PrinterAssignmentProps {
  groupId: string;
  assignedPrinters: PrinterGroupPrinter[];
}

function getBackendLabel(backend: PrinterBackend | number): string {
  if (typeof backend === 'number') {
    return PrinterBackend[backend] || 'Unknown';
  }
  return backend;
}

export function PrinterAssignment({ groupId, assignedPrinters }: PrinterAssignmentProps) {
  const queryClient = useQueryClient();
  const [selectedPrinterId, setSelectedPrinterId] = useState('');

  // Get all printers to show available ones
  const { data: allPrinters = [] } = usePrinters();

  // Filter out already assigned printers
  const availablePrinters = allPrinters.filter(
    (p) => !assignedPrinters.some((ap) => ap.id === p.id)
  );

  const assignMutation = useMutation({
    mutationFn: ({ groupId, printerId }: { groupId: string; printerId: string }) =>
      apiClient.assignPrinterToGroup(groupId, printerId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['printer-groups'] });
      queryClient.invalidateQueries({ queryKey: ['printer-groups', groupId] });
      toast.success('Printer assigned to group');
      setSelectedPrinterId('');
    },
    onError: (error: { message?: string; details?: string }) => {
      toast.error(`Failed to assign printer: ${error.details || error.message || 'Unknown error'}`);
    },
  });

  const removeMutation = useMutation({
    mutationFn: ({ groupId, printerId }: { groupId: string; printerId: string }) =>
      apiClient.removePrinterFromGroup(groupId, printerId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['printer-groups'] });
      queryClient.invalidateQueries({ queryKey: ['printer-groups', groupId] });
      toast.success('Printer removed from group');
    },
    onError: (error: { message?: string; details?: string }) => {
      toast.error(`Failed to remove printer: ${error.details || error.message || 'Unknown error'}`);
    },
  });

  const handleAssign = () => {
    if (!selectedPrinterId) {
      toast.error('Please select a printer');
      return;
    }
    assignMutation.mutate({ groupId, printerId: selectedPrinterId });
  };

  const handleRemove = (printerId: string) => {
    removeMutation.mutate({ groupId, printerId });
  };

  return (
    <div className="space-y-4">
      <div className="flex gap-3">
        <FormField label="Add Printer" htmlFor="printer-select" className="flex-1">
          <Select
            id="printer-select"
            value={selectedPrinterId}
            onChange={(e) => setSelectedPrinterId(e.target.value)}
            disabled={availablePrinters.length === 0 || assignMutation.isPending}
          >
            <option value="">
              {availablePrinters.length === 0 ? 'No available printers' : 'Select a printer'}
            </option>
            {availablePrinters.map((printer) => (
              <option key={printer.id} value={printer.id}>
                {printer.name} ({getBackendLabel(printer.backend)})
              </option>
            ))}
          </Select>
        </FormField>
        <div className="flex items-end">
          <Button
            variant="primary"
            onClick={handleAssign}
            disabled={!selectedPrinterId || assignMutation.isPending}
            loading={assignMutation.isPending}
            iconLeft={<PlusIcon />}
          >
            Assign
          </Button>
        </div>
      </div>

      <div className="space-y-2">
        <h4 className="text-sm font-medium text-pf-text-secondary">Assigned Printers</h4>
        {assignedPrinters.length === 0 ? (
          <p className="text-sm text-pf-text-tertiary">No printers assigned to this group</p>
        ) : (
          <div className="space-y-2">
            {assignedPrinters.map((printer) => (
              <div
                key={printer.id}
                className="flex items-center justify-between p-3 bg-pf-bg-1 rounded-lg border border-pf-border"
              >
                <div className="flex items-center gap-3">
                  <span className="text-sm font-medium text-pf-text-primary">{printer.name}</span>
                  <Badge variant="default" size="sm">
                    {getBackendLabel(printer.backend)}
                  </Badge>
                  {printer.inMaintenance && (
                    <Badge variant="warning" size="sm">
                      Maintenance
                    </Badge>
                  )}
                  {!printer.isAvailable && !printer.inMaintenance && (
                    <Badge variant="error" size="sm">
                      Offline
                    </Badge>
                  )}
                </div>
                <Button
                  variant="danger"
                  size="sm"
                  onClick={() => handleRemove(printer.id)}
                  disabled={removeMutation.isPending}
                  iconLeft={<DeleteIcon />}
                >
                  Remove
                </Button>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
