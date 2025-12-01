import React, { useState, useRef } from 'react';
import {
  PanelRightOpen, History, Edit, Camera, ExternalLink, RotateCcw
} from 'lucide-react';
import { PauseIcon, PlayIcon, EmergencyStopIcon } from '@/components/icons/MdiIcons';
import { Button } from '@/components/ui';
import { PrinterHistoryModal } from '@/components/PrinterHistoryModal';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { formatPrinterState } from '@/utils/printerStateDisplay';
import { PrinterBackend, type Printer } from '@/types/api';
import moonrakerIcon from '@/assets/moonraker.svg';
import prusalinkIcon from '@/assets/prusalink.svg';
import octoprintIcon from '@/assets/octoprint.svg';

// Backend icon helper
function getBackendIcon(backend: PrinterBackend | number | string) {
  let backendValue: PrinterBackend | undefined = undefined;
  
  // Handle numeric values
  if (typeof backend === 'number') {
    backendValue = backend;
  } 
  // Handle string values
  else if (typeof backend === 'string') {
    switch (backend.toLowerCase()) {
      case 'moonraker': backendValue = PrinterBackend.Moonraker; break;
      case 'prusalink': backendValue = PrinterBackend.PrusaLink; break;
      case 'sdcp': backendValue = PrinterBackend.SDCP; break;
      case 'octoprint': backendValue = PrinterBackend.OctoPrint; break;
      default: backendValue = undefined;
    }
  }
  
  switch (backendValue) {
    case PrinterBackend.Moonraker:
      return <img src={moonrakerIcon} alt="Moonraker" title="Moonraker" className="inline h-5 w-5 align-middle mr-1" />;
    case PrinterBackend.PrusaLink:
      return <img src={prusalinkIcon} alt="PrusaLink" title="PrusaLink" className="inline h-5 w-5 align-middle mr-1" />;
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
  const status = printerStatuses.get(printer.id);
  
  // Debug: Log all printerStatuses and this printer's lookup
  React.useEffect(() => {
    const allIds = Array.from(printerStatuses.keys());
    console.log(`[CollapsedCard] Printer ID: "${printer.id}", Type: ${typeof printer.id}`);
    console.log(`[CollapsedCard] Available IDs in map:`, allIds);
    console.log(`[CollapsedCard] Status lookup result:`, status ? { isOnline: status.isOnline, state: status.state } : 'NOT FOUND');
  }, [printer.id, status, printerStatuses]);
  
  // Merge SignalR status updates with printer data
  const displayPrinter = status ? {
    ...printer,
    isOnline: status.isOnline,
    state: status.state,
    progress: status.progress,
    jobName: status.jobName,
    thumbnailUrl: status.thumbnailUrl,
    cameraStreamUrl: status.cameraStreamUrl,
    cameraSnapshotUrl: status.cameraSnapshotUrl,
  } : printer;
  
  // State helpers
  const isOnline = displayPrinter.isOnline ?? false;
  const state = displayPrinter.state ?? 'Unknown';
  const isPrinting = state.toLowerCase().includes('printing');
  const isPaused = state.toLowerCase().includes('paused');
  const isShutdown = state.toLowerCase().includes('shutdown') || state.toLowerCase().includes('error');
  const hasCameraUrls = !!displayPrinter.cameraStreamUrl;
  const cameraStreamUrl = displayPrinter.cameraStreamUrl;

  // State color classes
  const getStateColorClasses = (isOnline: boolean, state: string): string => {
    if (!isOnline) return 'bg-pf-offline text-pf-text-primary';
    if (state.toLowerCase().includes('printing')) return 'bg-pf-printing text-pf-text-primary';
    if (state.toLowerCase().includes('paused')) return 'bg-pf-paused text-pf-text-primary';
    if (state.toLowerCase().includes('error')) return 'bg-pf-error text-pf-text-primary';
    return 'bg-pf-idle text-pf-text-primary';
  };
  const stateColorClasses = getStateColorClasses(isOnline, state);

  const displayState = (state: string | undefined): string => {
    return formatPrinterState(state);
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
    <div className="border border-pf-border rounded-xl p-3 bg-gradient-to-b from-pf-bg-1 to-pf-bg-0 shadow-lg min-w-[18rem] max-w-[18rem]">
      {/* Top row: Name + Status Pill */}
      <div className="flex justify-between items-center mb-2 gap-2">
        <div className="flex-1 min-w-0">
          <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase truncate">
            {printer.name}
          </div>
          {(printer.manufacturerName || printer.modelName) && (
            <div className="text-pf-text-secondary text-xs truncate">
              {`${printer.manufacturerName || ''} ${printer.modelName || ''}`.trim()}
            </div>
          )}
        </div>
        
        {/* Status pill - compact */}
        <div className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium flex-shrink-0 ${stateColorClasses}`}>
          {getBackendIcon(printer.backend)}
          <span className="hidden sm:inline">{isOnline ? displayState(state) : 'Offline'}</span>
        </div>
      </div>

      {/* Action buttons row - all grouped together */}
      <div className="flex items-center gap-1 mb-2">
        {/* Details/Sidebar button */}
        <Button
          type="button"
          variant="subtle"
          size="sm"
          onClick={onExpand}
          className="!p-1 !h-auto"
          title="Open details sidebar"
        >
          <PanelRightOpen className="h-4 w-4" />
        </Button>
        
        {/* External link */}
        <a 
          href={printer.serverUrl} 
          target="_blank" 
          rel="noopener noreferrer"
          className="text-pf-text-secondary hover:text-pf-text-primary flex-shrink-0 p-1"
          aria-label={`Open printer ${printer.name} in new tab`}
          title={`Open printer ${printer.name}`}
        >
          <ExternalLink className="h-4 w-4" />
        </a>
        
        {/* Camera button */}
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
        
        {/* History button */}
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
        
        {/* Edit button */}
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

      {/* Control buttons in collapsed view - same size as XY pad buttons */}
      <div className="grid grid-cols-3 gap-1 w-40 h-12 mb-2">
        <Button
          type="button"
          variant="secondary"
          size="sm"
          onClick={() => handleControlAction('pause')}
          disabled={!isPrinting}
          className="w-full h-full !p-0"
        >
          <PauseIcon className="h-6 w-6" />
        </Button>
        <Button
          type="button"
          variant="success"
          size="sm"
          onClick={() => handleControlAction('resume')}
          disabled={!isPaused}
          className="w-full h-full !p-0"
        >
          <PlayIcon className="h-6 w-6" />
        </Button>
        <Button
          type="button"
          variant={isShutdown ? 'secondary' : 'danger'}
          size="sm"
          onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
          disabled={!isOnline}
          title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
          className="w-full h-full !p-0"
        >
          {isShutdown ? <RotateCcw className="h-6 w-6" /> : <EmergencyStopIcon className="h-6 w-6" />}
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
