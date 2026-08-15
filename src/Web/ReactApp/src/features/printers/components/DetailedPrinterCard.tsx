import React, { Suspense, useState, useRef, useEffect, useMemo } from 'react';
import './DetailedPrinterCard.css';
import { Zap } from 'lucide-react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSpoolmanConfigured } from '@/common/hooks/useSpoolmanConfigured';
import { apiClient } from '@/services/api';
import { maintenanceService } from '@/services/maintenanceService';
import {
  mutationErrorMessage,
  mutationErrorStatus,
} from '@/common/utils/mutationError';
import { queryKeys, usePrintJobObjects } from '@/common/hooks/useApi';
import type {
  Printer,
  TempTargets,
  MoveRequest,
  PrinterBackendCapabilitiesDto,
  PrintJobObjectDto,
  PrintJobObjectListDto,
  ApiError,
} from '@/types/api';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { MaterialLoadout } from '@/features/printers/components/MaterialLoadout';
import { resolveMaterialLoadout } from '@/features/printers/utils/materialLoadout';
import { TemperatureControlSection } from '@/features/printers/components/TemperatureControlSection';
import { MovementControlSection } from '@/features/printers/components/MovementControlSection';
import { FilamentControlSection } from '@/features/printers/components/FilamentControlSection';
import type { ZOffsetCalibrationWizardProps } from '@/features/printers/components/ZOffsetCalibrationWizard';
import { PrinterActionBar } from '@/features/printers/components/PrinterActionBar';
import { BedClearBanner } from '@/features/printers/components/BedClearBanner';
import { PrintProgressBar } from '@/features/printers/components/PrintProgressBar';
import { EstimatedCompletionBadge } from '@/features/printers/components/EstimatedCompletionBadge';
import { useAutoDispatchStatus, useSetAutoDispatchEnabled } from '@/features/printers/hooks/useAutoDispatch';
import { useFailureDetectionAlert } from '@/features/printers/hooks/useFailureDetectionAlert';
import { usePrinterFailureDetectionStatus } from '@/features/printers/hooks/usePrinterFailureDetectionStatus';
import { useFailureDetectionPollingEnabled } from '@/features/printers/hooks/useFailureDetectionPolling';
import { toast } from 'sonner';
import { Button, CollapsibleSection, LoadedFilamentCard } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { FilamentCoverageBreakdown } from '@/features/filament-coverage/components/FilamentCoverageBreakdown';
import { 
  EditIcon, 
  ExternalLinkIcon,
  CameraIcon,
  HistoryIcon,
  FileIcon,
  FilamentChangeIcon,
  EjectIcon,
  RefreshIcon,
} from '@/common/components/icons/MdiIcons';
import { usePrinterDetails } from '@/common/hooks/useApi';
import type { PrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import { getPrinterDisplayState, requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import { FailureDetectionBadge } from '@/features/printers/components/FailureDetectionBadge';
import { FailureDetectionMonitoringBadge } from '@/features/printers/components/FailureDetectionMonitoringBadge';
import { FailureDetectionMonitoringSummary } from '@/features/printers/components/FailureDetectionMonitoringSummary';
import { OfflineTroubleshootingGuide } from '@/features/printers/components/OfflineTroubleshootingGuide';
import { PrinterCameraPreview } from '@/features/printers/components/PrinterCameraPreview';
import {
  canCancel,
  canCooldown,
  canDisableMotors,
  canEmergencyStop,
  canExcludeObject,
  canFilamentChange,
  canFilamentControl,
  canMove,
  canOpenFiles,
  canOpenHistory,
  canPauseOrResume,
  canSetStep,
  canSetTemperatures,
  canUseManualMove,
  getPrinterSupport,
} from '@/features/printers/utils/printerSupport';
import { getStatusHeaderClassName } from '@/features/printers/utils/statusColors';
import {
  getPresetTargets,
  getExtrudeMinTemp,
  DEFAULT_EXTRUDE_DISTANCE_MM,
  DEFAULT_EXTRUDE_SPEED_MMS,
} from '@/features/printers/constants/temperaturePresets';
import { lazyWithPreload } from '@/common/utils/lazyWithPreload';
import { isSafeHttpUrl, isBrowserReachableUrl, toSafeHref } from '@/common/utils/validation';

// Interaction-only: the Z-offset calibration wizard is a modal only opened
// via an explicit "Calibrate Z-Offset" click, so it's lazy-loaded out of the
// detailed-card bundle (#1146 item 10).
const ZOffsetCalibrationWizard = lazyWithPreload<ZOffsetCalibrationWizardProps, React.FC<ZOffsetCalibrationWizardProps>>(
  () => import('@/features/printers/components/ZOffsetCalibrationWizard').then(m => ({ default: m.ZOffsetCalibrationWizard }))
);

export interface DetailedPrinterCardProps {
  /** Display-ready printer — parents should pass data already merged with SignalR (usePrinterDisplays) */
  printer: Printer | PrinterDisplay;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onEdit?: (printer: Printer) => void;
}

function shouldRetryStatisticsQuery(failureCount: number, error: unknown) {
  const statusCode = typeof error === 'object' && error
    ? (error as ApiError).statusCode ?? (error as { response?: { status?: number } }).response?.status
    : undefined;

  if (typeof statusCode === 'number' && statusCode >= 400 && statusCode < 500) {
    return false;
  }

  return failureCount < 2;
}

const neverSyncedCutoff = new Date('1970-01-01T00:00:00.000Z').getTime();

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

function formatHours(hours: number): string {
  if (!Number.isFinite(hours)) return '—';
  return `${hours.toFixed(1)}h`;
}

function formatFilament(grams: number): string {
  if (!Number.isFinite(grams)) return '—';
  if (grams >= 1000) return `${(grams / 1000).toFixed(2)}kg`;
  return `${Math.round(grams)}g`;
}

// Memoized: with stable callbacks and structural sharing upstream, a card only
// re-renders when its own printer's data actually changed.
export const DetailedPrinterCard = React.memo(function DetailedPrinterCard({ printer, backendCapabilities, onEdit }: DetailedPrinterCardProps) {
  const queryClient = useQueryClient();
  const { ready: spoolmanReady } = useSpoolmanConfigured();
  const mmuStatus = (printer as PrinterDisplay).mmuStatus;
  const browserUrl = printer.frontendUrl && isBrowserReachableUrl(printer.frontendUrl)
    ? toSafeHref(printer.frontendUrl)
    : undefined;
  // Syntactically safe but known-unreachable from the browser (e.g. TestEmulator's
  // internal `testemulator-<guid>` hostname, #1546) — distinct from "no URL at all"
  // so the disabled action can explain *why* instead of just saying "unavailable".
  const isInternalOnlyBrowserUrl = !!printer.frontendUrl
    && isSafeHttpUrl(printer.frontendUrl)
    && !isBrowserReachableUrl(printer.frontendUrl);

  const [showCamera, setShowCamera] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [showSpoolPicker, setShowSpoolPicker] = useState(false);
  const [showZOffsetWizard, setShowZOffsetWizard] = useState(false);
  const [controlActionPending, setControlActionPending] = useState(false);
  const [temperatureActionPending, setTemperatureActionPending] = useState(false);
  const [movementActionPending, setMovementActionPending] = useState(false);
  const [filamentActionPending, setFilamentActionPending] = useState(false);
  const [spoolActionPending, setSpoolActionPending] = useState(false);
  const [hotendTemp, setHotendTemp] = useState<number | string>('');
  const [bedTemp, setBedTemp] = useState<number | string>('');
  const [moveX, setMoveX] = useState<number | string>('');
  const [moveY, setMoveY] = useState<number | string>('');
  const [moveZ, setMoveZ] = useState<number | string>('');
  const [step, setStep] = useState(1);
  const [extrudeStep, setExtrudeStep] = useState(DEFAULT_EXTRUDE_DISTANCE_MM);
  const [extrudeSpeed, setExtrudeSpeed] = useState(DEFAULT_EXTRUDE_SPEED_MMS);
  const [isStatisticsExpanded, setIsStatisticsExpanded] = useState(false);
  const [isVersionExpanded, setIsVersionExpanded] = useState(false);
  const [objectToSkip, setObjectToSkip] = useState<PrintJobObjectDto | null>(null);

  // This card always needs `printerDetails` once Spoolman is ready, because
  // every path it can take needs the persisted toolhead topology:
  //   - No live MMU gates: `printerDetails.toolheads` is the only signal that
  //     distinguishes a printer with independent physical toolheads, and the
  //     collapsed badges must render without a click.
  //   - Live MMU gates present: the persisted topology is what lets
  //     `MaterialLoadout` map a live gate index (offset by the shared hotend)
  //     to the backend API index. Without it `hasResolvedTopology` is false and
  //     the module blocks every assignment, so the materials UI is a dead end.
  //   - Z-Offset wizard: this component's other `printerDetails` consumer.
  //
  // Those cases are exhaustive, so there is deliberately NO deferral condition
  // here. #1146 item 4 originally deferred the fetch until the wizard opened;
  // the MMU index-mapping requirement above superseded that, since deferring
  // would leave the materials module permanently unable to assign a spool.
  // Do not reintroduce a condition on MMU gate presence — a gate-based
  // predicate is what silently produced that dead end.
  const { data: printerDetails } = usePrinterDetails(
    printer.id,
    {
      enabled: spoolmanReady,
      staleTime: 60000,
    }
  );

  // One resolution per render, shared by the materials module and the
  // single-spool fallback below. Calling the resolver separately in each
  // branch let the two guards drift out of sync (and did the work twice).
  const materialLoadout = useMemo(
    () => resolveMaterialLoadout(mmuStatus, printerDetails?.toolheads),
    [mmuStatus, printerDetails?.toolheads],
  );

  const { event: recentFailure, recentEvents = [] } = useFailureDetectionAlert(printer.id);
  // Polling for the shared failure-detection status query is controlled once
  // at the fleet level (`FailureDetectionPollingProvider` in `PrintersPage`),
  // not per-card from just this printer's own `obicoEnabled` flag (#1146
  // item 3) — every card in the grid shares the same enabled decision.
  const failureDetectionPollingEnabled = useFailureDetectionPollingEnabled();
  const { printerStatus: failureDetectionStatus } = usePrinterFailureDetectionStatus(
    printer.id,
    failureDetectionPollingEnabled
  );

  const expandedProgressRef = useRef<HTMLDivElement>(null);

  // Auto-dispatch opt-in status
  const { data: autoDispatchStatus } = useAutoDispatchStatus(printer.id);
  const setAutoDispatchEnabled = useSetAutoDispatchEnabled();

  const handleAutoDispatchToggle = async () => {
    const newEnabled = !(autoDispatchStatus?.enabled ?? false);
    try {
      if (!autoDispatchStatus?.dispatchStateETag || !autoDispatchStatus.printerETag) {
        throw new Error('Refresh the printer before changing auto-dispatch.');
      }
      await setAutoDispatchEnabled.mutateAsync({
        printerId: printer.id,
        enabled: newEnabled,
        dispatchStateETag: autoDispatchStatus.dispatchStateETag,
        printerETag: autoDispatchStatus.printerETag,
      });
      toast.success(newEnabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled');
    } catch {
      toast.error('Failed to toggle auto-dispatch');
    }
  };

  // Keep target temperature inputs in sync with the printer's actual targets via SignalR
  useEffect(() => {
    const hotend = printer.hotendTarget ?? 0;
    setHotendTemp(hotend > 0 ? hotend : '');
    const bed = printer.bedTarget ?? 0;
    setBedTemp(bed > 0 ? bed : '');
  }, [printer.hotendTarget, printer.bedTarget]);

  // Determine colors based on state
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isEnabled = printer.isEnabled ?? true;
  const isPrinting = isOnline && state === 'Printing';
  const isPaused = state === 'Paused';
  const isShutdown = state === 'Offline' || state === 'Shutdown' || state === 'Halted';
  const statusLabel = getPrinterDisplayState({
    printerState: state,
    autoDispatchState: autoDispatchStatus?.state,
    autoDispatchStatus,
    isOnline,
  });
  const isPendingReady = requiresBedClearConfirmation(autoDispatchStatus, state);
  const support = getPrinterSupport(backendCapabilities);

  const canPauseOrResumeNow = canPauseOrResume({ isOnline, isEnabled, isPrinting, isPaused, support });
  const canCancelNow = canCancel({ isOnline, isEnabled, isPrinting, isPaused, support });
  const canEmergencyStopNow = canEmergencyStop({ isOnline, isEnabled, support });
  const canDisableMotorsNow = canDisableMotors({ isOnline, isEnabled, isPrinting, support });
  const canMoveNow = canMove({ isOnline, isEnabled, isPrinting, support });
  const canSetStepNow = canSetStep({ isOnline, support });
  const canManualMoveNow = canUseManualMove({ isOnline, isEnabled, isPrinting, support });
  const canSetTemperaturesNow = canSetTemperatures({ isOnline, isEnabled, support });
  const canCooldownNow = canCooldown({ isOnline, isEnabled, isPrinting, support });
  const canOpenHistoryNow = canOpenHistory({ isOnline, isEnabled, support });

  const canOpenFilesNow = canOpenFiles({ isOnline, isEnabled, support });
  const canExcludeObjectNow = canExcludeObject({ isOnline, isEnabled, isPrinting, isPaused, support });

  const extrudeMinTemp = getExtrudeMinTemp(printer.spoolInfo?.material);
  const canExtrudeNow = canMoveNow && (printer.hotendTemp ?? 0) >= extrudeMinTemp;

  // Print statistics and version info: folded in from the details sidebar
  // (#1584) so the detailed card shows the same level of print detail
  // without needing to open a separate sidebar. Queries stay disabled until
  // their section is expanded, matching the sidebar's lazy-fetch behavior.
  const printerStatisticsQuery = useQuery({
    queryKey: ['printerStatistics', printer.id],
    queryFn: () => maintenanceService.getPrinterStatistics(printer.id),
    enabled: isStatisticsExpanded,
    staleTime: 60_000,
    gcTime: 10 * 60_000,
    refetchOnWindowFocus: false,
    retry: shouldRetryStatisticsQuery,
  });

  const printerVersionQuery = useQuery({
    queryKey: ['printerVersion', printer.id],
    queryFn: () => apiClient.getPrinterVersionInfo(printer.id),
    enabled: isVersionExpanded,
    staleTime: 10 * 60_000,
    gcTime: 60 * 60_000,
    refetchOnWindowFocus: false,
  });

  const isActivePrintForObjectQuery = isPrinting || isPaused;
  const printJobObjectsQuery = usePrintJobObjects(printer.id, {
    enabled: support.supportsObjectExclusion && isActivePrintForObjectQuery,
  });
  const printJobObjects = printJobObjectsQuery.data?.objects ?? [];

  const excludeObjectMutation = useMutation({
    mutationFn: (name: string) => apiClient.excludePrintJobObject(printer.id, name),
    onSuccess: async (result, name) => {
      if (result.success) {
        toast.success(`Skipped object "${name}"`);
        queryClient.setQueryData<PrintJobObjectListDto>(queryKeys.printJobObjects(printer.id), (old) =>
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
        setObjectToSkip(null);
      } else {
        toast.error(`Failed to skip object: ${result.message ?? result.error ?? 'Unknown error'}`);
      }

      await queryClient.invalidateQueries({ queryKey: queryKeys.printJobObjects(printer.id) });
    },
    onError: (error: Error) => {
      toast.error(`Failed to skip object: ${error.message}`);
    },
  });

  const homedAxesRaw = printer.homedAxes;

  const headerClassName = getStatusHeaderClassName({ state, isOnline, isPrinting, isPaused, isShutdown });

  // Camera source handling
  const cameraSnapshotUrl = printer.cameraSnapshotUrl ?? null;
  const cameraStreamUrl = printer.cameraStreamUrl ?? null;
  const hasCameraUrls = !!(cameraStreamUrl || cameraSnapshotUrl);

  const handleControlAction = async (action: string) => {
    if (controlActionPending) {
      return;
    }

    setControlActionPending(true);
    try {
      let result;
      switch (action) {
        case 'pause':
          result = await apiClient.pausePrint(printer.id);
          break;
        case 'resume':
          result = await apiClient.resumePrint(printer.id);
          break;
        case 'cancel':
          result = await apiClient.cancelPrint(printer.id);
          break;
        case 'stop':
          result = await apiClient.emergencyStop(printer.id);
          break;
        case 'firmware-restart':
          result = await apiClient.firmwareRestart(printer.id);
          break;
        case 'disable-motors':
          result = await apiClient.disableMotors(printer.id);
          break;
        default:
          console.warn(`Unknown action: ${action}`);
          return;
      }

      if (!result.success) {
        console.error(`Failed to ${action}:`, result.error);
      }
    } catch (error) {
      console.error(`Error during ${action}:`, error);
    } finally {
      setControlActionPending(false);
    }
  };

  const handleFilamentAction = async (action: 'load' | 'unload' | 'change') => {
    if (filamentActionPending) {
      return;
    }

    setFilamentActionPending(true);
    try {
      const methodMap: Record<string, () => Promise<{ success: boolean; error?: string | null }>> = {
        load: () => apiClient.loadFilament(printer.id),
        unload: () => apiClient.unloadFilament(printer.id),
        change: () => apiClient.changeFilament(printer.id),
      };
      const result = await methodMap[action]();
      if (!result.success) {
        console.error(`Failed to ${action} filament:`, result.error);
      }
    } catch (error) {
      console.error(`Error during filament ${action}:`, error);
    } finally {
      setFilamentActionPending(false);
    }
  };

  const handleStepChange = (newStep: number) => {
    setStep(newStep);
  };

  const handleHotendTempKeyDown = async (e: React.KeyboardEvent) => {
    if (e.key !== 'Enter' || hotendTemp === '' || temperatureActionPending) {
      return;
    }

    setTemperatureActionPending(true);
    try {
      const currentBedTarget = bedTemp === '' ? (printer.bedTarget ?? 0) : Number(bedTemp);
      const targets: TempTargets = { hotend: Number(hotendTemp), bed: currentBedTarget };
      const result = await apiClient.setTemperatures(printer.id, targets);
      if (!result.success) {
        console.error(`Failed to set hotend temp:`, result.error);
      }
    } catch (error) {
      console.error('Error setting hotend temperature:', error);
    } finally {
      setTemperatureActionPending(false);
    }
  };

  const handleBedTempKeyDown = async (e: React.KeyboardEvent) => {
    if (e.key !== 'Enter' || bedTemp === '' || temperatureActionPending) {
      return;
    }

    setTemperatureActionPending(true);
    try {
      const currentHotendTarget = hotendTemp === '' ? (printer.hotendTarget ?? 0) : Number(hotendTemp);
      const targets: TempTargets = { hotend: currentHotendTarget, bed: Number(bedTemp) };
      const result = await apiClient.setTemperatures(printer.id, targets);
      if (!result.success) {
        console.error(`Failed to set bed temp:`, result.error);
      }
    } catch (error) {
      console.error('Error setting bed temperature:', error);
    } finally {
      setTemperatureActionPending(false);
    }
  };

  const handleApplyPreset = async (preset: string) => {
    if (temperatureActionPending) {
      return;
    }

    setTemperatureActionPending(true);
    try {
      const targets = preset.toLowerCase() === 'cooldown'
        ? { hotend: 0, bed: 0 }
        : getPresetTargets(preset);

      if (!targets) {
        console.warn(`Unknown preset: ${preset}`);
        return;
      }
      
      const result = await apiClient.setTemperatures(printer.id, targets);
      
      if (result.success) {
        setHotendTemp(targets.hotend);
        setBedTemp(targets.bed);
      } else {
        console.error(`Failed to apply ${preset} preset:`, result.error);
      }
    } catch (error) {
      console.error(`Error applying ${preset} preset:`, error);
    } finally {
      setTemperatureActionPending(false);
    }
  };

  const handleApplySingleHeaterPreset = async (heater: 'hotend' | 'bed', preset: string) => {
    if (temperatureActionPending) {
      return;
    }

    const presetTargets = getPresetTargets(preset);
    if (!presetTargets) {
      console.warn(`Unknown preset: ${preset}`);
      return;
    }

    const currentHotend = hotendTemp === '' ? (printer.hotendTarget ?? 0) : Number(hotendTemp);
    const currentBed = bedTemp === '' ? (printer.bedTarget ?? 0) : Number(bedTemp);
    const targets: TempTargets = heater === 'hotend'
      ? { hotend: presetTargets.hotend, bed: currentBed }
      : { hotend: currentHotend, bed: presetTargets.bed };

    setTemperatureActionPending(true);
    try {
      const result = await apiClient.setTemperatures(printer.id, targets);
      if (result.success) {
        setHotendTemp(targets.hotend);
        setBedTemp(targets.bed);
      } else {
        console.error(`Failed to apply ${heater} preset:`, result.error);
      }
    } catch (error) {
      console.error(`Error applying ${heater} preset:`, error);
    } finally {
      setTemperatureActionPending(false);
    }
  };

  const handleMove = async (axis: 'X' | 'Y' | 'Z', distance: number) => {
    if (movementActionPending) {
      return;
    }

    setMovementActionPending(true);
    try {
      const move: MoveRequest = {};
      move[axis.toLowerCase() as keyof MoveRequest] = distance;
      
      const result = await apiClient.movePrinter(printer.id, move);
      
      if (!result.success) {
        console.error(`Failed to move ${axis} by ${distance}:`, result.error);
      }
    } catch (error) {
      console.error(`Error moving ${axis}:`, error);
    } finally {
      setMovementActionPending(false);
    }
  };

  const handleHome = async (axes?: string) => {
    if (movementActionPending) {
      return;
    }

    setMovementActionPending(true);
    try {
      let result;
      
      if (!axes || axes === 'all') {
        result = await apiClient.homePrinter(printer.id);
      } else if (axes === 'xy') {
        result = await apiClient.homeXY(printer.id);
      } else if (axes === 'z') {
        result = await apiClient.homeZ(printer.id);
      } else {
        console.warn(`Unknown home axes: ${axes}`);
        return;
      }
      
      if (!result.success) {
        console.error(`Failed to home ${axes || 'all'}:`, result.error);
      }
    } catch (error) {
      console.error(`Error homing ${axes || 'all'}:`, error);
    } finally {
      setMovementActionPending(false);
    }
  };

  const handleExtrude = async (direction: 'extrude' | 'retract') => {
    if (movementActionPending) return;

    setMovementActionPending(true);
    try {
      const distance = direction === 'extrude' ? extrudeStep : -extrudeStep;
      const feedrate = extrudeSpeed * 60; // mm/s to mm/min
      const result = await apiClient.extrudeFilament(
        printer.id,
        distance,
        feedrate
      );
      if (!result.success) {
        console.error(`Failed to ${direction}:`, result.error);
      }
    } catch (error) {
      console.error(`Error ${direction}ing:`, error);
    } finally {
      setMovementActionPending(false);
    }
  };

  const handleViewHistory = () => {
    setShowHistory(true);
  };

  return (
    <article
      data-pf-card
      className="pf-detailed-printer-card relative rounded-lg border border-white/10 bg-pf-card p-3 shadow-lg w-full transition-all duration-200 hover:-translate-y-0.5 hover:border-pf-accent/40 hover:shadow-2xl motion-reduce:transition-none motion-reduce:hover:-translate-y-0"
      style={{ transform: 'translateZ(0)' }}
    >
      {/* Colored header — background tinted by printer state */}
      <div className={`px-3 pt-3 pb-2 rounded-t-lg -mx-3 -mt-3 ${headerClassName}`}>
        <div className="flex items-center gap-2">
          <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase tracking-wide truncate">
            {printer.name}
          </div>
          <div className="inline-flex items-center px-2 py-0.5 rounded-xs text-xs font-medium shrink-0 bg-black/30 border border-white/20">
            <span className="text-pf-text-primary font-medium">
              {statusLabel}
            </span>
          </div>
          <FailureDetectionMonitoringBadge
            enabled={!!printer.obicoEnabled}
            status={failureDetectionStatus}
            isPrinting={isPrinting}
            printerId={printer.id}
            printerName={printer.name}
            recentEvents={recentEvents}
          />
          {recentFailure && <FailureDetectionBadge event={recentFailure} />}
        </div>
      </div>

      {/* Header actions */}
      <div className="mb-4 mt-3">

        {/* Action buttons row */}
        <div className="flex w-full items-center justify-between gap-2" role="toolbar" aria-label="Printer actions">
          <div className="flex items-center gap-1">
            {browserUrl ? (
              <a
                href={browserUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="text-pf-text-secondary hover:text-pf-text-primary shrink-0 h-8 w-8 inline-flex items-center justify-center rounded-xs"
                aria-label={`Open printer ${printer.name} in new tab`}
                title={`Open printer ${printer.name}`}
              >
                <ExternalLinkIcon className="h-4 w-4" />
              </a>
            ) : (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled
                explainedDisabled
                className="h-8 w-8 p-0 text-pf-text-secondary"
                aria-label={isInternalOnlyBrowserUrl
                  ? `Open in Browser unavailable for printer ${printer.name}: not available for simulated test printers`
                  : `Printer browser URL unavailable for ${printer.name}`}
                title={isInternalOnlyBrowserUrl
                  ? 'Not available for simulated test printers'
                  : 'Printer browser URL is unavailable'}
                iconCenter={<ExternalLinkIcon className="h-4 w-4" />}
              />
            )}

            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => setShowCamera(!showCamera)}
              disabled={!hasCameraUrls || !isEnabled}
              className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              aria-label={showCamera ? 'Hide camera preview' : 'Show camera preview'}
              title={!isEnabled ? 'Printer disabled' : hasCameraUrls ? 'Camera preview available' : 'No linked camera configured'}
              iconCenter={<CameraIcon className="h-4 w-4" />}
            >
            </Button>

            {support.supportsHistory && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={handleViewHistory}
                disabled={!canOpenHistoryNow}
                className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
                title="View print history"
                aria-label="View print history"
                iconCenter={<HistoryIcon className="h-4 w-4" />}
              >
              </Button>
            )}

            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => setShowFiles(true)}
              disabled={!canOpenFilesNow}
              className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              title="View printer files"
              aria-label="View printer files"
              iconCenter={<FileIcon className="h-4 w-4" />}
            >
            </Button>
          </div>

          <div className="flex items-center gap-1">
            <Button
              type="button"
              variant="unstyled"
              onClick={handleAutoDispatchToggle}
              disabled={setAutoDispatchEnabled.isPending}
              className={`h-8 w-8 p-0 rounded transition-colors ${
                autoDispatchStatus?.enabled
                  ? 'text-pf-accent bg-pf-accent-bg'
                  : 'text-pf-text-secondary hover:text-pf-text-primary'
              } disabled:opacity-50 inline-flex items-center justify-center`}
              aria-label={`Toggle auto-dispatch for ${printer.name}`}
              aria-pressed={autoDispatchStatus?.enabled ?? false}
              title={autoDispatchStatus?.enabled ? 'Auto-dispatch enabled' : 'Auto-dispatch disabled'}
            >
              <Zap className="h-4 w-4" fill={autoDispatchStatus?.enabled ? 'currentColor' : 'none'} />
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => onEdit?.(printer)}
              className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              title="Edit details"
              aria-label="Edit details"
              iconCenter={<EditIcon className="h-4 w-4" />}
            >
            </Button>
          </div>
        </div>

        {showCamera && (
          <PrinterCameraPreview
            printerId={printer.id}
            printerName={printer.name}
            cameraStreamUrl={cameraStreamUrl}
            cameraSnapshotUrl={cameraSnapshotUrl}
            cameraAccessMode={printer.cameraAccessMode}
            cameraStreamFormat={printer.cameraStreamFormat}
            cameraSnapshotStrategy={printer.cameraSnapshotStrategy}
            isPrinting={isPrinting}
            className="pf-detailed-printer-camera-preview mt-3 w-full"
          />
        )}
      </div>

      {/* Bed clear confirmation banner */}
      {autoDispatchStatus && isPendingReady && (
        <div className="mb-3">
          <BedClearBanner
            printerId={printer.id}
            printerName={printer.name ?? 'Printer'}
            autoDispatchStatus={autoDispatchStatus}
            printerState={state}
          />
        </div>
      )}

      {/* Offline troubleshooting guide */}
      {!isOnline && (
        <div className="mb-4">
          <OfflineTroubleshootingGuide
            printerBackend={printer.backend}
            printerIp={printer.ipAddress}
            serverUrl={printer.serverUrl ?? printer.backendUrl}
            frontendUrl={printer.frontendUrl}
            variant="full"
          />
        </div>
      )}

      {/* Progress bar — always visible to prevent layout shift */}
      <div className="mb-4">
        <PrintProgressBar
          progress={printer.progress}
          jobName={printer.fileName ?? printer.jobName}
          isActive={isOnline && (isPrinting || isPaused)}
          progressRef={expandedProgressRef}
          showInactiveState={false}
          showTemperatures={false}
        />
      </div>

      {(isPrinting || isPaused) && (printer.estimatedCompletionTimeUtc || printer.printTimeLeftSeconds != null) && (
        <EstimatedCompletionBadge completionTimeUtc={printer.estimatedCompletionTimeUtc} printTimeLeftSeconds={printer.printTimeLeftSeconds} className="mb-3" />
      )}

      {(isPrinting || isPaused) && (
        <FailureDetectionMonitoringSummary
          enabled={!!printer.obicoEnabled}
          status={failureDetectionStatus}
          recentEvents={recentEvents}
          printerName={printer.name}
          variant="detailed"
          className="mb-4"
        />
      )}

      {/* Print Objects (skip object) — folded in from the details sidebar (#1584) */}
      {support.supportsObjectExclusion && (
        <div className="mb-3">
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
            ) : !isPrinting && !isPaused ? (
              <div className="text-sm text-pf-text-secondary">Object skipping is available during an active print.</div>
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
        </div>
      )}

      {/* Temps Section */}
      <TemperatureControlSection
        hotendTemp={hotendTemp}
        bedTemp={bedTemp}
        hotendTarget={printer.hotendTarget}
        bedTarget={printer.bedTarget}
        hotendCurrent={printer.hotendTemp}
        bedCurrent={printer.bedTemp}
        temperatureActionPending={temperatureActionPending}
        canSetTemperatures={canSetTemperaturesNow}
        canCooldown={canCooldownNow}
        onHotendTempChange={setHotendTemp}
        onBedTempChange={setBedTemp}
        onHotendTempKeyDown={handleHotendTempKeyDown}
        onBedTempKeyDown={handleBedTempKeyDown}
        onApplyPreset={handleApplyPreset}
        onApplySingleHeaterPreset={handleApplySingleHeaterPreset}
      />

      {/* Move and Control Section */}
      <div className="mb-2">
          <MovementControlSection
            moveX={moveX}
            moveY={moveY}
            moveZ={moveZ}
            step={step}
            extrudeStep={extrudeStep}
            extrudeSpeed={extrudeSpeed}
            printerX={printer.x}
            printerY={printer.y}
            printerZ={printer.z}
            homedAxes={homedAxesRaw}
            hotendTemp={printer.hotendTemp}
            extrudeMinTemp={extrudeMinTemp}
            movementActionPending={movementActionPending}
            canMove={canMoveNow}
            canDisableMotors={canDisableMotorsNow}
            canSetStep={canSetStepNow}
            canManualMove={canManualMoveNow}
            canExtrude={canExtrudeNow}
            onMoveXChange={setMoveX}
            onMoveYChange={setMoveY}
            onMoveZChange={setMoveZ}
            onStepChange={handleStepChange}
            onExtrudeStepChange={setExtrudeStep}
            onExtrudeSpeedChange={setExtrudeSpeed}
            onMove={handleMove}
            onHome={handleHome}
            onDisableMotors={() => handleControlAction('disable-motors')}
            onExtrude={handleExtrude}
            rightContent={
              <div className="flex flex-col gap-1 items-start">
                <PrinterActionBar
                  isPaused={isPaused}
                  isShutdown={isShutdown}
                  controlActionPending={controlActionPending}
                  canPauseOrResume={canPauseOrResumeNow}
                  canCancel={canCancelNow}
                  canEmergencyStop={canEmergencyStopNow}
                  onControlAction={handleControlAction}
                />
                {support.supportsFilamentControl && (
                  <FilamentControlSection
                    filamentActionPending={filamentActionPending}
                    canFilamentControl={canFilamentControl({ isOnline, isEnabled, isPrinting, support })}
                    canFilamentChange={canFilamentChange({ isOnline, isEnabled, support })}
                    onFilamentAction={handleFilamentAction}
                  />
                )}
                {support.supportsMovement && (
                  <Button
                    variant="secondary"
                    size="sm"
                    disabled={!isOnline || isPrinting}
                    onClick={() => setShowZOffsetWizard(true)}
                    onMouseEnter={() => ZOffsetCalibrationWizard.preload()}
                    onFocus={() => ZOffsetCalibrationWizard.preload()}
                  >
                    Calibrate Z-Offset
                  </Button>
                )}
              </div>
            }
          />
      </div>

      {/* Consolidated materials module — replaces the old Material Slots strip
          and the parallel Spools assignment list, which could disagree. */}
      {materialLoadout && (
        <MaterialLoadout
          printerId={printer.id}
          mmuStatus={mmuStatus}
          toolheads={printerDetails?.toolheads}
          reviewedRowVersion={printerDetails?.rowVersion ?? printer.rowVersion}
          compact
          className="mb-2"
        />
      )}

      {/* Single-spool printers keep the classic spool card; multi-slot printers are
          fully described by the materials module above. */}
      {(spoolmanReady || printer.spoolInfo) && !materialLoadout && (
        <div className="mb-2">
          <div className="flex items-center justify-between mb-1">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide">Spool</div>
            <div className="flex items-center gap-0.5">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={spoolActionPending}
                onClick={() => setShowSpoolPicker(true)}
                className="p-0.5! h-auto!"
                title="Change spool"
                aria-label="Change spool"
                iconCenter={<FilamentChangeIcon className="h-3.5 w-3.5" />}
              ></Button>
              {printer.spoolInfo?.hasActiveSpool && (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  disabled={spoolActionPending}
                  onClick={async () => {
                    setSpoolActionPending(true);
                    try {
                      if (!printer.rowVersion) {
                        toast.error('Printer revision unavailable. Refresh and review again.');
                        return;
                      }
                      const nextRowVersion = await apiClient.clearActiveSpool(
                        printer.id,
                        printer.rowVersion
                      );
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
                      // Reconcile the optimistic update with server truth so
                      // downstream consumers (printer details, coverage) see
                      // the cleared spool. Awaiting the invalidation prevents
                      // a follow-up assignment from racing a stale refetch.
                      await queryClient.invalidateQueries({ queryKey: ['printers'] });
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
                  className="p-0.5! h-auto!"
                  title="Eject spool"
                  aria-label="Eject spool"
                  iconCenter={<EjectIcon className="h-3.5 w-3.5" />}
                ></Button>
              )}
            </div>
          </div>
          <FilamentCoverageBreakdown printerId={printer.id} className="mb-2" />
          <LoadedFilamentCard spoolInfo={printer.spoolInfo} />
        </div>
      )}

      {/* Statistics and Version — folded in from the details sidebar (#1584) */}
      <div className="mt-3 space-y-3">
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

        <CollapsibleSection
          title="Version"
          expanded={isVersionExpanded}
          onToggle={setIsVersionExpanded}
          defaultExpanded={false}
          headerActions={
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => void printerVersionQuery.refetch()}
              className="p-1! h-auto!"
              title="Refresh version info"
              aria-label="Refresh version info"
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
      </div>

      <Modal
        isOpen={objectToSkip !== null}
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
                if (objectToSkip) {
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

      {/* History Modal */}
      <PrinterHistoryModal
        isOpen={showHistory}
        onClose={() => setShowHistory(false)}
        printer={printer}
      />

      <PrinterFilesModal
        isOpen={showFiles}
        onClose={() => setShowFiles(false)}
        printer={printer}
      />

      <SpoolPickerModal
        isOpen={showSpoolPicker}
        onClose={() => setShowSpoolPicker(false)}
        printerId={printer.id}
        activeSpoolId={printer.spoolInfo?.activeSpoolId}
        onSelect={async (spoolId, spool) => {
          setSpoolActionPending(true);
          try {
            if (!printer.rowVersion) {
              toast.error('Printer revision unavailable. Refresh and review again.');
              return;
            }
            const nextRowVersion = await apiClient.setActiveSpool(
              printer.id,
              spoolId,
              printer.rowVersion
            );
            setShowSpoolPicker(false);
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

      {showZOffsetWizard && (
        <Suspense fallback={null}>
          <ZOffsetCalibrationWizard
            isOpen={showZOffsetWizard}
            onClose={() => setShowZOffsetWizard(false)}
            printer={printer}
            bedSizeX={printerDetails?.capabilities?.maxBuildVolumeX ?? 220}
            bedSizeY={printerDetails?.capabilities?.maxBuildVolumeY ?? 220}
          />
        </Suspense>
      )}
    </article>
  );
});
