import React, { useState, useRef } from 'react';
import { X } from 'lucide-react';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { usePrinter } from '@/hooks/useApi';
import { apiClient } from '@/services/api';
import type { TempTargets, MoveRequest } from '@/types/api';
import { PrinterHistoryModal } from '@/components/PrinterHistoryModal';
import { renderUnknown } from '@/utils/renderUnknown';
import { Button, TemperatureInput, MovementInput, Select } from '@/components/ui';
import { 
  NozzleIcon, 
  BedIcon, 
  DisableMotorsIcon,
  HomeIcon,
  PlayIcon,
  PauseIcon,
  EmergencyStopIcon,
  StopIcon
} from '@/components/icons/MdiIcons';
import { 
  ChevronUp,
  ChevronDown,
  Minus,
  RotateCcw,
} from 'lucide-react';

// Animation styles
const sidebarAnimationStyles = `
  @keyframes slideInRight {
    from {
      transform: translateX(100%);
      opacity: 0;
    }
    to {
      transform: translateX(0);
      opacity: 1;
    }
  }

  @keyframes slideOutRight {
    from {
      transform: translateX(0);
      opacity: 1;
    }
    to {
      transform: translateX(100%);
      opacity: 0;
    }
  }

  .sidebar-enter {
    animation: slideInRight 0.3s ease-out;
  }

  .sidebar-exit {
    animation: slideOutRight 0.3s ease-in;
  }
`;

// Inject animation styles
if (typeof document !== 'undefined') {
  const style = document.createElement('style');
  style.textContent = sidebarAnimationStyles;
  document.head.appendChild(style);
}

interface PrinterDetailsSidebarProps {
  printerId: string | null;
  onClose: () => void;
}

