import React, { useMemo, useState } from 'react';
import { usePrintersWithCameraUrls, useDeletePrinter } from '@/hooks/useApi';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { useQueryClient } from '@tanstack/react-query';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { useAuth } from '@/contexts/AuthHooks';
import { CollapsedPrinterCard } from '@/components/CollapsedPrinterCard';
import { PrinterDetailsSidebar } from '@/components/PrinterDetailsSidebar';
import { PrinterTableView } from '@/components/PrinterTableView';
import { EditPrinterModal } from '@/components/EditPrinterModal';
import { AddPrinterButton } from '@/components/AddPrinterButton';
import { DeleteConfirmationModal } from '@/components/DeleteConfirmationModal';
import { PrinterCardSkeleton } from '@/components/skeletons/PrinterCardSkeleton';
import { ExpandablePrinterCard } from '@/components/ExpandablePrinterCard';
import { PrinterCompactCard } from '@/components/PrinterCompactCard';
import { PageTemplate } from '@/components/PageTemplate';
import { Button } from '@/components/ui/Button';
import { Select } from '@/components/ui/Select';
import type { Printer } from '@/types/api';
import { PrinterBackend } from '@/types/api';

import { Printer as PrinterIcon } from 'lucide-react';
import { mdiViewList, mdiViewGrid, mdiViewComfy, mdiViewQuilt } from '@mdi/js';
import { toast } from 'sonner';

// Helper component for MDI icons
function MdiIcon({ path, size = 'w-4 h-4' }: { path: string; size?: string }) {
  return (
    <svg
      className={size}
      viewBox="0 0 24 24"
      role="img"
    >
      <path fill="currentColor" d={path} />
    </svg>
  );
}


type PrinterStateFilter = 'all' | 'online' | 'printing' | 'paused' | 'offline';
type BackendFilter = 'all' | 'Moonraker' | 'PrusaLink' | 'SDCP' | 'OctoPrint';
type ViewMode = 'collapsed' | 'compact' | 'expandable' | 'table';

// Helper function to get backend name from enum value
function getBackendName(backend: PrinterBackend | string | number): string {
  if (typeof backend === 'string') return backend;
  // Map enum value to name by looking up the enum
  return PrinterBackend[backend as number] || '';
}

