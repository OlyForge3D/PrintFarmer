import React, { useState, useRef, useEffect, useCallback } from 'react';
import { useQuery } from '@tanstack/react-query';
import { usePrinter } from '@/common/hooks/useApi';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import { apiClient } from '@/services/api';
import { maintenanceService } from '@/services/maintenanceService';
import { formatPrinterState } from '@/common/utils/printerStateDisplay';
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
import { getHomeButtonStyle } from '@/features/printers/utils/homeButtonStyle';
import { renderUnknown } from '@/common/utils/renderUnknown';
import { Button, TemperatureControlRow, MovementInput, MoveDistanceSlider, Select, CollapsibleSection } from '@/common/components/ui';
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
  const [isQuickAccessExpanded, setIsQuickAccessExpanded] = useState(true);
  const [isManualMoveExpanded, setIsManualMoveExpanded] = useState(true);
  const [isFilamentExpanded, setIsFilamentExpanded] = useState(true);
  const [isSpoolExpanded, setIsSpoolExpanded] = useState(true);
  const [showSpoolPicker, setShowSpoolPicker] = useState(false);
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

  // Track last known values for display fallback - use state not refs for render access
  const [lastKnownValues, setLastKnownValues] = useState({
    hotendTemp: null as number | null,
    bedTemp: null as number | null,
    x: null as number | null,
    y: null as number | null,
    z: null as number | null,
  });
  const scrollRef = useRef<HTMLDivElement>(null);

  // Poll printer data for PrusaLink (fallback - server now broadcasts via SignalR)
  // Server-side PrusaLinkPollingService polls every 5 seconds and broadcasts via SignalR
  // This client-side polling serves as a fallback in case SignalR connection drops
  useEffect(() => {
    if (!printer || printer.backend !== PrinterBackend.PrusaLink || !printerId) {
      return;
    }

    const pollInterval = setInterval(() => {
      refetch();
    }, 5000); // Poll every 5 seconds for PrusaLink as fallback

    return () => clearInterval(pollInterval);
  }, [printer, refetch, printerId]);

  // Update last known values when printer data changes
  useEffect(() => {
    if (printer) {
      // Defer state update to satisfy React Compiler rules
      queueMicrotask(() => {
        setLastKnownValues(prev => ({
          hotendTemp: printer.hotendTemp !== undefined ? printer.hotendTemp : prev.hotendTemp,
          bedTemp: printer.bedTemp !== undefined ? printer.bedTemp : prev.bedTemp,
          x: printer.x !== undefined ? printer.x : prev.x,
          y: printer.y !== undefined ? printer.y : prev.y,
          z: printer.z !== undefined ? printer.z : prev.z,
        }));
      });
    }
  }, [printer]);

  // Guard early after all hooks are called
  if (!printerId) {
    return null;
  }

  // API now returns complete printer DTO with status merged in - no client-side merge needed
  const displayPrinter = printer;

  // Show loading state while fetching printer data
  if (isLoading || !printer) {
    return (
      <div className="w-96 h-full bg-pf-sidebar border-l border-pf-border shadow-lg z-30 flex items-center justify-center shrink-0">
        <div className="text-pf-text-secondary">Loading...</div>
      </div>
    );
  }

  // State helpers - safe to access displayPrinter now (guaranteed by printer != null)
  const isOnline = displayPrinter?.isOnline ?? false;
  const isEnabled = displayPrinter?.isEnabled ?? true;
  const rawState = displayPrinter?.state ?? 'unknown';
  const state = formatPrinterState(rawState);
  const isPrinting = rawState.toLowerCase().includes('printing');
  const isPaused = rawState.toLowerCase().includes('paused');
  const isShutdown = rawState.toLowerCase().includes('shutdown') || rawState.toLowerCase().includes('error');

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

  // Format temperature with target
  const formatTempWithTarget = (current?: number, target?: number, lastKnown?: number | null): string => {
    const displayCurrent = current ?? lastKnown ?? 0;
    if (target && target > 0) {
      return `${displayCurrent.toFixed(1)}°C / ${target.toFixed(0)}°C`;
    }
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
    }
  };

  const handleMove = async (axis: 'X' | 'Y' | 'Z', distance: number) => {
    try {
      const move: MoveRequest = {};
      move[axis.toLowerCase() as keyof MoveRequest] = distance;
      const result = await apiClient.movePrinter(printer.id, move);
      if (!result.success) {
        console.error(`Failed to move ${axis}:`, result.error);
      }
    } catch (error) {
      console.error(`Error moving ${axis}:`, error);
    }
  };

  const handleControlAction = async (action: 'pause' | 'resume' | 'cancel' | 'stop' | 'firmware-restart' | 'disable-motors') => {
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
    }
  };

  const handleFilamentAction = async (action: 'load' | 'unload' | 'change') => {
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
    }
  };

  const handleHotendTempKeyDown = async (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter' || hotendTemp === '') return;

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
    }
  };

  const handleBedTempKeyDown = async (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter' || bedTemp === '') return;

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
    }
  };

  const handleApplyPreset = async (preset: string) => {
    try {
      let targets: TempTargets;

      switch (preset.toLowerCase()) {
        case 'abs':
          targets = { hotend: 250, bed: 100 };
          break;
        case 'asa':
          targets = { hotend: 260, bed: 100 };
          break;
        case 'pla':
          targets = { hotend: 210, bed: 60 };
          break;
        case 'pc':
          targets = { hotend: 280, bed: 110 };
          break;
        case 'pctg':
          targets = { hotend: 240, bed: 85 };
          break;
        case 'petg':
          targets = { hotend: 230, bed: 75 };
          break;
        case 'cooldown':
          targets = { hotend: 0, bed: 0 };
          break;
        default:
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
    }
  };

  return (
    <div
      className={
        layout === 'content'
          ? `w-full max-w-sm bg-pf-sidebar border border-pf-border shadow-lg z-30 overflow-hidden flex flex-col ${isClosing ? 'pf-printer-sidebar-exit' : 'pf-printer-sidebar-enter'}`
          : `w-96 h-full bg-pf-sidebar border-l border-pf-border shadow-lg z-30 overflow-hidden flex flex-col ${isClosing ? 'pf-printer-sidebar-exit' : 'pf-printer-sidebar-enter'} shrink-0`
      }
    >
      {/* Header */}
      <div className="flex justify-between items-start p-4 border-b border-pf-border shrink-0 gap-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <h2 className="text-lg font-bold text-pf-text-primary truncate">{printer.name}</h2>
            <div className={`shrink-0 w-2 h-2 rounded-full ${isOnline ? 'bg-green-500' : 'bg-gray-500'}`} title={isOnline ? 'Online' : 'Offline'} />
          </div>
          <p className="text-xs text-pf-text-secondary">{printer.manufacturerName} {printer.modelName}</p>
          <p className="text-xs text-pf-text-secondary mt-1">{state}</p>
        </div>
        <Button
          type="button"
          variant="subtle"
          size="sm"
          onClick={handleClose}
          className="!p-1 !h-auto shrink-0"
          title="Close sidebar"
          iconCenter={<CloseIcon className="h-6 w-6" />}
        ></Button>
      </div>

      {/* Scrollable Content */}
      <div ref={scrollRef} className="flex-1 overflow-y-auto p-4 space-y-4">
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
              className="!p-1 !h-auto"
              title="Refresh statistics"
              aria-label="Refresh statistics"
              iconCenter={<RefreshIcon className="h-4 w-4" />}
            ></Button>
          }
        >
          {printerStatisticsQuery.isLoading ? (
            <div className="text-sm text-pf-text-secondary">Loading statistics…</div>
          ) : printerStatisticsQuery.data ? (
            <dl className="grid grid-cols-2 gap-x-3 gap-y-2 text-sm">
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
              className="!p-1 !h-auto"
              title="Refresh version info"
              aria-label="Refresh version info"
              iconCenter={<RefreshIcon className="h-4 w-4" />}
            ></Button>
          }
        >
          {printerVersionQuery.isLoading ? (
            <div className="text-sm text-pf-text-secondary">Loading version…</div>
          ) : printerVersionQuery.data ? (
            <dl className="grid grid-cols-2 gap-x-3 gap-y-2 text-sm">
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
                  <dd className="text-pf-text-primary break-words">{printerVersionQuery.data.message}</dd>
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
          expanded={isControlExpanded}
          onToggle={setIsControlExpanded}
        >
          <div className="flex flex-col gap-0">
            {/* Control buttons row - 3 buttons matching XY pad width and height */}
            <div className="grid grid-cols-3 gap-1 w-40 h-12">
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canPauseOrResumeNow}
                onClick={() => handleControlAction(isPaused ? 'resume' : 'pause')}
                title={isPaused ? 'Resume print' : 'Pause print'}
                className="w-full h-full !p-0"
                iconCenter={isPaused ? <PlayIcon className="h-6 w-6" /> : <PauseIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canCancelNow}
                onClick={() => handleControlAction('cancel')}
                title="Cancel print"
                className="w-full h-full !p-0"
                iconCenter={<XCircleIcon className="h-6 w-6" ariaLabel="Cancel" />}
              ></Button>
              <Button
                type="button"
                variant={isShutdown ? 'secondary' : 'danger'}
                size="sm"
                disabled={!canEmergencyStopNow}
                onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
                title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
                className="w-full h-full !p-0"
                iconLeft={isShutdown ? (
                  <RefreshIcon className="h-6 w-6" />
                ) : (
                  <EmergencyStopIcon className="h-6 w-6" />
                )}
              ></Button>
            </div>
          </div>
        </CollapsibleSection>

        {/* Move Section */}
        <CollapsibleSection
          title="Move"
          expanded={isMoveExpanded}
          onToggle={setIsMoveExpanded}
        >
          <div className="flex gap-4 items-start">
            {/* XY Pad */}
            <div className="grid grid-cols-3 grid-rows-3 gap-1 w-40 h-36">
              {/* Top row */}
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canMoveNow}
                onClick={() => handleHome('all')}
                title="Home all axes"
                className={`w-full h-full !p-0 ${getHomeButtonStyle(isHomedStateKnown, isAllHomed).className ?? ''}`}
                style={getHomeButtonStyle(isHomedStateKnown, isAllHomed).style}
                iconCenter={<HomeIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canMoveNow}
                onClick={() => handleMove('Y', step)}
                className="w-full h-full !p-0"
                iconCenter={<ArrowUpIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canDisableMotorsNow}
                onClick={() => handleControlAction('disable-motors')}
                title="Disable Motors (M84)"
                className="w-full h-full !p-0"
                iconCenter={<DisableMotorsIcon className="w-6 h-6" />}
              ></Button>

              {/* Middle row */}
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canMoveNow}
                onClick={() => handleMove('X', -step)}
                className="w-full h-full !p-0"
                iconCenter={<ArrowLeftIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canMoveNow}
                onClick={() => handleHome('xy')}
                title="Home X/Y"
                className={`w-full h-full !p-0 ${getHomeButtonStyle(isHomedStateKnown, isXYHomed).className ?? ''}`}
                style={getHomeButtonStyle(isHomedStateKnown, isXYHomed).style}
                iconCenter={<HomeIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canMoveNow}
                onClick={() => handleMove('X', step)}
                className="w-full h-full !p-0"
                iconCenter={<ArrowRightIcon className="h-6 w-6" />}
              ></Button>

              {/* Bottom row */}
              <div></div>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canMoveNow}
                onClick={() => handleMove('Y', -step)}
                className="w-full h-full !p-0"
                iconCenter={<ArrowDownIcon className="h-6 w-6" />}
              ></Button>
              <div></div>
            </div>

            {/* Z Pad */}
            <div className="grid grid-cols-1 grid-rows-3 gap-1 h-36" style={{ width: 'calc(160px / 3 - 4px / 3)' }}>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canMoveNow}
                onClick={() => handleMove('Z', step)}
                className="w-full h-full !p-0"
              >
                Z+
              </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canMoveNow}
                onClick={() => handleHome('z')}
                title="Home Z"
                className={`w-full h-full !p-0 ${getHomeButtonStyle(isHomedStateKnown, isZHomed).className ?? ''}`}
                style={getHomeButtonStyle(isHomedStateKnown, isZHomed).style}
                iconCenter={<HomeIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canMoveNow}
                onClick={() => handleMove('Z', -step)}
                className="w-full h-full !p-0"
              >
                Z-
              </Button>
            </div>
          </div>
          {/* Move distance slider */}
          <MoveDistanceSlider value={step} onChange={setStep} disabled={!canSetStepNow} />
        </CollapsibleSection>

        {/* Files and History Section */}
        <CollapsibleSection
          title="Quick Access"
          expanded={isQuickAccessExpanded}
          onToggle={setIsQuickAccessExpanded}
        >
          <div className="grid grid-cols-2 gap-2">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={!canOpenFilesNow}
              onClick={() => setShowFiles(true)}
              className="flex items-center justify-center gap-2"
              title="View printer files"
              iconLeft={<FileIcon className="h-4 w-4" />}
            >
              <span>Files</span>
            </Button>
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={!canOpenHistoryNow}
              onClick={() => setShowHistory(true)}
              className="flex items-center justify-center gap-2"
              title="View print history"
              iconLeft={<HistoryIcon className="h-4 w-4" />}
            >
              <span>History</span>
            </Button>
          </div>
        </CollapsibleSection>

        {/* Manual Movement Input Section */}
        <CollapsibleSection
          title="Manual Move"
          expanded={isManualMoveExpanded}
          onToggle={setIsManualMoveExpanded}
        >
          <div className="grid grid-cols-3 gap-2">
            <MovementInput
              axis="X"
              currentPosition={lastKnownX}
              disabled={!canManualMoveNow}
              value={moveX}
              onChange={(e) => setMoveX(e.target.value === '' ? '' : Number(e.target.value))}
              onKeyDown={(e) => e.key === 'Enter' && moveX !== '' && handleMove('X', Number(moveX))}
              className="!w-full"
            />
            <MovementInput
              axis="Y"
              currentPosition={lastKnownY}
              disabled={!canManualMoveNow}
              value={moveY}
              onChange={(e) => setMoveY(e.target.value === '' ? '' : Number(e.target.value))}
              onKeyDown={(e) => e.key === 'Enter' && moveY !== '' && handleMove('Y', Number(moveY))}
              className="!w-full"
            />
            <MovementInput
              axis="Z"
              currentPosition={lastKnownZ}
              disabled={!canManualMoveNow}
              value={moveZ}
              onChange={(e) => setMoveZ(e.target.value === '' ? '' : Number(e.target.value))}
              onKeyDown={(e) => e.key === 'Enter' && moveZ !== '' && handleMove('Z', Number(moveZ))}
              className="!w-full"
            />
          </div>
        </CollapsibleSection>

        {/* Temperatures Section - REDESIGNED */}
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide">Temps</div>
            <div className="flex gap-1 items-stretch h-8">
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canCooldownNow}
                onClick={() => handleApplyPreset('cooldown')}
                title="Cooldown"
                className="shrink-0 px-2"
                iconCenter={<SnowflakeIcon className="h-4 w-4" />}
              ></Button>
              <Select
                value=""
                onChange={(e) => {
                  const value = e.target.value;
                  if (value) {
                    handleApplyPreset(value);
                  }
                }}
                disabled={!canSetTemperaturesNow}
                className="flex-1 text-xs"
              >
                <option value="">Presets</option>
                <option value="ABS">ABS</option>
                <option value="ASA">ASA</option>
                <option value="PLA">PLA</option>
                <option value="PC">PC</option>
                <option value="PCTG">PCTG</option>
                <option value="PETG">PETG</option>
              </Select>
            </div>
          </div>

          {/* Hotend Temperature Row */}
          <TemperatureControlRow
            icon={<NozzleIcon className="w-4 h-4 text-red-500" isOn={(displayPrinter?.hotendTarget ?? 0) > 0} />}
            label="Hotend"
            liveReading={formatTempWithTarget(
              displayPrinter?.hotendTemp,
              displayPrinter?.hotendTarget,
              lastKnownHotendTemp
            )}
            value={hotendTemp}
            onChange={(e) => setHotendTemp(e.target.value === '' ? '' : Number(e.target.value))}
            onKeyDown={handleHotendTempKeyDown}
            disabled={!canSetTemperaturesNow}
          />

          {/* Bed Temperature Row */}
          <TemperatureControlRow
            icon={<BedIcon className="w-4 h-4 text-blue-500" isOn={(displayPrinter?.bedTarget ?? 0) > 0} />}
            label="Bed"
            liveReading={formatTempWithTarget(
              displayPrinter?.bedTemp,
              displayPrinter?.bedTarget,
              lastKnownBedTemp
            )}
            value={bedTemp}
            onChange={(e) => setBedTemp(e.target.value === '' ? '' : Number(e.target.value))}
            onKeyDown={handleBedTempKeyDown}
            disabled={!canSetTemperaturesNow}
          />
        </div>

        {/* Filament Macros Section - capability-based */}
        {support.supportsFilamentControl && (
          <CollapsibleSection
            title="Filament"
            expanded={isFilamentExpanded}
            onToggle={setIsFilamentExpanded}
          >
            <div className="grid grid-cols-3 gap-1">
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canFilamentControl({ isOnline, isEnabled, isPrinting, support })}
                onClick={() => handleFilamentAction('load')}
                title="Load Filament"
                className="w-full text-xs inline-flex items-center justify-center gap-1"
                iconLeft={<FilamentLoadIcon className="w-3.5 h-3.5" />}
              >
                Load
              </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canFilamentControl({ isOnline, isEnabled, isPrinting, support })}
                onClick={() => handleFilamentAction('unload')}
                title="Unload Filament"
                className="w-full text-xs inline-flex items-center justify-center gap-1"
                iconLeft={<FilamentUnloadIcon className="w-3.5 h-3.5" />}
              >
                Unload
              </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!canFilamentChange({ isOnline, isEnabled, support })}
                onClick={() => handleFilamentAction('change')}
                title="Change Filament (M600)"
                className="w-full text-xs inline-flex items-center justify-center gap-1"
                iconLeft={<FilamentChangeIcon className="w-3.5 h-3.5" />}
              >
                Change
              </Button>
            </div>
          </CollapsibleSection>
        )}

        {/* Spool Section - Only show when Spoolman is configured */}
        {displayPrinter?.spoolInfo && (
        <CollapsibleSection
          title="Spool"
          expanded={isSpoolExpanded}
          onToggle={setIsSpoolExpanded}
          headerActions={
            <div className="flex items-center gap-0.5">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={spoolActionPending}
                onClick={() => setShowSpoolPicker(true)}
                className="!p-1 !h-auto"
                title="Change spool"
                aria-label="Change spool"
                iconCenter={<FilamentChangeIcon className="h-4 w-4" />}
              ></Button>
              {displayPrinter?.spoolInfo?.hasActiveSpool && (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  disabled={spoolActionPending}
                  onClick={async () => {
                    setSpoolActionPending(true);
                    try {
                      await apiClient.clearActiveSpool(printer.id);
                      refetch();
                      setTimeout(() => refetch(), 2000);
                    } catch (err) {
                      console.error('Failed to eject spool:', err);
                    } finally {
                      setSpoolActionPending(false);
                    }
                  }}
                  className="!p-1 !h-auto"
                  title="Eject spool"
                  aria-label="Eject spool"
                  iconCenter={<EjectIcon className="h-4 w-4" />}
                ></Button>
              )}
            </div>
          }
        >
          {displayPrinter?.spoolInfo?.hasActiveSpool ? (
            <div className="space-y-2 text-xs">
              {displayPrinter.spoolInfo.vendor && (
                <div className="flex justify-between">
                  <span className="text-pf-text-secondary">Vendor:</span>
                  <span className="text-pf-text-primary font-medium">{displayPrinter.spoolInfo.vendor}</span>
                </div>
              )}
              {displayPrinter.spoolInfo.material && (
                <div className="flex justify-between">
                  <span className="text-pf-text-secondary">Material:</span>
                  <span className="text-pf-text-primary font-medium">{displayPrinter.spoolInfo.material}</span>
                </div>
              )}
              {displayPrinter.spoolInfo.colorHex && (
                <div className="flex justify-between items-center">
                  <span className="text-pf-text-secondary">Color:</span>
                  <div className="flex items-center gap-2">
                    <div
                      className="w-3 h-3 rounded-sm border border-pf-border"
                      style={{ backgroundColor: displayPrinter.spoolInfo.colorHex }}
                      title={displayPrinter.spoolInfo.colorHex}
                    />
                    <span className="text-pf-text-primary font-medium">{displayPrinter.spoolInfo.colorHex}</span>
                  </div>
                </div>
              )}
              {displayPrinter.spoolInfo.spoolName && (
                <div className="flex justify-between">
                  <span className="text-pf-text-secondary">Spool:</span>
                  <span className="text-pf-text-primary font-medium">{displayPrinter.spoolInfo.spoolName}</span>
                </div>
              )}
              {displayPrinter.spoolInfo.remainingWeightG !== undefined && displayPrinter.spoolInfo.remainingWeightG !== null && (
                <div className="flex justify-between">
                  <span className="text-pf-text-secondary">Remaining:</span>
                  <span className="text-pf-text-primary font-medium">{Math.round(displayPrinter.spoolInfo.remainingWeightG)}g</span>
                </div>
              )}
            </div>
          ) : (
            <div className="text-xs text-pf-text-tertiary">
              <p>No spool loaded</p>
            </div>
          )}
        </CollapsibleSection>
        )}
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
        activeSpoolId={displayPrinter?.spoolInfo?.activeSpoolId}
        onSelect={async (spoolId) => {
          setSpoolActionPending(true);
          try {
            await apiClient.setActiveSpool(printer.id, spoolId);
            setShowSpoolPicker(false);
            // Immediate refetch + delayed re-fetch to pick up spool info
            // after the Moonraker subscription service updates the status cache
            refetch();
            setTimeout(() => refetch(), 2000);
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
