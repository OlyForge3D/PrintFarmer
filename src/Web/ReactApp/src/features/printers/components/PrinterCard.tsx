if (!window.PrintFarmerDebug) {
  window.PrintFarmerDebug = {};
}
import { PrinterBackend } from '@/types/api';
import moonrakerIcon from '@/assets/moonraker.svg';
import octoprintIcon from '@/assets/octoprint.svg';
import type { Printer } from '@/types/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { usePrinterDisplay } from '@/common/hooks/usePrinterDisplay';
import { PrinterActionsDropdown } from './PrinterActionsDropdown';
import { Button } from '@/common/components/ui';
import { PlayIcon, PauseIcon, StopIcon, GearIcon } from '@/common/components/icons/MdiIcons';
import { useEffect, useState } from 'react';
import { apiClient } from '@/services/api';
import type { PrintJobStatusDto } from '@/types/api';
import { renderUnknown } from '@/common/utils/renderUnknown';
import { assetService } from '@/services/assetService';

interface PrinterCardProps {
  printer: Printer;
  viewMode?: 'grid' | 'list' | 'detailed';
  onEdit?: (printer: Printer) => void;
  onDelete?: (printer: Printer) => void;
  onManage?: (printer: Printer) => void;
}

export function PrinterCard({
  printer: printerProp,
  viewMode = 'grid',
  onEdit = () => { },
  onDelete = () => { },
  onManage = () => { }
}: PrinterCardProps) {
  const { hasPermission } = useAuth();
  // Merge with realtime SignalR updates
  const printer = usePrinterDisplay(printerProp);

  // State for printer cover image
  const [coverImageUrl, setCoverImageUrl] = useState<string | undefined>(undefined);

  // Load printer cover image on mount
  useEffect(() => {
    if (printer.manufacturerName && printer.modelName) {
      const url = assetService.getCoverImageUrl(printer.manufacturerName, printer.modelName);
      setCoverImageUrl(url);
    }
  }, [printer.manufacturerName, printer.modelName]);

  // If debug info exists on window, we may log it — avoid runtime exceptions
  useEffect(() => {
    try {
      const pfDebug = (window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug;
      const maybeSignalR = pfDebug && (pfDebug['printerSignalR'] as unknown);
      if (maybeSignalR && typeof maybeSignalR === 'object') {
        const lastStatuses = (maybeSignalR as Record<string, unknown>)['lastStatuses'];
        if (lastStatuses && typeof lastStatuses === 'object') {
          const debugEntry = (lastStatuses as Record<string, unknown>)[printer.id];
          if (debugEntry && typeof debugEntry === 'object') {
            const maybeTimestamp = (debugEntry as Record<string, unknown>)['timestamp'];
            const t = typeof maybeTimestamp === 'number' ? maybeTimestamp : Date.now();
            if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.printerCard) {
              console.debug('[PrinterCard] debug timestamp for printer', printer.id, t);
            }
          }
        }
      }
    } catch (err) {
      // ignore debug inspection errors
      void err;
    }
  }, [printer.id]);

  const renderDebugBadge = (): React.ReactNode => {
    // Guarded debug - emit to console for developers
    if (window.PrintFarmerDebug?.printerCard) {
      console.log('[PrintFarmer] PrinterCard:', { printerId: printer.id, printerName: printer.name });
    }

    // Render-time debug (enable by setting window.PrintFarmerDebug.printerCardRender = true)
    if (window.PrintFarmerDebug?.printerCardRender) {
      console.debug('[PrintFarmer] PrinterCard render', { printerId: printer.id });
    }

    // Optionally render a small debug badge in the UI
    if (window.PrintFarmerDebug?.printerCardDisplay) {
      // Render debug payload safely using renderUnknown
      const payload = {
        printer,
        currentStatus
      } as unknown;
      return <div className="text-xs text-pf-text-tertiary">{renderUnknown(payload)}</div>;
    }
    return null;
  };

  // Keep a single guarded console log point to avoid spam; prefer the render badge for payloads
  if (window.PrintFarmerDebug?.printerCard) {
    console.log('[PrintFarmer] PrinterCard:', { printerId: printer.id, printerName: printer.name });
  }

  // State for print job status (Moonraker only)
  const [printJobStatus, setPrintJobStatus] = useState<PrintJobStatusDto | null>(null);

  useEffect(() => {
    let ignore = false;
    if (printer.backend === PrinterBackend.Moonraker) {
      apiClient.getPrintJobStatus(printer.id).then(res => {
        if (!ignore) setPrintJobStatus(res);
      });
    } else {
      setPrintJobStatus(null);
    }
    return () => { ignore = true; };
  }, [printer.id, printer.backend]);

  // Prefer print job status for Moonraker, fallback to printer data
  const currentStatus = (() => {
    if (printer.backend === PrinterBackend.Moonraker && printJobStatus && printJobStatus.state && printJobStatus.state !== 'offline') {
      return {
        isOnline: printer.isOnline,
        state: printJobStatus.state,
        progress: printJobStatus.progress ?? printer.progress,
        jobName: printJobStatus.jobName ?? printer.jobName,
        thumbnailUrl: printJobStatus.thumbnailUrl ?? printer.thumbnailUrl,
        hotendTemp: printer.hotendTemp,
        bedTemp: printer.bedTemp,
        hotendTarget: printer.hotendTarget,
        bedTarget: printer.bedTarget,
        x: printer.x,
        y: printer.y,
        z: printer.z,
        cameraStreamUrl: printer.cameraStreamUrl,
        cameraSnapshotUrl: printer.cameraSnapshotUrl,
      };
    }
    // Default: use printer data directly (already contains merged realtime status from API)
    return {
      isOnline: printer.isOnline,
      state: printer.state,
      progress: printer.progress,
      jobName: printer.jobName,
      hotendTemp: printer.hotendTemp,
      bedTemp: printer.bedTemp,
      hotendTarget: printer.hotendTarget,
      bedTarget: printer.bedTarget,
      x: printer.x,
      y: printer.y,
      z: printer.z,
      cameraStreamUrl: printer.cameraStreamUrl,
      cameraSnapshotUrl: printer.cameraSnapshotUrl,
      thumbnailUrl: printer.thumbnailUrl,
    };
  })();

  const getStatusColor = (isOnline: boolean, state?: string) => {
    if (!isOnline) return 'bg-pf-status-offline-bg text-pf-status-offline-text border-pf-status-offline-border';

    switch (state?.toLowerCase()) {
      case 'printing':
        return 'bg-pf-status-online-bg text-pf-status-online-text border-pf-status-online-border';
      case 'paused':
        return 'bg-pf-warning text-pf-text-primary border-pf-border-light';
      case 'error':
        return 'bg-pf-error-bg text-pf-error-text border-pf-error-border';
      case 'ready':
      case 'idle':
      case 'operational':
        return 'bg-pf-loading text-pf-loading border-pf-loading-border';
      default:
        return 'bg-pf-border-medium text-pf-text-secondary border-pf-border';
    }
  };

  const getBackendIcon = (backend: PrinterBackend) => {
    switch (backend) {
      case PrinterBackend.Moonraker:
        return <img src={moonrakerIcon} alt="Moonraker" title="Moonraker" className="inline h-6 w-6 align-middle" />;
      case PrinterBackend.PrusaLink:
        return <span title="PrusaLink" aria-label="PrusaLink" role="img">🔗</span>;
      case PrinterBackend.SDCP:
        return <span title="SDCP" aria-label="SDCP" role="img">📡</span>;
      case PrinterBackend.OctoPrint:
        return <img src={octoprintIcon} alt="OctoPrint" title="OctoPrint" className="inline h-6 w-6 align-middle" />;
      default:
        return <span title="Other" aria-label="Other" role="img">🖨️</span>;
    }
  };

  const formatTemperature = (temp?: number, target?: number) => {
    if (temp === undefined && target === undefined) return null;

    if (target !== undefined) {
      return `${(temp || 0).toFixed(1)}°/${(target || 0).toFixed(1)}°`;
    }
    return `${(temp || 0).toFixed(1)}°`;
  };

  const formatPosition = (x?: number, y?: number, z?: number) => {
    const coords = [];
    if (x !== undefined) coords.push(`X${x.toFixed(1)}`);
    if (y !== undefined) coords.push(`Y${y.toFixed(1)}`);
    if (z !== undefined) coords.push(`Z${z.toFixed(1)}`);
    return coords.length > 0 ? coords.join(' ') : null;
  };

  if (viewMode === 'list') {
    return (
      <div className="bg-pf-bg-1 border border-pf-border rounded-xl p-4 hover:shadow-lg transition-all duration-200">
        <div className="flex items-center justify-between">
          {/* Left: Basic Info */}
          <div className="flex items-center space-x-4 min-w-0 flex-1">
            <div className="flex-shrink-0">
              <span className="text-2xl">{getBackendIcon(printer.backend)}</span>
            </div>

            <div className="min-w-0 flex-1">
              <div className="flex items-center space-x-2">
                <h3 className="text-lg font-bold text-pf-text-primary font-bebas uppercase truncate">
                  {printer.name}
                </h3>
                <span className={`inline-flex items-center px-3 py-1 rounded text-xs font-bold uppercase tracking-wide border ${getStatusColor(currentStatus.isOnline, currentStatus.state)}`}>
                  {currentStatus.isOnline ? (currentStatus.state || 'Unknown') : 'Offline'}
                </span>
              </div>
              <p className="text-sm text-pf-text-secondary truncate">
                {printer.manufacturerName} {printer.modelName} • {printer.ipAddress}
              </p>
            </div>
          </div>

          {/* Center: Status Info */}
          <div className="hidden md:flex items-center space-x-6 flex-shrink-0">
            {/* Progress */}
            {currentStatus.isOnline && currentStatus.progress !== undefined && currentStatus.progress > 0 && (
              <div className="text-center">
                <div className="text-xs text-pf-text-tertiary uppercase font-bold tracking-wide">Progress</div>
                <div className="text-sm font-semibold text-pf-text-primary">
                  {Math.round(currentStatus.progress)}%
                </div>
              </div>
            )}

            {/* Temperatures */}
            {currentStatus.isOnline && (currentStatus.hotendTemp || currentStatus.bedTemp) && (
              <div className="text-center">
                <div className="text-xs text-pf-text-tertiary uppercase font-bold tracking-wide">Temps</div>
                <div className="text-sm font-medium text-pf-text-primary">
                  {[
                    currentStatus.hotendTemp && `H${formatTemperature(currentStatus.hotendTemp, currentStatus.hotendTarget)}`,
                    currentStatus.bedTemp && `B${formatTemperature(currentStatus.bedTemp, currentStatus.bedTarget)}`
                  ].filter(Boolean).join(' ')}
                </div>
              </div>
            )}

            {/* Position */}
            {currentStatus.isOnline && formatPosition(currentStatus.x, currentStatus.y, currentStatus.z) && (
              <div className="text-center">
                <div className="text-xs text-pf-text-tertiary uppercase font-bold tracking-wide">Position</div>
                <div className="text-sm font-medium text-pf-text-primary">
                  {formatPosition(currentStatus.x, currentStatus.y, currentStatus.z)}
                </div>
              </div>
            )}
          </div>

          {/* Right: Actions */}
          <div className="flex items-center space-x-2 flex-shrink-0">
            {hasPermission('printers', 'execute') && currentStatus.isOnline && (
              <>
                {currentStatus.state === 'printing' && (
                  <Button
                    type="button"
                    aria-label="Pause print"
                    title="Pause print"
                    variant="subtle"
                    size="sm"
                    className="!p-2 !h-auto text-pf-warning"
                  >
                    <PauseIcon className="h-4 w-4" />
                  </Button>
                )}
                {(currentStatus.state === 'paused' || currentStatus.state === 'ready') && (
                  <Button
                    type="button"
                    aria-label="Resume print"
                    title="Resume print"
                    variant="subtle"
                    size="sm"
                    className="!p-2 !h-auto text-pf-success"
                  >
                    <PlayIcon className="h-4 w-4" />
                  </Button>
                )}
                <Button
                  type="button"
                  aria-label="Stop print"
                  title="Stop print"
                  variant="subtle"
                  size="sm"
                  className="!p-2 !h-auto text-pf-error"
                >
                  <StopIcon className="h-4 w-4" />
                </Button>
              </>
            )}

            {hasPermission('printers', 'update') && (
              <Button
                type="button"
                aria-label="Manage printer"
                title="Manage printer"
                variant="subtle"
                size="sm"
                className="!p-2 !h-auto text-pf-accent"
              >
                <GearIcon className="h-4 w-4" />
              </Button>
            )}
          </div>
        </div>

        {/* Progress bar for list view */}
        {currentStatus.isOnline && currentStatus.progress !== undefined && currentStatus.progress > 0 && (
          <div className="mt-3 pt-3 border-t border-pf-border">
            <div className="flex justify-between text-sm text-pf-text-secondary mb-1">
              <span className="truncate">{currentStatus.jobName || 'Printing...'}</span>
              <span>{Math.round(currentStatus.progress)}%</span>
            </div>
            <div className="w-full bg-pf-border-dark rounded-full h-2">
              <div className="bg-pf-border-dark h-2 rounded-full overflow-hidden">
                {(() => {
                  const pct = Math.max(0, Math.min(100, Math.round(currentStatus.progress)));
                  const step = Math.round(pct / 5) * 5;
                  const widthClass = `w-[${step}%]` as const;
                  return <>
                    <span className="sr-only">Progress {pct}%</span>
                    <div className={`h-2 bg-pf-success transition-all duration-300 ${widthClass}`} data-progress-bar aria-hidden="true" />
                  </>;
                })()}
              </div>
            </div>
          </div>
        )}
      </div>
    );
  }

  // Grid view (default)
  return (
    <div className="bg-pf-bg-1 overflow-hidden border border-pf-border rounded-xl hover:shadow-lg transition-all duration-200 relative">
      {/* Header */}
      <div className="px-4 py-5 sm:p-6">
        {renderDebugBadge()}
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center min-w-0 flex-1">
            <span className="text-2xl mr-3 flex-shrink-0">{getBackendIcon(printer.backend)}</span>
            <div className="min-w-0 flex-1">
              <h3 className="text-lg font-bold text-pf-text-primary font-bebas uppercase truncate">
                {printer.name}
              </h3>
              <p className="text-sm text-pf-text-secondary truncate">
                {printer.manufacturerName} {printer.modelName}
              </p>
            </div>
          </div>

          {hasPermission('printers', 'update') && (
            <PrinterActionsDropdown
              printer={printer}
              onEdit={onEdit}
              onDelete={() => onDelete(printer)}
            />
          )}
        </div>

        {/* Status Badge */}
        <div className="mb-3">
          <span className={`inline-flex items-center px-3 py-1 rounded text-xs font-bold uppercase tracking-wide border ${getStatusColor(currentStatus.isOnline, currentStatus.state)}`}>
            {currentStatus.isOnline ? (currentStatus.state || 'Unknown') : 'Offline'}
          </span>
        </div>

        {/* Printer Cover Image */}
        {coverImageUrl && (
          <div className="mb-4">
            <img
              src={coverImageUrl}
              alt={`${printer.name} cover`}
              className="w-full h-32 object-cover rounded border border-pf-border"
              onError={(e) => {
                (e.target as HTMLImageElement).style.display = 'none';
              }}
            />
          </div>
        )}

        {/* Progress Bar */}
        {currentStatus.isOnline && currentStatus.progress !== undefined && currentStatus.progress > 0 && (
          <div className="mb-4">
            <div className="flex justify-between text-sm text-pf-text-secondary mb-1">
              <span className="truncate">{currentStatus.jobName || 'Printing...'}</span>
              <span>{Math.round(currentStatus.progress)}%</span>
            </div>
            <div className="w-full bg-pf-border-dark rounded-full h-2">
              <div className="bg-pf-border-dark h-2 rounded-full overflow-hidden">
                {(() => {
                  const pct = Math.max(0, Math.min(100, Math.round(currentStatus.progress)));
                  const step = Math.round(pct / 5) * 5;
                  const widthClass = `w-[${step}%]` as const;
                  return <>
                    <span className="sr-only">Progress {pct}%</span>
                    <div className={`h-2 bg-pf-success transition-all duration-300 ${widthClass}`} data-progress-bar aria-hidden="true" />
                  </>;
                })()}
              </div>
            </div>
          </div>
        )}

        {/* Temperature Display */}
        {currentStatus.isOnline && (currentStatus.hotendTemp !== undefined || currentStatus.bedTemp !== undefined) && (
          <div className="mb-4 grid grid-cols-2 gap-4">
            {currentStatus.hotendTemp !== undefined && (
              <div className="text-center">
                <div className="text-xs text-pf-text-tertiary uppercase font-bold tracking-wide">Hotend</div>
                <div className="text-lg font-semibold text-pf-text-primary">
                  {formatTemperature(currentStatus.hotendTemp, currentStatus.hotendTarget)}
                </div>
              </div>
            )}

            {currentStatus.bedTemp !== undefined && (
              <div className="text-center">
                <div className="text-xs text-pf-text-tertiary uppercase font-bold tracking-wide">Bed</div>
                <div className="text-lg font-semibold text-pf-text-primary">
                  {formatTemperature(currentStatus.bedTemp, currentStatus.bedTarget)}
                </div>
              </div>
            )}
          </div>
        )}

        {/* Position Display */}
        {currentStatus.isOnline && formatPosition(currentStatus.x, currentStatus.y, currentStatus.z) && (
          <div className="mb-4">
            <div className="text-xs text-pf-text-tertiary uppercase font-bold tracking-wide text-center">Position</div>
            <div className="text-sm font-medium text-pf-text-primary text-center">
              {formatPosition(currentStatus.x, currentStatus.y, currentStatus.z)}
            </div>
          </div>
        )}

        {/* Camera thumbnail (OctoPrint/Moonraker/PrusaLink) */}
        {(printer.cameraSnapshotUrl || printer.cameraStreamUrl) && (
          <div className="mb-4">
            <img
              src={printer.cameraSnapshotUrl || printer.cameraStreamUrl}
              alt={`${printer.name} camera`}
              className="w-full h-24 object-cover rounded border"
              onError={(e) => {
                (e.target as HTMLImageElement).style.display = 'none';
              }}
            />
            {/* For OctoPrint, optionally show a note if only stream is available */}
            {printer.backend === PrinterBackend.OctoPrint && !printer.cameraSnapshotUrl && printer.cameraStreamUrl && (
              <div className="text-xs text-pf-text-tertiary mt-1">Live stream only (no snapshot)</div>
            )}
          </div>
        )}

        {/* Action buttons */}
        <div className="flex space-x-2">
          <Button
            type="button"
            onClick={() => onManage(printer)}
            variant="secondary"
            size="sm"
            className="flex-1 justify-center flex items-center gap-2"
          >
            <GearIcon className="h-4 w-4" />
            <span>Manage</span>
          </Button>
          {/* OctoPrint/Moonraker/PrusaLink: Only show controls if supported */}
          {hasPermission('printers', 'execute') && currentStatus.isOnline && (
            <>
              {/* Only show Pause if printer is printing and backend supports it */}
              {[PrinterBackend.Moonraker, PrinterBackend.PrusaLink, PrinterBackend.OctoPrint].includes(printer.backend) && currentStatus.state === 'printing' && (
                <Button variant="danger" size="sm" className="inline-flex items-center gap-2">
                  <PauseIcon className="h-4 w-4" />
                  <span>Pause</span>
                </Button>
              )}
              {/* Only show Resume/Start if printer is paused or ready and backend supports it */}
              {[PrinterBackend.Moonraker, PrinterBackend.PrusaLink, PrinterBackend.OctoPrint].includes(printer.backend) && (currentStatus.state === 'paused' || currentStatus.state === 'ready') && (
                <Button variant="success" size="sm" className="inline-flex items-center gap-2">
                  <PlayIcon className="h-4 w-4" />
                  <span>{currentStatus.state === 'paused' ? 'Resume' : 'Start'}</span>
                </Button>
              )}
            </>
          )}
        </div>

        {/* Notes */}
        {printer.notes && (
          <div className="mt-3 pt-3 border-t border-pf-border">
            <p className="text-xs text-pf-text-tertiary italic">{printer.notes}</p>
          </div>
        )}
      </div>
    </div>
  );
}