export function PrintersPage() {
  const { hasPermission } = useAuth();
  const queryClient = useQueryClient();
  const { 
    data: printers, 
    isLoading, 
    error,
    refetch: refetchPrinters
  } = usePrintersWithCameraUrls();
  const { getPrinterStatus } = usePrinterStatusUpdates();
  
  const deletePrinterMutation = useDeletePrinter();
  const [viewMode, setViewMode] = useState<ViewMode>(() => {
    const saved = localStorage.getItem('printerViewMode');
    return (saved as ViewMode) || 'collapsed';
  });
  const [editPrinterId, setEditPrinterId] = useState<string | null>(null);
  const [showEditModal, setShowEditModal] = useState(false);
  const [expandedPrinterId, setExpandedPrinterId] = useState<string | null>(null);
  const [deleteConfirmation, setDeleteConfirmation] = useState<{
    isOpen: boolean;
    printers: Printer[];
  }>({ isOpen: false, printers: [] });

  // Save view mode preference to localStorage
  React.useEffect(() => {
    localStorage.setItem('printerViewMode', viewMode);
  }, [viewMode]);

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
      filtered = filtered.filter(p => getBackendName(p.backend) === backendFilter);
    }
    return filtered;
  }, [printers, stateFilter, backendFilter]);



  const handleDeleteClick = (printers: Printer[]) => {
    setDeleteConfirmation({ isOpen: true, printers });
  };

  const handleDeleteSinglePrinter = (printer: Printer) => {
    setDeleteConfirmation({ isOpen: true, printers: [printer] });
    toast(`Delete: "${printer.name}" — confirm to proceed`, { duration: 3000 });
  };

  const handleDeleteConfirm = async () => {
    try {
      await Promise.all(deleteConfirmation.printers.map(printer => 
        deletePrinterMutation.mutateAsync(printer.id)
      ));
      setDeleteConfirmation({ isOpen: false, printers: [] });
    } catch (error) {
      if (window.PrintFarmerDebug?.printers) {
        console.error('Failed to delete printers:', error);
      }
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
      if (window.PrintFarmerDebug?.printers) {
        console.log(`Starting maintenance update for ${printers.length} printer(s), inMaintenance=${inMaintenance}`);
      }
      
      const results = await Promise.all(printers.map(async (printer) => {
        if (window.PrintFarmerDebug?.printers) {
          console.log(`Updating printer ${printer.id} (${printer.name}) to inMaintenance=${inMaintenance}`);
        }
        const response = await fetch(`${getApiBaseUrl()}/printers/${printer.id}/maintenance`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
          body: JSON.stringify(inMaintenance)
        });
        
        if (!response.ok) {
          const errorData = await response.text();
          if (window.PrintFarmerDebug?.printers) {
            console.error(`Failed to update maintenance for ${printer.id}:`, response.status, errorData);
          }
          throw new Error(`HTTP ${response.status}: ${errorData}`);
        }
        
        return response.json();
      }));
      
      if (window.PrintFarmerDebug?.printers) {
        console.log('Maintenance status updated successfully:', results);
        console.log('Refetching printer queries...');
      }
      await queryClient.refetchQueries({ queryKey: ['printers'] });
      if (window.PrintFarmerDebug?.printers) {
        console.log('Printers refetched, UI should update now');
      }
    } catch (error) {
      if (window.PrintFarmerDebug?.printers) {
        console.error('Failed to update maintenance status:', error);
      }
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
            <p className="text-pf-text-secondary mb-6">{error.message}</p>
            <Button variant="primary" onClick={() => refetchPrinters()}>
              Retry
            </Button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <PageTemplate
      title="Printers"
      subtitle="Monitor and manage your 3D printer farm"
      icon={PrinterIcon}
      maxWidth="max-w-7xl"
    >
      <div className="flex flex-col md:flex-row md:items-center md:justify-between mb-8 gap-4">
        <div></div>
        <div className="flex flex-col md:flex-row md:items-center gap-3">
          {/* State Filter */}
          <Select
            value={stateFilter}
            onChange={e => setStateFilter(e.target.value as PrinterStateFilter)}
            aria-label="Filter by printer state"
          >
            <option value="all">All States</option>
            <option value="online">Online</option>
            <option value="printing">Printing</option>
            <option value="paused">Paused</option>
            <option value="offline">Offline</option>
          </Select>
          {/* Backend Filter */}
          <Select
            value={backendFilter}
            onChange={e => setBackendFilter(e.target.value as BackendFilter)}
            aria-label="Filter by backend"
          >
            <option value="all">All Backends</option>
            <option value="Moonraker">Moonraker</option>
            <option value="PrusaLink">PrusaLink</option>
            <option value="SDCP">SDCP</option>
            <option value="OctoPrint">OctoPrint</option>
          </Select>
          {/* View Mode Toggle */}
          <div className="flex items-center bg-pf-bg-1 border border-pf-border rounded-lg p-1">
            <Button
              type="button"
              onClick={() => setViewMode('collapsed')}
              variant={viewMode === 'collapsed' ? 'primary' : 'subtle'}
              size="sm"
              className="flex items-center space-x-2"
              title="Collapsed Card View"
            >
              <MdiIcon path={mdiViewList} />
            </Button>
            <Button
              type="button"
              onClick={() => setViewMode('compact')}
              variant={viewMode === 'compact' ? 'primary' : 'subtle'}
              size="sm"
              className="!p-2"
              title="Compact Cards"
            >
              <MdiIcon path={mdiViewGrid} />
            </Button>
            <Button
              type="button"
              onClick={() => setViewMode('expandable')}
              variant={viewMode === 'expandable' ? 'primary' : 'subtle'}
              size="sm"
              className="!p-2"
              title="Expandable Cards"
            >
              <MdiIcon path={mdiViewComfy} />
            </Button>
            <Button
              type="button"
              onClick={() => setViewMode('table')}
              variant={viewMode === 'table' ? 'primary' : 'subtle'}
              size="sm"
              className="flex items-center space-x-2"
              title="Table View"
            >
              <MdiIcon path={mdiViewQuilt} />
            </Button>
          </div>
          {hasPermission('printers', 'create') && (
            <AddPrinterButton onSuccess={refetchPrinters} />
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
              <div className="flex justify-center">
                {hasPermission('printers', 'create') && (
                  <AddPrinterButton onSuccess={refetchPrinters} />
                )}
              </div>
            </div>
          ) : viewMode === 'collapsed' ? (
            <div className="flex gap-6">
              <div className="grid grid-cols-[repeat(auto-fit,26rem)] gap-6 justify-start flex-1">
                {userPrinters.map((printer) => (
                  <CollapsedPrinterCard
                    key={printer.id}
                    printer={printer}
                    onExpand={() => setExpandedPrinterId(printer.id)}
                    onEdit={() => handleEditPrinter(printer)}
                  />
                ))}
              </div>
              {expandedPrinterId && (
                <PrinterDetailsSidebar
                  printerId={expandedPrinterId}
                  onClose={() => setExpandedPrinterId(null)}
                />
              )}
            </div>
          ) : viewMode === 'compact' ? (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
              {userPrinters.map((p) => (
                <PrinterCompactCard
                  key={p.id}
                  printer={p}
                  onEdit={(printer) => handleEditPrinter(printer)}
                  onDelete={handleDeleteSinglePrinter}
                  getPrinterStatus={getPrinterStatus}
                />
              ))}
            </div>
          ) : viewMode === 'expandable' ? (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
              {userPrinters.map((p) => (
                <ExpandablePrinterCard
                  key={p.id}
                  printer={p}
                  onEdit={() => handleEditPrinter(p)}
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