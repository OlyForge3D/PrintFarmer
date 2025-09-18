import { useState, useCallback } from 'react';
import { Printer, PrinterBackend } from '@/types/api';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { useAuth } from '@/contexts/AuthHooks';
import { Trash2, Edit, CheckCircle2, Circle, AlertTriangle, Wrench, Check, X } from 'lucide-react';

interface PrinterTableViewProps {
  printers: Printer[];
  onEdit: (printer: Printer) => void;
  onDelete: (printers: Printer[]) => void;
  onManage: (printer: Printer) => void;
  onBulkSetMaintenance: (printers: Printer[], inMaintenance: boolean) => void;
}

export function PrinterTableView({ 
  printers, 
  onEdit, 
  onDelete, 
  onManage,
  onBulkSetMaintenance 
}: PrinterTableViewProps) {
  const { hasPermission } = useAuth();
  const { getPrinterStatus } = usePrinterStatusUpdates();
  const [selectedPrinters, setSelectedPrinters] = useState<Set<string>>(new Set());
  const [bulkAction, setBulkAction] = useState<'none' | 'delete' | 'maintenance-on' | 'maintenance-off'>('none');

  const toggleSelectAll = useCallback(() => {
    if (selectedPrinters.size === printers.length) {
      setSelectedPrinters(new Set());
    } else {
      setSelectedPrinters(new Set(printers.map(p => p.id)));
    }
  }, [printers, selectedPrinters.size]);

  const toggleSelectPrinter = useCallback((printerId: string) => {
    const newSelection = new Set(selectedPrinters);
    if (newSelection.has(printerId)) {
      newSelection.delete(printerId);
    } else {
      newSelection.add(printerId);
    }
    setSelectedPrinters(newSelection);
  }, [selectedPrinters]);

  const handleBulkAction = useCallback(() => {
    const selectedPrinterObjects = printers.filter(p => selectedPrinters.has(p.id));
    
    switch (bulkAction) {
      case 'delete':
        onDelete(selectedPrinterObjects);
        break;
      case 'maintenance-on':
        onBulkSetMaintenance(selectedPrinterObjects, true);
        break;
      case 'maintenance-off':
        onBulkSetMaintenance(selectedPrinterObjects, false);
        break;
    }
    
    setSelectedPrinters(new Set());
    setBulkAction('none');
  }, [bulkAction, selectedPrinters, printers, onDelete, onBulkSetMaintenance]);

  const getStatusColor = (isOnline: boolean, state?: string) => {
    if (!isOnline) return 'text-pf-text-tertiary';
    
    switch (state?.toLowerCase()) {
      case 'printing':
        return 'text-pf-status-online-text';
      case 'paused':
        return 'text-pf-warning';
      case 'error':
        return 'text-pf-error-text';
      case 'ready':
      case 'idle':
      case 'operational':
        return 'text-pf-accent';
      default:
        return 'text-pf-text-secondary';
    }
  };

  const getBackendIcon = (backend: PrinterBackend) => {
    switch (backend) {
      case PrinterBackend.Moonraker:
        return <span title="Moonraker" aria-label="Moonraker" role="img">🌙</span>;
      case PrinterBackend.PrusaLink:
        return <span title="PrusaLink" aria-label="PrusaLink" role="img">🔗</span>;
      case PrinterBackend.SDCP:
        return <span title="SDCP" aria-label="SDCP" role="img">📡</span>;
      case PrinterBackend.OctoPrint:
        return <img src={require("@/assets/octoprint.svg")} alt="OctoPrint" title="OctoPrint" className="inline h-5 w-5 align-middle" />;
      default:
        return <span title="Other" aria-label="Other" role="img">🖨️</span>;
    }
  };

  const formatTemperature = (temp?: number, target?: number) => {
    if (temp === undefined && target === undefined) return '—';
    
    if (target !== undefined && target > 0) {
      return `${Math.round(temp || 0)}°/${Math.round(target)}°`;
    }
    return temp !== undefined ? `${Math.round(temp)}°` : '—';
  };

  return (
    <div className="bg-pf-bg-1 border border-pf-border rounded-xl overflow-hidden">
      {/* Bulk Actions Header */}
      {selectedPrinters.size > 0 && (
        <div className="bg-pf-accent-2 px-4 py-3 border-b border-pf-border flex items-center justify-between">
          <div className="flex items-center space-x-4">
            <span className="text-sm font-medium text-pf-text-primary">
              {selectedPrinters.size} printer{selectedPrinters.size !== 1 ? 's' : ''} selected
            </span>
            
            <select
              value={bulkAction}
              onChange={(e) => setBulkAction(e.target.value as typeof bulkAction)}
              className="text-sm bg-pf-panel border border-pf-border rounded px-2 py-1 text-pf-text-primary"
              aria-label="Bulk action selector"
            >
              <option value="none">Choose action...</option>
              {hasPermission('printers', 'delete') && (
                <option value="delete">Delete Selected</option>
              )}
              <option value="maintenance-on">Mark as In Maintenance</option>
              <option value="maintenance-off">Remove Maintenance Status</option>
            </select>
          </div>
          
          <div className="flex items-center space-x-2">
            <button
              onClick={handleBulkAction}
              disabled={bulkAction === 'none'}
              className="px-3 py-1 bg-pf-success text-white text-sm rounded hover:bg-pf-success-hover disabled:opacity-50 disabled:cursor-not-allowed flex items-center transition-colors"
            >
              <Check className="w-4 h-4 mr-1" />
              Apply
            </button>
            
            <button
              onClick={() => setSelectedPrinters(new Set())}
              className="px-3 py-1 bg-pf-text-tertiary text-white text-sm rounded hover:bg-pf-text-secondary flex items-center transition-colors"
            >
              <X className="w-4 h-4 mr-1" />
              Cancel
            </button>
          </div>
        </div>
      )}

      {/* Table */}
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead className="bg-pf-panel border-b border-pf-border">
            <tr>
              <th className="w-12 px-4 py-3">
                <button
                  onClick={toggleSelectAll}
                  className="text-pf-text-primary hover:text-pf-accent transition-colors"
                >
                  {selectedPrinters.size === printers.length ? (
                    <CheckCircle2 className="w-5 h-5" />
                  ) : selectedPrinters.size > 0 ? (
                    <AlertTriangle className="w-5 h-5" />
                  ) : (
                    <Circle className="w-5 h-5" />
                  )}
                </button>
              </th>
              <th className="text-left px-4 py-3 text-sm font-bold text-pf-text-primary uppercase tracking-wide">
                Printer
              </th>
              <th className="text-left px-4 py-3 text-sm font-bold text-pf-text-primary uppercase tracking-wide">
                Status
              </th>
              <th className="text-left px-4 py-3 text-sm font-bold text-pf-text-primary uppercase tracking-wide">
                Progress
              </th>
              <th className="text-left px-4 py-3 text-sm font-bold text-pf-text-primary uppercase tracking-wide">
                Temperatures
              </th>
              <th className="text-left px-4 py-3 text-sm font-bold text-pf-text-primary uppercase tracking-wide">
                Last Updated
              </th>
              <th className="text-center px-4 py-3 text-sm font-bold text-pf-text-primary uppercase tracking-wide">
                Actions
              </th>
            </tr>
          </thead>
          <tbody className="divide-y divide-pf-border">
            {printers.map((printer) => {
              const realtimeStatus = getPrinterStatus(printer.id);
              const currentStatus = {
                isOnline: realtimeStatus?.isOnline ?? printer.isOnline,
                state: realtimeStatus?.state ?? printer.state,
                progress: realtimeStatus?.progress ?? printer.progress,
                jobName: realtimeStatus?.jobName ?? printer.jobName,
                hotendTemp: realtimeStatus?.hotendTemp ?? printer.hotendTemp,
                bedTemp: realtimeStatus?.bedTemp ?? printer.bedTemp,
                hotendTarget: realtimeStatus?.hotendTarget ?? printer.hotendTarget,
                bedTarget: realtimeStatus?.bedTarget ?? printer.bedTarget,
              };

              return (
                <tr 
                  key={printer.id}
                  className={`hover:bg-pf-bg-2 transition-colors ${
                    selectedPrinters.has(printer.id) ? 'bg-pf-accent-2' : ''
                  }`}
                >
                  {/* Selection Checkbox */}
                  <td className="px-4 py-4">
                    <button
                      onClick={() => toggleSelectPrinter(printer.id)}
                      className="text-pf-text-primary hover:text-pf-accent transition-colors"
                    >
                      {selectedPrinters.has(printer.id) ? (
                        <CheckCircle2 className="w-5 h-5" />
                      ) : (
                        <Circle className="w-5 h-5" />
                      )}
                    </button>
                  </td>

                  {/* Printer Info */}
                  <td className="px-4 py-4">
                    <div className="flex items-center">
                      <span className="text-2xl mr-3 flex-shrink-0">
                        {getBackendIcon(printer.backend)}
                      </span>
                      <div className="min-w-0">
                        <div className="text-sm font-bold text-pf-text-primary font-bebas uppercase truncate">
                          {printer.name}
                        </div>
                        <div className="text-xs text-pf-text-secondary truncate">
                          {printer.manufacturerName} {printer.modelName}
                        </div>
                        <div className="text-xs text-pf-text-tertiary truncate">
                          {printer.ipAddress}
                        </div>
                      </div>
                    </div>
                  </td>

                  {/* Status */}
                  <td className="px-4 py-4">
                    <div className={`text-sm font-medium ${getStatusColor(currentStatus.isOnline, currentStatus.state)}`}>
                      {currentStatus.isOnline ? (currentStatus.state || 'Unknown') : 'Offline'}
                    </div>
                    {currentStatus.jobName && (
                      <div className="text-xs text-pf-text-tertiary truncate max-w-32">
                        {currentStatus.jobName}
                      </div>
                    )}
                  </td>

                  {/* Progress */}
                  <td className="px-4 py-4">
                    {currentStatus.progress !== undefined && currentStatus.progress > 0 ? (
                      <div className="flex items-center space-x-2">
                        <div className="w-12 bg-pf-border-dark rounded-full h-2">
                          <div
                            className="bg-pf-success h-2 rounded-full transition-all"
                            style={{ width: `${currentStatus.progress}%` }}
                          />
                        </div>
                        <span className="text-sm text-pf-text-primary">
                          {Math.round(currentStatus.progress)}%
                        </span>
                      </div>
                    ) : (
                      <span className="text-sm text-pf-text-tertiary">—</span>
                    )}
                  </td>

                  {/* Temperatures */}
                  <td className="px-4 py-4">
                    <div className="text-sm text-pf-text-primary">
                      <div>H: {formatTemperature(currentStatus.hotendTemp, currentStatus.hotendTarget)}</div>
                      <div>B: {formatTemperature(currentStatus.bedTemp, currentStatus.bedTarget)}</div>
                    </div>
                  </td>

                  {/* Last Updated */}
                  <td className="px-4 py-4">
                    <span className="text-sm text-pf-text-tertiary">
                      {/* TODO: Add lastUpdated field to Printer interface */}
                      Recently
                    </span>
                  </td>

                  {/* Actions */}
                  <td className="px-4 py-4">
                    <div className="flex items-center justify-center space-x-1">
                      <button
                        onClick={() => onManage(printer)}
                        className="p-2 text-pf-text-tertiary hover:text-pf-accent transition-colors rounded-md hover:bg-pf-bg-2"
                        title="Manage printer"
                      >
                        <Wrench className="w-4 h-4" />
                      </button>
                      
                      {hasPermission('printers', 'update') && (
                        <button
                          onClick={() => onEdit(printer)}
                          className="p-2 text-pf-text-tertiary hover:text-pf-accent transition-colors rounded-md hover:bg-pf-bg-2"
                          title="Edit printer"
                        >
                          <Edit className="w-4 h-4" />
                        </button>
                      )}
                      
                      {hasPermission('printers', 'delete') && (
                        <button
                          onClick={() => onDelete([printer])}
                          className="p-2 text-pf-text-tertiary hover:text-pf-error-text transition-colors rounded-md hover:bg-pf-error-bg"
                          title="Delete printer"
                        >
                          <Trash2 className="w-4 h-4" />
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      {printers.length === 0 && (
        <div className="text-center py-8 text-pf-text-tertiary">
          <div className="text-lg font-medium mb-2">No printers found</div>
          <div className="text-sm">Add your first printer to get started</div>
        </div>
      )}
    </div>
  );
}
