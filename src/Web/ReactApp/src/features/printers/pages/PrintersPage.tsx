import React, { useMemo, useState, useOptimistic, useTransition, useEffect } from 'react';
import { usePrinters, useDeletePrinter } from '@/common/hooks/useApi';
import { usePrinterDisplays } from '@/common/hooks/usePrinterDisplay';
import { useQueryClient } from '@tanstack/react-query';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { CollapsedPrinterCard } from '@/features/printers/components/CollapsedPrinterCard';
import { PrinterDetailsSidebar } from '@/features/printers/components/PrinterDetailsSidebar';
import { PrinterTableView } from '@/features/printers/components/PrinterTableView';
import { EditPrinterModal } from '@/features/printers/components/EditPrinterModal';
import { AddPrinterButton } from '@/features/printers/components/AddPrinterButton';
import { PrinterDiscoveryModal } from '@/features/printers/components/PrinterDiscoveryModal';
import { DeleteConfirmationModal } from '@/common/components/modals/DeleteConfirmationModal';
import { PrinterCardSkeleton } from '@/common/components/skeletons/PrinterCardSkeleton';
import { DetailedPrinterCard } from '@/features/printers/components/DetailedPrinterCard';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui/Button';
import { Select } from '@/common/components/ui/Select';
import { ViewModeToggle, type ViewMode } from '@/common/components/ViewModeToggle';
import type { Printer } from '@/types/api';
import { PrinterBackend } from '@/types/api';

import { PrinterIcon, PrinterSearchIcon } from '@/common/components/icons/MdiIcons';
import PrinterImportExportControls from '@/features/printers/components/admin/PrinterImportExportControls';
import PrinterBulkControls from '@/features/printers/components/admin/PrinterBulkControls';
import { PrinterMaintenanceActionsModal } from '@/features/maintenance/components/PrinterMaintenanceActionsModal';


