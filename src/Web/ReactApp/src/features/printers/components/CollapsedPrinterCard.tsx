import React, { useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
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
  ClockIcon,
  CheckCircleIcon,
  PackageIcon,
  ChevronDownIcon,
  ChevronRightIcon
} from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { ControlPadButton } from '@/common/components/ui/ControlPadButton';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { getBackendIcon } from '@/common/utils/printerBackendIcon';
import { PrinterBackend, type Printer } from '@/types/api';
import { maintenanceService } from '@/services/maintenanceService';

/**
 * Formats hours into a human-readable string (e.g., "12.5h" or "1,234h")
 */
function formatHours(hours: number | undefined | null): string {
  const h = hours ?? 0;
  if (h < 1) return `${Math.round(h * 60)}m`;
  if (h < 100) return `${h.toFixed(1)}h`;
  return `${Math.round(h).toLocaleString()}h`;
}

/**
 * Formats filament usage (grams or kg)
 */
function formatFilament(grams: number | undefined | null): string {
  const g = grams ?? 0;
  if (g < 1000) return `${Math.round(g)}g`;
  return `${(g / 1000).toFixed(1)}kg`;
}

interface CollapsedPrinterCardProps {
  printer: Printer;
  onExpand: () => void;
  onEdit?: (printer: Printer) => void;
}

