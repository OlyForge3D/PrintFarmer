import { useState, useRef, useEffect, useMemo } from 'react';
import { PanelRightOpen } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { Printer, TempTargets, MoveRequest, PrinterBackendCapabilitiesDto } from '@/types/api';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { SpoolPickerModal } from '@/features/printers/components/SpoolPickerModal';
import { Button, TemperatureControlRow, MovementInput, MoveDistanceSlider, Select, ControlPadButton, LoadedFilamentCard } from '@/common/components/ui';
import { 
  NozzleIcon, 
  BedIcon, 
  EditIcon, 
  PlayIcon, 
  PauseIcon, 
  EmergencyStopIcon, 
  HomeIcon,
  DisableMotorsIcon,
  HistoryIcon,
  RefreshIcon,
  ExternalLinkIcon,
  CameraIcon,
  SnowflakeIcon,
  XCircleIcon,
  FileIcon,
  FilamentLoadIcon,
  FilamentUnloadIcon,
  FilamentChangeIcon,
  EjectIcon,
} from '@/common/components/icons/MdiIcons';
import { usePrinters } from '@/common/hooks/useApi';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';
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
} from '@/features/printers/constants/temperaturePresets';
import { getHomeButtonStyle } from '@/features/printers/utils/homeButtonStyle';

interface DetailedPrinterCardProps {
  printer: Printer;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onEdit?: (printer: Printer) => void;
  onDismiss?: () => void;
}

function formatTemperature(temp: number | undefined): string {
  if (temp === undefined || temp === null) return '---';
  return `${temp.toFixed(1)}°C`;
}

function toCamelCase(str: string): string {
  return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
}

