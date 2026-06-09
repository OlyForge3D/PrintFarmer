import React, { useCallback, useMemo, useState, useOptimistic, useTransition, useEffect } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { usePrinters, useDeletePrinter, usePrinterBackendCapabilities, useBedTypes } from '@/common/hooks/useApi';
import { usePrinterDisplays } from '@/common/hooks/usePrinterDisplay';
import { useQueryClient } from '@tanstack/react-query';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useAllAutoDispatchStatuses } from '@/features/printers/hooks/useAutoDispatch';
import type { AutoDispatchStatus } from '@/types/api';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { CompactPrinterCard } from '@/features/printers/components/CompactPrinterCard';
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
import type { Printer, PrinterBackendCapabilitiesDto } from '@/types/api';
import { PrinterBackend } from '@/types/api';
import { requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';

import { PrinterIcon, PrinterSearchIcon } from '@/common/components/icons/MdiIcons';
import PrinterImportExportControls from '@/features/printers/components/admin/PrinterImportExportControls';
import PrinterBulkControls from '@/features/printers/components/admin/PrinterBulkControls';
import { usePageTour } from '@/common/hooks/usePageTour';
import { printersTour } from '@/features/printers/tours/printers.tour';
import { HelpButton } from '@/common/components/HelpButton';


type PrinterStateFilter = 'all' | 'online' | 'printing' | 'paused' | 'offline';
type BackendFilter = 'all' | 'Moonraker' | 'PrusaLink' | 'SDCP' | 'OctoPrint' | 'FlashForge';
type PrinterSortMode = 'state' | 'name' | 'backend';
type AvailabilityFilter = 'all' | '1' | '2' | '4' | '8' | '12' | '24';

/** State priority for sorting: lower number = higher in list */
function getStateSortPriority(printer: Printer, pendingIds: Set<string>): number {
  if (pendingIds.has(printer.id)) return 0;       // PendingReady (attention)
  if (!printer.isOnline) return 4;                 // Offline
  const state = (printer.state || '').toLowerCase();
  if (state.includes('printing')) return 1;        // Printing
  if (state.includes('paused')) return 2;           // Paused
  return 3;                                         // Idle / other online
}

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

  const { data: bedTypes = [] } = useBedTypes();
  
  // Merge with realtime SignalR updates for display
  const displayPrinters = usePrinterDisplays(printers || []);

  const printerBackendCapabilitiesQuery = usePrinterBackendCapabilities();
  const backendCapabilitiesByPrinterId = useMemo(() => {
    const map: Record<string, PrinterBackendCapabilitiesDto> = {};
    (printerBackendCapabilitiesQuery.data ?? []).forEach((caps) => {
      map[caps.printerId] = caps;
    });
    return map;
  }, [printerBackendCapabilitiesQuery.data]);
  
  // React 19: useTransition for async delete operations
  const [,startTransition] = useTransition();
  
  // React 19: useOptimistic for optimistic printer deletion
  const [optimisticPrinters, addOptimisticDelete] = useOptimistic<Printer[], string>(
    displayPrinters,
    (state, deletedPrinterId) => state.filter(p => p.id !== deletedPrinterId)
  );
  
  const deletePrinterMutation = useDeletePrinter();
  const { data: allAutoDispatchStatuses } = useAllAutoDispatchStatuses();
  const pendingPrinterIds = useMemo(
    () => new Set(
      ((allAutoDispatchStatuses ?? []) as AutoDispatchStatus[])
        .filter((status) => requiresBedClearConfirmation(status))
        .map((status) => status.printerId)
    ),
    [allAutoDispatchStatuses]
  );
  const [searchParams] = useSearchParams();
  const [viewMode, setViewMode] = useState<ViewMode>(() => {
    const fromUrl = searchParams.get('view');
    if (fromUrl === 'collapsed' || fromUrl === 'detailed' || fromUrl === 'table') {
      return fromUrl;
    }
    const saved = localStorage.getItem('printerViewMode');

    // Migrate legacy value (pre-rename) to the new name.
    if (saved === 'expandable') {
      return 'detailed';
    }

    if (saved === 'collapsed' || saved === 'detailed' || saved === 'table') {
      return saved;
    }

    return 'collapsed';
  });
  const [editPrinterId, setEditPrinterId] = useState<string | null>(null);
  const [showEditModal, setShowEditModal] = useState(false);
  const [deleteConfirmation, setDeleteConfirmation] = useState<{
    isOpen: boolean;
    printers: Printer[];
  }>({ isOpen: false, printers: [] });

  const navigate = useNavigate();
  const { startTour } = usePageTour({ tourId: 'printers', steps: printersTour });

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
  const [availabilityFilter, setAvailabilityFilter] = useState<AvailabilityFilter>('all');
  const [bedTypeFilter, setBedTypeFilter] = useState<string>('all');
  const [sortMode, setSortMode] = useState<PrinterSortMode>(() => {
    const saved = localStorage.getItem('printerSortMode');
    if (saved === 'state' || saved === 'name' || saved === 'backend') return saved;
    return 'state';
  });

  // Save sort mode preference to localStorage
  useEffect(() => {
    localStorage.setItem('printerSortMode', sortMode);
  }, [sortMode]);

  // Tabs removed — admin controls are now inline and permission-gated
  const [selectedPrinterIds, setSelectedPrinterIds] = useState<string[]>([]);
  const { printerId: routePrinterId } = useParams<{ printerId?: string }>();
  const printersById = useMemo(() => {
    const map: Record<string, Printer> = {};
    (printers || []).forEach(p => { map[p.id] = p; });
    return map;
  }, [printers]);
  const expandedPrinterId = routePrinterId && printersById[routePrinterId] ? routePrinterId : null;

  // Hours value for availability filter — cutoff is derived dynamically so it never goes stale
  const [availabilityHours, setAvailabilityHours] = useState<number | null>(null);

  // Ticking now value for availability filter — updates every 30s so filter stays fresh
  const [filterNow, setFilterNow] = useState(Date.now);
  useEffect(() => {
    if (availabilityHours === null) return;
    const id = setInterval(() => setFilterNow(Date.now()), 30_000);
    return () => clearInterval(id);
  }, [availabilityHours]);

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
    // Availability filter — show printers available within N hours (idle printers are always available)
    if (availabilityHours !== null) {
      const cutoffMs = filterNow + availabilityHours * 60 * 60 * 1000;
      filtered = filtered.filter(p => {
        if (!p.estimatedCompletionTimeUtc) return true; // idle = already available
        return new Date(p.estimatedCompletionTimeUtc).getTime() <= cutoffMs;
      });
    }
    // Bed type filter
    if (bedTypeFilter !== 'all') {
      filtered = filtered.filter(p => p.bedTypeId === bedTypeFilter);
    }
    // Sort based on selected mode
    filtered.sort((a, b) => {
      if (sortMode === 'state') {
        const aPriority = getStateSortPriority(a, pendingPrinterIds);
        const bPriority = getStateSortPriority(b, pendingPrinterIds);
        if (aPriority !== bPriority) return aPriority - bPriority;
        return (a.name ?? '').localeCompare(b.name ?? '');
      }
      if (sortMode === 'name') {
        return (a.name ?? '').localeCompare(b.name ?? '');
      }
      if (sortMode === 'backend') {
        const aBackend = getBackendName(a.backend);
        const bBackend = getBackendName(b.backend);
        const cmp = aBackend.localeCompare(bBackend);
        if (cmp !== 0) return cmp;
        return (a.name ?? '').localeCompare(b.name ?? '');
      }
      return 0;
    });
    return filtered;
  }, [optimisticPrinters, stateFilter, backendFilter, availabilityHours, filterNow, sortMode, pendingPrinterIds, bedTypeFilter]);

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
        const modes: ViewMode[] = ['collapsed', 'detailed', 'table'];
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
    navigate(`/printers/${printer.id}/maintenance`);
  };

  const handleOpenPrinterDetails = useCallback((printerId: string) => {
    navigate(`/printers/${printerId}`);
  }, [navigate]);

  const handleClosePrinterDetails = useCallback(() => {
    navigate('/printers');
  }, [navigate]);

  useEffect(() => {
    if (!isLoading && routePrinterId && !printersById[routePrinterId]) {
      navigate('/printers', { replace: true });
    }
  }, [isLoading, navigate, printersById, routePrinterId]);

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
      <div className="min-h-full bg-pf-bg-2 pt-4 pb-8 lg:pt-20">
        <div className="mx-auto px-4 sm:px-6 lg:px-8" role="status" aria-busy="true">
          <div className="pf-skeleton pf-animate-skeleton h-8 w-48 rounded-sm mb-6" />
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4 mb-8">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="pf-skeleton pf-animate-skeleton h-24 rounded-xl" />
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

  const isSidebarOpen = !!expandedPrinterId;

  return (
    <PageTemplate
      title="Printers"
      subtitle="Monitor and manage your 3D printer farm"
      icon={PrinterIcon}
      titleActions={<HelpButton onClick={startTour} />}
    >
      <div className={isSidebarOpen ? 'min-w-0 lg:grid lg:grid-cols-[minmax(0,1fr)_24rem] lg:items-start lg:gap-6' : 'min-w-0'}>
        <div className="min-w-0">
          {/* Toolbar with three-zone layout: Primary Actions | Spacer | View & Filters */}
          <div className="flex flex-col gap-4 mb-6">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
              {/* Primary Actions (Left) */}
              <div data-tour="printers-actions" className="flex flex-col sm:flex-row sm:items-center gap-2">
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
              <div data-tour="printers-filters" className="flex flex-col sm:flex-row sm:flex-wrap sm:items-center sm:justify-end gap-3">
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
                    <option value="FlashForge">FlashForge</option>
                  </Select>
                </div>

                {/* Availability Filter */}
                <div className="flex items-center gap-2">
                  <label htmlFor="availability-filter" className="text-sm text-pf-text-secondary hidden sm:inline">Done in:</label>
                  <Select
                    id="availability-filter"
                    value={availabilityFilter}
                    onChange={e => {
                      const val = e.target.value as AvailabilityFilter;
                      setAvailabilityFilter(val);
                      setAvailabilityHours(val !== 'all' ? parseInt(val, 10) : null);
                    }}
                    aria-label="Filter by estimated completion time"
                    className="min-w-0"
                  >
                    <option value="all">Any Time</option>
                    <option value="1">≤ 1 hour</option>
                    <option value="2">≤ 2 hours</option>
                    <option value="4">≤ 4 hours</option>
                    <option value="8">≤ 8 hours</option>
                    <option value="12">≤ 12 hours</option>
                    <option value="24">≤ 24 hours</option>
                  </Select>
                </div>

                {/* Bed Type Filter */}
                {bedTypes.length > 0 && (
                  <div className="flex items-center gap-2">
                    <label htmlFor="bed-type-filter" className="text-sm text-pf-text-secondary hidden sm:inline">Bed:</label>
                    <Select
                      id="bed-type-filter"
                      value={bedTypeFilter}
                      onChange={e => setBedTypeFilter(e.target.value)}
                      aria-label="Filter by bed type"
                      className="min-w-0"
                    >
                      <option value="all">All Beds</option>
                      {bedTypes.map(bt => (
                        <option key={bt.id} value={bt.id}>{bt.name}</option>
                      ))}
                    </Select>
                  </div>
                )}

                {/* Sort Order */}
                <div className="flex items-center gap-2">
                  <label htmlFor="sort-mode" className="text-sm text-pf-text-secondary hidden sm:inline">Sort:</label>
                  <Select
                    id="sort-mode"
                    value={sortMode}
                    onChange={e => setSortMode(e.target.value as PrinterSortMode)}
                    aria-label="Sort printers by"
                    className="min-w-0"
                  >
                    <option value="state">State</option>
                    <option value="name">Name</option>
                    <option value="backend">Backend</option>
                  </Select>
                </div>

                {/* View Mode Toggle */}
                <ViewModeToggle viewMode={viewMode} onChange={setViewMode} />
              </div>
            </div>
          </div>

          {/* Printer details sidebar on small screens: between toolbar and grid */}
          {isSidebarOpen && (
            <div className="lg:hidden mb-6 min-w-0">
              <PrinterDetailsSidebar
                printerId={expandedPrinterId}
                printer={expandedPrinterId ? printersById[expandedPrinterId] : undefined}
                backendCapabilities={expandedPrinterId ? backendCapabilitiesByPrinterId[expandedPrinterId] : undefined}
                onClose={handleClosePrinterDetails}
                layout="content"
              />
            </div>
          )}

          {/* Content Area */}
          <div data-tour="printers-grid" className="space-y-6">
            {(
              (userPrinters.length === 0) ? (
                <div className="text-center py-12">
                  <PrinterIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
                  <h3 className="text-xl font-semibold text-pf-text-primary mb-2">No Printers Found</h3>
                  <p className="text-pf-text-secondary mb-6">Get started by adding your first 3D printer using the "Add Printer" button above.</p>
                </div>
              ) : viewMode === 'collapsed' ? (
                <div className="grid grid-cols-1 sm:grid-cols-[repeat(auto-fill,18rem)] gap-4 transition-opacity duration-200 min-w-0">
                  {userPrinters.map((printer, index) => (
                    <div key={printer.id} {...(index === 0 ? { 'data-tour': 'printers-card' } : {})}>
                      <CompactPrinterCard
                        printer={printer}
                        backendCapabilities={backendCapabilitiesByPrinterId[printer.id]}
                        onExpand={() => handleOpenPrinterDetails(printer.id)}
                        onEdit={() => handleEditPrinter(printer)}
                      />
                    </div>
                  ))}
                </div>
              ) : viewMode === 'detailed' ? (
                <div className="grid grid-cols-1 sm:grid-cols-[repeat(auto-fill,26rem)] gap-4">
                  {userPrinters.map((printer) => (
                    <DetailedPrinterCard
                      key={printer.id}
                      printer={printer}
                      backendCapabilities={backendCapabilitiesByPrinterId[printer.id]}
                      onEdit={() => handleEditPrinter(printer)}
                      onOpenDetails={() => handleOpenPrinterDetails(printer.id)}
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
                    onOpenDetails={(printer) => handleOpenPrinterDetails(printer.id)}
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

        {isSidebarOpen && (
          <div className="hidden lg:block lg:self-start lg:sticky lg:top-0">
            <PrinterDetailsSidebar
              printerId={expandedPrinterId}
              printer={expandedPrinterId ? printersById[expandedPrinterId] : undefined}
              backendCapabilities={expandedPrinterId ? backendCapabilitiesByPrinterId[expandedPrinterId] : undefined}
              onClose={handleClosePrinterDetails}
              layout="content"
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

    </PageTemplate>
  );
}
