import React, { useState, useRef } from 'react';
import {
  ChevronDown, History, Edit, Camera, ExternalLink, RotateCcw
} from 'lucide-react';
import { PauseIcon, PlayIcon, EmergencyStopIcon } from '@/components/icons/MdiIcons';
import { Button } from '@/components/ui';
import { PrinterHistoryModal } from '@/components/PrinterHistoryModal';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { PrinterBackend, type Printer } from '@/types/api';
import moonrakerIcon from '@/assets/moonraker.svg';
import octoprintIcon from '@/assets/octoprint.svg';

// Backend icon helper
function getBackendIcon(backend: PrinterBackend | number | string) {
  let backendValue: PrinterBackend | undefined = undefined;
  if (typeof backend === 'number') {
    backendValue = backend;
  } else if (typeof backend === 'string') {
    switch (backend) {
      case 'Moonraker': backendValue = PrinterBackend.Moonraker; break;
      case 'PrusaLink': backendValue = PrinterBackend.PrusaLink; break;
      case 'SDCP': backendValue = PrinterBackend.SDCP; break;
      case 'OctoPrint': backendValue = PrinterBackend.OctoPrint; break;
      default: backendValue = undefined;
    }
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

interface CollapsedPrinterCardProps {
  printer: Printer;
  onExpand: () => void;
  onEdit?: (printer: Printer) => void;
  onDelete?: () => void;
}

export function CollapsedPrinterCard({
  printer,
  onExpand,
  onEdit,
  onDelete
}: CollapsedPrinterCardProps) {
  const [showCamera, setShowCamera] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [collapsedImageVisible, setCollapsedImageVisible] = useState(false);
  const collapsedProgressRef = useRef<HTMLDivElement>(null);

  // Real-time status updates
  const { printerStatuses } = usePrinterStatusUpdates();
  const status = printerStatuses.get(printer.id);  // State helpers
  const isOnline = status?.isOnline ?? printer.isOnline ?? false;
  const state = status?.state ?? printer.state ?? 'Unknown';
  const isPrinting = state.toLowerCase().includes('printing');
  const isPaused = state.toLowerCase().includes('paused');
  const isShutdown = state.toLowerCase().includes('shutdown') || state.toLowerCase().includes('error');
  const hasCameraUrls = !!printer.cameraStreamUrl;
  const cameraStreamUrl = printer.cameraStreamUrl;

  // State color classes
  const getStateColorClasses = (isOnline: boolean, state: string): string => {
    if (!isOnline) return 'bg-pf-offline text-pf-text-primary';
    if (state.toLowerCase().includes('printing')) return 'bg-pf-printing text-pf-text-primary';
    if (state.toLowerCase().includes('paused')) return 'bg-pf-paused text-pf-text-primary';
    if (state.toLowerCase().includes('error')) return 'bg-pf-error text-pf-text-primary';
    return 'bg-pf-idle text-pf-text-primary';
  };
  const stateColorClasses = getStateColorClasses(isOnline, state);

  const toCamelCase = (str: string): string => {
    return str.replace(/([A-Z])/g, ' $1').replace(/^./, (s) => s.toUpperCase()).trim();
  };

  const handleControlAction = async (action: 'pause' | 'resume' | 'stop' | 'firmware-restart') => {
    try {
      const endpoint = action === 'firmware-restart' ? 'firmware-restart' : action;
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

  const handleViewHistory = () => {
    setShowHistory(true);
  };

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
            onClick={onExpand}
            className="!p-1 !h-auto"
            title="Expand card"
          >
            <ChevronDown className="h-4 w-4" />
          </Button>
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
        <div className="mt-4 w-52 min-h-32 flex items-center justify-center bg-pf-bg-2 bg-opacity-30 border border-pf-border rounded-md overflow-hidden">
          {cameraStreamUrl && collapsedImageVisible ? (
            <img 
              src={cameraStreamUrl} 
              alt="webcam snapshot"
              className="max-w-full max-h-full object-contain"
              onError={() => setCollapsedImageVisible(false)}
              onLoad={() => setCollapsedImageVisible(true)}
            />
          ) : (
            <div className="text-center text-pf-text-secondary p-4">
              <Camera className="h-8 w-8 mx-auto mb-2 opacity-50" />
              <p className="text-sm">No camera configured</p>
            </div>
          )}
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
