import { useState } from 'react';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import type { Printer } from '@/types/api';
import { 
  ChevronDown, 
  ChevronUp, 
  ExternalLink,
  Edit,
  History,
  Camera,
  Play,
  Pause,
  Square
} from 'lucide-react';

interface ExpandablePrinterCardProps {
  printer: Printer;
  onEdit?: (printer: Printer) => void;
  onDelete?: (printer: Printer) => void;
  onManage?: (printer: Printer) => void;
}

export function ExpandablePrinterCard({ printer, onEdit, onDelete, onManage }: ExpandablePrinterCardProps) {
  const [isExpanded, setIsExpanded] = useState(false);
  const [showCamera, setShowCamera] = useState(false);
  const { getPrinterStatus } = usePrinterStatusUpdates();
  
  const status = getPrinterStatus(printer.id);
  const isOnline = status?.isOnline ?? printer.isOnline;
  const state = status?.state ?? printer.state;
  const isPrinting = state === 'printing';
  const isPaused = state === 'paused';

  const formatTemp = (temp: number | null | undefined): string => {
    if (temp === null || temp === undefined) return '--';
    return Math.round(temp).toString();
  };

  const statusColor = isOnline ? 
    (isPrinting ? 'text-green-600 bg-green-50' : 'text-blue-600 bg-blue-50') : 
    'text-red-600 bg-red-50';

  const handleToggleExpand = () => {
    setIsExpanded(!isExpanded);
  };

  const handleControlAction = (action: string) => {
    console.log(`${action} action for printer:`, printer.name);
    // TODO: Implement printer control actions
  };

  if (!isExpanded) {
    // Collapsed view - similar to Blazor collapsed card
    return (
      <div className="bg-pf-panel border border-pf-border rounded-lg shadow-sm hover:shadow-md transition-shadow">
        <div className="p-4">
          <div className="flex items-center justify-between">
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-3">
                <div className="flex-1">
                  <h3 className="text-lg font-semibold text-pf-text-primary font-bebas uppercase">
                    {printer.name}
                  </h3>
                  {(printer.manufacturerName || printer.modelName) && (
                    <p className="text-sm text-pf-text-secondary">
                      {`${printer.manufacturerName || ''} ${printer.modelName || ''}`.trim()}
                    </p>
                  )}
                  <div className="flex items-center gap-2 mt-1">
                    <span className="text-xs text-pf-text-secondary font-mono">
                      {printer.serverUrl}
                    </span>
                    <a 
                      href={printer.serverUrl} 
                      target="_blank" 
                      rel="noopener noreferrer"
                      className="text-pf-text-secondary hover:text-pf-text-primary"
                    >
                      <ExternalLink className="h-3 w-3" />
                    </a>
                  </div>
                </div>
                
                <div className="text-right">
                  <div className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ${statusColor}`}>
                    {isOnline ? (state || 'Online') : 'Offline'}
                  </div>
                  {isOnline && isPrinting && status?.progress && (
                    <div className="mt-1 text-xs text-pf-text-secondary">
                      {Math.round(status.progress)}%
                    </div>
                  )}
                </div>
              </div>

              {/* Compact controls for collapsed view */}
              <div className="flex items-center justify-between mt-3">
                <div className="flex items-center gap-2">
                  <button
                    onClick={() => handleControlAction('pause')}
                    disabled={!isPrinting}
                    className="inline-flex items-center px-2 py-1 text-xs font-medium border border-pf-border rounded hover:bg-pf-bg-2 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    <Pause className="h-3 w-3 mr-1" />
                    Pause
                  </button>
                  <button
                    onClick={() => handleControlAction('resume')}
                    disabled={!isPaused}
                    className="inline-flex items-center px-2 py-1 text-xs font-medium border border-pf-border rounded hover:bg-pf-bg-2 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    <Play className="h-3 w-3 mr-1" />
                    Resume
                  </button>
                  <button
                    onClick={() => handleControlAction('stop')}
                    disabled={!isOnline}
                    className="inline-flex items-center px-2 py-1 text-xs font-medium border border-red-300 text-red-700 rounded hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    <Square className="h-3 w-3 mr-1" />
                    Stop
                  </button>
                </div>

                <div className="flex items-center gap-1">
                  <button
                    onClick={() => onEdit?.(printer)}
                    className="p-1 text-pf-text-secondary hover:text-pf-text-primary"
                    title="Edit printer"
                  >
                    <Edit className="h-4 w-4" />
                  </button>
                  <button
                    onClick={handleToggleExpand}
                    className="p-1 text-pf-text-secondary hover:text-pf-text-primary"
                    title="Expand card"
                  >
                    <ChevronDown className="h-4 w-4" />
                  </button>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    );
  }

  // Expanded view - similar to Blazor expanded card with full controls
  return (
    <div className="bg-pf-panel border border-pf-border rounded-lg shadow-sm">
      <div className="p-6">
        {/* Header */}
        <div className="flex items-center justify-between mb-6">
          <div className="flex-1">
            <div className="flex items-center gap-4">
              <div className="flex-1">
                <h3 className="text-xl font-bold text-pf-text-primary font-bebas uppercase">
                  {printer.name}
                </h3>
                {(printer.manufacturerName || printer.modelName) && (
                  <p className="text-sm text-pf-text-secondary mt-1">
                    {`${printer.manufacturerName || ''} ${printer.modelName || ''}`.trim()}
                  </p>
                )}
                <div className="flex items-center gap-2 mt-2">
                  <span className="text-sm text-pf-text-secondary font-mono">
                    {printer.serverUrl}
                  </span>
                  <a 
                    href={printer.serverUrl} 
                    target="_blank" 
                    rel="noopener noreferrer"
                    className="text-pf-text-secondary hover:text-pf-text-primary"
                  >
                    <ExternalLink className="h-4 w-4" />
                  </a>
                  {printer.cameraStreamUrl && (
                    <button
                      onClick={() => setShowCamera(!showCamera)}
                      className="text-pf-text-secondary hover:text-pf-text-primary"
                      title={showCamera ? 'Hide camera' : 'Show camera'}
                    >
                      <Camera className="h-4 w-4" />
                    </button>
                  )}
                </div>
                
                {/* Camera snapshot */}
                {showCamera && printer.cameraStreamUrl && (
                  <div className="mt-3">
                    <img 
                      src={printer.cameraStreamUrl} 
                      alt="Printer camera" 
                      className="rounded border border-pf-border max-w-xs"
                      onError={(e) => {
                        e.currentTarget.style.display = 'none';
                      }}
                    />
                  </div>
                )}
              </div>
              
              <div className="text-right">
                <div className={`inline-flex items-center px-3 py-2 rounded-full text-sm font-medium ${statusColor}`}>
                  {isOnline ? (state || 'Online') : 'Offline'}
                </div>
                {isOnline && isPrinting && status?.progress && (
                  <div className="mt-2">
                    <div className="text-sm font-medium text-pf-text-primary">
                      {Math.round(status.progress)}%
                    </div>
                    <div className="w-24 bg-gray-200 rounded-full h-2 mt-1">
                      <div 
                        className="bg-blue-600 h-2 rounded-full" 
                        style={{ width: `${status.progress}%` }}
                      />
                    </div>
                  </div>
                )}
              </div>
            </div>
          </div>
          
          <div className="flex items-center gap-2 ml-4">
            <button
              onClick={() => console.log('History for', printer.name)}
              className="p-2 text-pf-text-secondary hover:text-pf-text-primary"
              title="View print history"
            >
              <History className="h-4 w-4" />
            </button>
            <button
              onClick={() => onEdit?.(printer)}
              className="p-2 text-pf-text-secondary hover:text-pf-text-primary"
              title="Edit printer"
            >
              <Edit className="h-4 w-4" />
            </button>
            <button
              onClick={handleToggleExpand}
              className="p-2 text-pf-text-secondary hover:text-pf-text-primary"
              title="Collapse card"
            >
              <ChevronUp className="h-4 w-4" />
            </button>
          </div>
        </div>

        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
          {/* Temperature Controls */}
          <div className="space-y-4">
            <h4 className="text-sm font-medium text-pf-text-primary uppercase tracking-wide">Temperature</h4>
            
            <div className="space-y-3">
              <div className="flex items-center gap-4">
                <label className="w-16 text-sm text-pf-text-secondary">Hotend</label>
                <input 
                  type="number" 
                  className="flex-1 px-3 py-2 border border-pf-border rounded-md text-sm"
                  placeholder="°C"
                />
                <span className="text-xs text-pf-text-secondary min-w-[80px]">
                  [{formatTemp(status?.hotendTemp)} → {formatTemp(status?.hotendTarget)}]
                </span>
              </div>
              
              <div className="flex items-center gap-4">
                <label className="w-16 text-sm text-pf-text-secondary">Bed</label>
                <input 
                  type="number" 
                  className="flex-1 px-3 py-2 border border-pf-border rounded-md text-sm"
                  placeholder="°C"
                />
                <span className="text-xs text-pf-text-secondary min-w-[80px]">
                  [{formatTemp(status?.bedTemp)} → {formatTemp(status?.bedTarget)}]
                </span>
              </div>
              
              <button className="px-4 py-2 text-sm font-medium bg-pf-accent text-white rounded-md hover:bg-pf-accent-dark">
                SET TEMPS
              </button>
            </div>

            {/* Temperature Presets */}
            <div className="flex flex-wrap gap-2 mt-4">
              {['ABS', 'ASA', 'PLA', 'PC', 'PCTG', 'PETG'].map((material) => (
                <button
                  key={material}
                  className="px-3 py-1 text-xs font-medium border border-pf-border rounded hover:bg-pf-bg-2"
                  disabled={isPrinting}
                >
                  {material}
                </button>
              ))}
              <button
                className="px-3 py-1 text-xs font-medium border border-blue-300 text-blue-700 rounded hover:bg-blue-50"
                disabled={isPrinting}
                title="Cooldown"
              >
                ❄
              </button>
            </div>
          </div>

          {/* Movement Controls */}
          <div className="space-y-4">
            <h4 className="text-sm font-medium text-pf-text-primary uppercase tracking-wide">Movement</h4>
            
            <div className="grid grid-cols-3 gap-2 max-w-[200px]">
              <div></div>
              <button className="p-2 border border-pf-border rounded hover:bg-pf-bg-2" disabled={isPrinting}>▲</button>
              <div></div>
              <button className="p-2 border border-pf-border rounded hover:bg-pf-bg-2" disabled={isPrinting}>◀</button>
              <button className="p-2 border border-pf-border rounded hover:bg-pf-bg-2" disabled={isPrinting} title="Home XY">🏠</button>
              <button className="p-2 border border-pf-border rounded hover:bg-pf-bg-2" disabled={isPrinting}>▶</button>
              <div></div>
              <button className="p-2 border border-pf-border rounded hover:bg-pf-bg-2" disabled={isPrinting}>▼</button>
              <div></div>
            </div>

            <div className="flex gap-2 mt-4">
              <button className="px-3 py-1 text-xs border border-pf-border rounded hover:bg-pf-bg-2" disabled={isPrinting}>Z+</button>
              <button className="px-3 py-1 text-xs border border-pf-border rounded hover:bg-pf-bg-2" disabled={isPrinting} title="Home Z">🏠</button>
              <button className="px-3 py-1 text-xs border border-pf-border rounded hover:bg-pf-bg-2" disabled={isPrinting}>Z-</button>
            </div>

            <div className="space-y-2">
              <div className="flex items-center gap-2">
                <span className="w-4 text-xs">X</span>
                <input type="number" className="w-20 px-2 py-1 border border-pf-border rounded text-xs" disabled={isPrinting} />
                <span className="text-xs text-pf-text-secondary">[{formatTemp(status?.x)}]</span>
              </div>
              <div className="flex items-center gap-2">
                <span className="w-4 text-xs">Y</span>
                <input type="number" className="w-20 px-2 py-1 border border-pf-border rounded text-xs" disabled={isPrinting} />
                <span className="text-xs text-pf-text-secondary">[{formatTemp(status?.y)}]</span>
              </div>
              <div className="flex items-center gap-2">
                <span className="w-4 text-xs">Z</span>
                <input type="number" className="w-20 px-2 py-1 border border-pf-border rounded text-xs" disabled={isPrinting} />
                <span className="text-xs text-pf-text-secondary">[{formatTemp(status?.z)}]</span>
              </div>
              <button className="px-4 py-1 text-xs font-medium bg-pf-accent text-white rounded hover:bg-pf-accent-dark" disabled={isPrinting}>
                GO
              </button>
            </div>
          </div>
        </div>

        {/* Print Controls */}
        <div className="mt-6 pt-6 border-t border-pf-border">
          <div className="flex items-center gap-4">
            <button
              onClick={() => handleControlAction('pause')}
              disabled={!isPrinting}
              className="inline-flex items-center px-4 py-2 text-sm font-medium border border-pf-border rounded-md hover:bg-pf-bg-2 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <Pause className="h-4 w-4 mr-2" />
              Pause
            </button>
            <button
              onClick={() => handleControlAction('resume')}
              disabled={!isPaused}
              className="inline-flex items-center px-4 py-2 text-sm font-medium border border-pf-border rounded-md hover:bg-pf-bg-2 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <Play className="h-4 w-4 mr-2" />
              Resume
            </button>
            <button
              onClick={() => handleControlAction('stop')}
              disabled={!isOnline}
              className="inline-flex items-center px-4 py-2 text-sm font-medium border border-red-300 text-red-700 rounded-md hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <Square className="h-4 w-4 mr-2" />
              Emergency Stop
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
