import { useState, useRef, useEffect, useMemo } from 'react';
import { getBackendIcon } from '@/common/utils/printerBackendIcon';
import { apiClient } from '@/services/api';
import type { Printer, TempTargets, MoveRequest } from '@/types/api';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { Button, TemperatureInput, MovementInput, Select, ControlPadButton } from '@/common/components/ui';
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
  CloseIcon,
  SnowflakeIcon,
} from '@/common/components/icons/MdiIcons';
import { usePrinters } from '@/common/hooks/useApi';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';

interface DetailedPrinterCardProps {
  printer: Printer;
  onEdit?: (printer: Printer) => void;
  onDismiss?: () => void;
}

function formatTemperature(temp: number | undefined): string {
  if (temp === undefined || temp === null) return '---';
  return `${temp.toFixed(1)}°C`;
}

function formatTempWithTarget(current: number | undefined, target: number | undefined): string {
  if (target && target > 0) {
    return `${formatTemperature(current)} → ${formatTemperature(target)}`;
  }
  return formatTemperature(current);
}

function formatPos(val: number | undefined): string {
  if (val === undefined || val === null) return '---';
  return val.toFixed(2);
}

function toCamelCase(str: string): string {
  return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
}

export function DetailedPrinterCard({ printer: initialPrinter, onEdit, onDismiss }: DetailedPrinterCardProps) {
  // Fetch from shared usePrinters() cache (same as table view/sidebar) to ensure consistency
  const { data: allPrinters = [] } = usePrinters();
  const apiPrinter = useMemo(
    () => allPrinters.find(p => p.id === initialPrinter.id) ?? initialPrinter,
    [allPrinters, initialPrinter]
  );
  // Merge with realtime SignalR updates
  const printer = usePrinterDisplay(apiPrinter);

  const [showCamera, setShowCamera] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [hotendTemp, setHotendTemp] = useState<number | string>('');
  const [bedTemp, setBedTemp] = useState<number | string>('');
  const [moveX, setMoveX] = useState<number | string>('');
  const [moveY, setMoveY] = useState<number | string>('');
  const [moveZ, setMoveZ] = useState<number | string>('');
  const [step, setStep] = useState(1);
  const [expandedImageVisible, setExpandedImageVisible] = useState(false);

  const expandedProgressRef = useRef<HTMLDivElement>(null);

  // Determine colors based on state
  const state = printer.state ?? 'Unknown';
  const isOnline = printer.isOnline ?? false;
  const isPrinting = isOnline && (state === 'Printing' || state === 'Busy');
  const isPaused = state === 'Paused';
  const isShutdown = state === 'Shutdown' || state === 'Halted';

  const stateColorClasses = (() => {
    if (!isOnline) return 'bg-slate-500 text-white';
    if (isPrinting) return 'bg-pf-success-bg text-white font-bold';
    if (isPaused) return 'bg-yellow-600 text-white';
    if (isShutdown) return 'bg-red-600 text-white';
    return 'bg-blue-600 text-white';
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
    try {
      let result;
      switch (action) {
        case 'pause':
          result = await apiClient.pausePrint(printer.id);
          break;
        case 'resume':
          result = await apiClient.resumePrint(printer.id);
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
    }
  };

  const handleStepChange = (newStep: number) => {
    setStep(newStep);
  };

  const handleHotendTempKeyDown = async (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      const targets: TempTargets = { hotend: hotendTemp as number, bed: bedTemp as number };
      const result = await apiClient.setTemperatures(printer.id, targets);
      if (!result.success) {
        console.error(`Failed to set hotend temp:`, result.error);
      }
    }
  };

  const handleBedTempKeyDown = async (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      const targets: TempTargets = { hotend: hotendTemp as number, bed: bedTemp as number };
      const result = await apiClient.setTemperatures(printer.id, targets);
      if (!result.success) {
        console.error(`Failed to set bed temp:`, result.error);
      }
    }
  };

  const handleApplyPreset = async (preset: string) => {
    try {
      let targets: TempTargets = { hotend: 0, bed: 0 };
      
      switch (preset.toLowerCase()) {
        case 'abs':
          targets = { hotend: 240, bed: 100 };
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

  const handleMove = async (axis: 'X' | 'Y' | 'Z', distance: number) => {
    try {
      const move: MoveRequest = {};
      move[axis.toLowerCase() as keyof MoveRequest] = distance;
      
      const result = await apiClient.movePrinter(printer.id, move);
      
      if (!result.success) {
        console.error(`Failed to move ${axis} by ${distance}:`, result.error);
      }
    } catch (error) {
      console.error(`Error moving ${axis}:`, error);
    }
  };

  const handleHome = async (axes?: string) => {
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
    }
  };

  const handleViewHistory = () => {
    setShowHistory(true);
  };

  return (
    <div className={`border rounded-xl p-3 bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 shadow-lg border-pf-border w-full min-w-0 max-w-sm`}>
      {/* Header */}
      <div className="flex justify-between items-start mb-4 gap-4">
        <div className="flex justify-between items-start flex-1 gap-4">
          <div className="flex-1">
            <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase mb-1">
              {printer.name}
            </div>
            {(printer.manufacturerName || printer.modelName) && (
              <div className="text-pf-text-secondary text-xs">
                {`${printer.manufacturerName || ''} ${printer.modelName || ''}`.trim()}
              </div>
            )}
            <div className="flex items-center gap-2 mb-1">
              <a 
                href={printer.frontendUrl} 
                target="_blank" 
                rel="noopener noreferrer"
                className="text-pf-text-secondary hover:text-pf-text-primary"
                aria-label={`Open printer ${printer.name} in new tab`}
                title={`Open printer ${printer.name}`}
              >
                <ExternalLinkIcon className="h-4 w-4" />
              </a>
              {/* Camera button - always visible, enabled/disabled based on camera URLs */}
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={() => setShowCamera(!showCamera)}
                disabled={!hasCameraUrls}
                className="!p-1 !h-auto"
                aria-label={showCamera ? 'Hide camera stream' : 'Show camera stream'}
                title={hasCameraUrls ? `Camera available` : 'No camera configured'}
                iconCenter={<CameraIcon className="h-4 w-4" />}
              >
        </Button>
            </div>
            {showCamera && (
              <div className="mt-2 w-52 min-h-32 flex items-center justify-center bg-pf-bg-2 bg-opacity-30 border border-pf-border rounded-md overflow-hidden">
                {cameraStreamUrl && expandedImageVisible ? (
                    <img 
                      src={cameraStreamUrl} 
                      alt="webcam snapshot"
                      className="max-w-full max-h-full object-contain"
                      onError={() => setExpandedImageVisible(false)}
                      onLoad={() => setExpandedImageVisible(true)}
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
          
          <div className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ${stateColorClasses}`}>
            {getBackendIcon(printer.backend)}
            {isOnline ? toCamelCase(state) : 'Offline'}
          </div>
        </div>
        
        <div className="flex items-center gap-1">
          <Button
            type="button"
            variant="subtle"
            size="sm"
            onClick={handleViewHistory}
            className="!p-1 !h-auto"
            title="View print history"
            aria-label="View print history"
            iconCenter={<HistoryIcon className="h-4 w-4" />}
          >
        </Button>
          <Button
            type="button"
            variant="subtle"
            size="sm"
            onClick={() => onEdit?.(printer)}
            className="!p-1 !h-auto"
            title="Edit details"
            aria-label="Edit details"
            iconCenter={<EditIcon className="h-4 w-4" />}
          >
        </Button>
          {onDismiss && (
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={onDismiss}
              className="!p-1 !h-auto"
              title="Close"
              aria-label="Close"
              iconCenter={<CloseIcon className="h-4 w-4" />}
            >
        </Button>
          )}
        </div>
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
        <div className="grid grid-cols-3 gap-2 w-full">
          {/* Row 1: Labels */}
          <div className="flex items-center h-5 min-w-0">
            <NozzleIcon className="w-4 h-4 text-red-500 flex-shrink-0" isOn={(printer.hotendTarget ?? 0) > 0} />
            <span className="text-[0.65rem] text-slate-400 ml-auto truncate">
              {formatTempWithTarget(
                printer.hotendTemp,
                printer.hotendTarget
              )}
            </span>
          </div>
          
          <div className="flex items-center h-5 min-w-0">
            <BedIcon className="w-4 h-4 text-blue-500 flex-shrink-0" isOn={(printer.bedTarget ?? 0) > 0} />
            <span className="text-[0.65rem] text-slate-400 ml-auto truncate">
              {formatTempWithTarget(
                printer.bedTemp,
                printer.bedTarget
              )}
            </span>
          </div>

          <div className="flex items-center h-5">
            <span className="text-xs font-bold text-pf-text-secondary">PRESETS</span>
          </div>

          {/* Row 2: Inputs */}
          <TemperatureInput
            value={hotendTemp}
            onChange={(e) => setHotendTemp(e.target.value === '' ? '' : Number(e.target.value))}
            onKeyDown={handleHotendTempKeyDown}
            className="!w-full"
          />
          
          <TemperatureInput
            value={bedTemp}
            onChange={(e) => setBedTemp(e.target.value === '' ? '' : Number(e.target.value))}
            onKeyDown={handleBedTempKeyDown}
            className="!w-full"
          />
          
          <div className="flex gap-1 items-stretch h-9 min-w-0">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={isPrinting}
              onClick={() => handleApplyPreset('cooldown')}
              title="Cooldown"
              aria-label="Cooldown"
              className="flex-shrink-0 px-2"
              iconCenter={<SnowflakeIcon className="h-4 w-4" />}
            >
        </Button>
            <Select
              value=""
              onChange={(e) => {
                const value = e.target.value;
                if (value) {
                  handleApplyPreset(value);
                }
              }}
              className="flex-1 min-w-0"
            >
              <option value="">---</option>
              <option value="ABS">ABS</option>
              <option value="ASA">ASA</option>
              <option value="PLA">PLA</option>
              <option value="PC">PC</option>
              <option value="PCTG">PCTG</option>
              <option value="PETG">PETG</option>
            </Select>
          </div>
        </div>
      </div>

      {/* Move and Control Section - Side by Side */}
      <div className="mb-2">
        {/* Row 1: Labels and Pads side by side */}
        <div className="flex gap-4 items-start flex-wrap">
          {/* Left Column: Move */}
          <div className="flex flex-col gap-2 items-start flex-1 min-w-[12rem]">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
              Move
            </div>
            <div className="flex gap-2 items-start">
              {/* XY Pad */}
              <div className="grid grid-cols-3 grid-rows-3 gap-1 w-fit">
                {/* Top row */}
                <ControlPadButton
                  disabled={isPrinting}
                  onClick={() => handleHome()}
                  title="Home all axes"
                  padSize="small"
                >
                  <HomeIcon className="h-4 w-4" />
                </ControlPadButton>
                <ControlPadButton
                  disabled={isPrinting}
                  onClick={() => handleMove('Y', step)}
                  padSize="small"
                >
                  ▲
                </ControlPadButton>
                <ControlPadButton
                  disabled={!isOnline || isPrinting}
                  onClick={() => handleControlAction('disable-motors')}
                  title="Disable Motors (M84)"
                  padSize="small"
                >
                  <DisableMotorsIcon className="h-4 w-4" />
                </ControlPadButton>
                
                {/* Middle row */}
                <ControlPadButton
                  disabled={isPrinting}
                  onClick={() => handleMove('X', -step)}
                  padSize="small"
                >
                  ◀
                </ControlPadButton>
                <ControlPadButton
                  disabled={isPrinting}
                  onClick={() => handleHome('xy')}
                  title="Home X/Y"
                  padSize="small"
                >
                  <HomeIcon className="h-4 w-4" />
                </ControlPadButton>
                <ControlPadButton
                  disabled={isPrinting}
                  onClick={() => handleMove('X', step)}
                  padSize="small"
                >
                  ▶
                </ControlPadButton>
                
                {/* Bottom row */}
                <div></div>
                <ControlPadButton
                  disabled={isPrinting}
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
                  disabled={isPrinting}
                  onClick={() => handleMove('Z', step)}
                  padSize="small"
                >
                  Z+
                </ControlPadButton>
                <ControlPadButton
                  disabled={isPrinting}
                  onClick={() => handleHome('z')}
                  title="Home Z"
                  padSize="small"
                >
                  <HomeIcon className="h-4 w-4" />
                </ControlPadButton>
                <ControlPadButton
                  disabled={isPrinting}
                  onClick={() => handleMove('Z', -step)}
                  padSize="small"
                >
                  Z-
                </ControlPadButton>
              </div>
            </div>
          </div>

          {/* Right Column: Control */}
          <div className="flex flex-col gap-2 items-start">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
              Control
            </div>
            <div className="grid grid-cols-3 gap-1 w-fit">
              {/* Control buttons row - 3 equal-sized buttons */}
              <ControlPadButton
                disabled={!isPrinting}
                onClick={() => handleControlAction('pause')}
                title="Pause"
                padSize="small"
              >
                <PauseIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                variant="success"
                disabled={!isPaused}
                onClick={() => handleControlAction('resume')}
                title="Resume"
                padSize="small"
              >
                <PlayIcon className="h-4 w-4" />
              </ControlPadButton>
              <ControlPadButton
                variant={isShutdown ? 'secondary' : 'danger'}
                disabled={!isOnline}
                onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
                title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
                padSize="small"
              >
                {isShutdown ? <RefreshIcon className="h-4 w-4" /> : <EmergencyStopIcon className="h-4 w-4" />}
              </ControlPadButton>
            </div>
            
            {/* Steps section - separate from Control buttons */}
            <div className="flex flex-col gap-1 mt-2">
              <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
                Steps
              </div>
              
              {/* Step buttons row - 3 equal-sized buttons */}
              <div className="grid grid-cols-3 gap-1 w-fit">
                {[1, 10, 50].map((stepValue) => (
                  <ControlPadButton
                    key={stepValue}
                    variant={step === stepValue ? 'primary' : 'secondary'}
                    onClick={() => handleStepChange(stepValue)}
                    padSize="small"
                  >
                    {stepValue}
                  </ControlPadButton>
                ))}
              </div>
            </div>
          </div>
        </div>

        {/* Row 2: Axis Fields - 4 column grid, 2 rows, h-12 total, right-aligned labels */}
        <div className="grid grid-cols-4 gap-2 mt-3 w-72 h-12">
          {/* Row 1: Labels (right-aligned) */}
          <div className="flex items-center justify-end pr-1">
            <span className="text-xs font-bold text-pf-text-secondary">[ {formatPos(printer.x)} ]</span>
          </div>
          <div className="flex items-center justify-end pr-1">
            <span className="text-xs font-bold text-pf-text-secondary">[ {formatPos(printer.y)} ]</span>
          </div>
          <div className="flex items-center justify-end pr-1">
            <span className="text-xs font-bold text-pf-text-secondary">[ {formatPos(printer.z)} ]</span>
          </div>
          <div className="flex items-center">
            <span className="text-xs font-bold text-pf-text-secondary">GO</span>
          </div>
          
          {/* Row 2: Inputs */}
          <MovementInput
            axis="X"
            disabled={isPrinting}
            value={moveX}
            onChange={(e) => setMoveX(e.target.value === '' ? '' : Number(e.target.value))}
            className="!w-full"
          />
          <MovementInput
            axis="Y"
            disabled={isPrinting}
            value={moveY}
            onChange={(e) => setMoveY(e.target.value === '' ? '' : Number(e.target.value))}
            className="!w-full"
          />
          <MovementInput
            axis="Z"
            disabled={isPrinting}
            value={moveZ}
            onChange={(e) => setMoveZ(e.target.value === '' ? '' : Number(e.target.value))}
            className="!w-full"
          />
          <Button
            type="button"
            variant="primary"
            size="sm"
            disabled={isPrinting}
            onClick={() => {
              const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
              if (win.PrintFarmerDebug?.expandablePrinterCard) {
                console.log('[PrintFarmer] DetailedPrinterCard: Moving to', moveX, moveY, moveZ);
              }
            }}
            className="w-full h-full !p-0"
          >
            GO
          </Button>
        </div>
      </div>

      {/* History Modal */}
      <PrinterHistoryModal
        isOpen={showHistory}
        onClose={() => setShowHistory(false)}
        printer={printer}
      />
    </div>
  );
}
