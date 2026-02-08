if (!window.PrintFarmerDebug) {
  window.PrintFarmerDebug = {};
}
import moonrakerIcon from '@/assets/moonraker.svg';
import octoprintIcon from '@/assets/octoprint.svg';
import { PrinterBackend } from '@/types/api';
// Official backend icon for this printer
function getBackendIcon(backend: PrinterBackend | number | string) {
  // Accepts enum, number, or string (for robustness)
  let backendValue: PrinterBackend | undefined = undefined;
  if (typeof backend === 'number') {
    backendValue = backend;
  } else if (typeof backend === 'string') {
    // Try to map string to enum
    switch (backend) {
      case 'Moonraker': backendValue = PrinterBackend.Moonraker; break;
      case 'PrusaLink': backendValue = PrinterBackend.PrusaLink; break;
      case 'SDCP': backendValue = PrinterBackend.SDCP; break;
      case 'OctoPrint': backendValue = PrinterBackend.OctoPrint; break;
      default: backendValue = undefined;
    }
  } else {
    backendValue = undefined;
  }
  switch (backendValue) {
    case PrinterBackend.Moonraker:
      return <img src={moonrakerIcon} alt="Moonraker" title="Moonraker" className="inline h-5 w-5 align-middle mr-1" />;
    case PrinterBackend.PrusaLink:
      return <span title="PrusaLink" aria-label="PrusaLink" role="img" className="mr-1">🔗</span>;
    case PrinterBackend.SDCP:
      return <span title="SDCP" aria-label="SDCP" role="img" className="mr-1">📡</span>;
    case PrinterBackend.OctoPrint:
      return <img src={octoprintIcon} alt="OctoPrint" title="OctoPrint" className="inline h-5 w-5 align-middle mr-1" />;
    default:
      return <span title="Other" aria-label="Other" role="img" className="mr-1">🖨️</span>;
  }
}
import { useState, useRef, useEffect } from 'react';
import { apiClient } from '@/services/api';
import type { Printer, TempTargets, MoveRequest } from '@/types/api';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { renderUnknown } from '@/common/utils/renderUnknown';
import { Button, TemperatureInput, MovementInput, Select } from '@/common/components/ui';
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
  ChevronDownIcon,
  ExternalLinkIcon,
  CameraIcon,
  MinusIcon,
  ArrowUpIcon,
  ArrowLeftIcon,
  ArrowRightIcon,
  ArrowDownIcon,
  SnowflakeIcon
} from '@/common/components/icons/MdiIcons';
import { usePrinter } from '@/common/hooks/useApi';

