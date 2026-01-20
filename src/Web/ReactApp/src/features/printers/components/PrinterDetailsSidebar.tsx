import React, { useState, useRef, useEffect } from 'react';
// No MdiIcons used in this component
import { usePrinter } from '@/common/hooks/useApi';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import { apiClient } from '@/services/api';
import { formatPrinterState } from '@/common/utils/printerStateDisplay';
import type { TempTargets, MoveRequest } from '@/types/api';
import { PrinterBackend } from '@/types/api';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { renderUnknown } from '@/common/utils/renderUnknown';
import { Button, TemperatureInput, MovementInput, Select } from '@/common/components/ui';
import type { Printer } from '@/types/api';
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
  MinusIcon,
  SnowflakeIcon,
  // StopIcon removed - unused in this file
} from '@/common/components/icons/MdiIcons';

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
  // Call hooks first before any early returns (React Rules of Hooks)
  // Use empty string as default to satisfy hook typing, but we'll guard against empty printerId
  const { data: apiPrinter, isLoading, refetch } = usePrinter(printerId || '');
  // Merge with realtime SignalR updates
  const printer = usePrinterDisplay((apiPrinter || {}) as Printer);

  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
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

  // Guard early after all hooks are called
  if (!printerId) {
    return null;
  }

  // API now returns complete printer DTO with status merged in - no client-side merge needed
  const displayPrinter = printer;

  // Update refs when display printer changes - refs don't trigger re-renders
  if (displayPrinter) {
    if (displayPrinter.hotendTemp !== undefined) lastKnownHotendTempRef.current = displayPrinter.hotendTemp;
    if (displayPrinter.bedTemp !== undefined) lastKnownBedTempRef.current = displayPrinter.bedTemp;
    if (displayPrinter.x !== undefined) lastKnownXRef.current = displayPrinter.x;
    if (displayPrinter.y !== undefined) lastKnownYRef.current = displayPrinter.y;
    if (displayPrinter.z !== undefined) lastKnownZRef.current = displayPrinter.z;
  }

  // Show loading state while fetching printer data
  if (isLoading || !printer) {
    return (
      <div className="w-96 h-full bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 border-l border-pf-border shadow-lg z-30 flex items-center justify-center flex-shrink-0">
        <div className="text-pf-text-secondary">Loading...</div>
      </div>
    );
  }

  // State helpers - safe to access displayPrinter now (guaranteed by printer != null)
  const isOnline = displayPrinter?.isOnline ?? false;
  const rawState = displayPrinter?.state ?? 'unknown';
  const state = formatPrinterState(rawState);
  const isPrinting = rawState.toLowerCase().includes('printing');
  const isPaused = rawState.toLowerCase().includes('paused');
  const isShutdown = rawState.toLowerCase().includes('shutdown') || rawState.toLowerCase().includes('error');

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
  const homedAxes = (displayPrinter?.homedAxes ?? '').toLowerCase();

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
  const isXHomed = homedAxes.includes('x');
  const isYHomed = homedAxes.includes('y');
  const isZHomed = homedAxes.includes('z');

  if ((window as unknown as { PrintFarmerDebug?: { printerDetailsSidebar?: boolean } }).PrintFarmerDebug?.printerDetailsSidebar) {
    console.log('Homing state:', { isXHomed, isYHomed, isZHomed, homedAxes });
  }

  // Printer is fully homed if all axes are homed
  const isAllHomed = isXHomed && isYHomed && isZHomed;


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

  const handleHome = async () => {
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
      if (result && !result.success) {
        console.error(`Failed to ${action}:`, result.error);
      }
    } catch (error) {
      console.error(`Error performing ${action}:`, error);
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
    <div className="w-96 h-full bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 border-l border-pf-border shadow-lg z-30 overflow-hidden flex flex-col sidebar-enter flex-shrink-0">
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
          iconCenter={<CloseIcon className="h-6 w-6" />}
        ></Button>
      </div>

      {/* Scrollable Content */}
      <div ref={scrollRef} className="flex-1 overflow-y-auto p-4 space-y-4">
        {/* Control Section - MOVED TO TOP */}
        <div className="flex flex-col gap-2">
          <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
            Control
          </div>
          <div className="flex flex-col gap-0">
            {/* Control buttons row - 3 buttons matching XY pad width and height */}
            <div className="grid grid-cols-3 gap-1 w-40 h-12">
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!isPrinting}
                onClick={() => handleControlAction('pause')}
                title="Pause print"
                className="w-full h-full !p-0"
                iconCenter={<PauseIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!isPaused}
                onClick={() => handleControlAction('resume')}
                title="Resume print"
                className="w-full h-full !p-0"
                iconCenter={<PlayIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant={isShutdown ? 'secondary' : 'danger'}
                size="sm"
                disabled={!isOnline}
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
            {/* Step control */}
            <div className="flex items-center gap-1">
              <Button
                type="button"
                variant="secondary"
                size="sm"
                onClick={() => setStep(Math.max(1, step - 5))}
                className="flex-1 p-0 text-xs"
                iconCenter={<MinusIcon className="h-3 w-3" />}
              ></Button>
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
                iconCenter={<HomeIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={isPrinting}
                onClick={() => handleMove('Y', step)}
                className="w-full h-full !p-0"
                iconCenter={<ArrowUpIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={!isOnline || isPrinting}
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
                disabled={isPrinting}
                onClick={() => handleMove('X', -step)}
                className="w-full h-full !p-0"
                iconCenter={<ArrowLeftIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={isPrinting}
                onClick={() => handleHome()}
                title="Home X"
                className={`w-full h-full !p-0 ${getHomeButtonStyle(isXHomed).className}`}
                style={getHomeButtonStyle(isXHomed).style}
                iconCenter={<HomeIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={isPrinting}
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
                disabled={isPrinting}
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
                disabled={isPrinting}
                onClick={() => handleMove('Z', step)}
                className="w-full h-full !p-0"
              >
                Z+
              </Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={isPrinting}
                onClick={() => handleHome()}
                title="Home Z"
                className={`w-full h-full !p-0 ${getHomeButtonStyle(isZHomed).className}`}
                style={getHomeButtonStyle(isZHomed).style}
                iconCenter={<HomeIcon className="h-6 w-6" />}
              ></Button>
              <Button
                type="button"
                variant="secondary"
                size="sm"
                disabled={isPrinting}
                onClick={() => handleMove('Z', -step)}
                className="w-full h-full !p-0"
              >
                Z-
              </Button>
            </div>
          </div>
        </div>

        {/* Files and History Section */}
        <div className="flex flex-col gap-2">
          <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide -ml-1">
            Quick Access
          </div>
          <div className="grid grid-cols-2 gap-2">
            <Button
              type="button"
              variant="secondary"
              size="sm"
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
              onClick={() => setShowHistory(true)}
              className="flex items-center justify-center gap-2"
              title="View print history"
              iconLeft={<HistoryIcon className="h-4 w-4" />}
            >
              <span>History</span>
            </Button>
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
            <NozzleIcon className="w-4 h-4 text-red-500 flex-shrink-0" isOn={(displayPrinter?.hotendTarget ?? 0) > 0} />
            <span className="text-xs text-pf-text-secondary min-w-16">Hotend</span>
            <span className="text-xs text-slate-400 flex-1">
              {formatTempWithTarget(
                displayPrinter?.hotendTemp,
                displayPrinter?.hotendTarget,
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
            <BedIcon className="w-4 h-4 text-blue-500 flex-shrink-0" isOn={(displayPrinter?.bedTarget ?? 0) > 0} />
            <span className="text-xs text-pf-text-secondary min-w-16">Bed</span>
            <span className="text-xs text-slate-400 flex-1">
              {formatTempWithTarget(
                displayPrinter?.bedTemp,
                displayPrinter?.bedTarget,
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

        {/* Spool Info Section - Display if available */}
        {displayPrinter?.spoolInfo && displayPrinter.spoolInfo.hasActiveSpool && (
          <div className="mt-4 pt-4 border-t border-pf-border">
            <div className="text-xs uppercase text-pf-text-secondary font-bold tracking-wide mb-2">Spool</div>
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
                      className="w-3 h-3 rounded border border-pf-border"
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
          </div>
        )}
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

      {/* Files Modal */}
      <PrinterFilesModal
        isOpen={showFiles}
        onClose={() => setShowFiles(false)}
        printer={printer}
      />
    </div>
  );
}
