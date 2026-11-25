import { MovementControlSection } from '@/features/printers/components/MovementControlSection';
import { PrinterActionBar } from '@/features/printers/components/PrinterActionBar';
import { CancelPrintConfirmation } from '@/features/printers/components/CancelPrintConfirmation';
import { usePrinterControls } from '@/features/printers/hooks/use-printer-controls';
import { BedClearEvidencePanel } from '@/features/printers/components/BedClearEvidencePanel';
import { RuntimeFactorControls } from '@/features/printers/components/RuntimeFactorControls';
import { useState, useRef, useEffect, useCallback, useMemo } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys, usePrintJobObjects, usePrinter, usePrinterDetails } from '@/common/hooks/useApi';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import { useSpoolmanConfigured } from '@/common/hooks/useSpoolmanConfigured';
import { apiClient } from '@/services/api';
import {
  mutationErrorMessage,
  mutationErrorStatus,
} from '@/common/utils/mutationError';
import { maintenanceService } from '@/services/maintenanceService';
import { getPrinterDisplayState } from '@/common/utils/printerStateDisplay';
import { formatPrinterModelSubtitle } from '@/common/utils/printerModelDisplay';
import { PrinterBackend, type ApiError, type Printer, type PrinterBackendCapabilitiesDto, type PrintJobObjectDto, type PrintJobObjectListDto } from '@/types/api';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { TemperatureControlSection } from '@/features/printers/components/TemperatureControlSection';
import { PrinterSensorsSection } from '@/features/printers/components/PrinterSensorsPanel';
import {
  canExcludeObject,
  isActivePrintForObjects,
  canOpenFiles,
  canOpenHistory,
  getPrinterSupport,
} from '@/features/printers/utils/printerSupport';
import { getStatusHeaderClassName, getStatusIndicatorColor, isPrinterStateShutdown } from '@/features/printers/utils/statusColors';
// This component's scrollable content follows PRINTER_DETAIL_SECTION_ORDER
// (see printerDetailSectionOrder.ts): Statistics, Version, Control, Objects,
// Move, Temperature, Materials/MMU, Spool. DetailedPrinterCard mirrors this
// order for its shared sections (#1698).
import { renderUnknown } from '@/common/utils/renderUnknown';
import { Button, CollapsibleSection, LoadedFilamentCard } from '@/common/components/ui';
import { ControlPadButton } from '@/common/components/ui/ControlPadButton';
import {
  FileIcon,
  RefreshIcon,
  HistoryIcon,
  CloseIcon,
  FilamentChangeIcon,
  EjectIcon,
} from '@/common/components/icons/MdiIcons';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { FilamentCoverageBreakdown } from '@/features/filament-coverage/components/FilamentCoverageBreakdown';
import { resolveMaterialLoadout } from '@/features/printers/utils/materialLoadout';
import { MmuProtocol } from '@/features/printers/constants/mmuProtocol';
import { MmuControlBox } from '@/features/printers/components/MmuControlBox';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import { useAutoDispatchStatus } from '@/features/printers/hooks/useAutoDispatch';
import { Modal } from '@/common/components/modals/Modal';
import { toast } from 'sonner';

// Animation styles
// Use unique keyframe/class names to avoid collisions with other injected styles.
const sidebarAnimationStyles = `
  @keyframes pfPrinterSidebarSlideInFromRight {
    from {
      transform: translateX(100%);
      opacity: 0;
    }
    to {
      transform: translateX(0);
      opacity: 1;
    }
  }

  @keyframes pfPrinterSidebarSlideOutToRight {
    from {
      transform: translateX(0);
      opacity: 1;
    }
    to {
      transform: translateX(100%);
      opacity: 0;
    }
  }

  .pf-printer-sidebar-enter {
    animation: pfPrinterSidebarSlideInFromRight 0.3s ease-out;
  }

  .pf-printer-sidebar-exit {
    animation: pfPrinterSidebarSlideOutToRight 0.3s ease-in;
  }
`;

const neverSyncedCutoff = new Date('1970-01-01T00:00:00.000Z').getTime();

function shouldRetryStatisticsQuery(failureCount: number, error: unknown) {
  const statusCode = typeof error === 'object' && error
    ? (error as ApiError).statusCode ?? (error as { response?: { status?: number } }).response?.status
    : undefined;

  if (typeof statusCode === 'number' && statusCode >= 400 && statusCode < 500) {
    return false;
  }

  return failureCount < 2;
}

function formatLastSyncTime(lastSyncTime?: string | null) {
  if (!lastSyncTime) {
    return '—';
  }

  const timestamp = Date.parse(lastSyncTime);
  if (!Number.isFinite(timestamp) || timestamp <= neverSyncedCutoff) {
    return '—';
  }

  return new Date(timestamp).toLocaleString();
}