interface ExpandablePrinterCardProps {
  printer: Printer;
  onEdit?: (printer: Printer) => void;
  // Optional callbacks not currently used by this component's internal UI; prefixed to silence unused var lint
  onDelete?: (printer: Printer) => void;
  onManage?: (printer: Printer) => void;
}
// We intentionally accept onDelete/onManage in props interface for future actions but do not destructure them to avoid unused vars
export function ExpandablePrinterCard({ printer: initialPrinter, onEdit }: ExpandablePrinterCardProps) {
  // Use the API hook to get the complete printer data with merged realtime status
  // The backend/API layer handles merging realtime status with printer data
  const { data: printer = initialPrinter } = usePrinter(initialPrinter.id);
  
  const [isExpanded, setIsExpanded] = useState(false);
  const [showCamera, setShowCamera] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [step, setStep] = useState(10);
  const [hotendTemp, setHotendTemp] = useState<number | ''>('');
  const [bedTemp, setBedTemp] = useState<number | ''>('');
  const [moveX, setMoveX] = useState<number | ''>('');
  const [moveY, setMoveY] = useState<number | ''>('');
  const [moveZ, setMoveZ] = useState<number | ''>('');
  const [collapsedImageVisible, setCollapsedImageVisible] = useState(true);
  const [expandedImageVisible, setExpandedImageVisible] = useState(true);

  // API hook provides printer data with merged realtime status - no need for manual merging
  const isOnline = printer.isOnline ?? false;
  const state = printer.state ?? 'unknown';
  const isPrinting = state.toLowerCase().includes('printing');
  const isPaused = state.toLowerCase().includes('paused');
  const isShutdown = state.toLowerCase().includes('shutdown') || state.toLowerCase().includes('error');

  // Camera URLs come from API (no need to merge with SignalR)
  const cameraStreamUrl = printer.cameraStreamUrl;
  const cameraSnapshotUrl = printer.cameraSnapshotUrl;
  const hasCameraUrls = !!(cameraSnapshotUrl || cameraStreamUrl);

  const formatTempWithTarget = (currentTemp: number | null | undefined, targetTemp: number | null | undefined): string => {
    if (currentTemp === null || currentTemp === undefined) return '[ --°C ]';
    
    const currentRounded = Math.round(currentTemp);
    
    // If target is null, undefined, or 0, just show current temperature
    if (targetTemp === null || targetTemp === undefined || targetTemp === 0) {
      return `[ ${currentRounded}°C ]`;
    }
    
    // If heating (target > current), show both
    const targetRounded = Math.round(targetTemp);
    if (targetRounded > currentRounded) {
      return `[ ${currentRounded}°C → ${targetRounded}°C ]`;
    }
    
    // If at target or cooling, just show current
    return `[ ${currentRounded}°C ]`;
  };

  const formatPos = (pos: number | null | undefined): string => {
    if (pos === null || pos === undefined) return '---';
    return pos.toFixed(1);
  };

  const toCamelCase = (str?: string) => {
    if (!str) return '';
    return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
  };

  // Function to get state-specific background and text colors based on Moonraker states
  // Colors chosen to meet WCAG AAA 7.5:1 contrast ratio with white text
  const getStateColors = (state?: string, isOnline: boolean = true): string => {
    if (!isOnline) {
      return 'bg-gray-700 text-white'; // Offline - dark gray background (7.59:1 ratio)
    }

    const normalizedState = state?.toLowerCase() || '';
    
    switch (normalizedState) {
      // Print states (from print_stats)
      case 'printing':
        return 'bg-blue-700 text-white'; // Dark blue background (8.59:1 ratio)
      case 'paused':
        return 'bg-yellow-700 text-white'; // Dark yellow/amber background (7.77:1 ratio)
      case 'complete':
        return 'bg-green-700 text-white'; // Dark green background (7.77:1 ratio)
      case 'cancelled':
        return 'bg-orange-700 text-white'; // Dark orange background (8.84:1 ratio)
      case 'error':
        return 'bg-red-700 text-white'; // Dark red background (9.67:1 ratio)
      case 'standby':
        return 'bg-emerald-700 text-white'; // Dark emerald background (8.14:1 ratio)
        
      // Klipper/webhooks states  
      case 'ready':
        return 'bg-emerald-700 text-white'; // Dark emerald background (8.14:1 ratio)
      case 'startup':
        return 'bg-purple-700 text-white'; // Dark purple background (8.59:1 ratio)
      case 'shutdown':
        return 'bg-red-800 text-white'; // Very dark red background (12.63:1 ratio)
        
      // Fallback for unknown states
      default:
        return 'bg-slate-700 text-white'; // Dark slate background (8.32:1 ratio)
    }
  };

  // Use the new state color function instead of the old statusColor
  const stateColorClasses = getStateColors(state, isOnline);

  // Refs for progress bar elements so we can set dynamic width without using React inline styles
  const collapsedProgressRef = useRef<HTMLDivElement | null>(null);
  const expandedProgressRef = useRef<HTMLDivElement | null>(null);

  // Update progress bar widths via DOM refs to avoid inline style props (project lint rule)
  useEffect(() => {
    const pct = printer.progress !== undefined && printer.progress !== null ? Math.max(0, Math.min(100, printer.progress)) : 0;
    try {
      if (collapsedProgressRef.current) collapsedProgressRef.current.style.width = `${Math.round(pct)}%`;
      if (expandedProgressRef.current) expandedProgressRef.current.style.width = `${Math.round(pct)}%`;
    } catch {
      // Ignore DOM write errors in very restricted test environments
    }
  }, [printer.progress]);

  // progressPct is available via status.progress when needed; dynamic width is set via refs

  const handleToggleExpand = () => {
    setIsExpanded(!isExpanded);
  };

  const handleControlAction = async (action: 'pause' | 'resume' | 'stop' | 'firmware-restart' | 'disable-motors') => {
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
      }
      
      if (!result.success) {
        console.error(`Failed to ${action} printer:`, result.error);
        // You could show a toast notification here
      }
    } catch (error) {
      console.error(`Error during ${action} action:`, error);
    }
  };

  const handleStepChange = (newStep: number) => {
    setStep(newStep);
  };

  const handleHotendTempKeyDown = async (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key !== 'Enter' || hotendTemp === '') return;
    
    try {
      const currentBedTemp = bedTemp === '' ? (printer.bedTarget ?? 0) : bedTemp;
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
      const currentHotendTemp = hotendTemp === '' ? (printer.hotendTarget ?? 0) : hotendTemp;
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
        // Update local state to reflect the change
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

  if (!isExpanded) {
    // Collapsed view - matching Blazor structure
    return (
      <div className="rounded-xl p-3 shadow-lg backdrop-blur-xl bg-white/5 border border-white/10 min-w-104 max-w-104 overflow-hidden flex flex-col min-h-0">
        <div className="flex justify-between items-start mb-4 gap-4">
          <div className="flex justify-between items-start flex-1 gap-4">
            <div className="flex-1">
              <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase mb-1">
                {printer.name}
              </div>
              {(printer.modelName) && (
                <div className="text-pf-text-secondary text-sm mb-1">
                  {`${printer.modelName || ''}`.trim()}
                </div>
              )}
              <div className="flex items-center gap-2 mb-1">
                <div className="text-pf-text-secondary text-xs">
                  {printer.backendUrl}
                </div>
                <a 
                  href={printer.frontendUrl || printer.backendUrl} 
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
            </div>
          </div>
          
          <div className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ${stateColorClasses}`}>
            {getBackendIcon(printer.backend)}
            {isOnline ? toCamelCase(state) : 'Offline'}
          </div>
          
          <div className="flex items-center gap-1">
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={handleToggleExpand}
              className="!p-1 !h-auto"
              title="Expand card"
              aria-label="Expand card"
              iconCenter={<ChevronDownIcon className="h-4 w-4" />}
            >
        </Button>
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
          </div>
        </div>

        {/* Control buttons in collapsed view */}
        <div className="flex items-center gap-2">
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={() => handleControlAction('pause')}
            disabled={!isPrinting}
            iconCenter={<PauseIcon className="h-4 w-4" />}
          >
        </Button>
          <Button
            type="button"
            variant="success"
            size="sm"
            onClick={() => handleControlAction('resume')}
            disabled={!isPaused}
            iconCenter={<PlayIcon className="h-4 w-4" />}
          >
        </Button>
          <Button
            type="button"
            variant={isShutdown ? 'secondary' : 'danger'}
            size="sm"
            onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
            disabled={!isOnline}
            title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
            iconCenter={isShutdown ? <RefreshIcon className="h-4 w-4" /> : <EmergencyStopIcon className="h-4 w-4" />}
          >
        </Button>
        </div>

        {/* Progress bar for active prints */}
        {isOnline && printer.progress !== undefined && printer.progress > 0 && (
          <div className="mt-3">
            <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
              <span className="truncate flex-1">{printer.jobName || 'Printing...'}</span>
              <span className="font-semibold ml-2">{Math.round(printer.progress)}%</span>
            </div>
            <div className="w-full bg-pf-border-dark rounded-full h-2 overflow-hidden">
              <div
                ref={collapsedProgressRef}
                className="bg-pf-success h-2 rounded-full transition-all duration-300"
              >
                <span className="sr-only">Print progress: {Math.round(Math.max(0, Math.min(100, printer.progress))) }%</span>
              </div>
            </div>
          </div>
        )}

              {showCamera && (
                <div className="mt-4 w-52 aspect-video flex items-center justify-center bg-pf-bg-2/30 border border-pf-border rounded-md overflow-hidden">
                  {cameraStreamUrl && collapsedImageVisible ? (
                    <img 
                      src={cameraStreamUrl} 
                      alt="webcam snapshot"
                      className="w-full h-full object-cover"
                      onError={() => setCollapsedImageVisible(false)}
                      onLoad={() => setCollapsedImageVisible(true)}
                    />
                  ) : (
                    <div className="text-center text-pf-text-secondary p-4">
                      <CameraIcon className="h-8 w-8 mx-auto mb-2 opacity-50" />
                      <p className="text-sm">No camera configured</p>
                    </div>
                  )}
                </div>
              )}

              {/* Optional debug panel controlled by window.PrintFarmerDebug.expandablePrinterCardDisplay */}
              {window.PrintFarmerDebug?.expandablePrinterCardDisplay && (
                <div className="mt-3 p-2 bg-pf-bg-0 border border-pf-border rounded-sm text-xs text-pf-text-tertiary">
                  {renderUnknown({ printer })}
                </div>
              )}
        
        {/* History Modal */}
        <PrinterHistoryModal
          isOpen={showHistory}
          onClose={() => setShowHistory(false)}
          printer={printer}
        />
      </div>
    );
  }

  // Expanded view - matching Blazor structure exactly
  return (
    <div className={`border rounded-xl p-3 bg-linear-to-b from-pf-bg-1 to-pf-bg-0 shadow-lg border-pf-border min-w-104 max-w-104`}>
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
              <div className="text-pf-text-secondary text-xs">
                {printer.backendUrl}
              </div>
              <a 
                href={printer.frontendUrl || printer.backendUrl} 
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
              <div className="mt-2 w-52 aspect-video flex items-center justify-center bg-pf-bg-2/30 border border-pf-border rounded-md overflow-hidden">
                {cameraStreamUrl && expandedImageVisible ? (
                    <img 
                      src={cameraStreamUrl} 
                      alt="webcam snapshot"
                      className="w-full h-full object-cover"
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
            onClick={handleToggleExpand}
            className="!p-1 !h-auto"
            title="Collapse card"
            aria-label="Collapse card"
            iconCenter={<MinusIcon className="h-4 w-4" />}
          >
        </Button>
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
              className="bg-pf-success h-2 rounded-full transition-all duration-300"
            >
              <span className="sr-only">Print progress: {Math.round(Math.max(0, Math.min(100, printer.progress))) }%</span>
            </div>
          </div>
        </div>
      )}

      {/* Temps Section */}
      <div className="mb-2">
        <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide mb-1 -ml-1">Temps</div>
        <div className="grid grid-cols-3 gap-2 w-98">
          {/* Row 1: Labels */}
          <div className="flex items-center h-5 w-31">
            <NozzleIcon className="w-4 h-4 text-red-500 shrink-0" isOn={(printer.hotendTarget ?? 0) > 0} />
            <span className="text-[0.65rem] text-slate-400 ml-auto">
              {formatTempWithTarget(
                printer.hotendTemp,
                printer.hotendTarget
              )}
            </span>
          </div>
          
          <div className="flex items-center h-5 w-31">
            <BedIcon className="w-4 h-4 text-blue-500 shrink-0" isOn={(printer.bedTarget ?? 0) > 0} />
            <span className="text-[0.65rem] text-slate-400 ml-auto">
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
          
          <div className="flex gap-1 items-stretch h-9">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={isPrinting}
              onClick={() => handleApplyPreset('cooldown')}
              title="Cooldown"
              aria-label="Cooldown"
              className="shrink-0"
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
              className="flex-1"
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
        <div className="flex gap-4 items-start">
          {/* Left Column: Move */}
          <div className="flex flex-col gap-2 items-start">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
              Move
            </div>
            <div className="flex gap-4 items-start">
              {/* XY Pad */}
              <div className="grid grid-cols-3 grid-rows-3 gap-1 w-40 h-36">
                {/* Top row */}
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleHome()}
                  title="Home all axes"
                  aria-label="Home all axes"
                  className="w-full h-full !p-0"
                  iconCenter={<HomeIcon className="h-4 w-4" />}
                >
        </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleMove('Y', step)}
                  className="w-full h-full !p-0"
                  iconCenter={<ArrowUpIcon className="h-4 w-4" />}
                >
        </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={!isOnline || isPrinting}
                  onClick={() => handleControlAction('disable-motors')}
                  title="Disable Motors (M84)"
                  aria-label="Disable Motors (M84)"
                  className="w-full h-full !p-0"
                  iconCenter={<DisableMotorsIcon className="h-4 w-4" />}
                >
        </Button>
                
                {/* Middle row */}
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleMove('X', -step)}
                  className="w-full h-full !p-0"
                  iconCenter={<ArrowLeftIcon className="h-4 w-4" />}
                >
        </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleHome('xy')}
                  title="Home X/Y"
                  aria-label="Home X/Y"
                  className="w-full h-full !p-0"
                  iconCenter={<HomeIcon className="h-4 w-4" />}
                >
        </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleMove('X', step)}
                  className="w-full h-full !p-0"
                  iconCenter={<ArrowRightIcon className="h-4 w-4" />}
                >
        </Button>
                
                {/* Bottom row */}
                <div></div>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleMove('Y', -step)}
                  className="w-full h-full !p-0"
                  iconCenter={<ArrowDownIcon className="h-4 w-4" />}
                >
        </Button>
                <div></div>
              </div>

              {/* Z Pad */}
              <div className="flex flex-col gap-1 w-16 h-36">
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleMove('Z', step)}
                  className="flex-1 p-0"
                >
                  Z+
                </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleHome('z')}
                  title="Home Z"
                  aria-label="Home Z"
                  className="flex-1 p-0"
                  iconCenter={<HomeIcon className="h-4 w-4" />}
                >
        </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleMove('Z', -step)}
                  className="flex-1 p-0"
                >
                  Z-
                </Button>
              </div>
            </div>
          </div>

          {/* Right Column: Control */}
          <div className="flex flex-col gap-2 items-start">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
              Control
            </div>
            <div className="flex flex-col gap-0 relative w-40 h-36">
              {/* Control buttons row */}
              <div className="flex items-stretch gap-1 flex-1">
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={!isPrinting}
                  onClick={() => handleControlAction('pause')}
                  title="Pause"
                  aria-label="Pause"
                  className="w-full h-full !p-0"
                  iconCenter={<PauseIcon className="h-4 w-4" />}
                >
        </Button>
                <Button
                  type="button"
                  variant="success"
                  size="sm"
                  disabled={!isPaused}
                  onClick={() => handleControlAction('resume')}
                  title="Resume"
                  aria-label="Resume"
                  className="w-full h-full !p-0"
                  iconCenter={<PlayIcon className="h-4 w-4" />}
                >
        </Button>
                <Button
                  type="button"
                  variant={isShutdown ? 'secondary' : 'danger'}
                  size="sm"
                  disabled={!isOnline}
                  onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
                  title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
                  className="w-full h-full !p-0"
                  iconCenter={isShutdown ? <RefreshIcon className="h-4 w-4" /> : <EmergencyStopIcon className="h-4 w-4" />}
                >
        </Button>
              </div>
              
              {/* Steps label row - fixed h-12, aligned bottom */}
              <div className="h-12 w-full flex items-end px-1">
                <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
                  Steps
                </div>
              </div>
              
              {/* Step buttons row */}
              <div className="flex items-stretch gap-1 flex-1">
                {[1, 10, 50].map((stepValue) => (
                  <Button
                    key={stepValue}
                    type="button"
                    variant={step === stepValue ? 'primary' : 'secondary'}
                    size="sm"
                    onClick={() => handleStepChange(stepValue)}
                    className="w-full h-full !p-0"
                  >
                    {stepValue}
                  </Button>
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
                console.log('[PrintFarmer] ExpandablePrinterCard: Moving to', moveX, moveY, moveZ);
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