export function DetailedPrinterCard({ printer: initialPrinter, backendCapabilities, onEdit, onDismiss }: DetailedPrinterCardProps) {
  // Fetch from shared usePrinters() cache (same as table view/sidebar) to ensure consistency
  const { data: allPrinters = [] } = usePrinters();
  const queryClient = useQueryClient();
  const apiPrinter = useMemo(
    () => allPrinters.find(p => p.id === initialPrinter.id) ?? initialPrinter,
    [allPrinters, initialPrinter]
  );
  // Merge with realtime SignalR updates
  const printer = usePrinterDisplay(apiPrinter);

  const [showCamera, setShowCamera] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [showSpoolPicker, setShowSpoolPicker] = useState(false);
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

  const expandedProgressRef = useRef<HTMLDivElement>(null);

  // Determine colors based on state
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isEnabled = printer.isEnabled ?? true;
  const isPrinting = isOnline && state === 'Printing';
  const isPaused = state === 'Paused';
  const isShutdown = state === 'Offline' || state === 'Shutdown' || state === 'Halted';
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

  const homedAxesRaw = printer.homedAxes;
  const isHomedStateKnown = typeof homedAxesRaw === 'string';
  const homedAxes = (homedAxesRaw ?? '').toLowerCase();
  const isXHomed = isHomedStateKnown && homedAxes.includes('x');
  const isYHomed = isHomedStateKnown && homedAxes.includes('y');
  const isZHomed = isHomedStateKnown && homedAxes.includes('z');
  const isXYHomed = isXHomed && isYHomed;
  const isAllHomed = isXYHomed && isZHomed;

  const statusDotClasses = (() => {
    if (!isOnline) return 'bg-slate-400';
    if (isPrinting) return 'bg-pf-success-bg';
    if (isPaused) return 'bg-yellow-500';
    if (isShutdown) return 'bg-red-500';
    return 'bg-blue-500';
  })();

  // Update progress bar width
  useEffect(() => {
    if (expandedProgressRef.current && printer.progress) {
      expandedProgressRef.current.style.width = `${Math.max(0, Math.min(100, printer.progress))}%`;
    }
  }, [printer.progress]);

  // Camera URL handling
  const cameraStreamUrl = apiPrinter.cameraStreamUrl ?? null;
  const hasCameraUrls = !!cameraStreamUrl;

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

  const handleViewHistory = () => {
    setShowHistory(true);
  };

  return (
    <div className="relative rounded-xl p-3 shadow-lg bg-pf-card border border-white/10 w-full min-w-92">
      {/* Header */}
      <div className="mb-4">
        {/* Top row: Name + Status Pill (match collapsed card) */}
        <div className="flex justify-between items-center mb-2 gap-2">
          <div className="flex-1 min-w-0">
            <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase tracking-wide truncate">
              {printer.name}
            </div>
            {(printer.modelName) && (
              <div className="text-pf-text-secondary text-xs truncate">
                {`${printer.modelName || ''}`.trim()}
              </div>
            )}
          </div>

          <div className="inline-flex items-center gap-1.5 px-2 py-1 rounded-full text-xs font-medium shrink-0 bg-white/[0.04] border border-white/10 text-pf-text-primary">
            <span className={`h-2 w-2 rounded-full ${statusDotClasses}`} aria-hidden />
            <span className="text-pf-text-secondary">
              {isOnline ? toCamelCase(state) : 'Offline'}
            </span>
          </div>
        </div>

        {/* Subtle separator above actions (match collapsed card) */}
        <div className="h-px w-full bg-white/10 mb-2" aria-hidden />

        {/* Action buttons row */}
        <div className="flex w-full items-center justify-between gap-2" role="toolbar" aria-label="Printer actions">
          <div className="flex items-center gap-1">
            <a
              href={printer.frontendUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="text-pf-text-secondary hover:text-pf-text-primary shrink-0 h-8 w-8 inline-flex items-center justify-center rounded-xs"
              aria-label={`Open printer ${printer.name} in new tab`}
              title={`Open printer ${printer.name}`}
            >
              <ExternalLinkIcon className="h-4 w-4" />
            </a>

            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => setShowCamera(!showCamera)}
              disabled={!hasCameraUrls || !isEnabled}
              className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              aria-label={showCamera ? 'Hide camera stream' : 'Show camera stream'}
              title={!isEnabled ? 'Printer disabled' : hasCameraUrls ? 'Camera available' : 'No camera configured'}
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
              variant="ghost"
              size="sm"
              onClick={() => onEdit?.(printer)}
              className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
              title="Edit details"
              aria-label="Edit details"
              iconCenter={<EditIcon className="h-4 w-4" />}
            >
            </Button>
            {onDismiss && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={onDismiss}
                className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
                title="Close details sidebar"
                aria-label="Close details sidebar"
                iconCenter={<PanelRightOpen className="h-4 w-4" />}
              >
              </Button>
            )}
          </div>
        </div>

        {showCamera && (
          <div className="mt-3 w-52 aspect-video flex items-center justify-center bg-pf-bg-2/30 border border-pf-border rounded-md overflow-hidden">
            {cameraStreamUrl ? (
              <img
                src={cameraStreamUrl}
                alt="webcam stream"
                className="w-full h-full object-cover"
                onError={(e) => {
                  // Avoid broken-image icon; fall back to "No camera" message.
                  (e.currentTarget as HTMLImageElement).src = '';
                }}
              />
            ) : (
              <div className="text-center text-pf-text-secondary p-4">
                <CameraIcon className="h-8 w-8 mx-auto mb-2 opacity-50" />
                <p className="text-sm">No camera configured</p>
              </div>
            )}
          </div>
        )}
      </div>

      {/* Progress bar for active prints */}
      {isOnline && printer.progress !== undefined && printer.progress > 0 && (
        <div className="mb-4">
          <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
            <span className="truncate flex-1">{printer.jobName || 'Printing...'}</span>
            <span className="font-semibold ml-2">{Math.round(printer.progress)}%</span>
          </div>
          <div className="w-full bg-pf-border-dark rounded-full h-2 overflow-hidden">
            <div
              ref={expandedProgressRef}
              className="bg-pf-success-bg h-2 rounded-full transition-all duration-300"
            >
              <span className="sr-only">Print progress: {Math.round(Math.max(0, Math.min(100, printer.progress))) }%</span>
            </div>
          </div>
        </div>
      )}

      {/* Temps Section */}
      <div className="mb-2">
        <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide mb-1 -ml-1">Temps</div>
        <div className="space-y-1">
          <div className="flex justify-end gap-1 items-stretch h-8 pb-1">
            <Button
              type="button"
              variant="ghost"
              size="sm"
              disabled={temperatureActionPending || !canCooldownNow}
              onClick={() => handleApplyPreset('cooldown')}
              title="Cooldown"
              aria-label="Cooldown"
              className="shrink-0 !px-2"
              iconCenter={<SnowflakeIcon className={`h-4 w-4 ${((printer.hotendTarget ?? 0) > 0 || (printer.bedTarget ?? 0) > 0) ? 'text-pf-accent' : 'text-pf-text-secondary'}`} />}
            >
            </Button>
            <div className="relative w-24">
              <Select
                value=""
                disabled={temperatureActionPending || !canSetTemperaturesNow}
                onChange={(e) => {
                  const value = e.target.value;
                  if (value) {
                    handleApplyPreset(value);
                  }
                }}
                className="h-8 text-[10px] uppercase tracking-wide font-semibold !pr-6 !border-transparent !bg-transparent enabled:hover:[background:rgba(255,255,255,0.10)] focus:border-transparent focus:ring-0"
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

          <TemperatureControlRow
            icon={<NozzleIcon className="w-4 h-4 text-red-500" isOn={(printer.hotendTarget ?? 0) > 0} />}
            label="Hotend"
            stateLabel={(printer.hotendTarget ?? 0) > 0 ? 'on' : 'off'}
            liveReading={formatTemperature(printer.hotendTemp)}
            value={hotendTemp}
            onChange={(e) => setHotendTemp(e.target.value === '' ? '' : Number(e.target.value))}
            onKeyDown={handleHotendTempKeyDown}
            disabled={temperatureActionPending || !canSetTemperaturesNow}
            presetOptions={hotendPresetOptions}
            onPresetSelect={(preset) => {
              void handleApplySingleHeaterPreset('hotend', preset);
            }}
          />

          <TemperatureControlRow
            icon={<BedIcon className="w-4 h-4 text-blue-500" isOn={(printer.bedTarget ?? 0) > 0} />}
            label="Bed"
            stateLabel={(printer.bedTarget ?? 0) > 0 ? 'on' : 'off'}
            liveReading={formatTemperature(printer.bedTemp)}
            value={bedTemp}
            onChange={(e) => setBedTemp(e.target.value === '' ? '' : Number(e.target.value))}
            onKeyDown={handleBedTempKeyDown}
            disabled={temperatureActionPending || !canSetTemperaturesNow}
            presetOptions={bedPresetOptions}
            onPresetSelect={(preset) => {
              void handleApplySingleHeaterPreset('bed', preset);
            }}
          />
        </div>
      </div>

      {/* Move and Control Section - Side by Side */}
      <div className="mb-2">
        {/* Row 1: Labels and Pads side by side */}
        <div className="flex gap-4 items-start flex-wrap">
          {/* Left Column: Move */}
          <div className="flex flex-col gap-2 items-start flex-1 min-w-48">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
              Move
            </div>
            <div className="flex gap-2 items-start">
              {/* XY Pad */}
              <div className="grid grid-cols-3 grid-rows-3 gap-1 w-fit">
                {/* Top row */}
                <ControlPadButton
                  disabled={movementActionPending || !canMoveNow}
                  onClick={() => handleHome()}
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
                  ▲
                </ControlPadButton>
                <ControlPadButton
                  disabled={!canDisableMotorsNow}
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
                  ◀
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
                  ▶
                </ControlPadButton>
                
                {/* Bottom row */}
                <div></div>
                <ControlPadButton
                  disabled={movementActionPending || !canMoveNow}
                  onClick={() => handleMove('Y', -step)}
                  padSize="small"
                >
                  ▼
                </ControlPadButton>
                <div></div>
              </div>

              {/* Z Pad */}
              <div className="flex flex-col gap-1 w-fit">
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
            </div>
            {/* Move distance slider */}
            <MoveDistanceSlider value={step} onChange={handleStepChange} disabled={!canSetStepNow} />
          </div>

          {/* Right Column: Control */}
          <div className="flex flex-col gap-2 items-start">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
              Control
            </div>
            <div className="grid grid-cols-3 gap-1 w-fit">
              {/* Control buttons row - 3 equal-sized buttons */}
              <ControlPadButton
                disabled={controlActionPending || !canPauseOrResumeNow}
                onClick={() => handleControlAction(isPaused ? 'resume' : 'pause')}
                title={isPaused ? 'Resume' : 'Pause'}
                padSize="small"
              >
                {isPaused ? <PlayIcon className="h-4 w-4" /> : <PauseIcon className="h-4 w-4" />}
              </ControlPadButton>
              <ControlPadButton
                disabled={controlActionPending || !canCancelNow}
                onClick={() => handleControlAction('cancel')}
                title="Cancel"
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
            
            {/* Filament Macros - capability-based */}
            {support.supportsFilamentControl && (
              <div className="flex flex-col gap-1 mt-2">
                <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
                  Filament
                </div>
                <div className="grid grid-cols-3 gap-1 w-fit">
                  <ControlPadButton
                    disabled={filamentActionPending || !canFilamentControl({ isOnline, isEnabled, isPrinting, support })}
                    onClick={() => handleFilamentAction('load')}
                    title="Load Filament"
                    padSize="small"
                  >
                    <FilamentLoadIcon className="w-4 h-4" />
                  </ControlPadButton>
                  <ControlPadButton
                    disabled={filamentActionPending || !canFilamentControl({ isOnline, isEnabled, isPrinting, support })}
                    onClick={() => handleFilamentAction('unload')}
                    title="Unload Filament"
                    padSize="small"
                  >
                    <FilamentUnloadIcon className="w-4 h-4" />
                  </ControlPadButton>
                  <ControlPadButton
                    disabled={filamentActionPending || !canFilamentChange({ isOnline, isEnabled, support })}
                    onClick={() => handleFilamentAction('change')}
                    title="Change Filament (M600)"
                    padSize="small"
                  >
                    <FilamentChangeIcon className="w-4 h-4" />
                  </ControlPadButton>
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Row 2: Axis Fields - 4 column grid with bracket-style position labels */}
        <div className="grid grid-cols-4 gap-2 mt-3 w-72 pt-2">
          <MovementInput
            axis="X"
            currentPosition={printer.x}
            showPlaceholderWhenUnavailable={!canManualMoveNow}
            disabled={movementActionPending || !canManualMoveNow}
            value={moveX}
            onChange={(e) => setMoveX(e.target.value === '' ? '' : Number(e.target.value))}
            className="!w-full"
          />
          <MovementInput
            axis="Y"
            currentPosition={printer.y}
            showPlaceholderWhenUnavailable={!canManualMoveNow}
            disabled={movementActionPending || !canManualMoveNow}
            value={moveY}
            onChange={(e) => setMoveY(e.target.value === '' ? '' : Number(e.target.value))}
            className="!w-full"
          />
          <MovementInput
            axis="Z"
            currentPosition={printer.z}
            showPlaceholderWhenUnavailable={!canManualMoveNow}
            disabled={movementActionPending || !canManualMoveNow}
            value={moveZ}
            onChange={(e) => setMoveZ(e.target.value === '' ? '' : Number(e.target.value))}
            className="!w-full"
          />
          <div className="pt-2">
            <ControlPadButton
              disabled={movementActionPending || !canManualMoveNow || (moveX === '' && moveY === '' && moveZ === '')}
              onClick={async () => {
                if (moveX !== '') await handleMove('X', Number(moveX));
                if (moveY !== '') await handleMove('Y', Number(moveY));
                if (moveZ !== '') await handleMove('Z', Number(moveZ));
              }}
              title="Go to position"
              padSize="small"
            >
              GO
            </ControlPadButton>
          </div>
        </div>
      </div>

      {/* Spool Info Section - Only show when Spoolman is configured */}
      {printer.spoolInfo && (
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
              className="!p-0.5 !h-auto"
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
                    await apiClient.clearActiveSpool(printer.id);
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
                className="!p-0.5 !h-auto"
                title="Eject spool"
                aria-label="Eject spool"
                iconCenter={<EjectIcon className="h-3.5 w-3.5" />}
              ></Button>
            )}
          </div>
        </div>
        <LoadedFilamentCard spoolInfo={printer.spoolInfo} />
      </div>
      )}

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
            await apiClient.setActiveSpool(printer.id, spoolId);
            setShowSpoolPicker(false);
            queryClient.setQueryData<Printer[]>(['printers'], (old) =>
              old?.map(p => p.id === printer.id
                ? {
                    ...p,
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
          } finally {
            setSpoolActionPending(false);
          }
        }}
      />
    </div>
  );
}
