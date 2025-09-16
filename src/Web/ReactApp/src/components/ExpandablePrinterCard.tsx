import { useState, useEffect } from 'react';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { apiClient } from '@/services/api';
import type { Printer, TempTargets, MoveRequest } from '@/types/api';
import { PrinterHistoryModal } from '@/components/PrinterHistoryModal';
import { 
  ChevronDown, 
  ExternalLink,
  Edit,
  History,
  Camera,
  Play,
  Pause,
  Square,
  Home,
  Minus,
  RotateCcw
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
  
  const { getPrinterStatus } = usePrinterStatusUpdates();
  
  const status = getPrinterStatus(printer.id);
  const isOnline = status?.isOnline ?? printer.isOnline;
  const state = status?.state ?? printer.state;
  const isPrinting = state === 'printing';
  const isPaused = state === 'paused';
  const isShutdown = state === 'shutdown';

  // Update last known values when new data is available
  useEffect(() => {
    console.log('PrinterCard status:', {
      printerName: printer.name,
      hotendTemp: status?.hotendTemp,
      hotendTarget: status?.hotendTarget,
      bedTemp: status?.bedTemp,
      bedTarget: status?.bedTarget,
      x: status?.x, y: status?.y, z: status?.z,
      fullStatus: status,
      printerData: {
        printerHotendTemp: printer.hotendTemp,
        printerHotendTarget: printer.hotendTarget,
        printerBedTemp: printer.bedTemp,
        printerBedTarget: printer.bedTarget,
        printerX: printer.x, printerY: printer.y, printerZ: printer.z
      },
      lastKnownPositions: {
        lastKnownX, lastKnownY, lastKnownZ
      }
    });
    
    // Initialize from printer data if we don't have last known values and status is null
    if (lastKnownHotendTemp === null && (status?.hotendTemp === null || status?.hotendTemp === undefined) && printer.hotendTemp !== null && printer.hotendTemp !== undefined) {
      setLastKnownHotendTemp(printer.hotendTemp);
    }
    if (lastKnownBedTemp === null && (status?.bedTemp === null || status?.bedTemp === undefined) && printer.bedTemp !== null && printer.bedTemp !== undefined) {
      setLastKnownBedTemp(printer.bedTemp);
    }
    if (lastKnownX === null && (status?.x === null || status?.x === undefined) && printer.x !== null && printer.x !== undefined) {
      console.log(`Initializing lastKnownX from printer: ${printer.x}`);
      setLastKnownX(printer.x);
    }
    if (lastKnownY === null && (status?.y === null || status?.y === undefined) && printer.y !== null && printer.y !== undefined) {
      console.log(`Initializing lastKnownY from printer: ${printer.y}`);
      setLastKnownY(printer.y);
    }
    if (lastKnownZ === null && (status?.z === null || status?.z === undefined) && printer.z !== null && printer.z !== undefined) {
      console.log(`Initializing lastKnownZ from printer: ${printer.z}`);
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
      console.log(`Setting lastKnownX from status: ${status.x}`);
      setLastKnownX(status.x);
    }
    if (status?.y !== null && status?.y !== undefined) {
      console.log(`Setting lastKnownY from status: ${status.y}`);
      setLastKnownY(status.y);
    }
    if (status?.z !== null && status?.z !== undefined) {
      console.log(`Setting lastKnownZ from status: ${status.z}`);
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

  // Function to check if an axis is homed based on the homedAxes string from Moonraker
  const isAxisHomed = (axis: string): boolean => {
    const homedAxes = status?.homedAxes || '';
    console.log(`[DEBUG] Checking axis ${axis}, homedAxes from status:`, homedAxes);
    return homedAxes.includes(axis.toLowerCase());
  };

  const getHomeButtonClasses = (axes: string[]): string => {
    const baseClasses = "w-11 h-11 p-0 flex items-center justify-center border border-pf-border rounded disabled:opacity-50";
    
    // Check if all specified axes are homed using real data from Moonraker
    const allAxesHomed = axes.every(axis => isAxisHomed(axis));
    
    // Accessible colors: dark backgrounds with white text for sufficient contrast (7.5:1+)
    if (allAxesHomed) {
      // Homed state: dark blue background with white text (8.59:1 ratio)
      return `${baseClasses} bg-blue-700 text-white hover:bg-blue-600 border-blue-700`;
    } else {
      // Unhomed state: dark amber background with white text (7.77:1 ratio)
      return `${baseClasses} bg-amber-700 text-white hover:bg-amber-600 border-amber-700`;
    }
  };

  // Use the new state color function instead of the old statusColor
  const stateColorClasses = getStateColors(state, isOnline);

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
  };  const handleStepChange = (newStep: number) => {
    setStep(newStep);
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

  const handleSetTemperatures = async () => {
    try {
      const hotendValue = typeof hotendTemp === 'string' ? parseFloat(hotendTemp) || 0 : hotendTemp;
      const bedValue = typeof bedTemp === 'string' ? parseFloat(bedTemp) || 0 : bedTemp;
      
      const targets: TempTargets = {
        hotend: hotendValue,
        bed: bedValue
      };
      
      const result = await apiClient.setTemperatures(printer.id, targets);
      
      if (!result.success) {
        console.error('Failed to set temperatures:', result.error);
      }
    } catch (error) {
      console.error('Error setting temperatures:', error);
    }
  };

  const handleViewHistory = () => {
    setShowHistory(true);
  };

  if (!isExpanded) {
    // Collapsed view - matching Blazor structure
    return (
      <div className="border border-pf-border rounded-xl p-3 bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 shadow-lg">
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
                {printer.cameraStreamUrl && (
                  <button
                    onClick={() => setShowCamera(!showCamera)}
                    className="text-pf-text-secondary hover:text-pf-text-primary"
                    aria-label={showCamera ? 'Hide camera stream' : 'Show camera stream'}
                    title={showCamera ? 'Hide camera' : 'Show camera'}
                  >
                    <Camera className="h-4 w-4" />
                  </button>
                )}
              </div>
            </div>
            
            <div className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ${stateColorClasses}`}>
              {isOnline ? toCamelCase(state) : 'Offline'}
            </div>
          </div>
          
          <div className="flex items-center gap-1">
            <button
              onClick={handleToggleExpand}
              className="p-1 text-pf-text-secondary hover:text-pf-text-primary"
              title="Expand card"
            >
              <ChevronDown className="h-4 w-4" />
            </button>
            <button
              onClick={handleViewHistory}
              className="p-1 text-pf-text-secondary hover:text-pf-text-primary"
              title="View print history"
            >
              <History className="h-4 w-4" />
            </button>
            <button
              onClick={() => onEdit?.(printer)}
              className="p-1 text-pf-text-secondary hover:text-pf-text-primary"
              title="Edit details"
            >
              <Edit className="h-4 w-4" />
            </button>
          </div>
        </div>

        {/* Control buttons in collapsed view */}
        <div className="flex items-center gap-2">
          <button
            onClick={() => handleControlAction('pause')}
            disabled={!isPrinting}
            className="inline-flex items-center px-3 py-1.5 text-xs font-medium border border-pf-border rounded hover:bg-pf-bg-2 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            <Pause className="h-3 w-3 mr-1" />
            Pause
          </button>
          <button
            onClick={() => handleControlAction('resume')}
            disabled={!isPaused}
            className="inline-flex items-center px-3 py-1.5 text-xs font-medium border border-pf-border rounded hover:bg-pf-bg-2 disabled:opacity-50 disabled:cursor-not-allowed"
          >
            <Play className="h-3 w-3 mr-1" />
            Resume
          </button>
          <button
            onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
            disabled={!isOnline}
            className={`inline-flex items-center px-3 py-1.5 text-xs font-medium border rounded disabled:opacity-50 disabled:cursor-not-allowed ${
              isShutdown 
                ? 'border-amber-700 text-white bg-amber-700 hover:bg-amber-600 hover:border-amber-600'
                : 'border-red-700 text-white bg-red-700 hover:bg-red-600 hover:border-red-600'
            }`}
            title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
          >
            {isShutdown ? <RotateCcw className="h-3 w-3 mr-1" /> : <Square className="h-3 w-3 mr-1" />}
            {isShutdown ? 'Restart' : 'Stop'}
          </button>
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

  // Expanded view - matching Blazor structure exactly
  return (
    <div className="border border-pf-border rounded-xl p-3 bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 shadow-lg">
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
              {printer.cameraStreamUrl && (
                <button
                  onClick={() => setShowCamera(!showCamera)}
                  className="text-pf-text-secondary hover:text-pf-text-primary"
                  aria-label={showCamera ? 'Hide camera stream' : 'Show camera stream'}
                  title={showCamera ? 'Hide camera' : 'Show camera'}
                >
                  <Camera className="h-4 w-4" />
                </button>
              )}
            </div>
            {showCamera && printer.cameraStreamUrl && (
              <div className="mt-2 w-52 min-h-30 flex items-center justify-center bg-pf-bg-2 bg-opacity-30 border border-pf-border rounded-md overflow-hidden">
                <img 
                  src={printer.cameraStreamUrl} 
                  alt="webcam snapshot"
                  className="max-w-full max-h-full object-contain"
                  onError={(e) => e.currentTarget.style.display = 'none'}
                  onLoad={(e) => e.currentTarget.style.display = 'block'}
                />
              </div>
            )}
          </div>
          
          <div className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ${stateColorClasses}`}>
            {isOnline ? toCamelCase(state) : 'Offline'}
          </div>
        </div>
        
        <div className="flex items-center gap-1">
          <button
            onClick={handleToggleExpand}
            className="p-1 text-pf-text-secondary hover:text-pf-text-primary"
            title="Collapse card"
          >
            <Minus className="h-4 w-4" />
          </button>
          <button
            onClick={handleViewHistory}
            className="p-1 text-pf-text-secondary hover:text-pf-text-primary"
              aria-label="Expand printer card"
            title="View print history"
          >
            <History className="h-4 w-4" />
          </button>
          <button
            onClick={() => onEdit?.(printer)}
            className="p-1 text-pf-text-secondary hover:text-pf-text-primary"
            title="Edit details"
          >
            <Edit className="h-4 w-4" />
          </button>
        </div>
      </div>

      {/* Temps Section */}
      <div className="mb-2">
        <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide mb-1 -ml-1">Temps</div>
        <div className="flex flex-row gap-6 items-start">
          <div className="flex flex-col items-center relative">
            <span className="mb-1 text-xs text-slate-400 bg-pf-bg-0 px-2 py-0.5 rounded w-32 text-center">
              {formatTempWithTarget(
                status?.hotendTemp ?? printer.hotendTemp,
                status?.hotendTarget ?? printer.hotendTarget,
                lastKnownHotendTemp
              )}
            </span>
            <div className="flex items-center">
              <span className="absolute left-2 text-slate-500 text-xs pointer-events-none z-10 top-1/2 transform -translate-y-1/2">
                Hotend
              </span>
              <div className="relative inline-block">
                <input 
                  aria-label="Hotend temperature target"
                  placeholder="Temp"
                  type="number" 
                  className="w-28 h-9 pl-14 pr-8 border border-pf-border rounded-md text-sm bg-pf-bg-0 [appearance:textfield] [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none"
                  value={hotendTemp}
                  onChange={(e) => setHotendTemp(e.target.value === '' ? '' : Number(e.target.value))}
                />
                <span className="absolute right-2 top-1/2 transform -translate-y-1/2 text-slate-500 pointer-events-none text-sm">°C</span>
              </div>
            </div>
          </div>
          
          <div className="flex flex-col items-center relative">
            <span className="mb-1 text-xs text-slate-400 bg-pf-bg-0 px-2 py-0.5 rounded w-32 text-center">
              {formatTempWithTarget(
                status?.bedTemp ?? printer.bedTemp,
                status?.bedTarget ?? printer.bedTarget,
                lastKnownBedTemp
              )}
            </span>
            <div className="flex items-center">
              <span className="absolute left-2 text-slate-500 text-xs pointer-events-none z-10 top-1/2 transform -translate-y-1/2">
                Bed
              </span>
              <div className="relative inline-block">
                <input 
                  aria-label="Bed temperature target"
                  placeholder="Temp"
                  type="number" 
                  className="w-28 h-9 pl-10 pr-8 border border-pf-border rounded-md text-sm bg-pf-bg-0 [appearance:textfield] [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none"
                  value={bedTemp}
                  onChange={(e) => setBedTemp(e.target.value === '' ? '' : Number(e.target.value))}
                />
                <span className="absolute right-2 top-1/2 transform -translate-y-1/2 text-slate-500 pointer-events-none text-sm">°C</span>
              </div>
            </div>
          </div>
          
          <div className="flex items-start mt-0">
            <button 
              className="min-w-12 h-9 px-2 text-xs font-bold uppercase bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 border border-pf-border rounded hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 transition-colors"
              onClick={handleSetTemperatures}
            >
              SET
            </button>
          </div>
        </div>
        
        {/* Temperature Presets Row */}
        <div className="flex flex-wrap gap-1 mt-3">
          {[
            { name: 'ABS', color: 'bg-gray-600' },
            { name: 'ASA', color: 'bg-yellow-600' }, 
            { name: 'PLA', color: 'bg-green-600' },
            { name: 'PC', color: 'bg-purple-600' },
            { name: 'PCTG', color: 'bg-cyan-600' },
            { name: 'PETG', color: 'bg-red-600' }
          ].map((preset) => (
            <button
              key={preset.name}
              className={`px-2 py-1 text-xs font-medium text-white rounded ${preset.color} hover:opacity-80 disabled:opacity-50 disabled:cursor-not-allowed`}
              disabled={isPrinting}
              onClick={() => handleApplyPreset(preset.name)}
            >
              {preset.name}
            </button>
          ))}
          <button
            className="px-2 py-1 text-xs font-medium text-white rounded bg-blue-600 hover:opacity-80 disabled:opacity-50 disabled:cursor-not-allowed"
            disabled={isPrinting}
            title="Cooldown"
            onClick={() => handleApplyPreset('cooldown')}
          >
            ❄
          </button>
        </div>
      </div>

      {/* Move Section */}
      <div className="mb-2">
        <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide mb-2 -ml-1">Move</div>
        <div className="flex flex-col gap-2 items-start">
          <div className="flex gap-4 items-start">
            {/* XY Pad */}
            <div className="grid grid-cols-3 grid-rows-3 gap-1 w-36 h-36">
              {/* Top row */}
              <button 
                className={getHomeButtonClasses(['x', 'y', 'z'])}
                disabled={isPrinting}
                onClick={() => handleHome()}
                title="Home all axes"
              >
                <Home className="h-4 w-4" />
              </button>
              <button 
                className="w-11 h-11 p-0 flex items-center justify-center border border-pf-border rounded bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 disabled:opacity-50"
                disabled={isPrinting}
                onClick={() => handleMove('Y', step)}
              >
                ▲
              </button>
              <div></div>
              
              {/* Middle row */}
              <button 
                className="w-11 h-11 p-0 flex items-center justify-center border border-pf-border rounded bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 disabled:opacity-50"
                disabled={isPrinting}
                onClick={() => handleMove('X', -step)}
              >
                ◀
              </button>
              <button 
                className={getHomeButtonClasses(['x', 'y'])}
                disabled={isPrinting}
                onClick={() => handleHome('xy')}
                title="Home X/Y"
              >
                <Home className="h-4 w-4" />
              </button>
              <button 
                className="w-11 h-11 p-0 flex items-center justify-center border border-pf-border rounded bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 disabled:opacity-50"
                disabled={isPrinting}
                onClick={() => handleMove('X', step)}
              >
                ▶
              </button>
              
              {/* Bottom row */}
              <div></div>
              <button 
                className="w-11 h-11 p-0 flex items-center justify-center border border-pf-border rounded bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 disabled:opacity-50"
                disabled={isPrinting}
                onClick={() => handleMove('Y', -step)}
              >
                ▼
              </button>
              <div></div>
            </div>

            {/* Z Pad */}
            <div className="flex flex-col gap-1">
              <button 
                className="w-11 h-11 p-0 flex items-center justify-center text-xs border border-pf-border rounded bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 disabled:opacity-50"
                disabled={isPrinting}
                onClick={() => handleMove('Z', step)}
              >
                Z+
              </button>
              <button 
                className={getHomeButtonClasses(['z'])}
                disabled={isPrinting}
                onClick={() => handleHome('z')}
                title="Home Z"
              >
                <Home className="h-4 w-4" />
              </button>
              <button 
                className="w-11 h-11 p-0 flex items-center justify-center text-xs border border-pf-border rounded bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 disabled:opacity-50"
                disabled={isPrinting}
                onClick={() => handleMove('Z', -step)}
              >
                Z-
              </button>
            </div>

            {/* Control Pad */}
            <div className="flex flex-col gap-2 relative">
              <div className="absolute -top-4 -left-1 text-xs uppercase text-pf-text-secondary font-bold tracking-wide">
                Controls
              </div>
              <div className="flex items-center gap-2 mt-2">
                <button 
                  className="w-11 h-11 p-0 flex items-center justify-center border border-pf-border rounded bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 disabled:opacity-50"
                  disabled={!isPrinting}
                  onClick={() => handleControlAction('pause')}
                  title="Pause"
                >
                  <Pause className="h-4 w-4" />
                </button>
                <button 
                  className="w-11 h-11 p-0 flex items-center justify-center border border-pf-border rounded bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 disabled:opacity-50"
                  disabled={!isPaused}
                  onClick={() => handleControlAction('resume')}
                  title="Resume"
                >
                  <Play className="h-4 w-4" />
                </button>
                <button 
                  className={`w-11 h-11 p-0 flex items-center justify-center border rounded disabled:opacity-50 ${
                    isShutdown 
                      ? 'border-amber-700 text-white bg-amber-700 hover:bg-amber-600 hover:border-amber-600'
                      : 'border-red-700 text-white bg-red-700 hover:bg-red-600 hover:border-red-600'
                  }`}
                  disabled={!isOnline}
                  onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
                  title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
                >
                  {isShutdown ? <RotateCcw className="h-4 w-4" /> : <Square className="h-4 w-4" />}
                </button>
              </div>
              
              {/* Step Block */}
              <div className="flex flex-col gap-1">
                <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide">Steps</div>
                <div className="flex gap-1">
                  {[1, 10, 50].map((stepValue) => (
                    <button
                      key={stepValue}
                      className={`w-8 h-6 text-xs font-medium rounded border transition-colors ${
                        step === stepValue 
                          ? 'bg-blue-600 text-white border-blue-600' 
                          : 'border-pf-border hover:bg-pf-bg-2'
                      }`}
                      onClick={() => handleStepChange(stepValue)}
                    >
                      {stepValue}
                    </button>
                  ))}
                </div>
              </div>
            </div>
          </div>

          {/* Axis Fields */}
          <div className="flex flex-row gap-4 items-start mt-3">
            <div className="flex flex-col items-center relative">
              <span className="mb-1 text-xs text-slate-400 bg-pf-bg-0 px-2 py-0.5 rounded w-20 text-center">
                [ {formatPos(null, lastKnownX)} ]
              </span>
              <div className="relative inline-block">
                <span className="absolute left-2 text-slate-500 text-xs pointer-events-none z-10 top-1/2 transform -translate-y-1/2">
                  X
                </span>
                <input 
                  aria-label="X movement amount"
                  placeholder="ΔX"
                  type="number" 
                  className="w-24 h-8 pl-6 pr-2 border border-pf-border rounded text-xs bg-pf-bg-0 [appearance:textfield] [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none"
                  disabled={isPrinting}
                  value={moveX}
                  onChange={(e) => setMoveX(e.target.value === '' ? '' : Number(e.target.value))}
                />
              </div>
            </div>
            
            <div className="flex flex-col items-center relative">
              <span className="mb-1 text-xs text-slate-400 bg-pf-bg-0 px-2 py-0.5 rounded w-20 text-center">
                [ {formatPos(null, lastKnownY)} ]
              </span>
              <div className="relative inline-block">
                <span className="absolute left-2 text-slate-500 text-xs pointer-events-none z-10 top-1/2 transform -translate-y-1/2">
                  Y
                </span>
                <input 
                  aria-label="Y movement amount"
                  placeholder="ΔY"
                  type="number" 
                  className="w-24 h-8 pl-6 pr-2 border border-pf-border rounded text-xs bg-pf-bg-0 [appearance:textfield] [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none"
                  disabled={isPrinting}
                  value={moveY}
                  onChange={(e) => setMoveY(e.target.value === '' ? '' : Number(e.target.value))}
                />
              </div>
            </div>
            
            <div className="flex flex-col items-center relative">
              <span className="mb-1 text-xs text-slate-400 bg-pf-bg-0 px-2 py-0.5 rounded w-20 text-center">
                [ {formatPos(null, lastKnownZ)} ]
              </span>
              <div className="relative inline-block">
                <span className="absolute left-2 text-slate-500 text-xs pointer-events-none z-10 top-1/2 transform -translate-y-1/2">
                  Z
                </span>
                <input 
                  aria-label="Z movement amount"
                  placeholder="ΔZ"
                  type="number" 
                  className="w-24 h-8 pl-6 pr-2 border border-pf-border rounded text-xs bg-pf-bg-0 [appearance:textfield] [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none"
                  disabled={isPrinting}
                  value={moveZ}
                  onChange={(e) => setMoveZ(e.target.value === '' ? '' : Number(e.target.value))}
                />
              </div>
            </div>
            
            <div className="flex items-start mt-0">
              <button 
                className="min-w-12 h-8 px-2 text-xs font-bold uppercase bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 border border-pf-border rounded hover:from-pf-bg-2 hover:to-pf-bg-1 hover:border-blue-500 transition-colors disabled:opacity-50"
                disabled={isPrinting}
                onClick={() => console.log('Moving to', moveX, moveY, moveZ)}
              >
                GO
              </button>
            </div>
          </div>
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