export function PrinterDetailsSidebar({ printerId, onClose }: PrinterDetailsSidebarProps) {
  // Guard early and don't fetch if no printerId
  if (!printerId) {
    return null;
  }

  const { data: printer, isLoading } = usePrinter(printerId);
  const { printerStatuses } = usePrinterStatusUpdates();
  const status = printerId ? printerStatuses.get(printerId) : undefined;
  
  const [showHistory, setShowHistory] = useState(false);
  const [hotendTemp, setHotendTemp] = useState<number | ''>('');
  const [bedTemp, setBedTemp] = useState<number | ''>('');
  const [moveX, setMoveX] = useState<number | ''>('');
  const [moveY, setMoveY] = useState<number | ''>('');
  const [moveZ, setMoveZ] = useState<number | ''>('');
  const [step, setStep] = useState(10);
  
  // Use refs instead of state for caching display values - this avoids setState during render
  const lastKnownHotendTempRef = useRef<number | null>(null);
  const lastKnownBedTempRef = useRef<number | null>(null);
  const lastKnownXRef = useRef<number | null>(null);
  const lastKnownYRef = useRef<number | null>(null);
  const lastKnownZRef = useRef<number | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);

  // Update refs when status changes - refs don't trigger re-renders
  if (status) {
    if (status.hotendTemp !== undefined) lastKnownHotendTempRef.current = status.hotendTemp;
    if (status.bedTemp !== undefined) lastKnownBedTempRef.current = status.bedTemp;
    if (status.x !== undefined) lastKnownXRef.current = status.x;
    if (status.y !== undefined) lastKnownYRef.current = status.y;
    if (status.z !== undefined) lastKnownZRef.current = status.z;
  }

  // Show loading state while fetching printer data
  if (isLoading || !printer) {
    return (
      <div className="fixed right-0 top-16 h-[calc(100vh-4rem)] w-96 bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 border-l border-pf-border shadow-lg z-30 flex items-center justify-center">
        <div className="text-pf-text-secondary">Loading...</div>
      </div>
    );
  }

  // State helpers - safe to access printer now
  const isOnline = status?.isOnline ?? printer.isOnline ?? false;
  const state = status?.state ?? printer.state ?? 'Unknown';
  const isPrinting = state.toLowerCase().includes('printing');
  const isPaused = state.toLowerCase().includes('paused');
  const isShutdown = state.toLowerCase().includes('shutdown') || state.toLowerCase().includes('error');

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
  const homedAxes = (status?.homedAxes ?? '').toLowerCase();
  
  // Guarded debug logging - only log if enabled in window.PrintFarmerDebug
  if ((window as unknown as { PrintFarmerDebug?: { printerDetailsSidebar?: boolean } }).PrintFarmerDebug?.printerDetailsSidebar) {
    // eslint-disable-next-line no-console
    console.log('PrinterDetailsSidebar - Status update:', {
      printerId,
      homedAxesRaw: status?.homedAxes,
      homedAxesLower: homedAxes,
      status: status?.state,
      x: status?.x,
      y: status?.y,
      z: status?.z
    });
  }
  
  // Determine if each axis is homed
  const isXHomed = homedAxes.includes('x');
  const isYHomed = homedAxes.includes('y');
  const isZHomed = homedAxes.includes('z');
  
  if ((window as unknown as { PrintFarmerDebug?: { printerDetailsSidebar?: boolean } }).PrintFarmerDebug?.printerDetailsSidebar) {
    // eslint-disable-next-line no-console
    console.log('Homing state:', { isXHomed, isYHomed, isZHomed, homedAxes });
  }
  
  // Printer is fully homed if all axes are homed
  const isAllHomed = isXHomed && isYHomed && isZHomed;
  const isXYHomed = isXHomed && isYHomed;

  // Get button class based on homed state
  const getHomeButtonStyle = (isHomed: boolean): { className: string; style?: React.CSSProperties } => {
    if (isHomed) {
      return {
        className: '!text-white',
        style: {
          backgroundColor: '#2096f3',
          backgroundImage: 'linear-gradient(to bottom, #2096f3, #2096f3)',
        },
      };
    }
    return {
      className: '!text-white',
      style: {
        backgroundColor: '#fb8c00',
        backgroundImage: 'linear-gradient(to bottom, #fb8c00, #fb8c00)',
      },
    };
  };

  const handleHome = async (axis?: string) => {
    try {
      const result = await apiClient.homePrinter(printer.id);
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

  const handleControlAction = async (action: 'pause' | 'resume' | 'stop' | 'firmware-restart' | 'disable-motors') => {
    try {
      let endpoint = action;
      if (action === 'disable-motors') {
        endpoint = 'disable-motors';
      }
      const response = await fetch(`http://localhost:5245/api/printers/${printer.id}/${endpoint}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
      });
      if (!response.ok) {
        console.error(`Failed to ${action}:`, response.statusText);
      }
    } catch (error) {
      console.error(`Error performing ${action}:`, error);
    }
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
    <div className="fixed right-0 top-16 h-[calc(100vh-4rem)] w-96 bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 border-l border-pf-border shadow-lg z-30 overflow-hidden flex flex-col sidebar-enter">
      {/* Header */}
      <div className="flex justify-between items-start p-4 border-b border-pf-border flex-shrink-0 gap-3">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 mb-1">
            <h2 className="text-lg font-bold text-pf-text-primary truncate">{printer.name}</h2>
            <div className={`flex-shrink-0 w-2 h-2 rounded-full ${isOnline ? 'bg-green-500' : 'bg-gray-500'}`} title={isOnline ? 'Online' : 'Offline'} />
          </div>
          <p className="text-xs text-pf-text-secondary">{printer.manufacturerName} {printer.modelName}</p>
          <p className="text-xs text-pf-text-secondary mt-1">{state}</p>
        </div>
        <Button
          type="button"
          variant="subtle"
          size="sm"
          onClick={onClose}
          className="!p-1 !h-auto flex-shrink-0"
          title="Close sidebar"
        >
          <X className="h-4 w-4" />
        </Button>
      </div>

      {/* Scrollable Content */}
      <div ref={scrollRef} className="flex-1 overflow-y-auto p-4 space-y-4">
        {/* Control Section - MOVED TO TOP */}
        <div className="flex flex-col gap-2">
          <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
            Control
          </div>
          <div className="flex flex-col gap-0">
            {/* Control buttons row */}
            <div className="flex items-stretch gap-1 mb-1">
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!isPrinting}
                onClick={() => handleControlAction('pause')}
                title="Pause print"
                className="flex-1 p-0"
              >
                <PauseIcon className="h-4 w-4" />
              </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!isPaused}
                onClick={() => handleControlAction('resume')}
                title="Resume print"
                className="flex-1 p-0"
              >
                <PlayIcon className="h-4 w-4" />
              </Button>
            </div>
            {/* Stop button row */}
            <Button
              type="button"
              variant={isShutdown ? 'secondary' : 'danger'}
              size="sm"
              disabled={!isOnline}
              onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
              title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
              className="w-full mb-1"
            >
              {isShutdown ? (
                <>
                  <RotateCcw className="h-3 w-3 mr-1" />
                  Restart
                </>
              ) : (
                <>
                  <EmergencyStopIcon className="h-3 w-3" />
                  <span className="ml-1">Stop</span>
                </>
              )}
            </Button>
            {/* Step control */}
            <div className="flex items-center gap-1">
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => setStep(Math.max(1, step - 5))}
                className="flex-1 p-0 text-xs"
              >
                <Minus className="h-3 w-3" />
              </Button>
              <span className="text-xs text-pf-text-secondary flex-1 text-center">{step}mm</span>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => setStep(step + 5)}
                className="flex-1 p-0 text-xs"
              >
                +
              </Button>
            </div>
          </div>
        </div>

        {/* Move Section */}
        <div className="flex flex-col gap-2">
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
                className={`w-full h-full !p-0 ${getHomeButtonStyle(isAllHomed).className}`}
                style={getHomeButtonStyle(isAllHomed).style}
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
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!isOnline || isPrinting}
                onClick={() => handleControlAction('disable-motors')}
                title="Disable Motors (M84)"
                className="w-full h-full !p-0"
              >
                <DisableMotorsIcon className="w-4 h-4" />
              </Button>

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
                onClick={() => handleHome('x')}
                title="Home X"
                className={`w-full h-full !p-0 ${getHomeButtonStyle(isXHomed).className}`}
                style={getHomeButtonStyle(isXHomed).style}
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
                className={`w-full h-full !p-0 ${getHomeButtonStyle(isZHomed).className}`}
                style={getHomeButtonStyle(isZHomed).style}
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

        {/* Manual Movement Input Section */}
        <div className="space-y-2">
          <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">Manual Move</div>
          <div className="grid grid-cols-3 gap-2 h-20">
            {/* Row 1: Current Position Labels */}
            <div className="flex items-center justify-center">
              <span className="text-xs font-bold text-pf-text-secondary">[ {(lastKnownXRef.current ?? 0).toFixed(1)} ]</span>
            </div>
            <div className="flex items-center justify-center">
              <span className="text-xs font-bold text-pf-text-secondary">[ {(lastKnownYRef.current ?? 0).toFixed(1)} ]</span>
            </div>
            <div className="flex items-center justify-center">
              <span className="text-xs font-bold text-pf-text-secondary">[ {(lastKnownZRef.current ?? 0).toFixed(1)} ]</span>
            </div>
            
            {/* Row 2: Input Fields */}
            <MovementInput
              axis="X"
              value={moveX}
              onChange={(e) => setMoveX(e.target.value === '' ? '' : Number(e.target.value))}
              onKeyDown={(e) => e.key === 'Enter' && moveX !== '' && handleMove('X', Number(moveX))}
              className="!w-full"
            />
            <MovementInput
              axis="Y"
              value={moveY}
              onChange={(e) => setMoveY(e.target.value === '' ? '' : Number(e.target.value))}
              onKeyDown={(e) => e.key === 'Enter' && moveY !== '' && handleMove('Y', Number(moveY))}
              className="!w-full"
            />
            <MovementInput
              axis="Z"
              value={moveZ}
              onChange={(e) => setMoveZ(e.target.value === '' ? '' : Number(e.target.value))}
              onKeyDown={(e) => e.key === 'Enter' && moveZ !== '' && handleMove('Z', Number(moveZ))}
              className="!w-full"
            />
          </div>
        </div>

        {/* Temperatures Section - REDESIGNED */}
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide">Temps</div>
            <div className="flex gap-1 items-stretch h-8">
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={isPrinting}
                onClick={() => handleApplyPreset('cooldown')}
                title="Cooldown"
                className="flex-shrink-0 px-2"
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
          <div className="flex items-center gap-2 py-1">
            <NozzleIcon className="w-4 h-4 text-red-500 flex-shrink-0" isOn={(status?.hotendTarget ?? printer.hotendTarget ?? 0) > 0} />
            <span className="text-xs text-pf-text-secondary min-w-16">Hotend</span>
            <span className="text-xs text-slate-400 flex-1">
              {formatTempWithTarget(
                status?.hotendTemp ?? printer.hotendTemp,
                status?.hotendTarget ?? printer.hotendTarget,
                lastKnownHotendTempRef.current
              )}
            </span>
            <TemperatureInput
              value={hotendTemp}
              onChange={(e) => setHotendTemp(e.target.value === '' ? '' : Number(e.target.value))}
              onKeyDown={handleHotendTempKeyDown}
              className="w-16"
            />
          </div>

          {/* Bed Temperature Row */}
          <div className="flex items-center gap-2 py-1">
            <BedIcon className="w-4 h-4 text-blue-500 flex-shrink-0" isOn={(status?.bedTarget ?? printer.bedTarget ?? 0) > 0} />
            <span className="text-xs text-pf-text-secondary min-w-16">Bed</span>
            <span className="text-xs text-slate-400 flex-1">
              {formatTempWithTarget(
                status?.bedTemp ?? printer.bedTemp,
                status?.bedTarget ?? printer.bedTarget,
                lastKnownBedTempRef.current
              )}
            </span>
            <TemperatureInput
              value={bedTemp}
              onChange={(e) => setBedTemp(e.target.value === '' ? '' : Number(e.target.value))}
              onKeyDown={handleBedTempKeyDown}
              className="w-16"
            />
          </div>
        </div>

        {/* Optional debug panel */}
        {window.PrintFarmerDebug?.expandablePrinterCardDisplay && (
          <div className="mt-3 p-2 bg-pf-bg-0 border border-pf-border rounded text-xs text-pf-text-tertiary">
            {renderUnknown({ status, lastKnownHotendTemp: lastKnownHotendTempRef.current, lastKnownBedTemp: lastKnownBedTempRef.current, lastKnownX: lastKnownXRef.current, lastKnownY: lastKnownYRef.current, lastKnownZ: lastKnownZRef.current })}
          </div>
        )}
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
