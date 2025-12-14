/* eslint-disable local/pf-no-raw-html-controls */
import { useState, useCallback } from 'react';
import moonrakerIcon from '@/assets/moonraker.svg';
import octoprintIcon from '@/assets/octoprint.svg';
import styles from './PrinterTableView.module.css';
import { Printer, PrinterBackend } from '@/types/api';
import { usePrinterDisplay } from '@/hooks/usePrinterDisplay';
import { useAuth } from '@/contexts/AuthHooks';
import { Trash2, Edit, CheckCircle2, Circle, AlertTriangle, Wrench, Check, X } from 'lucide-react';
import { renderUnknown } from '@/utils/renderUnknown';
import { Button } from '@/components/ui';

interface PrinterTableViewProps {
  printers: Printer[];
  onEdit: (printer: Printer) => void;
  onDelete: (printers: Printer[]) => void;
  onBulkSetMaintenance: (printers: Printer[], inMaintenance: boolean) => void;
}

export function PrinterTableView({
  printers,
  onEdit,
  onDelete,
  onBulkSetMaintenance
}: PrinterTableViewProps) {
  const { hasPermission } = useAuth();
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
        return <img src={moonrakerIcon} alt="Moonraker" title="Moonraker" className="inline h-5 w-5 align-middle" />;
      case PrinterBackend.PrusaLink:
        return <span title="PrusaLink" aria-label="PrusaLink" role="img">🔗</span>;
      case PrinterBackend.SDCP:
        return <span title="SDCP" aria-label="SDCP" role="img">📡</span>;
      case PrinterBackend.OctoPrint:
        return <img src={octoprintIcon} alt="OctoPrint" title="OctoPrint" className="inline h-5 w-5 align-middle" />;
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
      {/* Optional debug panel for table-level realtime data */}
      {window.PrintFarmerDebug?.printerTableDisplay && (
        <div className="p-2 border-b border-pf-border bg-pf-bg-0 text-xs text-pf-text-tertiary">
          {renderUnknown({ printers, selectedCount: selectedPrinters.size })}
        </div>
      )}
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
            <Button
              type="button"
              onClick={handleBulkAction}
              disabled={bulkAction === 'none'}
              variant="success"
              size="sm"
              className="flex items-center gap-1"
            >
              <Check className="w-4 h-4" />
              Apply
            </Button>
            
            <Button
              type="button"
              onClick={() => setSelectedPrinters(new Set())}
              variant="secondary"
              size="sm"
              className="flex items-center gap-1"
            >
              <X className="w-4 h-4" />
              Cancel
            </Button>
          </div>
        </div>
      )}

      {/* Table */}
      <div className="overflow-x-auto">
        <table className="w-full">
          <thead className="bg-pf-panel border-b border-pf-border">
            <tr>
              <th className="w-12 px-4 py-3">
                <Button
                  type="button"
                  onClick={toggleSelectAll}
                  variant="subtle"
                  size="sm"
                  className="!p-0 !h-auto"
                >
                  {selectedPrinters.size === printers.length ? (
                    <CheckCircle2 className="w-5 h-5" />
                  ) : selectedPrinters.size > 0 ? (
                    <AlertTriangle className="w-5 h-5" />
                  ) : (
                    <Circle className="w-5 h-5" />
                  )}
                </Button>
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
              const displayPrinter = usePrinterDisplay(printer);

              return (
                <tr 
                  key={printer.id}
                  className={`hover:bg-pf-bg-2 transition-colors ${
                    selectedPrinters.has(printer.id) ? 'bg-pf-accent-2' : ''
                  }`}
                >
                  {/* Selection Checkbox */}
                  <td className="px-4 py-4">
                    <Button
                      type="button"
                      onClick={() => toggleSelectPrinter(printer.id)}
                      variant="subtle"
                      size="sm"
                      className="!p-0 !h-auto"
                    >
                      {selectedPrinters.has(printer.id) ? (
                        <CheckCircle2 className="w-5 h-5" />
                      ) : (
                        <Circle className="w-5 h-5" />
                      )}
                    </Button>
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
                    <div className={`text-sm font-medium ${getStatusColor(displayPrinter.isOnline, displayPrinter.state)}`}>
                      {displayPrinter.isOnline ? (displayPrinter.state || 'Unknown') : 'Offline'}
                    </div>
                    {displayPrinter.jobName && (
                      <div className="text-xs text-pf-text-tertiary truncate max-w-32">
                        {displayPrinter.jobName}
                      </div>
                    )}
                  </td>

                  {/* Progress */}
                  <td className="px-4 py-4">
                    {displayPrinter.progress !== undefined && displayPrinter.progress > 0 ? (
                      <div className="flex items-center space-x-2">
                        <div className="w-12 bg-pf-border-dark rounded-full h-2">
                          <div
                            className={`bg-pf-success ${styles['pf-progress-bar']} ${styles[`w-${Math.min(100, Math.max(0, Math.round(displayPrinter.progress / 5) * 5))}`]}`}
                          />
                        </div>
                        <span className="text-sm text-pf-text-primary">
                          {Math.round(displayPrinter.progress)}%
                        </span>
                      </div>
                    ) : (
                      <span className="text-sm text-pf-text-tertiary">—</span>
                    )}
                  </td>

                  {/* Temperatures */}
                  <td className="px-4 py-4">
                    <div className="text-sm text-pf-text-primary">
                      <div>H: {formatTemperature(displayPrinter.hotendTemp, displayPrinter.hotendTarget)}</div>
                      <div>B: {formatTemperature(displayPrinter.bedTemp, displayPrinter.bedTarget)}</div>
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
                      <Button
                        type="button"
                        onClick={() => onBulkSetMaintenance([printer], !printer.inMaintenance)}
                        variant={printer.inMaintenance ? 'success' : 'secondary'}
                        size="sm"
                        className="!p-2 !h-auto"
                        title={printer.inMaintenance ? 'Disable Maintenance Mode' : 'Enable Maintenance Mode'}
                      >
                        <Wrench className="w-4 h-4" />
                      </Button>
                      
                      {hasPermission('printers', 'update') && (
                        <Button
                          type="button"
                          onClick={() => onEdit(printer)}
                          variant="subtle"
                          size="sm"
                          className="!p-2 !h-auto"
                          title="Edit printer"
                        >
                          <Edit className="w-4 h-4" />
                        </Button>
                      )}
                      
                      {hasPermission('printers', 'delete') && (
                        <Button
                          type="button"
                          onClick={() => onDelete([printer])}
                          variant="danger"
                          size="sm"
                          className="!p-2 !h-auto"
                          title="Delete printer"
                        >
                          <Trash2 className="w-4 h-4" />
                        </Button>
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
