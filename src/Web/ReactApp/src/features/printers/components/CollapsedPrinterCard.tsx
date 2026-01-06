import React, { useRef, useState } from 'react';
import {
  HistoryIcon,
  FileIcon,
  RefreshIcon,
  PauseIcon, 
  PlayIcon, 
  EmergencyStopIcon, 
  EditIcon,
  CameraIcon,
  ExternalLinkIcon,
  PanelRightIcon,
  ImageIcon,
  VideoIcon,
  DeleteIcon
} from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { ControlPadButton } from '@/common/components/ui/ControlPadButton';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { formatPrinterState } from '@/common/utils/printerStateDisplay';
import { getBackendIcon } from '@/common/utils/printerBackendIcon';
import { PrinterBackend, type Printer } from '@/types/api';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';

interface CollapsedPrinterCardProps {
  printer: Printer;
  onExpand: () => void;
  onEdit?: (printer: Printer) => void;
  onDelete?: (printer: Printer) => void;
}

export function CollapsedPrinterCard({
  printer: printerProp,
  onExpand,
  onEdit,
  onDelete
}: CollapsedPrinterCardProps) {
  // Merge with realtime SignalR updates
  const printer = usePrinterDisplay(printerProp);
  const [showCamera, setShowCamera] = useState(false);
  const [cameraMode, setCameraMode] = useState<'snapshot' | 'stream'>('snapshot');
  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const collapsedProgressRef = useRef<HTMLDivElement>(null);

  // Use printer data directly (already contains merged realtime status from API)
  const isOnline = printer.isOnline ?? false;
  const state = printer.state ?? 'Unknown';
  const isPrinting = state.toLowerCase().includes('printing');
  const isPaused = state.toLowerCase().includes('paused');
  const isShutdown = state.toLowerCase().includes('shutdown') || state.toLowerCase().includes('error');
  // Check if printer has camera URLs - just verify if URLs have values from database
  const cameraSnapshotUrl = printer.cameraSnapshotUrl;
  const cameraStreamUrl = printer.cameraStreamUrl;
  const hasCameraUrls = !!(cameraSnapshotUrl || cameraStreamUrl);

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
      const response = await fetch(`${getApiBaseUrl()}/printers/${printer.id}/${endpoint}`, {
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
    <div className="bg-pf-bg-1 rounded-lg p-3 shadow border border-pf-border hover:border-pf-primary transition-colors w-full max-w-sm overflow-hidden flex flex-col min-h-0">
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
          iconLeft={<PanelRightIcon className="h-4 w-4" />}
        >
        </Button>
        
        {/* External link */}
        <a 
          href={printer.frontendUrl} 
          target="_blank" 
          rel="noopener noreferrer"
          className="text-pf-text-secondary hover:text-pf-text-primary flex-shrink-0 p-1"
          aria-label={`Open printer ${printer.name} in new tab`}
          title={`Open printer ${printer.name}`}
        >
          <ExternalLinkIcon className="h-4 w-4" />
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
          iconLeft={<CameraIcon className="h-4 w-4" />}
        >
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
            iconLeft={<HistoryIcon className="h-4 w-4" />}
          >
          </Button>
        )}
        
        {/* Files button */}
        <Button
          type="button"
          variant="subtle"
          size="sm"
          onClick={() => setShowFiles(true)}
          className="!p-1 !h-auto"
          title="View printer files"
          iconLeft={<FileIcon className="h-4 w-4" />}
        >
        </Button>
        
        {/* Edit button */}
        <Button
          type="button"
          variant="subtle"
          size="sm"
          onClick={() => onEdit?.(printer)}
          className="!p-1 !h-auto"
          title="Edit details"
          iconLeft={<EditIcon className="h-4 w-4" />}
        >
        </Button>
        
        {/* Delete button */}
        <Button
          type="button"
          variant="danger"
          size="sm"
          onClick={() => onDelete?.(printer)}
          className="!p-1 !h-auto"
          title="Delete printer"
          iconLeft={<DeleteIcon className="h-4 w-4" />}
        >
        </Button>
      </div>

      {/* Control buttons */}
      <div className="flex gap-1 mb-2">
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

      {/* Progress bar for active prints */}
      {(() => {
        const progress = printer.progress ?? 0;
        return isOnline && progress !== undefined && progress > 0 && (
          <div className="mt-3">
            <div className="flex justify-between text-xs text-pf-text-secondary mb-1">
              <span className="truncate flex-1">{printer.jobName || 'Printing...'}</span>
              <span className="font-semibold ml-2">{Math.round(progress)}%</span>
            </div>
            <div
              className="w-full bg-pf-border-dark rounded-full h-2 overflow-hidden"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={Math.round(Math.max(0, Math.min(100, progress)))}
            >
              <div
                ref={collapsedProgressRef}
                className="bg-pf-success-bg h-2 rounded-full transition-all duration-300"
                style={{ width: `${Math.max(0, Math.min(100, progress))}%` }}
              >
                <span className="sr-only">Print progress: {Math.round(Math.max(0, Math.min(100, progress)))}%</span>
              </div>
            </div>
          </div>
        );
      })()}

      {showCamera && (
        <div className="mt-4 w-52 flex flex-col bg-pf-bg-2 bg-opacity-30 border border-pf-border rounded-md overflow-hidden">
          {/* Camera mode toggle - show if both snapshot and stream are available */}
          {hasCameraUrls && cameraSnapshotUrl && cameraStreamUrl && (
            <div className="flex gap-1 p-2 border-b border-pf-border bg-pf-bg-1 bg-opacity-50">
              <Button
                type="button"
                onClick={() => setCameraMode('snapshot')}
                title="Snapshot"
                variant={cameraMode === 'snapshot' ? 'primary' : 'secondary'}
                size="sm"
                className="flex-1"
                iconLeft={<ImageIcon className="h-4 w-4" />}
              >
              </Button>
              <Button
                type="button"
                onClick={() => setCameraMode('stream')}
                title="Stream"
                variant={cameraMode === 'stream' ? 'primary' : 'secondary'}
                size="sm"
                className="flex-1"
                iconLeft={<VideoIcon className="h-4 w-4" />}
              >
              </Button>
            </div>
          )}
          
          {/* Camera display */}
          <div className="min-h-32 flex items-center justify-center overflow-hidden">
            {hasCameraUrls ? (
              cameraMode === 'snapshot' && cameraSnapshotUrl ? (
                <img 
                  src={cameraSnapshotUrl}
                  alt="webcam snapshot"
                  className="max-w-full max-h-full object-contain"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : cameraMode === 'stream' && cameraStreamUrl ? (
                <img 
                  src={cameraStreamUrl}
                  alt="webcam stream"
                  className="max-w-full max-h-full object-contain"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : cameraSnapshotUrl ? (
                <img 
                  src={cameraSnapshotUrl}
                  alt="webcam snapshot"
                  className="max-w-full max-h-full object-contain"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : cameraStreamUrl ? (
                <img 
                  src={cameraStreamUrl}
                  alt="webcam stream"
                  className="max-w-full max-h-full object-contain"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : (
                <div className="text-center text-pf-text-secondary p-4">
                  <CameraIcon className="h-8 w-8 mx-auto mb-2 opacity-50" />
                  <p className="text-sm">Camera mode not available</p>
                </div>
              )
            ) : (
              <div className="text-center text-pf-text-secondary p-4 w-full">
                <CameraIcon className="h-8 w-8 mx-auto mb-2 opacity-50" />
                <p className="text-sm">No camera configured</p>
              </div>
            )}
          </div>
        </div>
      )}

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
