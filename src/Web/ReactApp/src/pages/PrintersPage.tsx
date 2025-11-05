import React, { useMemo, useState } from 'react';
import { usePrintersWithCameraUrls, useDeletePrinter } from '@/hooks/useApi';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { useAuth } from '@/contexts/AuthHooks';
import { ExpandablePrinterCard } from '@/components/ExpandablePrinterCard';
import { PrinterTableView } from '@/components/PrinterTableView';
import { EditPrinterModal } from '@/components/EditPrinterModal';
import { AddPrinterButton } from '@/components/AddPrinterButton';
import { PrinterDiscoveryModal } from '@/components/PrinterDiscoveryModal';
import { DeleteConfirmationModal } from '@/components/DeleteConfirmationModal';
import { PrinterCardSkeleton } from '@/components/skeletons/PrinterCardSkeleton';
import { PageTemplate } from '@/components/PageTemplate';
import type { Printer } from '@/types/api';

import { Printer as PrinterIcon, Search, LayoutGrid, List } from 'lucide-react';


type PrinterStateFilter = 'all' | 'online' | 'printing' | 'paused' | 'offline';
type BackendFilter = 'all' | 'Moonraker' | 'PrusaLink' | 'SDCP' | 'OctoPrint';
type ViewMode = 'cards' | 'table';




