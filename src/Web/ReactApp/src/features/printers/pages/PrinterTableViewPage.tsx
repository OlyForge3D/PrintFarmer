import { useState } from 'react';
import { usePrinters } from '@/common/hooks/useApi';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { apiClient } from '@/services/api';
import { PrinterTableView } from '@/features/printers/components/PrinterTableView';
import { AddPrinterButton } from '@/features/printers/components/AddPrinterButton';
import { DeleteConfirmationModal } from '@/common/components/modals/DeleteConfirmationModal';
import type { Printer } from '@/types/api';
import { EditPrinterModal } from '@/features/printers/components/EditPrinterModal';
import { Button, Alert } from '@/common/components/ui';
import { PrinterMaintenanceActionsModal } from '@/features/maintenance/components/PrinterMaintenanceActionsModal';

export function PrinterTableViewPage() {
  const { hasPermission } = useAuth();
  const { data: printers, isLoading, error, refetch } = usePrinters();
  
  const [deleteModalOpen, setDeleteModalOpen] = useState(false);
  const [printersToDelete, setPrintersToDelete] = useState<Printer[]>([]);
  const [editPrinterId, setEditPrinterId] = useState<string | null>(null);
  const [showEditModal, setShowEditModal] = useState(false);
  const [maintenancePrinter, setMaintenancePrinter] = useState<Printer | null>(null);

  // Handler functions
  const handleEditPrinter = (printer: Printer) => {
    setEditPrinterId(printer.id);
    setShowEditModal(true);
  };

  const handleDeletePrinters = (printers: Printer[]) => {
    setPrintersToDelete(printers);
    setDeleteModalOpen(true);
  };

  const handleConfirmDelete = async () => {
    try {
      // Delete all selected printers
      await Promise.all(printersToDelete.map(printer => 
        apiClient.deletePrinter(printer.id)
      ));
      
      // Refresh the printer list
      refetch();
      
      // Close the modal
      setDeleteModalOpen(false);
      setPrintersToDelete([]);
    } catch (error) {
      console.error('Failed to delete printers:', error);
      // TODO: Show error toast
    }
  };


  const handleBulkSetMaintenance = async (printers: Printer[], inMaintenance: boolean) => {
    try {
      await Promise.all(printers.map(printer => 
        apiClient.setPrinterMaintenance(printer.id, inMaintenance)
      ));
      refetch();
    } catch (error) {
      console.error('Failed to update maintenance status:', error);
    }
  };

  const handleOpenMaintenance = (printer: Printer) => {
    setMaintenancePrinter(printer);
  };

  if (isLoading) {
    return (
      <div className="space-y-6">
        <div className="flex justify-between items-center">
          <div className="space-y-2">
            <div className="h-8 w-32 bg-gray-200 rounded-sm animate-pulse"></div>
            <div className="h-4 w-64 bg-gray-200 rounded-sm animate-pulse"></div>
          </div>
          <div className="h-10 w-32 bg-gray-200 rounded-sm animate-pulse"></div>
        </div>
        <div className="bg-white shadow-sm rounded-lg p-6">
          <div className="animate-pulse space-y-4">
            {[...Array(5)].map((_, i) => (
              <div key={i} className="h-16 bg-gray-200 rounded-sm"></div>
            ))}
          </div>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="space-y-6">
        <Alert type="error" title="Failed to load printers">
          <div className="space-y-3">
            <p>{error.message}</p>
            <Button variant="secondary" onClick={() => refetch()}>
              Try again
            </Button>
          </div>
        </Alert>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold text-pf-text-primary font-bebas uppercase">Printers</h1>
          <p className="mt-1 text-sm text-pf-text-secondary">
            Manage your 3D printer fleet with bulk operations
          </p>
        </div>
        
        <div className="flex flex-col sm:flex-row gap-3">
          {hasPermission('printers', 'create') && (
            <AddPrinterButton onSuccess={() => refetch()} />
          )}
        </div>
      </div>

      {/* Table View */}
      {printers && printers.length > 0 ? (
        <PrinterTableView
          printers={printers}
          onEdit={handleEditPrinter}
          onDelete={handleDeletePrinters}
          onBulkSetMaintenance={handleBulkSetMaintenance}
          onOpenMaintenance={handleOpenMaintenance}
        />
      ) : (
        <div className="text-center py-12">
          <svg
            className="mx-auto h-12 w-12 text-gray-400"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            aria-hidden="true"
          >
            <path
              vectorEffect="non-scaling-stroke"
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth={2}
              d="M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z"
            />
          </svg>
          <h3 className="mt-2 text-sm font-medium text-gray-900">No printers</h3>
          <p className="mt-1 text-sm text-gray-500">Get started by adding your first 3D printer.</p>
          {hasPermission('printers', 'create') && (
            <div className="mt-6">
              <AddPrinterButton onSuccess={() => refetch()} />
            </div>
          )}
        </div>
      )}

      {/* Modals */}
      <DeleteConfirmationModal
        isOpen={deleteModalOpen}
        printers={printersToDelete}
        onConfirm={handleConfirmDelete}
        onCancel={() => setDeleteModalOpen(false)}
      />

      <EditPrinterModal
        printerId={editPrinterId}
        isOpen={showEditModal}
        onClose={() => setShowEditModal(false)}
        onSuccess={() => {
          setShowEditModal(false);
          refetch();
        }}
      />

      {maintenancePrinter && (
        <PrinterMaintenanceActionsModal
          isOpen={true}
          printer={maintenancePrinter}
          onClose={() => setMaintenancePrinter(null)}
        />
      )}
    </div>
  );
}