export function CollapsedPrinterCard({
  printer: printerProp,
  onExpand,
  onEdit
}: CollapsedPrinterCardProps) {
  // Merge with realtime SignalR updates
  const printer = printerProp; // printerProp already includes display data
  const [showCamera, setShowCamera] = useState(false);
  const [cameraMode, setCameraMode] = useState<'snapshot' | 'stream'>('snapshot');
  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [showStats, setShowStats] = useState(false);
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

  // Fetch printer statistics (cached, stale time 5 minutes)
  const { data: stats } = useQuery({
    queryKey: ['printerStatistics', printer.id],
    queryFn: () => maintenanceService.getPrinterStatistics(printer.id),
    staleTime: 5 * 60 * 1000, // 5 minutes
    retry: false, // Don't retry on failure (printer may not have stats yet)
  });

  const hasStats = stats && (stats.totalPrintHours > 0 || stats.totalJobsCompleted > 0 || stats.totalFilamentUsedGrams > 0);

  // State color classes (matches DetailedPrinterCard)
  const stateColorClasses = (() => {
    if (!isOnline) return 'bg-slate-500 text-white';
    if (isPrinting) return 'bg-pf-success-bg text-white font-bold';
    if (isPaused) return 'bg-yellow-600 text-white';
    if (isShutdown) return 'bg-red-600 text-white';
    return 'bg-blue-600 text-white';
  })();

  const toCamelCase = (str: string): string => {
    return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
  };

  const handleControlAction = async (action: 'pause' | 'resume' | 'stop' | 'firmware-restart') => {
    try {
      // Note: These endpoints would need to be added to apiClient
      // For now, using direct POST
      if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { printerActions?: boolean } }).PrintFarmerDebug?.printerActions) {
        console.log(`Performing ${action} on printer ${printer.id}`);
      }
    } catch (error) {
      console.error(`Error performing ${action}:`, error);
    }
  };

  const handleViewHistory = () => {
    setShowHistory(true);
  };

  return (
    <div className="rounded-xl p-3 shadow-lg backdrop-blur-xl bg-white/5 border border-white/10 hover:border-white/20 transition-colors w-full max-w-sm overflow-hidden flex flex-col min-h-0">
      {/* Top row: Name + Status Pill */}
      <div className="flex justify-between items-center mb-2 gap-2">
        <div className="flex-1 min-w-0">
          <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase truncate">
            {printer.name}
          </div>
          {(printer.modelName) && (
            <div className="text-pf-text-secondary text-xs truncate">
              {`${printer.modelName || ''}`.trim()}
            </div>
          )}
        </div>
        
        {/* Status pill - matches DetailedPrinterCard */}
        <div className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium shrink-0 ${stateColorClasses}`}>
          {getBackendIcon(printer.backend)}
          {isOnline ? toCamelCase(state) : 'Offline'}
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
          className="p-1! h-auto!"
          title="Open details sidebar"
          aria-label="Open details sidebar"
          iconCenter={<PanelRightIcon className="h-4 w-4" />}
        >
        </Button>
        
        {/* External link */}
        <a 
          href={printer.frontendUrl} 
          target="_blank" 
          rel="noopener noreferrer"
          className="text-pf-text-secondary hover:text-pf-text-primary shrink-0 p-1"
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
          className="p-1! h-auto!"
          aria-label={showCamera ? 'Hide camera stream' : 'Show camera stream'}
          title={hasCameraUrls ? `Camera available` : 'No camera configured'}
          iconCenter={<CameraIcon className="h-4 w-4" />}
        >
        </Button>
        
        {/* History button - only show for backends that support it (Moonraker, OctoPrint) */}
        {(printer.backend === PrinterBackend.Moonraker || printer.backend === PrinterBackend.OctoPrint) && (
          <Button
            type="button"
            variant="subtle"
            size="sm"
            onClick={handleViewHistory}
            className="p-1! h-auto!"
            title="View print history"
            aria-label="View print history"
            iconCenter={<HistoryIcon className="h-4 w-4" />}
          >
          </Button>
        )}
        
        {/* Files button */}
        <Button
          type="button"
          variant="subtle"
          size="sm"
          onClick={() => setShowFiles(true)}
          className="p-1! h-auto!"
          title="View printer files"
          aria-label="View printer files"
          iconCenter={<FileIcon className="h-4 w-4" />}
        >
        </Button>
        
        {/* Edit button */}
        <Button
          type="button"
          variant="subtle"
          size="sm"
          onClick={() => onEdit?.(printer)}
          className="p-1! h-auto!"
          title="Edit details"
          aria-label="Edit details"
          iconCenter={<EditIcon className="h-4 w-4" />}
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

      {/* Print Statistics Section */}
      {hasStats && stats && (
        <div className="mb-2 bg-pf-bg-2 rounded-sm overflow-hidden">
          {/* Collapsible header */}
          <Button
            type="button"
            variant="subtle"
            size="sm"
            onClick={() => setShowStats(!showStats)}
            className="w-full! justify-start! gap-1! px-2! py-1.5! h-auto! text-xs text-pf-text-secondary hover:text-pf-text-primary"
            aria-expanded={showStats}
            aria-controls="printer-stats-content"
            iconLeft={showStats ? <ChevronDownIcon className="w-3.5 h-3.5" /> : <ChevronRightIcon className="w-3.5 h-3.5" />}
          >
            <span className="font-medium">Statistics</span>
          </Button>
          
          {/* Stats content */}
          {showStats && (
            <div id="printer-stats-content" className="grid grid-cols-3 gap-1 py-2 px-1 text-center border-t border-pf-border/30">
              <div className="flex flex-col items-center" title="Total print time">
                <ClockIcon className="w-3.5 h-3.5 text-pf-text-secondary mb-0.5" />
                <span className="text-xs font-medium text-pf-text-primary">{formatHours(stats.totalPrintHours ?? 0)}</span>
              </div>
              <div className="flex flex-col items-center" title="Jobs completed">
                <CheckCircleIcon className="w-3.5 h-3.5 text-pf-text-secondary mb-0.5" />
                <span className="text-xs font-medium text-pf-text-primary">{(stats.totalJobsCompleted ?? 0).toLocaleString()}</span>
              </div>
              <div className="flex flex-col items-center" title="Filament used">
                <PackageIcon className="w-3.5 h-3.5 text-pf-text-secondary mb-0.5" />
                <span className="text-xs font-medium text-pf-text-primary">{formatFilament(stats.totalFilamentUsedGrams ?? 0)}</span>
              </div>
            </div>
          )}
        </div>
      )}

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
        <div className="mt-4 w-52 flex flex-col bg-pf-bg-2/30 border border-pf-border rounded-md overflow-hidden">
          {/* Camera mode toggle - show if both snapshot and stream are available */}
          {hasCameraUrls && cameraSnapshotUrl && cameraStreamUrl && (
            <div className="flex gap-1 p-2 border-b border-pf-border bg-pf-bg-1/50">
              <Button
                type="button"
                onClick={() => setCameraMode('snapshot')}
                title="Snapshot"
                aria-label="Snapshot"
                variant={cameraMode === 'snapshot' ? 'primary' : 'secondary'}
                size="sm"
                className="flex-1"
                iconCenter={<ImageIcon className="h-4 w-4" />}
              >
              </Button>
              <Button
                type="button"
                onClick={() => setCameraMode('stream')}
                title="Stream"
                aria-label="Stream"
                variant={cameraMode === 'stream' ? 'primary' : 'secondary'}
                size="sm"
                className="flex-1"
                iconCenter={<VideoIcon className="h-4 w-4" />}
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
