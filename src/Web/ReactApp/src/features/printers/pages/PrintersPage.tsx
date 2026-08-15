import React, { Suspense, useCallback, useDeferredValue, useMemo, useState, useOptimistic, useTransition, useEffect } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { usePrinters, useDeletePrinter, usePrinterBackendCapabilities, useBedTypes, usePrinterCameraUrls } from '@/common/hooks/useApi';
import { usePrinterDisplays } from '@/common/hooks/usePrinterDisplay';
import { useQueryClient } from '@tanstack/react-query';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { useAllAutoDispatchStatuses } from '@/features/printers/hooks/useAutoDispatch';
import type { AutoDispatchStatus } from '@/types/api';
import { apiClient } from '@/services/api';
import {
  mutationErrorMessage,
  mutationErrorStatus,
} from '@/common/utils/mutationError';
import { toast } from 'sonner';
import { CompactPrinterCard } from '@/features/printers/components/CompactPrinterCard';
import { PrinterCardGrid } from '@/features/printers/components/PrinterCardGrid';
import { PrinterDetailsSidebar } from '@/features/printers/components/PrinterDetailsSidebar';
import { PrinterTableView } from '@/features/printers/components/PrinterTableView';
import { AddPrinterButton } from '@/features/printers/components/AddPrinterButton';
import { DeleteConfirmationModal } from '@/common/components/modals/DeleteConfirmationModal';
import { PrinterCardSkeleton } from '@/common/components/skeletons/PrinterCardSkeleton';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui/Button';
import { Select } from '@/common/components/ui/Select';
import { ViewModeToggle, type ViewMode } from '@/common/components/ViewModeToggle';
import type { Printer, PrinterBackendCapabilitiesDto } from '@/types/api';
import { requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import { sortPrintersForDisplay, getBackendName, type PrinterSortMode } from '@/features/printers/utils/printerDisplaySort';

import { PrinterIcon, PrinterSearchIcon } from '@/common/components/icons/MdiIcons';
import PrinterImportExportControls from '@/features/printers/components/admin/PrinterImportExportControls';
import PrinterBulkControls from '@/features/printers/components/admin/PrinterBulkControls';
import { usePageTour } from '@/common/hooks/usePageTour';
import { printersTour } from '@/features/printers/tours/printers.tour';
import { HelpButton } from '@/common/components/HelpButton';
import { useFleetFilamentCoverage } from '@/features/filament-coverage/hooks';
import { useFleetPrinterTags } from '@/features/printers/hooks/usePrinterTagsFleet';
import { useFleetQueueSummaries } from '@/features/printers/hooks/useQueueSummariesFleet';
import { useDiscoveryAvailable } from '@/features/printers/hooks/useDiscoveryAvailability';
import { FailureDetectionPollingProvider } from '@/features/printers/hooks/useFailureDetectionPolling';
import type { DetailedPrinterCardProps } from '@/features/printers/components/DetailedPrinterCard';
import type { EditPrinterModalProps } from '@/features/printers/components/EditPrinterModal';
import type { PrinterDiscoveryModalProps } from '@/features/printers/components/PrinterDiscoveryModal';

// Interaction-only surfaces: not needed for the initial grid paint, so they're
// lazy-loaded out of the PrintersPage chunk (#1146 item 10). `DetailedPrinterCard`
// only renders in the "detailed" view mode; the modals only render once a user
// opens them. Each keeps its existing behavior — only the import timing changes.
const DetailedPrinterCard = lazyWithPreload<DetailedPrinterCardProps, React.FC<DetailedPrinterCardProps>>(
  () => import('@/features/printers/components/DetailedPrinterCard').then(m => ({ default: m.DetailedPrinterCard }))
);
const EditPrinterModal = lazyWithPreload<EditPrinterModalProps, React.FC<EditPrinterModalProps>>(
  () => import('@/features/printers/components/EditPrinterModal').then(m => ({ default: m.EditPrinterModal }))
);
const PrinterDiscoveryModal = lazyWithPreload<PrinterDiscoveryModalProps, React.FC<PrinterDiscoveryModalProps>>(
  () => import('@/features/printers/components/PrinterDiscoveryModal').then(m => ({ default: m.PrinterDiscoveryModal }))
);


type PrinterStateFilter = 'all' | 'online' | 'printing' | 'paused' | 'offline';
type BackendFilter = 'all' | 'Moonraker' | 'PrusaLink' | 'SDCP' | 'OctoPrint' | 'FlashForge';
type AvailabilityFilter = 'all' | '1' | '2' | '4' | '8' | '12' | '24';

export function PrintersPage() {
  const { hasPermission } = useAuth();
  // Prime the fleet filament coverage cache for all compact-card slots so
  // per-printer hooks dedupe via the fleet snapshot instead of each issuing
  // a separate request (N+1 guard, issue #717).
  useFleetFilamentCoverage();
  // Prime the batched printer-tags and queue-summary fleet caches the same
  // way, so CompactPrinterCard's per-printer selectors dedupe onto these
  // single requests instead of one tag/queue request per card (#1146 items
  // 1 and 9).
  useFleetPrinterTags();
  useFleetQueueSummaries();
  const queryClient = useQueryClient();
  
  const {
    data: printers,
    isLoading,
    isError: isPrintersError,
    error: printersError,
    refetch: refetchPrinters,
  } = usePrinters();

  // The printers query never reaches a "successful" state while every attempt
  // 503s, so `dataUpdatedAt` never advances. Any refetch of that still-empty
  // query — a manual Retry click, or QueueRealtimeBridge's
  // `invalidateQueries(['printers'])` on SignalR reconnect/queue events —
  // resets React Query's `status` back to `pending`, which flips `isLoading`
  // true again and would otherwise replace the error alert (and its Retry
  // button) with the full-page skeleton (#1581). Persist the last-seen error
  // across those transient pending windows so the alert stays put until
  // printers data actually arrives (including a genuinely empty `[]` fleet,
  // which is distinct from `undefined` and must still render normally).
  const [stalePrintersError, setStalePrintersError] = useState<unknown>(null);
  useEffect(() => {
    if (isPrintersError) {
      setStalePrintersError(printersError);
    } else if (printers) {
      setStalePrintersError(null);
    }
  }, [isPrintersError, printersError, printers]);
  const showPrintersError = isPrintersError || (!!stalePrintersError && !printers);
  const displayedPrintersError = isPrintersError ? printersError : stalePrintersError;
  const { data: cameraUrls = [] } = usePrinterCameraUrls();

  const { data: bedTypes = [] } = useBedTypes();

  const printersWithCameraUrls = useMemo(() => {
    const cameraUrlsByPrinterId = new Map(
      cameraUrls.map((camera) => [camera.id, camera])
    );

    return (printers || []).map((printer) => {
      const camera = cameraUrlsByPrinterId.get(printer.id);
      return camera
        ? {
            ...printer,
            cameraStreamUrl: camera.cameraStreamUrl,
            cameraSnapshotUrl: camera.cameraSnapshotUrl,
            cameraAccessMode: camera.cameraAccessMode,
            cameraStreamFormat: camera.cameraStreamFormat,
            cameraSnapshotStrategy: camera.cameraSnapshotStrategy,
          }
        : printer;
    });
  }, [cameraUrls, printers]);

  // Merge with realtime SignalR updates for display
  const displayPrinters = usePrinterDisplays(printersWithCameraUrls);

  // Whether ANY printer in the fleet has Obico/failure detection enabled,
  // computed once here (not per-card) and shared via context so the
  // failure-detection status poll is controlled at the page level instead of
  // depending on which mix of cards happens to be mounted (#1146 item 3).
  const anyObicoEnabled = useMemo(
    () => displayPrinters.some((printer) => !!printer.obicoEnabled),
    [displayPrinters]
  );

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
  const displayPrinterStateById = useMemo(
    () => new Map(displayPrinters.map((printer) => [printer.id, printer.state])),
    [displayPrinters],
  );
  const pendingPrinterIds = useMemo(
    () => new Set(
      ((allAutoDispatchStatuses ?? []) as AutoDispatchStatus[])
        .filter((status) => requiresBedClearConfirmation(
          status,
          displayPrinterStateById.get(status.printerId),
        ))
        .map((status) => status.printerId)
    ),
    [allAutoDispatchStatuses, displayPrinterStateById]
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

  // Page-level discovery modal state (header button opens modal)
  const [showDiscovery, setShowDiscovery] = useState(false);

  // Discovery availability: one shared TanStack Query hook (admin-gated,
  // visibility/backoff handled by local convention defaults) replacing the
  // previous raw setInterval + local state (#1146 item 7).
  const discoveryAvailable = useDiscoveryAvailable();

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
    // Copy before sort (#1146 item 5): when every filter below is a no-op,
    // `filtered` would otherwise stay aliased to `optimisticPrinters` (and
    // transitively to `displayPrinters`/the query cache); `filtered` is only
    // ever read from below (never sorted in place — see
    // `sortPrintersForDisplay`), so this copy exists purely so nothing
    // downstream can be surprised by a shared reference.
    let filtered = [...(optimisticPrinters ?? [])];
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
    return sortPrintersForDisplay(filtered, sortMode, pendingPrinterIds);
  }, [optimisticPrinters, stateFilter, backendFilter, availabilityHours, filterNow, sortMode, pendingPrinterIds, bedTypeFilter]);

  const deferredUserPrinters = useDeferredValue(userPrinters);

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

  // Stable identity so memoized printer cards don't re-render when the page does
  const handleEditPrinter = useCallback((printer: Printer) => {
    setEditPrinterId(printer.id);
    setShowEditModal(true);
  }, []);

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
        if (!printer.rowVersion) {
          throw new Error(
            `Printer ${printer.name} has no reviewed revision. Refresh and review again.`
          );
        }
        await apiClient.setPrinterMaintenance(
          printer.id,
          inMaintenance,
          printer.rowVersion
        );
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
      if ([412, 428].includes(mutationErrorStatus(error) ?? 0)) {
        await queryClient.refetchQueries({ queryKey: ['printers'] });
      }
      toast.error(
        mutationErrorMessage(error, 'Failed to update maintenance status')
      );
    }
  };

  if (isLoading && !showPrintersError) {
    return (
      <div className="min-h-full bg-pf-bg-2 pt-4 pb-8 lg:pt-20">
        <div className="mx-auto px-4 sm:px-6 lg:px-8" role="status" aria-busy="true">
          <div className="pf-skeleton pf-animate-skeleton h-8 w-48 rounded-sm mb-6" />
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4 mb-8">
            {Array.from({ length: 5 }).map((_, i) => (
              <div key={i} className="pf-skeleton pf-animate-skeleton h-24 rounded-lg" />
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

  if (showPrintersError) {
    return (
      <PageTemplate
        title="Printers"
        subtitle="Monitor and manage your 3D printer farm"
        icon={PrinterIcon}
        titleActions={<HelpButton onClick={startTour} />}
      >
        <div
          role="alert"
          className="flex min-h-72 flex-col items-center justify-center rounded-lg border border-pf-error/40 bg-pf-error/5 p-8 text-center"
        >
          <PrinterIcon className="mb-4 h-14 w-14 text-pf-error" />
          <h2 className="mb-2 text-xl font-semibold text-pf-text-primary">Unable to Load Printers</h2>
          <p className="mb-6 max-w-md text-pf-text-secondary">
            {displayedPrintersError instanceof Error
              ? displayedPrintersError.message
              : 'PrintFarmer could not retrieve the printer list. Try again.'}
          </p>
          <Button type="button" variant="primary" onClick={() => void refetchPrinters()}>
            Retry
          </Button>
        </div>
      </PageTemplate>
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
      <FailureDetectionPollingProvider value={anyObicoEnabled}>
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
                    aria-label="Discover Printers on the local network"
                    onClick={() => setShowDiscovery(true)}
                    onMouseEnter={() => PrinterDiscoveryModal.preload()}
                    onFocus={() => PrinterDiscoveryModal.preload()}
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
              (deferredUserPrinters.length === 0) ? (
                <div className="text-center py-12">
                  <PrinterIcon className="h-12 w-12 text-pf-text-tertiary mx-auto mb-4" />
                  <h3 className="text-xl font-semibold text-pf-text-primary mb-2">No Printers Found</h3>
                  <p className="text-pf-text-secondary mb-6">Get started by adding your first 3D printer using the "Add Printer" button above.</p>
                </div>
              ) : viewMode === 'collapsed' ? (
                <PrinterCardGrid
                  printers={deferredUserPrinters}
                  mode="compact"
                  activePrinterId={expandedPrinterId}
                  renderPrinter={(printer) => (
                    <CompactPrinterCard
                      printer={printer}
                      backendCapabilities={backendCapabilitiesByPrinterId[printer.id]}
                      onExpand={handleOpenPrinterDetails}
                      onEdit={handleEditPrinter}
                    />
                  )}
                />
              ) : viewMode === 'detailed' ? (
                <Suspense
                  fallback={(
                    <PrinterCardGrid
                      printers={deferredUserPrinters}
                      mode="detailed"
                      activePrinterId={expandedPrinterId}
                      renderPrinter={() => <PrinterCardSkeleton />}
                    />
                  )}
                >
                  <PrinterCardGrid
                    printers={deferredUserPrinters}
                    mode="detailed"
                    activePrinterId={expandedPrinterId}
                    renderPrinter={(printer) => (
                      <DetailedPrinterCard
                        printer={printer}
                        backendCapabilities={backendCapabilitiesByPrinterId[printer.id]}
                        onEdit={handleEditPrinter}
                      />
                    )}
                  />
                </Suspense>
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
                    printers={deferredUserPrinters}
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
                        if (!printer.rowVersion) {
                          throw new Error('Printer revision unavailable; refresh and review again.');
                        }
                        await apiClient.updatePrinter(
                          printer.id,
                          updated,
                          printer.rowVersion
                        );
                        toast.success(`${printer.name || 'Printer'} ${updated.isEnabled ? 'enabled' : 'disabled'}`);
                        await queryClient.invalidateQueries({ queryKey: ['printers'] });
                      } catch (err) {
                        console.error('Failed to toggle enabled', err);
                        if ([412, 428].includes(mutationErrorStatus(err) ?? 0)) {
                          await queryClient.refetchQueries({
                            queryKey: ['printers'],
                          });
                        }
                        toast.error(
                          mutationErrorMessage(
                            err,
                            'Failed to toggle enabled state'
                          )
                        );
                      }
                    }}
                  />
                </>
              )
            )}
          </div>
        </div>

        {isSidebarOpen && (
          <div className="hidden lg:block lg:self-start lg:sticky lg:top-0 lg:max-h-[calc(100dvh-5rem)]">
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
        
        {showEditModal && (
          <Suspense fallback={null}>
            <EditPrinterModal
              printerId={editPrinterId}
              isOpen={showEditModal}
              onClose={() => setShowEditModal(false)}
              onSuccess={() => { setShowEditModal(false); refetchPrinters(); }}
            />
          </Suspense>
        )}

        {/* Page-level discovery modal: header button opens this and we refetch on success */}
        {showDiscovery && (
          <Suspense fallback={null}>
            <PrinterDiscoveryModal
              isOpen={showDiscovery}
              onClose={() => setShowDiscovery(false)}
              onSuccess={() => { setShowDiscovery(false); refetchPrinters(); }}
            />
          </Suspense>
        )}

      </FailureDetectionPollingProvider>
    </PageTemplate>
  );
}