export function PrintersPage() {
  const { hasPermission } = useAuth();
  const { 
    data: printers, 
    isLoading, 
    error,
    refetch: refetchPrinters
  } = usePrintersWithCameraUrls();
  const deletePrinterMutation = useDeletePrinter();
  const [viewMode, setViewMode] = useState<ViewMode>('cards');
  const [showDiscovery, setShowDiscovery] = useState(false);
  const [editPrinterId, setEditPrinterId] = useState<string | null>(null);
  const [showEditModal, setShowEditModal] = useState(false);
  const [deleteConfirmation, setDeleteConfirmation] = useState<{
    isOpen: boolean;
    printers: Printer[];
  }>({ isOpen: false, printers: [] });

  // Filter state
  const [stateFilter, setStateFilter] = useState<PrinterStateFilter>('all');
  const [backendFilter, setBackendFilter] = useState<BackendFilter>('all');

  // Filter printers for the current user (for now show all printers since userId isn't on Printer)
  const userPrinters = useMemo(() => {
    let filtered = printers || [];
    // State filter
    if (stateFilter !== 'all') {
      filtered = filtered.filter(p => {
        const state = (p.state || '').toLowerCase();
        if (stateFilter === 'online') return p.isOnline;
        if (stateFilter === 'printing') return state.includes('printing');
        if (stateFilter === 'paused') return state.includes('paused');
        if (stateFilter === 'offline') return !p.isOnline;
        return true;
      });
    }
    // Backend filter
    if (backendFilter !== 'all') {
      filtered = filtered.filter(p => {
        let backendName = '';
        if (typeof p.backend === 'string') {
          backendName = p.backend;
        } else if (typeof p.backend === 'number') {
          switch (p.backend) {
            case 0: backendName = 'Moonraker'; break;
            case 1: backendName = 'PrusaLink'; break;
            case 2: backendName = 'SDCP'; break;
            case 3: backendName = 'OctoPrint'; break;
            default: backendName = '';
          }
        }
        return backendName === backendFilter;
      });
    }
    return filtered;
  }, [printers, stateFilter, backendFilter]);



  const handleDeleteClick = (printers: Printer[]) => {
    setDeleteConfirmation({ isOpen: true, printers });
  };

  const handleDeleteSinglePrinter = (printer: Printer) => {
    setDeleteConfirmation({ isOpen: true, printers: [printer] });
  };

  const handleDeleteConfirm = async () => {
    try {
      await Promise.all(deleteConfirmation.printers.map(printer => 
        deletePrinterMutation.mutateAsync(printer.id)
      ));
      setDeleteConfirmation({ isOpen: false, printers: [] });
    } catch (error) {
      console.error('Failed to delete printers:', error);
    }
  };

  const handleDeleteCancel = () => {
    setDeleteConfirmation({ isOpen: false, printers: [] });
  };

  const handleEditPrinter = (printer: Printer) => {
    setEditPrinterId(printer.id);
    setShowEditModal(true);
  };


  const handleBulkSetMaintenance = async (printers: Printer[], inMaintenance: boolean) => {
    try {
      // TODO: Implement maintenance status API calls
      await Promise.all(printers.map(printer => 
        fetch(`${getApiBaseUrl()}/printers/${printer.id}/maintenance`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
          body: JSON.stringify({ inMaintenance })
        })
      ));
      refetchPrinters();
    } catch (error) {
      console.error('Failed to update maintenance status:', error);
    }
  };

  if (isLoading) {
    return (
      <div className="min-h-screen bg-pf-bg-2 pt-20 pb-8">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8" role="status" aria-busy="true">
          <div className="h-8 w-48 bg-pf-bg-1 rounded mb-6 animate-pulse" />
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4 mb-8">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="h-24 bg-pf-bg-1 rounded-xl animate-pulse" />
            ))}
          </div>
          <div className="grid grid-cols-1 lg:grid-cols-2 xl:grid-cols-3 gap-6">
            {Array.from({ length: 6 }).map((_, i) => (
              <PrinterCardSkeleton key={i} />
            ))}
          </div>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="min-h-screen bg-pf-bg-2 pt-20 pb-8">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center">
            <h2 className="text-2xl font-bold text-pf-text-primary mb-4">Error Loading Printers</h2>
            <p className="text-pf-text-secondary mb-4">{error.message}</p>
            <button
              type="button"
              onClick={() => refetchPrinters()}
              className="px-4 py-2 bg-pf-primary-500 text-white rounded-lg hover:bg-pf-primary-600 transition-colors"
            >
              Retry
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <PageTemplate
      title="Printers"
      subtitle={viewMode === 'cards' ? 'Monitor and manage your 3D printers' : 'Manage your 3D printer fleet with bulk operations'}
      icon={PrinterIcon}
      maxWidth="max-w-7xl"
    >
      <div className="flex flex-col md:flex-row md:items-center md:justify-between mb-8 gap-4">
        <div></div>
        <div className="flex flex-col md:flex-row md:items-center gap-3">
          {/* State Filter */}
          <select
            value={stateFilter}
            onChange={e => setStateFilter(e.target.value as PrinterStateFilter)}
            className="border border-pf-border rounded-lg px-3 py-1.5 text-sm bg-pf-bg-1 text-pf-text-primary"
            aria-label="Filter by printer state"
          >
            <option value="all">All States</option>
            <option value="online">Online</option>
            <option value="printing">Printing</option>
            <option value="paused">Paused</option>
            <option value="offline">Offline</option>
          </select>
          {/* Backend Filter */}
          <select
            value={backendFilter}
            onChange={e => setBackendFilter(e.target.value as BackendFilter)}
            className="border border-pf-border rounded-lg px-3 py-1.5 text-sm bg-pf-bg-1 text-pf-text-primary"
            aria-label="Filter by backend"
          >
            <option value="all">All Backends</option>
            <option value="Moonraker">Moonraker</option>
            <option value="PrusaLink">PrusaLink</option>
            <option value="SDCP">SDCP</option>
            <option value="OctoPrint">OctoPrint</option>
          </select>
          {/* View Mode Toggle */}
          <div className="flex items-center bg-pf-bg-1 border border-pf-border rounded-lg p-1">
            <button
              type="button"
              onClick={() => setViewMode('cards')}
              className={`flex items-center space-x-2 px-3 py-1.5 rounded-md text-sm font-medium transition-colors ${
                viewMode === 'cards'
                  ? 'bg-pf-accent text-white shadow-sm'
                  : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
              }`}
              title="Card View"
            >
              <LayoutGrid className="h-4 w-4" />
              <span className="hidden sm:inline">Cards</span>
            </button>
            <button
              type="button"
              onClick={() => setViewMode('table')}
              className={`flex items-center space-x-2 px-3 py-1.5 rounded-md text-sm font-medium transition-colors ${
                viewMode === 'table'
                  ? 'bg-pf-accent text-white shadow-sm'
                  : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
              }`}
              title="Table View"
            >
              <List className="h-4 w-4" />
              <span className="hidden sm:inline">Table</span>
            </button>
          </div>
          {hasPermission('printers', 'create') && (
            <>
              <button
                type="button"
                onClick={() => {
                  setShowDiscovery(true);
                }}
                className="flex items-center space-x-2 px-4 py-2 bg-pf-bg-1 border border-pf-border text-pf-text-primary rounded-lg hover:bg-pf-bg-2 transition-colors"
              >
                <Search className="h-4 w-4" />
                <span>Discover</span>
              </button>
              <AddPrinterButton onSuccess={refetchPrinters} />
            </>
          )}
        </div>
      </div>



        {/* Content Area */}
        <div className="space-y-6">
          {userPrinters.length === 0 ? (
            <div className="text-center py-12">
              <PrinterIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
              <h3 className="text-xl font-semibold text-pf-text-primary mb-2">No Printers Found</h3>
              <p className="text-pf-text-secondary mb-6">Get started by adding your first 3D printer.</p>
              <div className="flex justify-center space-x-4">
                {hasPermission('printers', 'create') && (
                  <>
                    <AddPrinterButton onSuccess={refetchPrinters} />
                    <button
                      type="button"
                      onClick={() => setShowDiscovery(true)}
                      className="flex items-center space-x-2 px-4 py-2 bg-pf-bg-1 border border-pf-border text-pf-text-primary rounded-lg hover:bg-pf-bg-2 transition-colors"
                    >
                      <Search className="h-4 w-4" />
                      <span>Discover Printers</span>
                    </button>
                  </>
                )}
              </div>
            </div>
          ) : viewMode === 'cards' ? (
            <div className="grid grid-cols-[repeat(auto-fit,26rem)] gap-6 justify-start">
              {userPrinters.map((printer) => (
                <ExpandablePrinterCard
                  key={printer.id}
                  printer={printer}
                  onDelete={() => handleDeleteSinglePrinter(printer)}
                  onEdit={() => handleEditPrinter(printer)}
                />
              ))}
            </div>
          ) : (
            <PrinterTableView
              printers={userPrinters}
              onEdit={handleEditPrinter}
              onDelete={handleDeleteClick}
              onBulkSetMaintenance={handleBulkSetMaintenance}
            />
          )}
        </div>

        {/* Modals */}
        <PrinterDiscoveryModal
          isOpen={showDiscovery}
          onClose={() => setShowDiscovery(false)}
          onSuccess={refetchPrinters}
        />
        
        <DeleteConfirmationModal
          isOpen={deleteConfirmation.isOpen}
          printers={deleteConfirmation.printers}
          onConfirm={handleDeleteConfirm}
          onCancel={handleDeleteCancel}
        />
        
        <EditPrinterModal
          printerId={editPrinterId}
          isOpen={showEditModal}
          onClose={() => setShowEditModal(false)}
          onSuccess={() => { setShowEditModal(false); refetchPrinters(); }}
        />
    </PageTemplate>
  );
}