type PrinterStateFilter = 'all' | 'online' | 'printing' | 'paused' | 'offline';
type BackendFilter = 'all' | 'Moonraker' | 'PrusaLink' | 'SDCP' | 'OctoPrint';

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
    refetch: refetchPrinters
  } = usePrinters();
  
  // Merge with realtime SignalR updates for display
  const displayPrinters = usePrinterDisplays(printers || []);
  
  // React 19: useTransition for async delete operations
  const [,startTransition] = useTransition();
  
  // React 19: useOptimistic for optimistic printer deletion
  const [optimisticPrinters, addOptimisticDelete] = useOptimistic<Printer[], string>(
    displayPrinters,
    (state, deletedPrinterId) => state.filter(p => p.id !== deletedPrinterId)
  );
  
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

  const [maintenancePrinter, setMaintenancePrinter] = useState<Printer | null>(null);

  // Discovery availability state
  const [discoveryAvailable, setDiscoveryAvailable] = useState(false);
  // Page-level discovery modal state (header button opens modal)
  const [showDiscovery, setShowDiscovery] = useState(false);

  // Check if discovery service is available
  useEffect(() => {
    const checkDiscoveryAvailability = async () => {
      try {
        const settings = await apiClient.getSettings<import('@/types/NetworkDiscoverySettings').NetworkDiscoverySettings>('NetworkDiscovery');
        const isEnabled = settings?.enableDiscovery === true;
        const hasRecentHeartbeat = settings?.lastHeartbeat 
          ? new Date().getTime() - new Date(settings.lastHeartbeat).getTime() < 60000
          : false;
        setDiscoveryAvailable(isEnabled && hasRecentHeartbeat);
      } catch {
        setDiscoveryAvailable(false);
      }
    };

    checkDiscoveryAvailability();
    const interval = setInterval(checkDiscoveryAvailability, 30000);
    return () => clearInterval(interval);
  }, []);

  // Save view mode preference to localStorage
  useEffect(() => {
    localStorage.setItem('printerViewMode', viewMode);
  }, [viewMode]);

  // Filter state
  const [stateFilter, setStateFilter] = useState<PrinterStateFilter>('all');
  const [backendFilter, setBackendFilter] = useState<BackendFilter>('all');
  // Tabs removed — admin controls are now inline and permission-gated
  const [selectedPrinterIds, setSelectedPrinterIds] = useState<string[]>([]);
  const printersById = useMemo(() => {
    const map: Record<string, Printer> = {};
    (printers || []).forEach(p => { map[p.id] = p; });
    return map;
  }, [printers]);

  // React 19: Filter printers using optimisticPrinters for optimistic deletion feedback
  const userPrinters = useMemo(() => {
    let filtered = optimisticPrinters || [];
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
  }, [optimisticPrinters, stateFilter, backendFilter]);

  // Keyboard shortcuts for printer management
  useKeyboardShortcuts([
    {
      key: 'n',
      handler: () => {
        // Open add printer dialog
        const addButton = document.querySelector('[data-testid="add-printer-button"]') as HTMLButtonElement;
        addButton?.click();
      },
      description: 'Add new printer'
    },
    {
      key: 'd',
      handler: () => setShowDiscovery(true),
      description: 'Discover printers on network'
    },
    {
      key: 'v',
      handler: () => {
        //const modes: ViewMode[] = ['collapsed', 'expandable', 'table', 'glass', 'segmented', 'statusGlow', 'dashboard', 'flip', 'drawer'];
        const modes: ViewMode[] = ['collapsed', 'expandable', 'table'];
        const currentIdx = modes.indexOf(viewMode);
        const nextMode = modes[(currentIdx + 1) % modes.length];
        setViewMode(nextMode);
      },
      description: 'Cycle view mode'
    }
  ]);



  const handleDeleteClick = (printers: Printer[]) => {
    setDeleteConfirmation({ isOpen: true, printers });
  };

  const handleDeleteConfirm = async () => {
    // React 19: Use startTransition for async operations
    startTransition(async () => {
      try {
        // React 19: Optimistic delete - remove each printer immediately
        for (const printer of deleteConfirmation.printers) {
          addOptimisticDelete(printer.id);
        }
        
        // Execute deletions in background
        await Promise.all(deleteConfirmation.printers.map(printer => 
          deletePrinterMutation.mutateAsync(printer.id)
        ));
        setDeleteConfirmation({ isOpen: false, printers: [] });
      } catch (error) {
        // State rolls back automatically via useOptimistic on error
        if (window.PrintFarmerDebug?.printers) {
          console.error('Failed to delete printers:', error);
        }
      }
    });
  };

  const handleDeleteCancel = () => {
    setDeleteConfirmation({ isOpen: false, printers: [] });
  };

  // Import/export handled by admin components (PrinterImportControls / PrinterExportControls)

  const handleEditPrinter = (printer: Printer) => {
    setEditPrinterId(printer.id);
    setShowEditModal(true);
  };

  const handleOpenMaintenance = (printer: Printer) => {
    setMaintenancePrinter(printer);
  };


  const handleBulkSetMaintenance = async (printers: Printer[], inMaintenance: boolean) => {
    try {
      if (window.PrintFarmerDebug?.printers) {
        console.log(`Starting maintenance update for ${printers.length} printer(s), inMaintenance=${inMaintenance}`);
      }
      
      await Promise.all(printers.map(async (printer) => {
        if (window.PrintFarmerDebug?.printers) {
          console.log(`Updating printer ${printer.id} (${printer.name}) to inMaintenance=${inMaintenance}`);
        }
        await apiClient.setPrinterMaintenance(printer.id, inMaintenance);
      }));
      
      
      if (window.PrintFarmerDebug?.printers) {
        console.log('Maintenance status updated successfully');
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
        <div className="mx-auto px-4 sm:px-6 lg:px-8" role="status" aria-busy="true">
          <div className="h-8 w-48 bg-pf-bg-1 rounded-sm mb-6 animate-pulse" />
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

  const isCollapsedSidebarOpen = viewMode === 'collapsed' && !!expandedPrinterId;

  return (
    <PageTemplate
      title="Printers"
      subtitle="Monitor and manage your 3D printer farm"
      icon={PrinterIcon}
    >
      <div className={isCollapsedSidebarOpen ? 'min-w-0 lg:pr-96' : 'min-w-0'}>
        <div className="min-w-0">
          {/* Toolbar with three-zone layout: Primary Actions | Spacer | View & Filters */}
          <div className="flex flex-col gap-4 mb-6">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
              {/* Primary Actions (Left) */}
              <div className="flex flex-col sm:flex-row sm:items-center gap-2">
                {hasPermission('printers', 'create') && (
                  <AddPrinterButton onSuccess={refetchPrinters} />
                )}
                {hasPermission('printers', 'admin') && discoveryAvailable && (
                  <Button
                    variant="secondary"
                    aria-label="Trigger network discovery to find printers on local network"
                    onClick={() => setShowDiscovery(true)}
                    iconLeft={<PrinterSearchIcon className="w-4 h-4" ariaLabel="Discover" />}
                  >
                    Discover Printers
                  </Button>
                )}
                {hasPermission('printers', 'admin') && (
                  <PrinterImportExportControls />
                )}
              </div>

              {/* View & Filters (Right) */}
              <div className="flex flex-col sm:flex-row sm:flex-wrap sm:items-center sm:justify-end gap-3">
                {/* State Filter */}
                <div className="flex items-center gap-2">
                  <label htmlFor="state-filter" className="text-sm text-pf-text-secondary hidden sm:inline">State:</label>
                  <Select
                    id="state-filter"
                    value={stateFilter}
                    onChange={e => setStateFilter(e.target.value as PrinterStateFilter)}
                    aria-label="Filter by printer state"
                    className="min-w-0"
                  >
                    <option value="all">All States</option>
                    <option value="online">Online</option>
                    <option value="printing">Printing</option>
                    <option value="paused">Paused</option>
                    <option value="offline">Offline</option>
                  </Select>
                </div>

                {/* Backend Filter */}
                <div className="flex items-center gap-2">
                  <label htmlFor="backend-filter" className="text-sm text-pf-text-secondary hidden sm:inline">Backend:</label>
                  <Select
                    id="backend-filter"
                    value={backendFilter}
                    onChange={e => setBackendFilter(e.target.value as BackendFilter)}
                    aria-label="Filter by backend"
                    className="min-w-0"
                  >
                    <option value="all">All Backends</option>
                    <option value="Moonraker">Moonraker</option>
                    <option value="PrusaLink">PrusaLink</option>
                    <option value="SDCP">SDCP</option>
                    <option value="OctoPrint">OctoPrint</option>
                  </Select>
                </div>

                {/* View Mode Toggle */}
                <ViewModeToggle viewMode={viewMode} onChange={setViewMode} />
              </div>
            </div>
          </div>

          {/* Sidebar (Collapsed View) - small screens: between toolbar and grid */}
          {isCollapsedSidebarOpen && (
            <div className="lg:hidden mb-6 min-w-0">
              <PrinterDetailsSidebar
                printerId={expandedPrinterId}
                printer={expandedPrinterId ? printersById[expandedPrinterId] : undefined}
                onClose={() => setExpandedPrinterId(null)}
                layout="content"
              />
            </div>
          )}

          {/* Content Area */}
          <div className="space-y-6">
            {(
              (userPrinters.length === 0) ? (
                <div className="text-center py-12">
                  <PrinterIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
                  <h3 className="text-xl font-semibold text-pf-text-primary mb-2">No Printers Found</h3>
                  <p className="text-pf-text-secondary mb-6">Get started by adding your first 3D printer using the "Add Printer" button above.</p>
                </div>
              ) : viewMode === 'collapsed' ? (
                <div className="grid grid-cols-1 sm:grid-cols-[repeat(auto-fill,14rem)] gap-4 transition-opacity duration-200 min-w-0">
                  {userPrinters.map((printer) => (
                    <CollapsedPrinterCard
                      key={printer.id}
                      printer={printer}
                      onExpand={() => setExpandedPrinterId(printer.id)}
                      onEdit={() => handleEditPrinter(printer)}
                    />
                  ))}
                </div>
              ) : viewMode === 'expandable' ? (
                <div className="grid grid-cols-1 sm:grid-cols-[repeat(auto-fill,23rem)] gap-4 justify-center">
                  {userPrinters.map((p) => (
                    <DetailedPrinterCard
                      key={p.id}
                      printer={p}
                      onEdit={() => handleEditPrinter(p)}
                    />
                  ))}
                </div>
              ) : (
                <>
                  <div className="mb-4">
                    <PrinterBulkControls
                      selectedIds={selectedPrinterIds}
                      printersById={printersById}
                      onDelete={(ps) => handleDeleteClick(ps)}
                      onBulkSetMaintenance={handleBulkSetMaintenance}
                    />
                  </div>

                  <PrinterTableView
                    printers={userPrinters}
                    onEdit={handleEditPrinter}
                    onDelete={handleDeleteClick}
                    onBulkSetMaintenance={handleBulkSetMaintenance}
                    onOpenMaintenance={handleOpenMaintenance}
                    showEnableColumn={hasPermission('printers', 'admin')}
                    onSelectionChange={(ids) => setSelectedPrinterIds(ids)}
                    onToggleEnabled={async (printer) => {
                      try {
                        const updated = { isEnabled: !printer.isEnabled } as unknown as import('@/types/api').UpdatePrinterDto;
                        await apiClient.updatePrinter(printer.id, updated);
                        toast.success(`${printer.name || 'Printer'} ${updated.isEnabled ? 'enabled' : 'disabled'}`);
                        await queryClient.invalidateQueries({ queryKey: ['printers'] });
                      } catch (err) {
                        console.error('Failed to toggle enabled', err);
                        toast.error('Failed to toggle enabled state');
                      }
                    }}
                  />
                </>
              )
            )}
          </div>
        </div>

        {/* Sidebar (Collapsed View) - large screens: fixed overlay on the right */}
        {isCollapsedSidebarOpen && (
          <div className="hidden lg:block fixed right-0 top-12 bottom-0 w-96 z-40">
            <PrinterDetailsSidebar
              printerId={expandedPrinterId}
              printer={expandedPrinterId ? printersById[expandedPrinterId] : undefined}
              onClose={() => setExpandedPrinterId(null)}
            />
          </div>
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

        {/* Page-level discovery modal: header button opens this and we refetch on success */}
        <PrinterDiscoveryModal
          isOpen={showDiscovery}
          onClose={() => setShowDiscovery(false)}
          onSuccess={() => { setShowDiscovery(false); refetchPrinters(); }}
        />

        {maintenancePrinter && (
          <PrinterMaintenanceActionsModal
            isOpen={true}
            printer={maintenancePrinter}
            onClose={() => setMaintenancePrinter(null)}
          />
        )}
    </PageTemplate>
  );
}