import React, { useMemo } from 'react';
import { Select } from '@/common/components/ui';
import type { ToolheadDto } from '@/types/api';

export interface PrinterForSlicing {
  id: string;
  name: string;
  manufacturerId?: string;
  manufacturerName?: string;
  modelId?: string;
  modelName?: string;
  nozzleDiameter?: number;
  thumbnailUrl?: string;
  isOnline?: boolean;
  toolheads?: ToolheadDto[];
}

interface PrinterSlicerSelectorProps {
  /** Available printers */
  printers: PrinterForSlicing[];
  /** Whether printers are loading */
  isLoading?: boolean;
  /** Selected printer ID */
  selectedPrinterId: string;
  /** Callback when printer changes */
  onPrinterChange: (printerId: string, printer: PrinterForSlicing | undefined) => void;
  /** Optional CSS class name */
  className?: string;
}

/**
 * Get primary nozzle diameter from printer.
 * Checks toolheads first (primary toolhead), falls back to nozzleDiameter field.
 */
function getPrimaryNozzleDiameter(printer: PrinterForSlicing): number | undefined {
  // Check toolheads first - find primary or first
  if (printer.toolheads && printer.toolheads.length > 0) {
    const primary = printer.toolheads.find(t => t.isPrimary) || printer.toolheads[0];
    if (primary.nozzleDiameter) {
      return primary.nozzleDiameter;
    }
  }
  // Fall back to direct nozzleDiameter field
  return printer.nozzleDiameter;
}

/**
 * Printer selection component for slicing.
 * Displays printer name, model, and nozzle size in a rich format.
 */
export const PrinterSlicerSelector: React.FC<PrinterSlicerSelectorProps> = ({
  printers,
  isLoading,
  selectedPrinterId,
  onPrinterChange,
  className
}) => {
  const selectedPrinter = useMemo(() => {
    return printers.find(p => p.id === selectedPrinterId);
  }, [printers, selectedPrinterId]);

  const handleChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    const printerId = e.target.value;
    const printer = printers.find(p => p.id === printerId);
    onPrinterChange(printerId, printer);
  };

  // Group printers by manufacturer for better organization
  const printersByManufacturer = useMemo(() => {
    const grouped = new Map<string, PrinterForSlicing[]>();
    
    for (const printer of printers) {
      const mfr = printer.manufacturerName || 'Other';
      if (!grouped.has(mfr)) {
        grouped.set(mfr, []);
      }
      grouped.get(mfr)!.push(printer);
    }
    
    // Sort manufacturers alphabetically, but put "Other" last
    return Array.from(grouped.entries()).sort((a, b) => {
      if (a[0] === 'Other') return 1;
      if (b[0] === 'Other') return -1;
      return a[0].localeCompare(b[0]);
    });
  }, [printers]);

  const formatPrinterOption = (printer: PrinterForSlicing): string => {
    const nozzle = getPrimaryNozzleDiameter(printer);
    const parts = [printer.name];
    
    if (printer.modelName) {
      parts.push(`(${printer.modelName}`);
      if (nozzle) {
        parts[parts.length - 1] += ` • ${nozzle}mm`;
      }
      parts[parts.length - 1] += ')';
    } else if (nozzle) {
      parts.push(`(${nozzle}mm nozzle)`);
    }
    
    return parts.join(' ');
  };

  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 ${className ?? ''}`}>
      <label className="block text-sm font-semibold text-pf-text mb-2">
        Printer
      </label>
      
      {isLoading ? (
        <Select disabled className="bg-pf-disabled">
          <option>Loading printers...</option>
        </Select>
      ) : printers.length === 0 ? (
        <div className="text-sm text-pf-text-muted p-2 bg-pf-surface rounded">
          No printers configured. <a href="/printers" className="text-pf-primary hover:underline">Add a printer</a> to get started.
        </div>
      ) : (
        <>
          <Select
            value={selectedPrinterId}
            onChange={handleChange}
            className="w-full"
          >
            <option value="">-- Select printer to slice for --</option>
            {printersByManufacturer.map(([manufacturer, manufacturerPrinters]) => (
              <optgroup key={manufacturer} label={manufacturer}>
                {manufacturerPrinters.map(printer => (
                  <option key={printer.id} value={printer.id}>
                    {formatPrinterOption(printer)}
                  </option>
                ))}
              </optgroup>
            ))}
          </Select>
          
          {/* Rich display of selected printer */}
          {selectedPrinter && (
            <div className="mt-3 p-3 bg-pf-surface rounded-lg flex items-start gap-3">
              {/* Printer thumbnail or icon */}
              <div className="flex-shrink-0 w-16 h-16 bg-pf-bg-1 rounded flex items-center justify-center overflow-hidden">
                {selectedPrinter.thumbnailUrl ? (
                  <img 
                    src={selectedPrinter.thumbnailUrl} 
                    alt={selectedPrinter.name}
                    className="w-full h-full object-cover"
                  />
                ) : (
                  <span className="text-2xl" role="img" aria-label="Printer">🖨️</span>
                )}
              </div>
              
              {/* Printer info */}
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2">
                  <span className="font-medium text-pf-text truncate">
                    {selectedPrinter.name}
                  </span>
                  {selectedPrinter.isOnline !== undefined && (
                    <span className={`w-2 h-2 rounded-full flex-shrink-0 ${
                      selectedPrinter.isOnline ? 'bg-green-500' : 'bg-gray-400'
                    }`} title={selectedPrinter.isOnline ? 'Online' : 'Offline'} />
                  )}
                </div>
                <div className="text-sm text-pf-text-muted flex items-center gap-1 mt-0.5">
                  {selectedPrinter.modelName && (
                    <span>{selectedPrinter.modelName}</span>
                  )}
                  {selectedPrinter.modelName && getPrimaryNozzleDiameter(selectedPrinter) && (
                    <span className="text-pf-text-muted">•</span>
                  )}
                  {getPrimaryNozzleDiameter(selectedPrinter) && (
                    <span>{getPrimaryNozzleDiameter(selectedPrinter)}mm nozzle</span>
                  )}
                </div>
                {selectedPrinter.manufacturerName && (
                  <div className="text-xs text-pf-text-muted mt-1">
                    {selectedPrinter.manufacturerName}
                  </div>
                )}
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
};

export default PrinterSlicerSelector;
