import React, { useRef, useState } from 'react';
import { PanelRightOpen } from 'lucide-react';
import {
  HistoryIcon,
  FileIcon,
  RefreshIcon,
  PauseIcon, 
  PlayIcon, 
  EmergencyStopIcon, 
  XCircleIcon,
  EditIcon,
  CameraIcon,
  ExternalLinkIcon,
  ImageIcon,
  VideoIcon
} from '@/common/components/icons/MdiIcons';
import { Button, Toggle } from '@/common/components/ui';
import { ControlPadButton } from '@/common/components/ui/ControlPadButton';
import { PrinterHistoryModal } from '@/features/printers/components/PrinterHistoryModal';
import { PrinterFilesModal } from '@/features/printers/components/PrinterFilesModal';
import { PrinterBackend, type Printer, type PrinterBackendCapabilitiesDto } from '@/types/api';
import { apiClient } from '@/services/api';
import { useAutoPrintStatus, useSetAutoPrintEnabled } from '@/features/printers/hooks/useAutoPrint';
import { toast } from 'sonner';
import {
  canCancel,
  canEmergencyStop,
  canOpenFiles,
  canOpenHistory,
  canPauseOrResume,
  getPrinterSupport,
} from '@/features/printers/utils/printerSupport';

interface CollapsedPrinterCardProps {
  printer: Printer;
  backendCapabilities?: PrinterBackendCapabilitiesDto;
  onExpand: () => void;
  onEdit?: (printer: Printer) => void;
}