// Inject animation styles (once)
if (typeof document !== 'undefined') {
  const styleId = 'pf-printer-sidebar-animations';
  if (!document.getElementById(styleId)) {
    const style = document.createElement('style');
    style.id = styleId;
    style.textContent = sidebarAnimationStyles;
    document.head.appendChild(style);
  }
}

interface PrinterDetailsSidebarProps {
  printerId: string | null;
  /** Optional printer object - if provided, skips API fetch (useful when parent already has data) */
  printer?: Printer;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onClose: () => void;
  /** Layout mode: drawer panel or bounded content panel. */
  layout?: 'panel' | 'content';
}

export function PrinterDetailsSidebar({ printerId, printer: printerProp, backendCapabilities, onClose, layout = 'panel' }: PrinterDetailsSidebarProps) {
  // Call hooks first before any early returns (React Rules of Hooks)
  // Only fetch if printer prop is not provided
  const shouldFetch = !printerProp && !!printerId;
  const { data: apiPrinter, isLoading, refetch } = usePrinter(shouldFetch ? printerId : '');
  const { data: autoDispatchStatus } = useAutoDispatchStatus(printerId ?? '');
  const queryClient = useQueryClient();
  const { ready: spoolmanReady } = useSpoolmanConfigured();
  
  // The list endpoint (`/api/printers`) returns a compact DTO that can omit the
  // concurrency token, so the passed-in prop may lack `rowVersion`. The single
  // printer GET and the details endpoint both carry it; capture the prop-sourced
  // token here so the details query below can self-fetch the authoritative one
  // when it is missing (independent of Spoolman readiness).
  const propRowVersion = printerProp?.rowVersion ?? apiPrinter?.rowVersion ?? null;
  const support = getPrinterSupport(backendCapabilities);

  // Fetch printer details to check for multi-toolhead configuration and to
  // recover the reviewed revision. Fetch when Spoolman is configured (spool
  // section would be shown) OR when the prop lacks a revision, so the spool
  // mutation guards below always have an authoritative token to review against.
  // Temperature controls also need the authoritative heater limits in details.
  const { data: printerDetails } = usePrinterDetails(
    printerId ?? '', 
    { 
      enabled: !!printerId && (spoolmanReady || !propRowVersion || support.supportsTemperatureControl),
      staleTime: 60000, // Cache for 1 minute since toolhead config doesn't change frequently
    }
  );

  const [isClosing, setIsClosing] = useState(false);
  const closeTimeoutRef = useRef<number | null>(null);

  const handleClose = useCallback(() => {
    if (isClosing) return;
    setIsClosing(true);
    // Match the CSS animation duration (0.3s)
    closeTimeoutRef.current = window.setTimeout(() => {
      onClose();
    }, 300);
  }, [isClosing, onClose]);

  useEffect(() => {
    return () => {
      if (closeTimeoutRef.current !== null) {
        window.clearTimeout(closeTimeoutRef.current);
      }
    };
  }, []);

  const [isStatisticsExpanded, setIsStatisticsExpanded] = useState(false);
  const [isVersionExpanded, setIsVersionExpanded] = useState(true);
  const [isControlExpanded, setIsControlExpanded] = useState(true);
  const [isMoveExpanded, setIsMoveExpanded] = useState(true);

  const [showSpoolPicker, setShowSpoolPicker] = useState(false);
  const [spoolActionPending, setSpoolActionPending] = useState(false);

  const printerStatisticsQuery = useQuery({
    queryKey: ['printerStatistics', printerId],
    queryFn: () => maintenanceService.getPrinterStatistics(printerId!),
    enabled: !!printerId && isStatisticsExpanded,
    staleTime: 60_000,
    gcTime: 10 * 60_000,
    refetchOnWindowFocus: false,
    retry: shouldRetryStatisticsQuery,
  });

  const printerVersionQuery = useQuery({
    queryKey: ['printerVersion', printerId],
    queryFn: () => apiClient.getPrinterVersionInfo(printerId!),
    enabled: !!printerId && isVersionExpanded,
    staleTime: 10 * 60_000,
    gcTime: 60 * 60_000,
    refetchOnWindowFocus: false,
  });

  // Explicit "Refresh version info" bypasses the backend's partial-result cache (#1651) so a
  // recovered Backend/API version is visible immediately instead of waiting out its ~10 minute
  // TTL. This is deliberately a separate mutation from printerVersionQuery's own refetch(), which
  // would just re-read the same stale cache entry — automatic/lazy polling keeps using the
  // normal query above, untouched.
  const refreshVersionMutation = useMutation({
    mutationFn: () => apiClient.getPrinterVersionInfo(printerId!, { forceRefresh: true }),
    onSuccess: (data) => {
      queryClient.setQueryData(['printerVersion', printerId], data);
    },
    onError: (error) => {
      toast.error(mutationErrorMessage(error, 'Failed to refresh version info'));
    },
  });

  // Use provided printer or fall back to API data, merged with realtime SignalR updates
  const basePrinter = printerProp || apiPrinter;
  const printer = usePrinterDisplay((basePrinter || {}) as Printer);

  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [objectToSkip, setObjectToSkip] = useState<PrintJobObjectDto | null>(null);
  const [objectToSkipPrinterId, setObjectToSkipPrinterId] = useState(printerId);

  // Track last known values for display fallback - use state not refs for render access
  const [lastKnownValues, setLastKnownValues] = useState({
    hotendTemp: null as number | null,
    bedTemp: null as number | null,
    x: null as number | null,
    y: null as number | null,
    z: null as number | null,
  });
  const scrollRef = useRef<HTMLDivElement>(null);

  // Poll printer data for PrusaLink only when this component is doing its own fetches.
  // When printer data is provided by parent (`printerProp`), polling here is redundant/no-op.
  useEffect(() => {
    if (!shouldFetch || !printer || printer.backend !== PrinterBackend.PrusaLink || !printerId) {
      return;
    }

    const pollInterval = setInterval(() => {
      if (document.visibilityState !== 'visible') {
        return;
      }
      void refetch();
    }, 5000); // Poll every 5 seconds for PrusaLink as fallback

    return () => clearInterval(pollInterval);
  }, [shouldFetch, printer, refetch, printerId]);

  // Update last known values when printer data changes.
  // Avoid microtask churn and skip state updates when values are unchanged.
  useEffect(() => {
    if (!printer) {
      return;
    }

    setLastKnownValues(prev => {
      const next = {
        hotendTemp: printer.hotendTemp !== undefined ? printer.hotendTemp : prev.hotendTemp,
        bedTemp: printer.bedTemp !== undefined ? printer.bedTemp : prev.bedTemp,
        x: printer.x !== undefined ? printer.x : prev.x,
        y: printer.y !== undefined ? printer.y : prev.y,
        z: printer.z !== undefined ? printer.z : prev.z,
      };

      return (
        next.hotendTemp === prev.hotendTemp &&
        next.bedTemp === prev.bedTemp &&
        next.x === prev.x &&
        next.y === prev.y &&
        next.z === prev.z
      )
        ? prev
        : next;
    });
  }, [printer]);

  const preRenderRawState = printer?.state ?? '';
  const isActivePrintForObjectQuery = isActivePrintForObjects({ state: preRenderRawState, isOnline: printer?.isOnline ?? false });
  // A pending skip confirmation never outlives its print or its printer.
  if (objectToSkipPrinterId !== printerId) {
    setObjectToSkipPrinterId(printerId);
    setObjectToSkip(null);
  } else if (objectToSkip !== null && !(support.supportsObjectExclusion && isActivePrintForObjectQuery)) {
    setObjectToSkip(null);
  }
  const printJobObjectsQuery = usePrintJobObjects(printerId, {
    enabled: !!printerId && support.supportsObjectExclusion && isActivePrintForObjectQuery,
  });
  const excludeObjectMutation = useMutation({
    mutationFn: (name: string) => apiClient.excludePrintJobObject(printerId!, name),
    onSuccess: async (result, name) => {
      if (result.success) {
        toast.success(`Skipped object "${name}"`);
        if (printerId) {
          queryClient.setQueryData<PrintJobObjectListDto>(queryKeys.printJobObjects(printerId), (old) =>
            old
              ? {
                  ...old,
                  objects: old.objects.map((object) =>
                    object.name === name
                      ? { ...object, isExcluded: true, isCurrent: false }
                      : object
                  ),
                }
              : old
          );
        }
        setObjectToSkip(null);
      } else {
        toast.error(`Failed to skip object: ${result.message ?? result.error ?? 'Unknown error'}`);
      }

      if (printerId) {
        await queryClient.invalidateQueries({ queryKey: queryKeys.printJobObjects(printerId) });
      }
    },
    onError: (error: Error) => {
      toast.error(`Failed to skip object: ${error.message}`);
    },
  });

  // Resolved once per render and shared by the materials module and the
  // single-spool fallback, so the two guards cannot disagree. Must sit above
  // the `printerId` early-return below to keep hook order stable.
  const materialLoadout = useMemo(
    () => resolveMaterialLoadout(printer?.mmuStatus, printerDetails?.toolheads, printer?.currentSpoolId, printer?.modelName, printer?.spoolInfo),
    [printer?.mmuStatus, printerDetails?.toolheads, printer?.currentSpoolId, printer?.modelName, printer?.spoolInfo],
  );

  const controls = usePrinterControls(printerId && printer?.id === printerId ? printer : undefined, backendCapabilities, printerDetails, 10);
  const { activeExtruderId, filamentControl } = controls;

  // Guard early after all hooks are called
  if (!printerId) {
    return null;
  }

  // API now returns complete printer DTO with status merged in - no client-side merge needed
  const displayPrinter = printer;
  const hasMoonrakerSensorSnapshot = printer?.backend === PrinterBackend.Moonraker && printer.sensors !== undefined;

  // Authoritative reviewed revision for spool mutations. `displayPrinter`
  // (and thus the passed-in prop) can lack `rowVersion` when it was sourced from
  // the compact list endpoint, so fall back to the details record, which always
  // carries the concurrency token. Never fabricate one — an unresolved token
  // must block the action, not send an unreviewed mutation.
  const spoolReviewedRowVersion = displayPrinter?.rowVersion ?? printerDetails?.rowVersion ?? null;
  const spoolRevisionUnavailable = !spoolReviewedRowVersion;
  const spoolRevisionBlockedReason = 'Printer revision unavailable — refresh to manage spools';
  const sidebarShellClassName = layout === 'content'
    ? `w-full max-w-sm overflow-hidden flex flex-col lg:max-h-[calc(100dvh-5rem)] lg:min-h-0 rounded-lg border border-white/10 bg-pf-sidebar shadow-[0_24px_48px_rgba(0,0,0,0.35)] ring-1 ring-white/5 ${isClosing ? 'pf-printer-sidebar-exit' : 'pf-printer-sidebar-enter'}`
    : `w-[calc(100%-1.5rem)] h-[calc(100%-1.5rem)] m-3 overflow-hidden flex flex-col rounded-lg border border-white/10 bg-pf-sidebar shadow-[0_24px_48px_rgba(0,0,0,0.4)] ring-1 ring-white/5 ${isClosing ? 'pf-printer-sidebar-exit' : 'pf-printer-sidebar-enter'} shrink-0`;

  // Show loading state while fetching printer data
  if (isLoading || !printer) {
    return (
      <div className={`${sidebarShellClassName} z-30 flex items-center justify-center`}>
        <div className="text-pf-text-secondary">Loading...</div>
      </div>
    );
  }

  // State helpers - safe to access displayPrinter now (guaranteed by printer != null)
  const isOnline = displayPrinter?.isOnline ?? false;
  const isEnabled = displayPrinter?.isEnabled ?? true;
  const rawState = displayPrinter?.state ?? 'unknown';
  const isSnapmakerU1Mmu = displayPrinter?.mmuStatus?.mmuType === MmuProtocol.SnapmakerU1;
  const showMmuControlBox = !!displayPrinter?.mmuStatus && !isSnapmakerU1Mmu;
  const statusLabel = getPrinterDisplayState({
    printerState: rawState,
    autoDispatchState: autoDispatchStatus?.state,
    autoDispatchStatus,
    isOnline,
  });
  const isPrinting = rawState.toLowerCase().includes('printing');
  const isPaused = rawState.toLowerCase().includes('paused');
  const isShutdown = isPrinterStateShutdown(rawState);
  const headerClassName = getStatusHeaderClassName({ state: rawState, isOnline, isPrinting, isPaused, isShutdown });
  const statusIndicatorClassName = getStatusIndicatorColor({ state: rawState, isOnline, isPrinting, isPaused, isShutdown });

  const canOpenFilesNow = canOpenFiles({ isOnline, isEnabled, support });
  const canOpenHistoryNow = canOpenHistory({ isOnline, isEnabled, support });
  const canExcludeObjectNow = canExcludeObject({ isOnline, isEnabled, isPrinting, isPaused, support });
  const printJobObjects = printJobObjectsQuery.data?.objects ?? [];

  // Use state values to track last known values for display (fallback when data is undefined)
  const lastKnownHotendTemp = lastKnownValues.hotendTemp;
  const lastKnownBedTemp = lastKnownValues.bedTemp;
  const lastKnownX = lastKnownValues.x;
  const lastKnownY = lastKnownValues.y;
  const lastKnownZ = lastKnownValues.z;

  const formatHours = (hours: number): string => {
    if (!Number.isFinite(hours)) return '—';
    return `${hours.toFixed(1)}h`;
  };

  const formatFilament = (grams: number): string => {
    if (!Number.isFinite(grams)) return '—';
    if (grams >= 1000) return `${(grams / 1000).toFixed(2)}kg`;
    return `${Math.round(grams)}g`;
  };

  const showExternalSpool = displayPrinter?.showExternalSpool ?? printerDetails?.showExternalSpool ?? false;

  return (
    <div
      className={`${sidebarShellClassName} z-30`}
      role="complementary"
      aria-label={`${printer.name} details`}
    >
      {/* Header */}
      <div className={`flex justify-between items-start px-4 pt-4 pb-3 border-b border-white/10 shrink-0 gap-3 ${headerClassName}`}>
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2 mb-1 flex-wrap">
              <h2 className="text-2xl font-bebas uppercase tracking-wide leading-none text-pf-text-primary truncate">{printer.name}</h2>
              <div className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-xs text-xs font-medium shrink-0 bg-black/30 border border-white/20">
                <span className={`h-2 w-2 rounded-full ${statusIndicatorClassName}`} aria-hidden="true" />
                <span className="text-pf-text-primary">{statusLabel}</span>
              </div>
            </div>
            <p className="text-xs text-pf-text-secondary">{formatPrinterModelSubtitle(printer.manufacturerName, printer.modelName)}</p>
            <p className="text-xs text-pf-text-secondary mt-1">Live printer controls and status</p>
          </div>
          <Button
            type="button"
            variant="subtle"
            size="sm"
            onClick={handleClose}
            aria-label="Close sidebar"
            className="p-1! h-auto! shrink-0 bg-black/20 hover:bg-black/30 border border-white/10"
            title="Close sidebar"
            iconCenter={<CloseIcon className="h-6 w-6" />}
          ></Button>
      </div>

      {/* Scrollable Content */}
      <div
        ref={scrollRef}
        className="flex-1 min-h-0 overflow-y-auto p-4 space-y-4 bg-pf-sidebar"
      >
        {/* Statistics */}
        <CollapsibleSection
          title="Statistics"
          expanded={isStatisticsExpanded}
          onToggle={setIsStatisticsExpanded}
          defaultExpanded={false}
          headerActions={
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => void printerStatisticsQuery.refetch()}
              className="p-1! h-auto!"
              title="Refresh statistics"
              aria-label="Refresh statistics"
              iconCenter={<RefreshIcon className="h-4 w-4" />}
            ></Button>
          }
        >
          {printerStatisticsQuery.isLoading ? (
            <div className="text-sm text-pf-text-secondary">Loading statistics…</div>
          ) : printerStatisticsQuery.data ? (
            <dl className="grid grid-cols-2 gap-x-3 gap-y-2 text-xs">
              <div>
                <dt className="text-xs text-pf-text-secondary">Print time</dt>
                <dd className="font-medium text-pf-text-primary">{formatHours(printerStatisticsQuery.data.totalPrintHours)}</dd>
              </div>
              <div>
                <dt className="text-xs text-pf-text-secondary">Filament</dt>
                <dd className="font-medium text-pf-text-primary">{formatFilament(printerStatisticsQuery.data.totalFilamentUsedGrams)}</dd>
              </div>
              <div>
                <dt className="text-xs text-pf-text-secondary">Completed</dt>
                <dd className="font-medium text-pf-text-primary">{printerStatisticsQuery.data.totalJobsCompleted}</dd>
              </div>
              <div>
                <dt className="text-xs text-pf-text-secondary">Failed</dt>
                <dd className="font-medium text-pf-text-primary">{printerStatisticsQuery.data.totalJobsFailed}</dd>
              </div>
              <div className="col-span-2">
                <dt className="text-xs text-pf-text-secondary">Last sync</dt>
                <dd className="text-pf-text-primary">
                  {formatLastSyncTime(printerStatisticsQuery.data.lastSyncTime)}
                </dd>
              </div>
            </dl>
          ) : (
            <div className="text-sm text-pf-text-secondary">Statistics unavailable.</div>
          )}
        </CollapsibleSection>

        {/* Version */}
        <CollapsibleSection
          title="Version"
          expanded={isVersionExpanded}
          onToggle={setIsVersionExpanded}
          headerActions={
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => void refreshVersionMutation.mutate()}
              className="p-1! h-auto!"
              title="Refresh version info"
              aria-label="Refresh version info"
              disabled={refreshVersionMutation.isPending}
              iconCenter={<RefreshIcon className="h-4 w-4" />}
            ></Button>
          }
        >
          {printerVersionQuery.isLoading ? (
            <div className="text-sm text-pf-text-secondary">Loading version…</div>
          ) : printerVersionQuery.data ? (
            <dl className="grid grid-cols-2 gap-x-3 gap-y-2 text-xs">
              <div>
                <dt className="text-xs text-pf-text-secondary">Firmware</dt>
                <dd className="font-medium text-pf-text-primary">{printerVersionQuery.data.firmwareVersion || '—'}</dd>
                <dd className="text-[11px] text-pf-text-secondary">
                  {printerVersionQuery.data.recordedFirmwareIdentity
                    ? 'Recorded firmware identity'
                    : 'No recorded firmware identity'}
                </dd>
              </div>
              <div>
                <dt className="text-xs text-pf-text-secondary">Backend</dt>
                <dd className="font-medium text-pf-text-primary">{printerVersionQuery.data.backendVersion || '—'}</dd>
              </div>
              <div>
                <dt className="text-xs text-pf-text-secondary">API</dt>
                <dd className="font-medium text-pf-text-primary">{printerVersionQuery.data.apiVersion || '—'}</dd>
              </div>
              <div>
                <dt className="text-xs text-pf-text-secondary">Supported</dt>
                <dd className="font-medium text-pf-text-primary">{printerVersionQuery.data.supported ? 'Yes' : 'No'}</dd>
              </div>
              {printerVersionQuery.data.message ? (
                <div className="col-span-2">
                  <dt className="text-xs text-pf-text-secondary">Message</dt>
                  <dd className="text-pf-text-primary wrap-break-word">{printerVersionQuery.data.message}</dd>
                </div>
              ) : null}
            </dl>
          ) : (
            <div className="text-sm text-pf-text-secondary">Version unavailable.</div>
          )}
        </CollapsibleSection>

        {/* Control Section */}
        <CollapsibleSection
          title="Control"
          collapsedTitle="Control and Quick Access"
          expanded={isControlExpanded}
          onToggle={setIsControlExpanded}
          hideExpandedTitle
        >
          <div className="flex gap-4 items-start">
            {/* Control buttons */}
            <PrinterActionBar layout="sidebar" {...controls.actionBarProps} />

            {/* Quick Access buttons */}
            <div className="flex flex-col gap-1">
              <div className="text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide">Quick Access</div>
              <div className="grid grid-cols-2 gap-1 w-fit">
                <ControlPadButton
                  disabled={!canOpenFilesNow}
                  onClick={() => setShowFiles(true)}
                  title="View printer files"
                  aria-label="View printer files"
                  padSize="small"
                >
                  <FileIcon className="h-4 w-4" />
                </ControlPadButton>
                <ControlPadButton
                  disabled={!canOpenHistoryNow}
                  onClick={() => setShowHistory(true)}
                  title="View print history"
                  aria-label="View print history"
                  padSize="small"
                >
                  <HistoryIcon className="h-4 w-4" />
                </ControlPadButton>
              </div>
            </div>
          </div>
        </CollapsibleSection>

        {support.supportsObjectExclusion && isActivePrintForObjectQuery && (
          <CollapsibleSection
            title="Objects"
            expanded={true}
            headerActions={
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => void printJobObjectsQuery.refetch()}
                disabled={!isPrinting || printJobObjectsQuery.isFetching}
                className="p-1! h-auto!"
                title="Refresh print objects"
                aria-label="Refresh print objects"
                iconCenter={<RefreshIcon className="h-4 w-4" />}
              ></Button>
            }
          >
            {printJobObjectsQuery.isLoading ? (
              <div className="text-sm text-pf-text-secondary">Loading print objects…</div>
            ) : printJobObjects.length === 0 ? (
              <div className="text-sm text-pf-text-secondary">No object metadata is available for this job.</div>
            ) : (
              <ul className="space-y-2" aria-label="Current print objects">
                {printJobObjects.map((object) => (
                  <li
                    key={object.name}
                    className="flex items-center justify-between gap-3 rounded-lg border border-white/10 bg-black/15 px-3 py-2"
                  >
                    <div className="min-w-0">
                      <div className="truncate text-sm font-medium text-pf-text-primary">{object.name}</div>
                      <div className="mt-1 flex flex-wrap gap-1 text-[10px] uppercase tracking-wide">
                        {object.isCurrent && (
                          <span className="rounded-xs border border-pf-accent/50 bg-pf-accent-bg px-2 py-0.5 text-pf-accent">
                            Printing
                          </span>
                        )}
                        {object.isExcluded && (
                          <span className="rounded-xs border border-pf-border bg-pf-bg-2 px-2 py-0.5 text-pf-text-secondary">
                            Skipped
                          </span>
                        )}
                      </div>
                    </div>
                    <Button
                      type="button"
                      variant="danger"
                      size="sm"
                      disabled={!canExcludeObjectNow || object.isExcluded || excludeObjectMutation.isPending}
                      onClick={() => setObjectToSkip(object)}
                      aria-label={`Skip object ${object.name}`}
                    >
                      Skip
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </CollapsibleSection>
        )}

        {/* Move Section */}
        <CollapsibleSection
          title="Move"
          collapsedTitle="Movement and Manual Move"
          expanded={isMoveExpanded}
          onToggle={setIsMoveExpanded}
          hideExpandedTitle
        >
          <MovementControlSection
            {...controls.movementProps}
            layout="sidebar"
            printerX={lastKnownX}
            printerY={lastKnownY}
            printerZ={lastKnownZ}
          />
        </CollapsibleSection>

        {/* Temperatures Section */}
        <PrinterSensorsSection
          printerId={printer.id}
          sensors={displayPrinter?.sensors}
          temperatures={{ ...displayPrinter, activeExtruderId }}
          supportsTelemetry={backendCapabilities?.supportsSensorTelemetry}
          canControl={(backendCapabilities?.supportsSensorControl ?? false) && isEnabled}
          isOnline={isOnline}
        >
          {additionalRows => <TemperatureControlSection
            {...controls.temperatureProps}
            printerId={printer.id}
            showHeading={false}
            additionalRows={additionalRows}
            hotendTarget={displayPrinter?.hotendTarget}
            bedTarget={displayPrinter?.bedTarget}
            hotendCurrent={hasMoonrakerSensorSnapshot ? displayPrinter?.hotendTemp : displayPrinter?.hotendTemp ?? lastKnownHotendTemp ?? undefined}
            bedCurrent={hasMoonrakerSensorSnapshot ? displayPrinter?.bedTemp : displayPrinter?.bedTemp ?? lastKnownBedTemp ?? undefined}
            reportedTemperatures={displayPrinter ?? {}}
          />}
        </PrinterSensorsSection>

        <RuntimeFactorControls printer={displayPrinter} backendCapabilities={backendCapabilities} />

        {/* MMU Control Box - the single slot representation when MMU/ERCF/AMS is
            detected via real-time status; spool assignment lives in its action row. */}
        {showMmuControlBox && displayPrinter?.mmuStatus && (
          <CollapsibleSection title="Materials" keepMounted>
            <MmuControlBox
              printerId={printer.id}
              mmuStatus={displayPrinter.mmuStatus}
              isOnline={isOnline}
              toolheads={printerDetails?.toolheads}
              modelName={printer.modelName}
              reviewedRowVersion={spoolReviewedRowVersion ?? undefined}
              showExternalSpool={showExternalSpool}
              filamentControl={filamentControl}
              onSpoolChange={() => {
                queryClient.invalidateQueries({ queryKey: ['printers', printer.id, 'details'] });
              }}
            />
          </CollapsibleSection>
        )}

        {/* Consolidated materials module — one slot list drives the rail, the
            coverage rings and the assignment drawer. Suppressed when the MMU
            control box already shows the slots so there is exactly one view. */}
        {materialLoadout && !showMmuControlBox && (
          <CollapsibleSection title="Materials" keepMounted>
            <MaterialLoadout
              printerId={printer.id}
              mmuStatus={displayPrinter?.mmuStatus}
              toolheads={printerDetails?.toolheads}
              currentSpoolId={printer?.currentSpoolId}
              spoolInfo={printer?.spoolInfo}
              modelName={printer?.modelName}
              reviewedRowVersion={spoolReviewedRowVersion ?? undefined}
              isOnline={isOnline}
              filamentControl={filamentControl}
              onSpoolChange={() => {
                queryClient.invalidateQueries({ queryKey: ['printers', printer.id, 'details'] });
              }}
            />
          </CollapsibleSection>
        )}

        {/* Single-spool printers keep the classic spool card; multi-slot printers are
            fully described by the materials module above. */}
        {(spoolmanReady || displayPrinter?.spoolInfo || displayPrinter?.currentSpoolId) && (() => {
          if (materialLoadout || showMmuControlBox) return null;

          return (
            <CollapsibleSection
              title="Materials"
              keepMounted
              headerActions={
                <div className="flex items-center gap-0.5">
                  {(displayPrinter?.spoolInfo?.hasActiveSpool || displayPrinter?.currentSpoolId) && (
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        disabled={spoolActionPending || spoolRevisionUnavailable}
                        explainedDisabled={spoolRevisionUnavailable}
                        onClick={async () => {
                          setSpoolActionPending(true);
                          try {
                            const reviewedRowVersion = spoolReviewedRowVersion;
                            if (!reviewedRowVersion) {
                              toast.error('Printer revision unavailable. Refresh and review again.');
                              return;
                            }
                            const nextRowVersion = await apiClient.clearActiveSpool(
                              printer.id,
                              reviewedRowVersion
                            );
                            // Optimistically update cached printers to clear spool info
                            // SignalR will deliver the authoritative update on next status cycle
                            queryClient.setQueryData<Printer[]>(['printers'], (old) =>
                              old?.map(p => p.id === printer.id
                                ? {
                                    ...p,
                                    rowVersion: nextRowVersion,
                                    spoolInfo: { hasActiveSpool: false },
                                  }
                                : p
                              )
                            );
                          } catch (err) {
                            console.error('Failed to eject spool:', err);
                            if ([412, 428].includes(mutationErrorStatus(err) ?? 0)) {
                              await queryClient.invalidateQueries({
                                queryKey: ['printers'],
                              });
                            }
                            toast.error(
                              mutationErrorMessage(err, 'Failed to eject spool')
                            );
                          } finally {
                            setSpoolActionPending(false);
                          }
                        }}
                        className="p-1! h-auto!"
                        title={spoolRevisionUnavailable ? spoolRevisionBlockedReason : 'Eject spool'}
                        aria-label={spoolRevisionUnavailable ? `Eject spool — ${spoolRevisionBlockedReason}` : 'Eject spool'}
                        iconCenter={<EjectIcon className="h-4 w-4" />}
                      ></Button>
                    )}
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      disabled={spoolActionPending || spoolRevisionUnavailable}
                      explainedDisabled={spoolRevisionUnavailable}
                      onClick={() => setShowSpoolPicker(true)}
                      className="p-1! h-auto!"
                      title={spoolRevisionUnavailable ? spoolRevisionBlockedReason : 'Change spool'}
                      aria-label={spoolRevisionUnavailable ? `Change spool — ${spoolRevisionBlockedReason}` : 'Change spool'}
                      iconCenter={<FilamentChangeIcon className="h-4 w-4" />}
                    ></Button>
                  </div>
              }
            >
              <FilamentCoverageBreakdown printerId={printer.id} isOnline={isOnline} />
              <LoadedFilamentCard spoolInfo={displayPrinter?.spoolInfo ?? (displayPrinter?.currentSpoolId ? { hasActiveSpool: true, activeSpoolId: displayPrinter.currentSpoolId } : undefined)} />
            </CollapsibleSection>
          );
        })()}
        <CollapsibleSection title="Camera bed-clear evidence" defaultExpanded={false}>
          <BedClearEvidencePanel printerId={printer.id} />
        </CollapsibleSection>
        {window.PrintFarmerDebug?.expandablePrinterCardDisplay && (
          <div className="mt-3 p-2 bg-pf-bg-0 border border-pf-border rounded-sm text-xs text-pf-text-tertiary">
            {renderUnknown({ status, lastKnownHotendTemp, lastKnownBedTemp, lastKnownX, lastKnownY, lastKnownZ })}
          </div>
        )}
      </div>

      {/* History Modal */}
      <PrinterHistoryModal
        isOpen={showHistory}
        onClose={() => setShowHistory(false)}
        printer={printer}
      />

      {/* Files Modal */}
      <PrinterFilesModal
        isOpen={showFiles}
        onClose={() => setShowFiles(false)}
        printer={printer}
      />

      <CancelPrintConfirmation {...controls.cancelConfirmation} />

      <Modal
        isOpen={objectToSkip !== null && support.supportsObjectExclusion && isActivePrintForObjectQuery}
        onClose={() => {
          if (!excludeObjectMutation.isPending) {
            setObjectToSkip(null);
          }
        }}
        title="Skip print object?"
        size="sm"
        isDisabled={excludeObjectMutation.isPending}
        footer={(
          <div className="flex justify-end gap-2">
            <Button
              type="button"
              variant="secondary"
              onClick={() => setObjectToSkip(null)}
              disabled={excludeObjectMutation.isPending}
            >
              Keep printing
            </Button>
            <Button
              type="button"
              variant="danger"
              loading={excludeObjectMutation.isPending}
              onClick={() => {
                if (objectToSkip && canExcludeObjectNow) {
                  excludeObjectMutation.mutate(objectToSkip.name);
                }
              }}
            >
              Skip object
            </Button>
          </div>
        )}
      >
        <p className="text-sm text-pf-text-primary">
          Skip <span className="font-semibold">{objectToSkip?.name}</span> for the active print on {printer.name}?
        </p>
        <p className="mt-2 text-xs text-pf-text-secondary">
          The printer will continue printing the remaining objects. This action cannot be undone from PrintFarmer.
        </p>
      </Modal>

      {/* Spool Picker Modal */}
      <SpoolPickerModal
        isOpen={showSpoolPicker}
        onClose={() => setShowSpoolPicker(false)}
        printerId={printer.id}
        activeSpoolId={displayPrinter?.spoolInfo?.activeSpoolId ?? displayPrinter?.currentSpoolId}
        onSelect={async (spoolId, spool) => {
          setSpoolActionPending(true);
          try {
            const reviewedRowVersion = spoolReviewedRowVersion;
            if (!reviewedRowVersion) {
              toast.error('Printer revision unavailable. Refresh and review again.');
              return;
            }
            const nextRowVersion = await apiClient.setActiveSpool(
              printer.id,
              spoolId,
              reviewedRowVersion
            );
            setShowSpoolPicker(false);
            // Optimistically update cached printers with new spool info
            queryClient.setQueryData<Printer[]>(['printers'], (old) =>
              old?.map(p => p.id === printer.id
                ? {
                    ...p,
                    rowVersion: nextRowVersion,
                    currentSpoolId: spool.id,
                    spoolInfo: {
                      hasActiveSpool: true,
                      activeSpoolId: spool.id,
                      spoolName: spool.name,
                      material: spool.material,
                      colorHex: spool.colorHex ?? undefined,
                      filamentName: spool.filamentName ?? undefined,
                      vendor: spool.vendor ?? undefined,
                      remainingWeightG: spool.remainingWeightG ?? undefined,
                      spoolInUse: true,
                    },
                  }
                : p
              )
            );
            // SignalR will deliver the authoritative update on next status cycle
          } catch (err) {
            console.error('Failed to set active spool:', err);
            if ([412, 428].includes(mutationErrorStatus(err) ?? 0)) {
              await queryClient.invalidateQueries({ queryKey: ['printers'] });
            }
            toast.error(
              mutationErrorMessage(err, 'Failed to set active spool')
            );
          } finally {
            setSpoolActionPending(false);
          }
        }}
      />
    </div>
  );
}
