import React from 'react';
import { Button } from '@/common/components/ui';
import type { PrinterBasicInfo } from './types';

interface TargetPrinterSelectorProps {
  /** Currently selected printer (null if none) */
  selectedPrinter: PrinterBasicInfo | null | undefined;
  /** Callback to open the printer selector modal */
  onOpenSelector: () => void;
  /** Optional CSS class name */
  className?: string;
}

/**
 * Target printer selection component.
 * Allows selecting a specific printer to send sliced G-code to.
 */
export const TargetPrinterSelector: React.FC<TargetPrinterSelectorProps> = ({
  selectedPrinter,
  onOpenSelector,
  className
}) => {
  return (
    <div className={`bg-pf-panel border border-pf-border rounded-lg p-4 ${className ?? ''}`}>
      <label className="block text-sm font-semibold text-pf-text-primary mb-2">Target Printer (Optional)</label>
      {selectedPrinter ? (
        <div className="space-y-2">
          <div className="p-3 bg-pf-bg-0 rounded-sm border border-pf-border">
            <p className="font-medium text-pf-text-primary">{selectedPrinter.name}</p>
            {selectedPrinter.modelName && (
              <p className="text-sm text-pf-text-muted">
                {selectedPrinter.manufacturerName && `${selectedPrinter.manufacturerName} • `}
                {selectedPrinter.modelName}
              </p>
            )}
          </div>
          <Button
            type="button"
            onClick={onOpenSelector}
            variant="secondary"
            size="sm"
            className="w-full"
          >
            Change Printer
          </Button>
        </div>
      ) : (
        <Button
          type="button"
          onClick={onOpenSelector}
          variant="secondary"
          size="sm"
          className="w-full"
        >
          Select Target Printer
        </Button>
      )}
      <p className="text-xs text-pf-text-muted mt-2">
        Select a specific printer to send the sliced G-code to
      </p>
    </div>
  );
};

export default TargetPrinterSelector;