export function CollapsedPrinterCard({
  printer: printerProp,
  backendCapabilities,
  onExpand,
  onEdit
}: CollapsedPrinterCardProps) {
  // Merge with realtime SignalR updates
  const printer = printerProp; // printerProp already includes display data
  const [showCamera, setShowCamera] = useState(false);
  const [cameraMode, setCameraMode] = useState<'snapshot' | 'stream'>('snapshot');
  const [showHistory, setShowHistory] = useState(false);
  const [showFiles, setShowFiles] = useState(false);
  const [controlActionPending, setControlActionPending] = useState(false);
  const collapsedProgressRef = useRef<HTMLDivElement>(null);

  // Auto-print status
  const { data: autoPrintStatus } = useAutoPrintStatus(printer.id);
  const setAutoPrintEnabled = useSetAutoPrintEnabled();

  const handleAutoPrintToggle = async () => {
    const newEnabled = !(autoPrintStatus?.autoPrintEnabled ?? false);
    try {
      await setAutoPrintEnabled.mutateAsync({ printerId: printer.id, enabled: newEnabled });
      toast.success(newEnabled ? 'Auto-print enabled' : 'Auto-print disabled');
    } catch {
      toast.error('Failed to toggle auto-print');
    }
  };

  // Use printer data directly (already contains merged realtime status from API)
  const isOnline = printer.isOnline ?? false;
  const isEnabled = printer.isEnabled ?? true;
  const state = printer.state ?? 'Unknown';
  const isPrinting = state.toLowerCase().includes('printing');
  const isPaused = state.toLowerCase().includes('paused');
  const isShutdown = state.toLowerCase().includes('shutdown') || state.toLowerCase().includes('error');

  const support = getPrinterSupport(backendCapabilities, {
    supportsHistory: printer.backend === PrinterBackend.Moonraker || printer.backend === PrinterBackend.OctoPrint,
  });

  const canPauseOrResumeNow = canPauseOrResume({ isOnline, isEnabled, isPrinting, isPaused, support });
  const canCancelNow = canCancel({ isOnline, isEnabled, isPrinting, isPaused, support });
  const canEmergencyStopNow = canEmergencyStop({ isOnline, isEnabled, support });
  const canOpenFilesNow = canOpenFiles({ isOnline, isEnabled, support });
  const canOpenHistoryNow = canOpenHistory({ isOnline, isEnabled, support });
  // Check if printer has camera URLs - just verify if URLs have values from database
  const cameraSnapshotUrl = printer.cameraSnapshotUrl;
  const cameraStreamUrl = printer.cameraStreamUrl;
  const hasCameraUrls = !!(cameraSnapshotUrl || cameraStreamUrl);

  const statusDotClasses = (() => {
    if (!isOnline) return 'bg-slate-400';
    if (isPrinting) return 'bg-pf-success-bg';
    if (isPaused) return 'bg-yellow-500';
    if (isShutdown) return 'bg-red-500';
    return 'bg-blue-500';
  })();

  const toCamelCase = (str: string): string => {
    return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
  };

  const handleControlAction = async (action: 'pause' | 'resume' | 'cancel' | 'stop' | 'firmware-restart') => {
    if (controlActionPending) {
      return;
    }

    setControlActionPending(true);
    try {
      if (typeof window !== 'undefined' && (window as { PrintFarmerDebug?: { printerActions?: boolean } }).PrintFarmerDebug?.printerActions) {
        console.log(`Performing ${action} on printer ${printer.id}`);
      }

      switch (action) {
        case 'pause':
          await apiClient.pausePrint(printer.id);
          break;
        case 'resume':
          await apiClient.resumePrint(printer.id);
          break;
        case 'cancel':
          await apiClient.cancelPrint(printer.id);
          break;
        case 'stop':
          await apiClient.emergencyStop(printer.id);
          break;
        case 'firmware-restart':
          await apiClient.firmwareRestart(printer.id);
          break;
        default: {
          const exhaustiveCheck: never = action;
          return exhaustiveCheck;
        }
      }
    } catch (error) {
      console.error(`Error performing ${action}:`, error);
    } finally {
      setControlActionPending(false);
    }
  };

  const handleViewHistory = () => {
    setShowHistory(true);
  };

  return (
    <div className="relative rounded-xl p-3 shadow-lg bg-pf-card border border-white/10 w-full">
      {/* Top row: Name + Status Pill */}
      <div className="flex justify-between items-center mb-2 gap-2">
        <div className="flex-1 min-w-0">
          <div className="font-bold text-lg text-pf-text-primary font-bebas uppercase tracking-wide truncate">
            {printer.name}
          </div>
          {(printer.modelName) && (
            <div className="text-pf-text-secondary text-xs truncate">
              {`${printer.modelName || ''}`.trim()}
            </div>
          )}
        </div>
        
        {/* Status chip (enterprise/subtle) */}
        <div className="inline-flex items-center gap-1.5 px-2 py-1 rounded-full text-xs font-medium shrink-0 bg-white/[0.04] border border-white/10 text-pf-text-primary">
          <span className={`h-2 w-2 rounded-full ${statusDotClasses}`} aria-hidden />
          <span className="text-pf-text-secondary">
            {isOnline ? toCamelCase(state) : 'Offline'}
          </span>
        </div>
      </div>

      {/* Subtle separator above actions */}
      <div className="h-px w-full bg-white/10 mb-2" aria-hidden />

      {/* Action buttons row */}
      <div
        className="flex w-full items-center justify-between mb-2"
        role="toolbar"
        aria-label="Printer actions"
      >
        {/* Details/Sidebar button */}
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={onExpand}
          className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
          title="Open details sidebar"
          aria-label="Open details sidebar"
          iconCenter={<PanelRightOpen className="h-4 w-4" />}
        >
        </Button>
        
        {/* External link */}
        <a 
          href={printer.frontendUrl} 
          target="_blank" 
          rel="noopener noreferrer"
          className="text-pf-text-secondary hover:text-pf-text-primary shrink-0 h-8 w-8 inline-flex items-center justify-center rounded-xs"
          aria-label={`Open printer ${printer.name} in new tab`}
          title={`Open printer ${printer.name}`}
        >
          <ExternalLinkIcon className="h-4 w-4" />
        </a>
        
        {/* Camera button */}
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={() => setShowCamera(!showCamera)}
          disabled={!hasCameraUrls || !isEnabled}
          className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
          aria-label={showCamera ? 'Hide camera stream' : 'Show camera stream'}
          title={!isEnabled ? 'Printer disabled' : hasCameraUrls ? 'Camera available' : 'No camera configured'}
          iconCenter={<CameraIcon className="h-4 w-4" />}
        >
        </Button>
        
        {/* History button - only show for backends that support it */}
        {support.supportsHistory && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={handleViewHistory}
            disabled={!canOpenHistoryNow}
            className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
            title="View print history"
            aria-label="View print history"
            iconCenter={<HistoryIcon className="h-4 w-4" />}
          >
          </Button>
        )}
        
        {/* Files button */}
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={() => setShowFiles(true)}
          disabled={!canOpenFilesNow}
          className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
          title="View printer files"
          aria-label="View printer files"
          iconCenter={<FileIcon className="h-4 w-4" />}
        >
        </Button>
        
        {/* Edit button */}
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={() => onEdit?.(printer)}
          className="h-8 w-8 p-0 text-pf-text-secondary enabled:hover:text-pf-text-primary"
          title="Edit details"
          aria-label="Edit details"
          iconCenter={<EditIcon className="h-4 w-4" />}
        >
        </Button>
      </div>

      {/* Control buttons */}
      <div className="flex items-center gap-1 mb-2">
        <ControlPadButton
          disabled={controlActionPending || !canPauseOrResumeNow}
          onClick={() => handleControlAction(isPaused ? 'resume' : 'pause')}
          title={isPaused ? 'Resume' : 'Pause'}
          padSize="small"
        >
          {isPaused ? <PlayIcon className="h-4 w-4" /> : <PauseIcon className="h-4 w-4" />}
        </ControlPadButton>
        <ControlPadButton
          disabled={controlActionPending || !canCancelNow}
          onClick={() => handleControlAction('cancel')}
          title="Cancel"
          padSize="small"
        >
          <XCircleIcon className="h-4 w-4" ariaLabel="Cancel" />
        </ControlPadButton>
        <ControlPadButton
          variant={isShutdown ? 'secondary' : 'danger'}
          disabled={controlActionPending || !canEmergencyStopNow}
          onClick={() => handleControlAction(isShutdown ? 'firmware-restart' : 'stop')}
          title={isShutdown ? "Firmware Restart" : "Emergency Stop"}
          padSize="small"
        >
          {isShutdown ? <RefreshIcon className="h-4 w-4" /> : <EmergencyStopIcon className="h-4 w-4" />}
        </ControlPadButton>
      </div>

      {/* Auto-print toggle */}
      <div className="flex items-center justify-between mb-2 px-1">
        <span className="text-xs text-pf-text-secondary">Auto-print</span>
        <Toggle
          checked={autoPrintStatus?.autoPrintEnabled ?? false}
          onChange={handleAutoPrintToggle}
          disabled={setAutoPrintEnabled.isPending}
          size="sm"
          aria-label={`Toggle auto-print for ${printer.name}`}
        />
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
              aria-label="Print progress"
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
          <div className="w-full aspect-video bg-pf-bg-0 flex items-center justify-center overflow-hidden">
            {hasCameraUrls ? (
              cameraMode === 'snapshot' && cameraSnapshotUrl ? (
                <img 
                  src={cameraSnapshotUrl}
                  alt="webcam snapshot"
                  className="w-full h-full object-cover"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : cameraMode === 'stream' && cameraStreamUrl ? (
                <img 
                  src={cameraStreamUrl}
                  alt="webcam stream"
                  className="w-full h-full object-cover"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : cameraSnapshotUrl ? (
                <img 
                  src={cameraSnapshotUrl}
                  alt="webcam snapshot"
                  className="w-full h-full object-cover"
                  onError={() => {}}
                  onLoad={() => {}}
                />
              ) : cameraStreamUrl ? (
                <img 
                  src={cameraStreamUrl}
                  alt="webcam stream"
                  className="w-full h-full object-cover"
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
