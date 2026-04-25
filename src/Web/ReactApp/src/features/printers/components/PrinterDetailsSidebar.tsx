import React, { useState, useRef, useEffect, useCallback } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { usePrinter, usePrinterDetails } from '@/common/hooks/useApi';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import { useSpoolmanConfigured } from '@/common/hooks/useSpoolmanConfigured';
import { apiClient } from '@/services/api';
import { maintenanceService } from '@/services/maintenanceService';
import { getPrinterDisplayState } from '@/common/utils/printerStateDisplay';
import { PrinterBackend, type MoveRequest, type Printer, type PrinterBackendCapabilitiesDto, type TempTargets } from '@/types/api';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import {
  canCancel,
  canCooldown,
  canDisableMotors,
  canEmergencyStop,
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
import {
  bedPresetOptions,
  getPresetTargets,
  hotendPresetOptions,
  materialPresets,
  getExtrudeMinTemp,
  EXTRUDE_DISTANCE_OPTIONS,
  DEFAULT_EXTRUDE_DISTANCE_MM,
  EXTRUDE_SPEED_OPTIONS,
  DEFAULT_EXTRUDE_SPEED_MMS,
} from '@/features/printers/constants/temperaturePresets';
import { getHomeButtonStyle } from '@/features/printers/utils/homeButtonStyle';
import { getStatusHeaderClassName, getStatusIndicatorColor } from '@/features/printers/utils/statusColors';
import { renderUnknown } from '@/common/utils/renderUnknown';
import { Button, TemperatureControlRow, MovementInput, MoveDistanceSlider, Select, CollapsibleSection, LoadedFilamentCard } from '@/common/components/ui';
import { ControlPadButton } from '@/common/components/ui/ControlPadButton';
import {
  NozzleIcon,
  BedIcon,
  DisableMotorsIcon,
  HomeIcon,
  PlayIcon,
  PauseIcon,
  EmergencyStopIcon,
  ArrowUpIcon,
  ArrowDownIcon,
  ArrowLeftIcon,
  ArrowRightIcon,
  FileIcon,
  RefreshIcon,
  HistoryIcon,
  CloseIcon,
  SnowflakeIcon,
  XCircleIcon,
  FilamentLoadIcon,
  FilamentUnloadIcon,
  FilamentChangeIcon,
  EjectIcon,
} from '@/common/components/icons/MdiIcons';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { ToolheadSpoolPicker } from '@/features/printers/components/ToolheadSpoolPicker';
import { mmuGatesToToolheads } from '@/features/printers/utils/mmuGatesToToolheads';
import { MmuControlBox } from '@/features/printers/components/MmuControlBox';
import { AmsSlotVisualization } from '@/features/printers/components/AmsSlotVisualization';
import { useAutoDispatchStatus } from '@/features/printers/hooks/useAutoDispatch';

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
  /** Layout mode: traditional right-side panel, or full-width content takeover */
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
  
  // Fetch printer details to check for multi-toolhead configuration
  // Only fetch when printerId is available and Spoolman is configured (when spool section would be shown)
  const { data: printerDetails } = usePrinterDetails(
    printerId ?? '', 
    { 
      enabled: !!printerId && spoolmanReady,
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

  const [isTempsExpanded, setIsTempsExpanded] = useState(true);
  const [isSpoolExpanded, setIsSpoolExpanded] = useState(true);
  const [showSpoolPicker, setShowSpoolPicker] = useState(false);
  const [controlActionPending, setControlActionPending] = useState(false);
  const [temperatureActionPending, setTemperatureActionPending] = useState(false);
  const [movementActionPending, setMovementActionPending] = useState(false);
  const [filamentActionPending, setFilamentActionPending] = useState(false);
  const [spoolActionPending, setSpoolActionPending] = useState(false);

  const printerStatisticsQuery = useQuery({
    queryKey: ['printerStatistics', printerId],
    queryFn: () => maintenanceService.getPrinterStatistics(printerId!),
    enabled: !!printerId && isStatisticsExpanded,
    staleTime: 60_000,
    gcTime: 10 * 60_000,
    refetchOnWindowFocus: false,
  });

  const printerVersionQuery = useQuery({
    queryKey: ['printerVersion', printerId],
    queryFn: () => apiClient.getPrinterVersionInfo(printerId!),
    enabled: !!printerId && isVersionExpanded,
    staleTime: 10 * 60_000,
    gcTime: 60 * 60_000,
    refetchOnWindowFocus: false,
  });

  // Use provided printer or fall back to API data, merged with realtime SignalR updates
  const basePrinter = printerProp || apiPrinter;
  const printer = usePrinterDisplay((basePrinter || {}) as Printer);

  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [hotendTemp, setHotendTemp] = useState<number | ''>('');
  const [bedTemp, setBedTemp] = useState<number | ''>('');
  const [moveX, setMoveX] = useState<number | ''>('');
  const [moveY, setMoveY] = useState<number | ''>('');
  const [moveZ, setMoveZ] = useState<number | ''>('');
  const [step, setStep] = useState(10);
  const [extrudeStep, setExtrudeStep] = useState(DEFAULT_EXTRUDE_DISTANCE_MM);
  const [extrudeSpeed, setExtrudeSpeed] = useState(DEFAULT_EXTRUDE_SPEED_MMS);

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

  // Keep target temperature inputs in sync with the printer's actual targets via SignalR
  const printerHotendTarget = printer?.hotendTarget ?? 0;
  const printerBedTarget = printer?.bedTarget ?? 0;
  useEffect(() => {
    setHotendTemp(printerHotendTarget > 0 ? printerHotendTarget : '');
    setBedTemp(printerBedTarget > 0 ? printerBedTarget : '');
  }, [printerHotendTarget, printerBedTarget]);

  // Guard early after all hooks are called
  if (!printerId) {
    return null;
  }

  // API now returns complete printer DTO with status merged in - no client-side merge needed
  const displayPrinter = printer;
  const sidebarShellClassName = layout === 'content'
    ? `w-full max-w-sm overflow-hidden flex flex-col rounded-2xl border border-white/10 bg-pf-sidebar shadow-[0_24px_48px_rgba(0,0,0,0.35)] ring-1 ring-white/5 ${isClosing ? 'pf-printer-sidebar-exit' : 'pf-printer-sidebar-enter'}`
    : `w-[calc(100%-1.5rem)] h-[calc(100%-1.5rem)] m-3 overflow-hidden flex flex-col rounded-2xl border border-white/10 bg-pf-sidebar shadow-[0_24px_48px_rgba(0,0,0,0.4)] ring-1 ring-white/5 ${isClosing ? 'pf-printer-sidebar-exit' : 'pf-printer-sidebar-enter'} shrink-0`;

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
  const statusLabel = getPrinterDisplayState({
    printerState: rawState,
    autoDispatchState: autoDispatchStatus?.state,
    autoDispatchStatus,
    isOnline,
  });
  const isPrinting = rawState.toLowerCase().includes('printing');
  const isPaused = rawState.toLowerCase().includes('paused');
  const isShutdown = rawState.toLowerCase().includes('shutdown') || rawState.toLowerCase().includes('error');
  const headerClassName = getStatusHeaderClassName({ state: rawState, isOnline, isPrinting, isPaused, isShutdown });
  const statusIndicatorClassName = getStatusIndicatorColor({ state: rawState, isOnline, isPrinting, isPaused, isShutdown });

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
  const canOpenFilesNow = canOpenFiles({ isOnline, isEnabled, support });
  const canOpenHistoryNow = canOpenHistory({ isOnline, isEnabled, support });

  const extrudeMinTemp = getExtrudeMinTemp(printer.spoolInfo?.material);
  const canExtrudeNow = canMoveNow && (printer.hotendTemp ?? 0) >= extrudeMinTemp;

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

  const formatCurrentTemp = (current?: number, lastKnown?: number | null): string => {
    const displayCurrent = current ?? lastKnown ?? 0;
    return `${displayCurrent.toFixed(1)}°C`;
  };

  // Check if axes are homed based on homedAxes string from Moonraker
  // homedAxes is a string like "xyz", "xy", "z", or "" if not homed
  const homedAxesRaw = displayPrinter?.homedAxes;
  const isHomedStateKnown = typeof homedAxesRaw === 'string';
  const homedAxes = (homedAxesRaw ?? '').toLowerCase();

  // Guarded debug logging - only log if enabled in window.PrintFarmerDebug
  if ((window as unknown as { PrintFarmerDebug?: { printerDetailsSidebar?: boolean } }).PrintFarmerDebug?.printerDetailsSidebar) {
    console.log('PrinterDetailsSidebar - Status update:', {
      printerId,
      homedAxesRaw: displayPrinter?.homedAxes,
      homedAxesLower: homedAxes,
      status: displayPrinter?.state,
      x: displayPrinter?.x,
      y: displayPrinter?.y,
      z: displayPrinter?.z
    });
  }

  // Determine if each axis is homed
  const isXHomed = isHomedStateKnown && homedAxes.includes('x');
  const isYHomed = isHomedStateKnown && homedAxes.includes('y');
  const isZHomed = isHomedStateKnown && homedAxes.includes('z');
  const isXYHomed = isXHomed && isYHomed;

  // Printer is fully homed if all axes are homed
  const isAllHomed = isXYHomed && isZHomed;

  if ((window as unknown as { PrintFarmerDebug?: { printerDetailsSidebar?: boolean } }).PrintFarmerDebug?.printerDetailsSidebar) {
    console.log('Homing state:', { isXHomed, isYHomed, isZHomed, homedAxes });
  }

  const handleHome = async (axes?: 'all' | 'xy' | 'z') => {
    if (movementActionPending) {
      return;
    }

    setMovementActionPending(true);
    try {
      const result = await (axes === 'xy'
        ? apiClient.homeXY(printer.id)
        : axes === 'z'
          ? apiClient.homeZ(printer.id)
          : apiClient.homePrinter(printer.id));
      if (!result.success) {
        console.error('Failed to home:', result.error);
      }
    } catch (error) {
      console.error('Error homing:', error);
    } finally {
      setMovementActionPending(false);
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
        console.error(`Failed to move ${axis}:`, result.error);
      }
    } catch (error) {
      console.error(`Error moving ${axis}:`, error);
    } finally {
      setMovementActionPending(false);
    }
  };

  const handleControlAction = async (action: 'pause' | 'resume' | 'cancel' | 'stop' | 'firmware-restart' | 'disable-motors') => {
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
      }
      if (result && !result.success) {
        console.error(`Failed to ${action}:`, result.error);
      }
    } catch (error) {
      console.error(`Error performing ${action}:`, error);
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

  const handleExtrude = async (direction: 'extrude' | 'retract') => {
    if (movementActionPending) return;

    setMovementActionPending(true);
    try {
      const distance = direction === 'extrude' ? extrudeStep : -extrudeStep;
      const feedrate = extrudeSpeed * 60; // mm/s to mm/min
      const gcode = `M83\nG1 E${distance} F${feedrate}\nM82`;
      const result = await apiClient.sendGcode(printer.id, gcode);
      if (!result.success) {
        console.error(`Failed to ${direction}:`, result.error);
      }
    } catch (error) {
      console.error(`Error ${direction}ing:`, error);
    } finally {
      setMovementActionPending(false);
    }
  };

  const handleHotendTempKeyDown = async (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter' || hotendTemp === '' || temperatureActionPending) return;

    setTemperatureActionPending(true);
    try {
      const currentBedTemp = bedTemp === '' ? (displayPrinter?.bedTarget ?? 0) : bedTemp;
      const result = await apiClient.setTemperatures(printer.id, {
        hotend: Number(hotendTemp),
        bed: Number(currentBedTemp)
      });

      if (!result.success) {
        console.error('Failed to set hotend temperature:', result.error);
      }
    } catch (error) {
      console.error('Error setting hotend temperature:', error);
    } finally {
      setTemperatureActionPending(false);
    }
  };

  const handleBedTempKeyDown = async (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter' || bedTemp === '' || temperatureActionPending) return;

    setTemperatureActionPending(true);
    try {
      const currentHotendTemp = hotendTemp === '' ? (displayPrinter?.hotendTarget ?? 0) : hotendTemp;
      const result = await apiClient.setTemperatures(printer.id, {
        hotend: Number(currentHotendTemp),
        bed: Number(bedTemp)
      });

      if (!result.success) {
        console.error('Failed to set bed temperature:', result.error);
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

    const currentHotend = hotendTemp === '' ? (displayPrinter?.hotendTarget ?? 0) : Number(hotendTemp);
    const currentBed = bedTemp === '' ? (displayPrinter?.bedTarget ?? 0) : Number(bedTemp);
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

  return (
    <div className={`${sidebarShellClassName} z-30`}>
      {/* Header */}
      <div className={`flex justify-between items-start px-4 pt-4 pb-3 border-b border-white/10 shrink-0 gap-3 ${headerClassName}`}>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1 flex-wrap">
            <h2 className="text-2xl font-bebas uppercase tracking-wide leading-none text-pf-text-primary truncate">{printer.name}</h2>
            <div className="inline-flex items-center gap-1.5 px-2 py-0.5 rounded-full text-xs font-medium shrink-0 bg-black/30 border border-white/20">
              <span className={`h-2 w-2 rounded-full ${statusIndicatorClassName}`} aria-hidden="true" />
              <span className="text-pf-text-primary">{statusLabel}</span>
            </div>
          </div>
          <p className="text-xs text-pf-text-secondary">{printer.manufacturerName} {printer.modelName}</p>
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
      <div ref={scrollRef} className="flex-1 overflow-y-auto p-4 space-y-4 bg-pf-sidebar">
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
                  {printerStatisticsQuery.data.lastSyncTime ? new Date(printerStatisticsQuery.data.lastSyncTime).toLocaleString() : '—'}
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
            <div className="flex flex-col gap-1">
              <div className="text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide">Control</div>
              <div className="grid grid-cols-3 gap-1 w-fit">
                <ControlPadButton
                  disabled={controlActionPending || !canPauseOrResumeNow}
                  onClick={() => handleControlAction(isPaused ? 'resume' : 'pause')}
                  title={isPaused ? 'Resume print' : 'Pause print'}
                  padSize="small"
                >
                  {isPaused ? <PlayIcon className="h-4 w-4" /> : <PauseIcon className="h-4 w-4" />}
                </ControlPadButton>
                <ControlPadButton
                  disabled={controlActionPending || !canCancelNow}
                  onClick={() => handleControlAction('cancel')}
                  title="Cancel print"
                  padSize="small"
                >
                  <XCircleIcon className="h-4 w-4" ariaLabel="Cancel" />
                </ControlPadButton>
                <ControlPadButton
                  variant={isShutdown ? 'secondary' : 'danger'}
                  disabled={controlActionPending || !canEmergencyStopNow}
                  onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
                  title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
                  padSize="small"
                >
                  {isShutdown ? <RefreshIcon className="h-4 w-4" /> : <EmergencyStopIcon className="h-4 w-4" />}
                </ControlPadButton>
              </div>
            </div>

            {/* Quick Access buttons */}
            <div className="flex flex-col gap-1">
              <div className="text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide">Quick Access</div>
              <div className="grid grid-cols-2 gap-1 w-fit">
                <ControlPadButton
                  disabled={!canOpenFilesNow}
                  onClick={() => setShowFiles(true)}
                  title="View printer files"
                  padSize="small"
                >
                  <FileIcon className="h-4 w-4" />
                </ControlPadButton>
                <ControlPadButton
                  disabled={!canOpenHistoryNow}
                  onClick={() => setShowHistory(true)}
                  title="View print history"
                  padSize="small"
                >
                  <HistoryIcon className="h-4 w-4" />
                </ControlPadButton>
              </div>
            </div>
          </div>
        </CollapsibleSection>

        {/* Move Section */}
        <CollapsibleSection
          title="Move"
          collapsedTitle="Movement, Macros, and Manual Move"
          expanded={isMoveExpanded}
          onToggle={setIsMoveExpanded}
          hideExpandedTitle
        >
          <div className="flex gap-4 items-start">
            {/* XY + Z Pad */}
            <div className="flex flex-col gap-1">
              <div className="text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide">Move</div>
              <div className="flex gap-2 items-start">
            <div className="grid grid-cols-3 grid-rows-3 gap-1 w-fit">
              {/* Top row */}
              <ControlPadButton
                disabled={movementActionPending || !canMoveNow}
                onClick={() => handleHome('all')}
                title="Home all axes"
                padSize="small"
                className={getHomeButtonStyle(isHomedStateKnown, isAllHomed).className}
                style={getHomeButtonStyle(isHomedStateKnown, isAllHomed).style}
              >
                <HomeIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMoveNow}
                onClick={() => handleMove('Y', step)}
                padSize="small"
              >
                <ArrowUpIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                disabled={controlActionPending || !canDisableMotorsNow}
                onClick={() => handleControlAction('disable-motors')}
                title="Disable Motors (M84)"
                padSize="small"
              >
                <DisableMotorsIcon className="h-4 w-4" />
              </ControlPadButton>

              {/* Middle row */}
              <ControlPadButton
                disabled={movementActionPending || !canMoveNow}
                onClick={() => handleMove('X', -step)}
                padSize="small"
              >
                <ArrowLeftIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMoveNow}
                onClick={() => handleHome('xy')}
                title="Home X/Y"
                padSize="small"
                className={getHomeButtonStyle(isHomedStateKnown, isXYHomed).className}
                style={getHomeButtonStyle(isHomedStateKnown, isXYHomed).style}
              >
                <HomeIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMoveNow}
                onClick={() => handleMove('X', step)}
                padSize="small"
              >
                <ArrowRightIcon className="h-4 w-4" />
              </ControlPadButton>

              {/* Bottom row */}
              <div></div>
              <ControlPadButton
                disabled={movementActionPending || !canMoveNow}
                onClick={() => handleMove('Y', -step)}
                padSize="small"
              >
                <ArrowDownIcon className="h-4 w-4" />
              </ControlPadButton>
              <div></div>
            </div>

            {/* Z Pad */}
            <div className="grid grid-cols-1 grid-rows-3 gap-1 w-fit">
              <ControlPadButton
                disabled={movementActionPending || !canMoveNow}
                onClick={() => handleMove('Z', step)}
                padSize="small"
              >
                Z+
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMoveNow}
                onClick={() => handleHome('z')}
                title="Home Z"
                padSize="small"
                className={getHomeButtonStyle(isHomedStateKnown, isZHomed).className}
                style={getHomeButtonStyle(isHomedStateKnown, isZHomed).style}
              >
                <HomeIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                disabled={movementActionPending || !canMoveNow}
                onClick={() => handleMove('Z', -step)}
                padSize="small"
              >
                Z-
              </ControlPadButton>
            </div>

            {/* Extrude Pad - vertical sliders flanking E+/E- buttons */}
            <div className="flex gap-1.5 items-center">
              {/* Length vertical slider */}
              <div className="flex flex-col items-center gap-0.5">
                <span className="text-[8px] text-pf-text-tertiary uppercase leading-none">len</span>
                <input
                  type="range"
                  min={0}
                  max={EXTRUDE_DISTANCE_OPTIONS.length - 1}
                  step={1}
                  value={(EXTRUDE_DISTANCE_OPTIONS as readonly number[]).indexOf(extrudeStep) >= 0 ? (EXTRUDE_DISTANCE_OPTIONS as readonly number[]).indexOf(extrudeStep) : 0}
                  onChange={(e) => setExtrudeStep(EXTRUDE_DISTANCE_OPTIONS[Number(e.target.value)])}
                  disabled={!canExtrudeNow}
                  className="h-20 w-4 accent-pf-accent cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed [writing-mode:vertical-lr] [direction:rtl]"
                  aria-label="Extrude distance"
                />
                <span className="text-[9px] font-bold text-pf-text-primary tabular-nums leading-none">{extrudeStep}mm</span>
              </div>

              {/* E+ / E- buttons */}
              <div className="flex flex-col gap-1 w-fit">
                <ControlPadButton
                  disabled={movementActionPending || !canExtrudeNow}
                  onClick={() => handleExtrude('extrude')}
                  title={`Extrude filament (min ${extrudeMinTemp}°C)`}
                  aria-label={`Extrude ${extrudeStep}mm at ${extrudeSpeed}mm/s`}
                  padSize="small"
                >
                  E+
                </ControlPadButton>
                <div className="h-8 w-8" />
                <ControlPadButton
                  disabled={movementActionPending || !canExtrudeNow}
                  onClick={() => handleExtrude('retract')}
                  title={`Retract filament (min ${extrudeMinTemp}°C)`}
                  aria-label={`Retract ${extrudeStep}mm at ${extrudeSpeed}mm/s`}
                  padSize="small"
                >
                  E-
                </ControlPadButton>
              </div>

              {/* Speed vertical slider */}
              <div className="flex flex-col items-center gap-0.5">
                <span className="text-[8px] text-pf-text-tertiary uppercase leading-none">spd</span>
                <input
                  type="range"
                  min={0}
                  max={EXTRUDE_SPEED_OPTIONS.length - 1}
                  step={1}
                  value={(EXTRUDE_SPEED_OPTIONS as readonly number[]).indexOf(extrudeSpeed) >= 0 ? (EXTRUDE_SPEED_OPTIONS as readonly number[]).indexOf(extrudeSpeed) : 0}
                  onChange={(e) => setExtrudeSpeed(EXTRUDE_SPEED_OPTIONS[Number(e.target.value)])}
                  disabled={!canExtrudeNow}
                  className="h-20 w-4 accent-pf-accent cursor-pointer disabled:opacity-40 disabled:cursor-not-allowed [writing-mode:vertical-lr] [direction:rtl]"
                  aria-label="Extrude speed"
                />
                <span className="text-[9px] font-bold text-pf-text-primary tabular-nums leading-none">{extrudeSpeed}mm/s</span>
              </div>
            </div>

            </div>
            </div>

            {/* Filament buttons */}
              <div className="flex flex-col gap-1">
                <div className="text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide">Macros</div>
                <div className="grid grid-cols-1 grid-rows-3 gap-1 w-fit">
                <ControlPadButton
                  disabled={filamentActionPending || !canFilamentControl({ isOnline, isEnabled, isPrinting, support })}
                  onClick={() => handleFilamentAction('load')}
                  title="Load Filament"
                  padSize="small"
                >
                  <FilamentLoadIcon className="h-4 w-4" />
                </ControlPadButton>
                <ControlPadButton
                  disabled={filamentActionPending || !canFilamentControl({ isOnline, isEnabled, isPrinting, support })}
                  onClick={() => handleFilamentAction('unload')}
                  title="Unload Filament"
                  padSize="small"
                >
                  <FilamentUnloadIcon className="h-4 w-4" />
                </ControlPadButton>
                <ControlPadButton
                  disabled={filamentActionPending || !canFilamentChange({ isOnline, isEnabled, support })}
                  onClick={() => handleFilamentAction('change')}
                  title="Change Filament (M600)"
                  padSize="small"
                >
                  <FilamentChangeIcon className="h-4 w-4" />
                </ControlPadButton>
                </div>
              </div>
          </div>
          {/* Move distance slider */}
          <div className="my-4">
            <div className="text-[10px] uppercase text-pf-text-secondary font-bold tracking-wide mb-1">Step Size</div>
            <MoveDistanceSlider value={step} onChange={setStep} disabled={!canSetStepNow} />
          </div>
          {/* Manual Movement Inputs */}
          <div className="mt-3">
            <div className="flex gap-1 items-end">
              <MovementInput
                axis="X"
                currentPosition={lastKnownX}
                disabled={movementActionPending || !canManualMoveNow}
                value={moveX}
                max={500}
                onChange={(e) => setMoveX(e.target.value === '' ? '' : Number(e.target.value))}
                onKeyDown={(e) => e.key === 'Enter' && moveX !== '' && handleMove('X', Number(moveX))}
                className="w-16! min-w-0"
              />
              <MovementInput
                axis="Y"
                currentPosition={lastKnownY}
                disabled={movementActionPending || !canManualMoveNow}
                value={moveY}
                max={500}
                onChange={(e) => setMoveY(e.target.value === '' ? '' : Number(e.target.value))}
                onKeyDown={(e) => e.key === 'Enter' && moveY !== '' && handleMove('Y', Number(moveY))}
                className="w-16! min-w-0"
              />
              <MovementInput
                axis="Z"
                currentPosition={lastKnownZ}
                disabled={movementActionPending || !canManualMoveNow}
                value={moveZ}
                max={500}
                onChange={(e) => setMoveZ(e.target.value === '' ? '' : Number(e.target.value))}
                onKeyDown={(e) => e.key === 'Enter' && moveZ !== '' && handleMove('Z', Number(moveZ))}
                className="w-16! min-w-0"
              />
              <ControlPadButton
                disabled={movementActionPending || !canManualMoveNow || (moveX === '' && moveY === '' && moveZ === '')}
                onClick={async () => {
                  if (moveX !== '') await handleMove('X', Number(moveX));
                  if (moveY !== '') await handleMove('Y', Number(moveY));
                  if (moveZ !== '') await handleMove('Z', Number(moveZ));
                }}
                title="Move to entered coordinates"
                padSize="small"
                className="bg-pf-success! hover:bg-pf-success-hover! text-white!"
              >
                <span className="text-[10px] font-bold">GO</span>
              </ControlPadButton>
            </div>
          </div>
        </CollapsibleSection>

        {/* Temperatures Section */}
        <CollapsibleSection
          title="Temps"
          expanded={isTempsExpanded}
          onToggle={setIsTempsExpanded}
        >
          <div className="flex justify-end gap-1 items-stretch h-8 pb-1">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              disabled={temperatureActionPending || !canCooldownNow}
              onClick={() => handleApplyPreset('cooldown')}
              title="Cooldown"
              className="shrink-0 px-2!"
              iconCenter={<SnowflakeIcon className={`h-4 w-4 ${((displayPrinter?.hotendTarget ?? 0) > 0 || (displayPrinter?.bedTarget ?? 0) > 0) ? 'text-pf-accent' : 'text-pf-text-secondary'}`} />}
            ></Button>
            <div className="relative w-24">
              <Select
                value=""
                onChange={(e) => {
                  const value = e.target.value;
                  if (value) {
                    handleApplyPreset(value);
                  }
                }}
                disabled={temperatureActionPending || !canSetTemperaturesNow}
                className="h-8 text-[10px] uppercase tracking-wide font-semibold pr-6! border-transparent! bg-transparent! enabled:hover:[background:rgba(255,255,255,0.10)] focus:border-transparent focus:ring-0"
              >
                <option value="">PRESETS</option>
                {materialPresets.map((preset) => (
                  <option key={preset.value} value={preset.value}>{preset.label}</option>
                ))}
              </Select>
            </div>
          </div>

          <div className="grid grid-cols-[minmax(0,1fr)_3rem_4.75rem_5rem_1.5rem] gap-2 pb-1 text-[10px] uppercase tracking-wide text-pf-text-secondary">
            <span>Name</span>
            <span className="text-right">State</span>
            <span className="text-right">Current</span>
            <span className="text-right">Target</span>
            <span></span>
          </div>

          {/* Hotend Temperature Row */}
          <TemperatureControlRow
            icon={<NozzleIcon className="w-4 h-4 text-pf-error" isOn={(displayPrinter?.hotendTarget ?? 0) > 0} />}
            label="Hotend"
            stateLabel={(displayPrinter?.hotendTarget ?? 0) > 0 ? 'on' : 'off'}
            liveReading={formatCurrentTemp(
              displayPrinter?.hotendTemp,
              lastKnownHotendTemp
            )}
            value={hotendTemp}
            onChange={(e) => setHotendTemp(e.target.value === '' ? '' : Number(e.target.value))}
            onKeyDown={handleHotendTempKeyDown}
            disabled={temperatureActionPending || !canSetTemperaturesNow}
            presetOptions={hotendPresetOptions}
            onPresetSelect={(preset) => {
              void handleApplySingleHeaterPreset('hotend', preset);
            }}
          />

          {/* Bed Temperature Row */}
          <TemperatureControlRow
            icon={<BedIcon className="w-4 h-4 text-pf-accent" isOn={(displayPrinter?.bedTarget ?? 0) > 0} />}
            label="Bed"
            stateLabel={(displayPrinter?.bedTarget ?? 0) > 0 ? 'on' : 'off'}
            liveReading={formatCurrentTemp(
              displayPrinter?.bedTemp,
              lastKnownBedTemp
            )}
            value={bedTemp}
            onChange={(e) => setBedTemp(e.target.value === '' ? '' : Number(e.target.value))}
            onKeyDown={handleBedTempKeyDown}
            disabled={temperatureActionPending || !canSetTemperaturesNow}
            presetOptions={bedPresetOptions}
            onPresetSelect={(preset) => {
              void handleApplySingleHeaterPreset('bed', preset);
            }}
          />
        </CollapsibleSection>

        {/* MMU Control Box - Show when MMU/ERCF is detected via real-time status */}
        {displayPrinter?.mmuStatus && (
          <MmuControlBox
            printerId={printer.id}
            mmuStatus={displayPrinter.mmuStatus}
            isOnline={isOnline}
          />
        )}

        {/* AMS/MMU Slot Visualization - Show when printer has multiple toolheads */}
        {(() => {
          const toolheads = printerDetails?.toolheads && printerDetails.toolheads.length > 1
            ? printerDetails.toolheads
            : displayPrinter?.mmuStatus?.gates && displayPrinter.mmuStatus.gates.length > 0
              ? mmuGatesToToolheads(displayPrinter.mmuStatus.gates)
              : undefined;
          if (!toolheads) return null;
          return (
            <CollapsibleSection title="Material Slots" expanded={true}>
              <AmsSlotVisualization toolheads={toolheads} printerId={printerId ?? undefined} />
            </CollapsibleSection>
          );
        })()}

        {/* Spool Section - Show when Spoolman is configured (all backends) */}
        {(spoolmanReady || displayPrinter?.spoolInfo || displayPrinter?.currentSpoolId) && (() => {
          // Physical multi-toolhead (e.g., Snapmaker U1): toolheads stored in config DB
          const hasMultipleToolheads = printerDetails?.toolheads && printerDetails.toolheads.length > 1;
          // MMU multi-material (e.g., QidiBox, HappyHare, AFC): gates from live SignalR status
          const hasMmuGates = !hasMultipleToolheads
            && displayPrinter?.mmuStatus?.gates
            && displayPrinter.mmuStatus.gates.length > 0;
          const hasMultipleSpoolSources = hasMultipleToolheads || hasMmuGates;
          const sectionTitle = hasMultipleSpoolSources ? 'Spools' : 'Spool';

          // For multi-spool mode, determine the toolheads to display.
          // Prefer printerDetails.toolheads (includes virtual MmuGate entries after backend sync).
          // Fall back to converting live mmuStatus.gates for MMU printers without synced toolheads.
          const effectiveToolheads = hasMultipleToolheads
            ? printerDetails!.toolheads!
            : hasMmuGates
              ? mmuGatesToToolheads(displayPrinter!.mmuStatus!.gates)
              : undefined;
          
          return (
            <CollapsibleSection
              title={sectionTitle}
              expanded={isSpoolExpanded}
              onToggle={setIsSpoolExpanded}
              headerActions={
                // Only show header actions for single-spool mode
                !hasMultipleSpoolSources ? (
                  <div className="flex items-center gap-0.5">
                    {(displayPrinter?.spoolInfo?.hasActiveSpool || displayPrinter?.currentSpoolId) && (
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        disabled={spoolActionPending}
                        onClick={async () => {
                          setSpoolActionPending(true);
                          try {
                            await apiClient.clearActiveSpool(printer.id);
                            // Optimistically update cached printers to clear spool info
                            // SignalR will deliver the authoritative update on next status cycle
                            queryClient.setQueryData<Printer[]>(['printers'], (old) =>
                              old?.map(p => p.id === printer.id
                                ? { ...p, spoolInfo: { hasActiveSpool: false } }
                                : p
                              )
                            );
                          } catch (err) {
                            console.error('Failed to eject spool:', err);
                          } finally {
                            setSpoolActionPending(false);
                          }
                        }}
                        className="p-1! h-auto!"
                        title="Eject spool"
                        aria-label="Eject spool"
                        iconCenter={<EjectIcon className="h-4 w-4" />}
                      ></Button>
                    )}
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      disabled={spoolActionPending}
                      onClick={() => setShowSpoolPicker(true)}
                      className="p-1! h-auto!"
                      title="Change spool"
                      aria-label="Change spool"
                      iconCenter={<FilamentChangeIcon className="h-4 w-4" />}
                    ></Button>
                  </div>
                ) : undefined
              }
            >
              {hasMultipleSpoolSources && effectiveToolheads ? (
                // Multi-toolhead or MMU spool picker
                <ToolheadSpoolPicker
                  printerId={printer.id}
                  toolheads={effectiveToolheads}
                  onSpoolChange={() => {
                    queryClient.invalidateQueries({ queryKey: ['printers', printer.id, 'details'] });
                  }}
                />
              ) : (
                // Single spool display
                <LoadedFilamentCard spoolInfo={displayPrinter?.spoolInfo ?? (displayPrinter?.currentSpoolId ? { hasActiveSpool: true, activeSpoolId: displayPrinter.currentSpoolId } : undefined)} />
              )}
            </CollapsibleSection>
          );
        })()}
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

      {/* Spool Picker Modal */}
      <SpoolPickerModal
        isOpen={showSpoolPicker}
        onClose={() => setShowSpoolPicker(false)}
        printerId={printer.id}
        activeSpoolId={displayPrinter?.spoolInfo?.activeSpoolId ?? displayPrinter?.currentSpoolId}
        onSelect={async (spoolId, spool) => {
          setSpoolActionPending(true);
          try {
            await apiClient.setActiveSpool(printer.id, spoolId);
            setShowSpoolPicker(false);
            // Optimistically update cached printers with new spool info
            queryClient.setQueryData<Printer[]>(['printers'], (old) =>
              old?.map(p => p.id === printer.id
                ? {
                    ...p,
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
          } finally {
            setSpoolActionPending(false);
          }
        }}
      />
    </div>
  );
}
