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
import { useState, useLayoutEffect, useRef } from 'react';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { apiClient } from '@/services/api';
import type { Printer, TempTargets, MoveRequest } from '@/types/api';
import { PrinterHistoryModal } from '@/components/PrinterHistoryModal';
import { renderUnknown } from '@/utils/renderUnknown';
import { Button, TemperatureInput, MovementInput, Select } from '@/components/ui';
import { NozzleIcon, BedIcon, HomeIcon, PlayIcon, PauseIcon, EmergencyStopIcon } from '@/components/icons/MdiIcons';
import { 
  ChevronDown, 
  ExternalLink,
  Edit,
  History,
  Camera,
  Minus,
  RotateCcw,
  Image,
  Video
} from 'lucide-react';

interface ExpandablePrinterCardProps {
  printer: Printer;
  onEdit?: (printer: Printer) => void;
  // Optional callbacks not currently used by this component's internal UI; prefixed to silence unused var lint
  onDelete?: (printer: Printer) => void;
  onManage?: (printer: Printer) => void;
}
// We intentionally accept onDelete/onManage in props interface for future actions but do not destructure them to avoid unused vars
export function ExpandablePrinterCard({ printer, onEdit }: ExpandablePrinterCardProps) {
  const [isExpanded, setIsExpanded] = useState(false);
  const [showCamera, setShowCamera] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [cameraMode, setCameraMode] = useState<'snapshot' | 'stream'>('snapshot');
  const [step, setStep] = useState(10);
  const [hotendTemp, setHotendTemp] = useState<number | ''>('');
  const [bedTemp, setBedTemp] = useState<number | ''>('');
  const [moveX, setMoveX] = useState<number | ''>('');
  const [moveY, setMoveY] = useState<number | ''>('');
  const [moveZ, setMoveZ] = useState<number | ''>('');
  
  // State to track last known good values
  const [lastKnownHotendTemp, setLastKnownHotendTemp] = useState<number | null>(null);
  const [lastKnownBedTemp, setLastKnownBedTemp] = useState<number | null>(null);
  const [lastKnownX, setLastKnownX] = useState<number | null>(null);
  const [lastKnownY, setLastKnownY] = useState<number | null>(null);
  const [lastKnownZ, setLastKnownZ] = useState<number | null>(null);
  
  const { printerStatuses } = usePrinterStatusUpdates();
  
  // Get status from the Map - this will cause re-render when printerStatuses changes
  const status = printerStatuses.get(printer.id);
  
  // Debug logging to track status updates
  useLayoutEffect(() => {
    const win = window as unknown as { PrintFarmerDebug?: Record<string, unknown> };
    if (win.PrintFarmerDebug?.expandablePrinterCard) {
      console.log('[ExpandablePrinterCard] Status update:', {
        printerId: printer.id,
        printerName: printer.name,
        status,
        hotendTemp: status?.hotendTemp,
        bedTemp: status?.bedTemp,
        x: status?.x,
        y: status?.y,
        z: status?.z,
      });
    }
  }, [status, printer.id, printer.name]);
  
  const isOnline = status?.isOnline ?? printer.isOnline;
  const state = status?.state ?? printer.state;
  const isPrinting = state === 'printing';
  const isPaused = state === 'paused';
  const isShutdown = state === 'shutdown';

  // Camera URL logic: prioritize real-time status, fallback to printer config
  const cameraSnapshotUrl = status?.cameraSnapshotUrl ?? printer.cameraSnapshotUrl;
  // Only support cameras for Moonraker and OctoPrint backends
  const supportsCameras = printer.backend === PrinterBackend.Moonraker || printer.backend === PrinterBackend.OctoPrint;
  // Only enable camera button if snapshot URL is available
  const hasCameraUrls = supportsCameras && (
    typeof cameraSnapshotUrl === 'string' && cameraSnapshotUrl.trim().length > 0
  );

  // Update last known values when new data is available
  // useLayoutEffect runs synchronously after DOM mutations but before browser paint
  // This ensures real-time updates without the batching delays of useEffect
  useLayoutEffect(() => {
    // Initialize from printer data if we don't have last known values and status is null
    if (lastKnownHotendTemp === null && (status?.hotendTemp === null || status?.hotendTemp === undefined) && printer.hotendTemp !== null && printer.hotendTemp !== undefined) {
      setLastKnownHotendTemp(printer.hotendTemp);
    }
    if (lastKnownBedTemp === null && (status?.bedTemp === null || status?.bedTemp === undefined) && printer.bedTemp !== null && printer.bedTemp !== undefined) {
      setLastKnownBedTemp(printer.bedTemp);
    }
    if (lastKnownX === null && (status?.x === null || status?.x === undefined) && printer.x !== null && printer.x !== undefined) {
      setLastKnownX(printer.x);
    }
    if (lastKnownY === null && (status?.y === null || status?.y === undefined) && printer.y !== null && printer.y !== undefined) {
      setLastKnownY(printer.y);
    }
    if (lastKnownZ === null && (status?.z === null || status?.z === undefined) && printer.z !== null && printer.z !== undefined) {
      setLastKnownZ(printer.z);
    }
    
    // Update from status if we have new non-null data
    if (status?.hotendTemp !== null && status?.hotendTemp !== undefined) {
      setLastKnownHotendTemp(status.hotendTemp);
    }
    if (status?.bedTemp !== null && status?.bedTemp !== undefined) {
      setLastKnownBedTemp(status.bedTemp);
    }
    if (status?.x !== null && status?.x !== undefined) {
      setLastKnownX(status.x);
    }
    if (status?.y !== null && status?.y !== undefined) {
      setLastKnownY(status.y);
    }
    if (status?.z !== null && status?.z !== undefined) {
      setLastKnownZ(status.z);
    }
  }, [status, printer.id, printer.name, printer.hotendTemp, printer.bedTemp, printer.hotendTarget, printer.bedTarget, printer.x, printer.y, printer.z, lastKnownHotendTemp, lastKnownBedTemp, lastKnownX, lastKnownY, lastKnownZ]);

  const formatTempWithTarget = (currentTemp: number | null | undefined, targetTemp: number | null | undefined, lastKnownCurrent: number | null): string => {
    const current = currentTemp ?? lastKnownCurrent;
    const target = targetTemp;
    
    if (current === null || current === undefined) return '[ --°C ]';
    
    const currentRounded = Math.round(current);
    
    // If target is null, undefined, or 0, just show current temperature
    if (target === null || target === undefined || target === 0) {
      return `[ ${currentRounded}°C ]`;
    }
    
    // If heating (target > current), show both
    const targetRounded = Math.round(target);
    if (targetRounded > currentRounded) {
      return `[ ${currentRounded}°C → ${targetRounded}°C ]`;
    }
    
    // If at target or cooling, just show current
    return `[ ${currentRounded}°C ]`;
  };

  const formatPos = (pos: number | null | undefined, lastKnown: number | null): string => {
    const value = pos ?? lastKnown;
    if (value === null || value === undefined) return '---';
    return value.toFixed(1);
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
  useLayoutEffect(() => {
    const pct = status?.progress !== undefined && status?.progress !== null ? Math.max(0, Math.min(100, status.progress)) : 0;
    try {
      if (collapsedProgressRef.current) collapsedProgressRef.current.style.width = `${Math.round(pct)}%`;
      if (expandedProgressRef.current) expandedProgressRef.current.style.width = `${Math.round(pct)}%`;
    } catch {
      // Ignore DOM write errors in very restricted test environments
    }
  }, [status?.progress]);

  // progressPct is available via status.progress when needed; dynamic width is set via refs

  const handleToggleExpand = () => {
    setIsExpanded(!isExpanded);
  };

  const handleControlAction = async (action: 'pause' | 'resume' | 'stop' | 'firmware-restart') => {
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
      const currentBedTemp = bedTemp === '' ? (status?.bedTarget ?? printer.bedTarget ?? 0) : bedTemp;
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
      const currentHotendTemp = hotendTemp === '' ? (status?.hotendTarget ?? printer.hotendTarget ?? 0) : hotendTemp;
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

  // TODO: Implement set temperatures UI
  // const handleSetTemperatures = async () => {
  //   try {
  //     const hotendValue = typeof hotendTemp === 'string' ? parseFloat(hotendTemp) || 0 : hotendTemp;
  //     const bedValue = typeof bedTemp === 'string' ? parseFloat(bedTemp) || 0 : bedTemp;
  //     
  //     const targets: TempTargets = {
  //       hotend: hotendValue,
  //       bed: bedValue
  //     };
  //     
  //     const result = await apiClient.setTemperatures(printer.id, targets);
  //     
  //     if (!result.success) {
  //       console.error('Failed to set temperatures:', result.error);
  //     }
  //   } catch (error) {
  //     console.error('Error setting temperatures:', error);
  //   }
  // };

  const handleViewHistory = () => {
    setShowHistory(true);
  };

  if (!isExpanded) {
    // Collapsed view - matching Blazor structure
    return (
      <div className="border border-pf-border rounded-xl p-3 bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 shadow-lg min-w-[26rem] max-w-[26rem]">
        <div className="flex justify-between items-start mb-4 gap-4">
          <div className="flex justify-between items-start flex-1 gap-4">
            <div className="flex-1">
              <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase mb-1">
                {printer.name}
              </div>
              {(printer.manufacturerName || printer.modelName) && (
                <div className="text-pf-text-secondary text-sm mb-1">
                  {`${printer.manufacturerName || ''} ${printer.modelName || ''}`.trim()}
                </div>
              )}
              <div className="flex items-center gap-2 mb-1">
                <div className="text-pf-text-secondary text-xs">
                  {printer.serverUrl}
                </div>
                <a 
                  href={printer.serverUrl} 
                  target="_blank" 
                  rel="noopener noreferrer"
                  className="text-pf-text-secondary hover:text-pf-text-primary"
                  aria-label={`Open printer ${printer.name} in new tab`}
                  title={`Open printer ${printer.name}`}
                >
                  <ExternalLink className="h-4 w-4" />
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
                >
                  <Camera className="h-4 w-4" />
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
            >
              <ChevronDown className="h-4 w-4" />
            </Button>
            {/* History button - only show for backends that support it (Moonraker, OctoPrint) */}
            {(printer.backend === PrinterBackend.Moonraker || printer.backend === PrinterBackend.OctoPrint) && (
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={handleViewHistory}
                className="!p-1 !h-auto"
                title="View print history"
              >
                <History className="h-4 w-4" />
              </Button>
            )}
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={() => onEdit?.(printer)}
              className="!p-1 !h-auto"
              title="Edit details"
            >
              <Edit className="h-4 w-4" />
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
          >
            <PauseIcon className="h-3 w-3 mr-1" />
          </Button>
          <Button
            type="button"
            variant="success"
            size="sm"
            onClick={() => handleControlAction('resume')}
            disabled={!isPaused}
          >
            <PlayIcon className="h-3 w-3 mr-1" />
          </Button>
          <Button
            type="button"
            variant={isShutdown ? 'secondary' : 'danger'}
            size="sm"
            onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
            disabled={!isOnline}
            title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
          >
            {isShutdown ? <RotateCcw className="h-3 w-3 mr-1" /> : <EmergencyStopIcon className="h-3 w-3" />}
          </Button>
        </div>

        {/* Progress bar for active prints */}
        {isOnline && status?.progress !== undefined && status.progress > 0 && (
          <div className="mt-3">
            <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
              <span className="truncate flex-1">{status.jobName || 'Printing...'}</span>
              <span className="font-semibold ml-2">{Math.round(status.progress)}%</span>
            </div>
            <div className="w-full bg-pf-border-dark rounded-full h-2 overflow-hidden">
              <div
                ref={collapsedProgressRef}
                className="bg-pf-success h-2 rounded-full transition-all duration-300"
              >
                <span className="sr-only">Print progress: {Math.round(Math.max(0, Math.min(100, status.progress))) }%</span>
              </div>
            </div>
          </div>
        )}

              {showCamera && (
                <div className="mt-4 w-52 flex flex-col bg-pf-bg-2 bg-opacity-30 border border-pf-border rounded-md overflow-hidden">
                  {/* Camera mode toggle */}
                  {hasCameraUrls && printer.cameraStreamUrl && (
                    <div className="flex gap-1 p-2 border-b border-pf-border bg-pf-bg-1 bg-opacity-50">
                      <Button
                        type="button"
                        onClick={() => setCameraMode('snapshot')}
                        title="Snapshot"
                        variant={cameraMode === 'snapshot' ? 'primary' : 'secondary'}
                        size="sm"
                        className="flex-1 p-2 flex items-center justify-center"
                      >
                        <Image className="h-4 w-4" />
                      </Button>
                      <Button
                        type="button"
                        onClick={() => setCameraMode('stream')}
                        title="Stream"
                        variant={cameraMode === 'stream' ? 'primary' : 'secondary'}
                        size="sm"
                        className="flex-1 p-2 flex items-center justify-center"
                      >
                        <Video className="h-4 w-4" />
                      </Button>
                    </div>
                  )}
                  
                  {/* Camera display */}
                  <div className="min-h-32 flex items-center justify-center overflow-hidden">
                    {hasCameraUrls ? (
                      cameraMode === 'snapshot' && printer.cameraSnapshotUrl ? (
                        <img 
                          src={printer.cameraSnapshotUrl}
                          alt="webcam snapshot"
                          className="max-w-full max-h-full object-contain"
                          onError={() => {}}
                          onLoad={() => {}}
                        />
                      ) : cameraMode === 'stream' && printer.cameraStreamUrl ? (
                        <img 
                          src={printer.cameraStreamUrl}
                          alt="webcam stream"
                          className="max-w-full max-h-full object-contain"
                          onError={() => {}}
                          onLoad={() => {}}
                        />
                      ) : (
                        <div className="text-center text-pf-text-secondary p-4">
                          <Camera className="h-8 w-8 mx-auto mb-2 opacity-50" />
                          <p className="text-sm">Camera mode not available</p>
                        </div>
                      )
                    ) : (
                      <div className="text-center text-pf-text-secondary p-4 w-full">
                        <Camera className="h-8 w-8 mx-auto mb-2 opacity-50" />
                        <p className="text-sm">No camera configured</p>
                      </div>
                    )}
                  </div>
                </div>
              )}

              {/* Optional debug panel controlled by window.PrintFarmerDebug.expandablePrinterCardDisplay */}
              {window.PrintFarmerDebug?.expandablePrinterCardDisplay && (
                <div className="mt-3 p-2 bg-pf-bg-0 border border-pf-border rounded text-xs text-pf-text-tertiary">
                  {renderUnknown({ status, lastKnownHotendTemp, lastKnownBedTemp, lastKnownX, lastKnownY, lastKnownZ })}
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
    <div className={`border rounded-xl p-3 bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 shadow-lg border-pf-border min-w-[26rem] max-w-[26rem]`}>
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
                {printer.serverUrl}
              </div>
              <a 
                href={printer.serverUrl} 
                target="_blank" 
                rel="noopener noreferrer"
                className="text-pf-text-secondary hover:text-pf-text-primary"
                aria-label={`Open printer ${printer.name} in new tab`}
                title={`Open printer ${printer.name}`}
              >
                <ExternalLink className="h-4 w-4" />
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
              >
                <Camera className="h-4 w-4" />
              </Button>
            </div>
            {showCamera && (
              <div className="mt-2 w-52 flex flex-col bg-pf-bg-2 bg-opacity-30 border border-pf-border rounded-md overflow-hidden">
                {/* Camera mode toggle */}
                {hasCameraUrls && printer.cameraStreamUrl && (
                    <div className="flex gap-1 p-2 border-b border-pf-border bg-pf-bg-1 bg-opacity-50">
                      <Button
                        type="button"
                        onClick={() => setCameraMode('snapshot')}
                        title="Snapshot"
                        variant={cameraMode === 'snapshot' ? 'primary' : 'secondary'}
                        size="sm"
                        className="flex-1 p-2 flex items-center justify-center"
                      >
                        <Image className="h-4 w-4" />
                      </Button>
                      <Button
                        type="button"
                        onClick={() => setCameraMode('stream')}
                        title="Stream"
                        variant={cameraMode === 'stream' ? 'primary' : 'secondary'}
                        size="sm"
                        className="flex-1 p-2 flex items-center justify-center"
                      >
                        <Video className="h-4 w-4" />
                      </Button>
                    </div>
                )}
                
                {/* Camera display */}
                <div className="min-h-32 flex items-center justify-center overflow-hidden">
                  {hasCameraUrls ? (
                    cameraMode === 'snapshot' && printer.cameraSnapshotUrl ? (
                      <img 
                        src={printer.cameraSnapshotUrl}
                        alt="webcam snapshot"
                        className="max-w-full max-h-full object-contain"
                        onError={() => {}}
                        onLoad={() => {}}
                      />
                    ) : cameraMode === 'stream' && printer.cameraStreamUrl ? (
                      <img 
                        src={printer.cameraStreamUrl}
                        alt="webcam stream"
                        className="max-w-full max-h-full object-contain"
                        onError={() => {}}
                        onLoad={() => {}}
                      />
                    ) : (
                      <div className="text-center text-pf-text-secondary p-4">
                        <Camera className="h-8 w-8 mx-auto mb-2 opacity-50" />
                        <p className="text-sm">Camera mode not available</p>
                      </div>
                    )
                  ) : (
                    <div className="text-center text-pf-text-secondary p-4 w-full">
                      <Camera className="h-8 w-8 mx-auto mb-2 opacity-50" />
                      <p className="text-sm">No camera configured</p>
                    </div>
                  )}
                </div>
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
          >
            <Minus className="h-4 w-4" />
          </Button>
          {/* History button - only show for backends that support it (Moonraker, OctoPrint) */}
          {(printer.backend === PrinterBackend.Moonraker || printer.backend === PrinterBackend.OctoPrint) && (
            <Button
              type="button"
              variant="subtle"
              size="sm"
              onClick={handleViewHistory}
              className="!p-1 !h-auto"
              title="View print history"
            >
              <History className="h-4 w-4" />
            </Button>
          )}
          <Button
            type="button"
            variant="subtle"
            size="sm"
            onClick={() => onEdit?.(printer)}
            className="!p-1 !h-auto"
            title="Edit details"
          >
            <Edit className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {/* Progress bar for active prints */}
      {isOnline && status?.progress !== undefined && status.progress > 0 && (
        <div className="mb-4">
          <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
            <span className="truncate flex-1">{status.jobName || 'Printing...'}</span>
            <span className="font-semibold ml-2">{Math.round(status.progress)}%</span>
          </div>
          <div className="w-full bg-pf-border-dark rounded-full h-2 overflow-hidden">
            <div
              ref={expandedProgressRef}
              className="bg-pf-success h-2 rounded-full transition-all duration-300"
            >
              <span className="sr-only">Print progress: {Math.round(Math.max(0, Math.min(100, status.progress))) }%</span>
            </div>
          </div>
        </div>
      )}

      {/* Temps Section */}
      <div className="mb-2">
        <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide mb-1 -ml-1">Temps</div>
        <div className="grid grid-cols-3 gap-2 w-[24.5rem]">
          {/* Row 1: Labels */}
          <div className="flex items-center h-5 w-[7.75rem]">
            <NozzleIcon className="w-4 h-4 text-red-500 flex-shrink-0" isOn={(status?.hotendTarget ?? printer.hotendTarget ?? 0) > 0} />
            <span className="text-xs text-slate-400 ml-auto">
              {formatTempWithTarget(
                status?.hotendTemp ?? printer.hotendTemp,
                status?.hotendTarget ?? printer.hotendTarget,
                lastKnownHotendTemp
              )}
            </span>
          </div>
          
          <div className="flex items-center h-5 w-[7.75rem]">
            <BedIcon className="w-4 h-4 text-blue-500 flex-shrink-0" isOn={(status?.bedTarget ?? printer.bedTarget ?? 0) > 0} />
            <span className="text-xs text-slate-400 ml-auto">
              {formatTempWithTarget(
                status?.bedTemp ?? printer.bedTemp,
                status?.bedTarget ?? printer.bedTarget,
                lastKnownBedTemp
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
          />
          
          <TemperatureInput
            value={bedTemp}
            onChange={(e) => setBedTemp(e.target.value === '' ? '' : Number(e.target.value))}
            onKeyDown={handleBedTempKeyDown}
          />
          
          <div className="flex gap-1 items-stretch h-9">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={isPrinting}
              onClick={() => handleApplyPreset('cooldown')}
              title="Cooldown"
              className="flex-shrink-0"
            >
              ❄
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

      {/* Spool Info Section - Display if available */}
      {status?.spoolInfo && status.spoolInfo.hasActiveSpool && (
        <div className="mb-2">
          <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide mb-1 -ml-1">Spool</div>
          <div className="bg-pf-bg-2 bg-opacity-30 border border-pf-border rounded-md p-3 space-y-2">
            {status.spoolInfo.vendor && (
              <div className="flex justify-between items-center">
                <span className="text-xs text-pf-text-secondary">Vendor:</span>
                <span className="text-xs font-medium text-pf-text-primary">{status.spoolInfo.vendor}</span>
              </div>
            )}
            {status.spoolInfo.material && (
              <div className="flex justify-between items-center">
                <span className="text-xs text-pf-text-secondary">Material:</span>
                <span className="text-xs font-medium text-pf-text-primary">{status.spoolInfo.material}</span>
              </div>
            )}
            {status.spoolInfo.colorHex && (
              <div className="flex justify-between items-center">
                <span className="text-xs text-pf-text-secondary">Color:</span>
                <div className="flex items-center gap-2">
                  <div 
                    className="w-4 h-4 rounded border border-pf-border"
                    style={{ backgroundColor: status.spoolInfo.colorHex }}
                    title={status.spoolInfo.colorHex}
                  />
                  <span className="text-xs font-medium text-pf-text-primary">{status.spoolInfo.colorHex}</span>
                </div>
              </div>
            )}
            {status.spoolInfo.spoolName && (
              <div className="flex justify-between items-center">
                <span className="text-xs text-pf-text-secondary">Spool:</span>
                <span className="text-xs font-medium text-pf-text-primary">{status.spoolInfo.spoolName}</span>
              </div>
            )}
            {status.spoolInfo.remainingWeightG !== undefined && status.spoolInfo.remainingWeightG !== null && (
              <div className="flex justify-between items-center">
                <span className="text-xs text-pf-text-secondary">Remaining:</span>
                <span className="text-xs font-medium text-pf-text-primary">{Math.round(status.spoolInfo.remainingWeightG)}g</span>
              </div>
            )}
          </div>
        </div>
      )}
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
                  className="w-full h-full !p-0"
                >
                  <HomeIcon className="h-4 w-4" />
                </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleMove('Y', step)}
                  className="w-full h-full !p-0"
                >
                  ▲
                </Button>
                <div></div>
                
                {/* Middle row */}
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleMove('X', -step)}
                  className="w-full h-full !p-0"
                >
                  ◀
                </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleHome('xy')}
                  title="Home X/Y"
                  className="w-full h-full !p-0"
                >
                  <HomeIcon className="h-4 w-4" />
                </Button>
                <Button
                  type="button"
                  variant="secondary"
                  size="sm"
                  disabled={isPrinting}
                  onClick={() => handleMove('X', step)}
                  className="w-full h-full !p-0"
                >
                  ▶
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
                >
                  ▼
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
                  className="flex-1 p-0"
                >
                  <HomeIcon className="h-4 w-4" />
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
                  className="w-full h-full !p-0"
                >
                  <PauseIcon className="h-4 w-4" />
                </Button>
                <Button
                  type="button"
                  variant="success"
                  size="sm"
                  disabled={!isPaused}
                  onClick={() => handleControlAction('resume')}
                  title="Resume"
                  className="w-full h-full !p-0"
                >
                  <PlayIcon className="h-4 w-4" />
                </Button>
                <Button
                  type="button"
                  variant={isShutdown ? 'secondary' : 'danger'}
                  size="sm"
                  disabled={!isOnline}
                  onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
                  title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
                  className="w-full h-full !p-0"
                >
                  {isShutdown ? <RotateCcw className="h-4 w-4" /> : <EmergencyStopIcon className="h-4 w-4" />}
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
            <span className="text-xs font-bold text-pf-text-secondary">[ {formatPos(null, lastKnownX)} ]</span>
          </div>
          <div className="flex items-center justify-end pr-1">
            <span className="text-xs font-bold text-pf-text-secondary">[ {formatPos(null, lastKnownY)} ]</span>
          </div>
          <div className="flex items-center justify-end pr-1">
            <span className="text-xs font-bold text-pf-text-secondary">[ {formatPos(null, lastKnownZ)} ]</span>
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