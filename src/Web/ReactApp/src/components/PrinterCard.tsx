import { PrinterBackend } from '@/types/api';
import type { Printer } from '@/types/api';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { useAuth } from '@/contexts/AuthContext';
import { 
  MoreVertical,
  Cog,
  Play,
  Pause,
  Square as StopIcon 
} from 'lucide-react';
import { formatDistanceToNow } from 'date-fns';

interface PrinterCardProps {
  printer: Printer;
  viewMode?: 'grid' | 'list' | 'detailed';
}

export function PrinterCard({ printer, viewMode = 'grid' }: PrinterCardProps) {
  const { hasPermission } = useAuth();
  const { getPrinterStatus } = usePrinterStatusUpdates();
  const realtimeStatus = getPrinterStatus(printer.id);

  // Use realtime status if available, otherwise use the printer data
  const currentStatus = {
    isOnline: realtimeStatus?.isOnline ?? printer.isOnline,
    state: realtimeStatus?.state ?? printer.state,
    progress: realtimeStatus?.progress ?? printer.progress,
    jobName: realtimeStatus?.jobName ?? printer.jobName,
    hotendTemp: realtimeStatus?.hotendTemp ?? printer.hotendTemp,
    bedTemp: realtimeStatus?.bedTemp ?? printer.bedTemp,
    hotendTarget: realtimeStatus?.hotendTarget ?? printer.hotendTarget,
    bedTarget: realtimeStatus?.bedTarget ?? printer.bedTarget,
    x: realtimeStatus?.x ?? printer.x,
    y: realtimeStatus?.y ?? printer.y,
    z: realtimeStatus?.z ?? printer.z,
    cameraStreamUrl: realtimeStatus?.cameraStreamUrl ?? printer.cameraStreamUrl,
    cameraSnapshotUrl: realtimeStatus?.cameraSnapshotUrl ?? printer.cameraSnapshotUrl,
    thumbnailUrl: realtimeStatus?.thumbnailUrl ?? printer.thumbnailUrl,
  };

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
        return '🌙';
      case PrinterBackend.PrusaLink:
        return '🔗';
      case PrinterBackend.SDCP:
        return '📡';
      default:
        return '🖨️';
    }
  };

  const formatTemperature = (temp?: number, target?: number) => {
    if (temp === undefined && target === undefined) return null;
    
    if (target !== undefined) {
      return `${Math.round(temp || 0)}°/${Math.round(target)}°`;
    }
    return `${Math.round(temp || 0)}°`;
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
                  <button className="p-2 text-pf-text-secondary hover:text-pf-warning bg-pf-panel hover:bg-pf-bg-2 border border-pf-border-light rounded-md transition-colors">
                    <Pause className="h-4 w-4" />
                  </button>
                )}
                {(currentStatus.state === 'paused' || currentStatus.state === 'ready') && (
                  <button className="p-2 text-pf-text-secondary hover:text-pf-success bg-pf-panel hover:bg-pf-bg-2 border border-pf-border-light rounded-md transition-colors">
                    <Play className="h-4 w-4" />
                  </button>
                )}
                <button className="p-2 text-pf-text-secondary hover:text-pf-error bg-pf-panel hover:bg-pf-bg-2 border border-pf-border-light rounded-md transition-colors">
                  <StopIcon className="h-4 w-4" />
                </button>
              </>
            )}
            
            {hasPermission('printers', 'update') && (
              <button className="p-2 text-pf-text-secondary hover:text-pf-accent bg-pf-panel hover:bg-pf-bg-2 border border-pf-border-light rounded-md transition-colors">
                <Cog className="h-4 w-4" />
              </button>
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
              <div
                className="bg-pf-success h-2 rounded-full transition-all duration-300"
                style={{ width: `${currentStatus.progress}%` }}
              />
            </div>
          </div>
        )}
      </div>
    );
  }

  // Grid view (default)
  return (
    <div className="bg-pf-bg-1 overflow-hidden border border-pf-border rounded-xl hover:shadow-lg transition-all duration-200">
      {/* Header */}
      <div className="px-4 py-5 sm:p-6">
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
            <button className="flex-shrink-0 text-pf-text-tertiary hover:text-pf-accent transition-colors">
              <MoreVertical className="w-5 h-5" />
            </button>
          )}
        </div>

        {/* Status Badge */}
        <div className="mb-3">
          <span className={`inline-flex items-center px-3 py-1 rounded text-xs font-bold uppercase tracking-wide border ${getStatusColor(currentStatus.isOnline, currentStatus.state)}`}>
            {currentStatus.isOnline ? (currentStatus.state || 'Unknown') : 'Offline'}
          </span>
        </div>

        {/* Progress Bar */}
        {currentStatus.isOnline && currentStatus.progress !== undefined && currentStatus.progress > 0 && (
          <div className="mb-4">
            <div className="flex justify-between text-sm text-pf-text-secondary mb-1">
              <span className="truncate">{currentStatus.jobName || 'Printing...'}</span>
              <span>{Math.round(currentStatus.progress)}%</span>
            </div>
            <div className="w-full bg-pf-border-dark rounded-full h-2">
              <div
                className="bg-pf-success h-2 rounded-full transition-all duration-300"
                style={{ width: `${currentStatus.progress}%` }}
              />
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

        {/* Camera thumbnail */}
        {printer.cameraSnapshotUrl && (
          <div className="mb-4">
            <img
              src={printer.cameraSnapshotUrl}
              alt={`${printer.name} camera`}
              className="w-full h-24 object-cover rounded border"
              onError={(e) => {
                (e.target as HTMLImageElement).style.display = 'none';
              }}
            />
          </div>
        )}

        {/* Action buttons */}
        <div className="flex space-x-2">
          <button className="flex-1 inline-flex items-center justify-center px-3 py-2 border border-pf-border-light shadow-sm text-sm leading-4 font-medium rounded-md text-pf-text-primary bg-pf-panel hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-accent-2 transition-colors">
            <Cog className="h-4 w-4 mr-1.5" />
            Manage
          </button>
          
          {hasPermission('printers', 'execute') && currentStatus.isOnline && (
            <>
              {currentStatus.state === 'printing' && (
                <button className="inline-flex items-center px-3 py-2 border border-transparent text-sm leading-4 font-medium rounded-md text-white bg-pf-warning hover:bg-pf-warning focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-warning transition-colors">
                  <Pause className="h-4 w-4 mr-1.5" />
                  Pause
                </button>
              )}
              {(currentStatus.state === 'paused' || currentStatus.state === 'ready') && (
                <button className="inline-flex items-center px-3 py-2 border border-transparent text-sm leading-4 font-medium rounded-md text-white bg-pf-success hover:bg-pf-success-hover focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-pf-success transition-colors">
                  <Play className="h-4 w-4 mr-1.5" />
                  {currentStatus.state === 'paused' ? 'Resume' : 'Start'}
                </button>
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