import { useState } from 'react';
import { usePrinters } from '@/hooks/useApi';
import { useAuth } from '@/contexts/AuthHooks';
import { PrinterTableView } from '@/components/PrinterTableView';
import { AddPrinterButton } from '@/components/AddPrinterButton';
import { PrinterDiscoveryModal } from '@/components/PrinterDiscoveryModal';
import { DeleteConfirmationModal } from '@/components/DeleteConfirmationModal';
import type { Printer } from '@/types/api';
import { Search } from 'lucide-react';
import { EditPrinterModal } from '@/components/EditPrinterModal';

export function PrinterTableViewPage() {
  const { hasPermission } = useAuth();
  const { data: printers, isLoading, error, refetch } = usePrinters();
  
  const [showDiscovery, setShowDiscovery] = useState(false);
  const [deleteModalOpen, setDeleteModalOpen] = useState(false);
  const [printersToDelete, setPrintersToDelete] = useState<Printer[]>([]);
  const [editPrinterId, setEditPrinterId] = useState<string | null>(null);
  const [showEditModal, setShowEditModal] = useState(false);

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
        fetch(`/api/printers/${printer.id}`, { method: 'DELETE' })
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
        fetch(`/api/printers/${printer.id}/maintenance`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(inMaintenance)
        })
      ));
      refetch();
    } catch (error) {
      console.error('Failed to update maintenance status:', error);
    }
  };

  if (isLoading) {
    return (
      <div className="space-y-6">
        <div className="flex justify-between items-center">
          <div className="space-y-2">
            <div className="h-8 w-32 bg-gray-200 rounded animate-pulse"></div>
            <div className="h-4 w-64 bg-gray-200 rounded animate-pulse"></div>
          </div>
          <div className="h-10 w-32 bg-gray-200 rounded animate-pulse"></div>
        </div>
        <div className="bg-white shadow rounded-lg p-6">
          <div className="animate-pulse space-y-4">
            {[...Array(5)].map((_, i) => (
              <div key={i} className="h-16 bg-gray-200 rounded"></div>
            ))}
          </div>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="space-y-6">
        <div className="bg-red-50 border border-red-200 rounded-md p-4">
          <div className="flex">
            <div className="ml-3">
              <h3 className="text-sm font-medium text-red-800">
                Failed to load printers
              </h3>
              <div className="mt-2 text-sm text-red-700">
                <p>{error.message}</p>
              </div>
              <div className="mt-4">
                <button
                  onClick={() => refetch()}
                  className="bg-red-100 hover:bg-red-200 text-red-800 font-medium py-2 px-4 rounded text-sm transition-colors"
                >
                  Try again
                </button>
              </div>
            </div>
          </div>
        </div>
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
            <>
              <button
                onClick={() => setShowDiscovery(true)}
                className="inline-flex items-center px-4 py-2 border border-pf-border-light shadow-sm text-sm font-medium rounded-md text-pf-text-primary bg-pf-panel hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-accent-2"
              >
                <Search className="h-4 w-4 mr-2" />
                Discover Printers
              </button>
              <AddPrinterButton onSuccess={() => refetch()} />
            </>
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
      <PrinterDiscoveryModal
        isOpen={showDiscovery}
        onClose={() => setShowDiscovery(false)}
        onSuccess={() => {
          setShowDiscovery(false);
          refetch();
        }}
      />

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
    </div>
  );
}
