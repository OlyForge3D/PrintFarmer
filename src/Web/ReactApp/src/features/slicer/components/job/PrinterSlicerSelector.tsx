import React, { useMemo, useState } from 'react';
import { Button } from '@/common/components/ui';
import { PrinterImage } from '@/common/components/PrinterImage';
import { PrinterSelectorModal } from '@/features/printers/components/PrinterSelectorModal';
import type { ToolheadDto, MotionType } from '@/types/api';

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
  motionType?: MotionType;
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
  /** Optional compact control rendered beside the selected printer card */
  accessory?: React.ReactNode;
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
  accessory,
  className
}) => {
  const [isModalOpen, setIsModalOpen] = useState(false);

  const selectedPrinter = useMemo(() => {
    return printers.find(p => p.id === selectedPrinterId);
  }, [printers, selectedPrinterId]);

  const handleSelect = (printerId: string) => {
    const printer = printers.find(p => p.id === printerId);
    onPrinterChange(printerId, printer);
  };

  // Map printers to modal format with nozzle diameter and motion type
  const modalPrinters = useMemo(() => {
    return printers.map(p => ({
      id: p.id,
      name: p.name,
      modelName: p.modelName,
      manufacturerName: p.manufacturerName,
      isOnline: p.isOnline,
      nozzleDiameter: getPrimaryNozzleDiameter(p),
      motionType: p.motionType
    }));
  }, [printers]);

  return (
    <div className={className ?? ''}>
      <label className="block text-sm font-semibold text-pf-text-primary mb-2">
        Printer
      </label>
      
      {isLoading ? (
        <div className="p-3 bg-pf-bg-1 rounded-lg text-pf-text-muted">
          Loading printers...
        </div>
      ) : printers.length === 0 ? (
        <div className="text-sm text-pf-text-muted p-2 bg-pf-bg-1 rounded-sm">
          No printers configured. <a href="/printers" className="text-pf-primary hover:underline">Add a printer</a> to get started.
        </div>
      ) : (
        <>
          <div className="flex items-stretch gap-2">
            {/* Clickable printer card that opens modal */}
            <Button
              type="button"
              variant="secondary"
              onClick={() => setIsModalOpen(true)}
              className="min-w-0 flex-1 justify-start! p-3! rounded-lg!"
            >
              {selectedPrinter ? (
                <div className="flex items-start gap-3 w-full text-left">
                  {/* Printer cover image from manufacturer/model or fallback based on motion type */}
                  <div className="shrink-0 w-12 h-12 bg-pf-bg-1 rounded-sm flex items-center justify-center overflow-hidden">
                    <PrinterImage
                      manufacturerName={selectedPrinter.manufacturerName}
                      modelName={selectedPrinter.modelName}
                      motionType={selectedPrinter.motionType}
                      alt={selectedPrinter.name}
                      className="w-full h-full object-cover"
                    />
                  </div>

                  {/* Printer info */}
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="font-medium text-pf-text-primary truncate">
                        {selectedPrinter.name}
                      </span>
                      {selectedPrinter.isOnline !== undefined && (
                        <span className={`w-2 h-2 rounded-full shrink-0 ${
                          selectedPrinter.isOnline ? 'bg-pf-success' : 'bg-pf-disabled'
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
                  </div>

                  {/* Chevron indicator */}
                  <svg className="w-4 h-4 text-pf-text-muted" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" /></svg>
                </div>
              ) : (
                <div className="flex items-center gap-3 text-pf-text-muted">
                  <span className="text-xl">🖨️</span>
                  <span>Click to select a printer...</span>
                </div>
              )}
            </Button>
            {accessory && (
              <div className="w-28 shrink-0">
                {accessory}
              </div>
            )}
          </div>
          
          {/* Printer selector modal */}
          <PrinterSelectorModal
            isOpen={isModalOpen}
            printers={modalPrinters}
            selectedPrinterId={selectedPrinterId}
            onSelect={handleSelect}
            onClose={() => setIsModalOpen(false)}
          />
        </>
      )}
    </div>
  );
};

export default PrinterSlicerSelector;
