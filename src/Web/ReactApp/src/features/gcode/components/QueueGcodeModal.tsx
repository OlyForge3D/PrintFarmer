import React, { use, Suspense, useState, useMemo } from 'react';
import { Button, Checkbox, Select } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { queueService } from '@/services/queueService';
import printJobQueueService, { EnqueuePrintJobRequest } from '@/services/printJobQueueService';
import { GcodeFile } from '@/types/api';

interface Props {
  file: GcodeFile;
  isOpen: boolean;
  onClose: (added?: boolean) => void;
}

interface PrinterOption {
  id: string;
  name: string;
  model: string;
  isAvailable: boolean;
  nozzleDiameter?: number;
  supportedMaterials?: string[];
}

/**
 * React 19 async function to fetch printer list
 */
async function fetchPrinters(): Promise<PrinterOption[]> {
  const list = await queueService.getQueueOverview();
  return list.map(p => ({
    id: p.printerId,
    name: p.printerName,
    model: p.printerModel,
    isAvailable: p.isAvailable,
    nozzleDiameter: p.nozzleDiameter,
    supportedMaterials: p.supportedMaterials
  }));
}

/**
 * Inner content component using React 19 use() hook for async data
 * This must be inside the Suspense boundary to work correctly
 */
function QueueGcodeModalInner({ file, printerPromise, isOpen, onClose }: { 
  file: GcodeFile;
  printerPromise: Promise<PrinterOption[]>;
  isOpen: boolean;
  onClose: (added?: boolean) => void;
}) {
  // Call use() here, inside the actual component render
  const printers = use(printerPromise);
  
  return (
    <QueueGcodeModalContent 
      file={file}
      printers={printers}
      isOpen={isOpen}
      onClose={onClose}
    />
  );
}

/**
 * Content component with modal UI
 */
function QueueGcodeModalContent({ file, printers, isOpen, onClose }: { 
  file: GcodeFile; 
  printers: PrinterOption[];
  isOpen: boolean;
  onClose: (added?: boolean) => void;
}) {
  const [selectedPrinter, setSelectedPrinter] = useState<string | undefined>(undefined);
  const [autoAssign, setAutoAssign] = useState(true);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleQueue = async () => {
    setLoading(true);
    setError(null);
    try {
      const req: EnqueuePrintJobRequest = {
        gcodeFileId: file.id,
        assignedPrinterId: autoAssign ? undefined : (selectedPrinter || undefined),
        priority: 'Normal'
      };

      // Support extracted fields from lightweight GcodeFile
      const requiredNozzle = file.extractedNozzleDiameter;
      const requiredMaterial = file.extractedMaterial;

      if (typeof requiredNozzle === 'number') req.requiredNozzleDiameter = requiredNozzle;
      if (typeof requiredMaterial === 'string' && requiredMaterial.length > 0) req.requiredMaterialType = requiredMaterial;

      await printJobQueueService.enqueue(req);
      setLoading(false);
      onClose(true);
    } catch (err) {
      setLoading(false);
      setError(err instanceof Error ? err.message : 'Failed to queue job');
    }
  };

  const isPrinterCompatible = (p: { model: string; nozzleDiameter?: number; supportedMaterials?: string[] }) => {
    if (!file) return true;
    const requiredNozzle = file.extractedNozzleDiameter;
    const requiredMaterial = file.extractedMaterial;
    const requiredModel = file.extractedPrinterModel || file.extractedPrinterModelName;

    // Check printer model compatibility (case-insensitive, normalizing spaces/dashes)
    if (requiredModel && p.model && p.model.toLowerCase() !== 'unknown') {
      const normalizeModel = (s: string) => s.toLowerCase().replace(/[-_\s]+/g, ' ').trim();
      const normalizedRequired = normalizeModel(requiredModel);
      const normalizedPrinter = normalizeModel(p.model);
      // Use partial matching - either string contains the other (handles variations like "Qidi X-Plus 4" vs "X-Plus 4")
      const modelsMatch = normalizedRequired === normalizedPrinter 
        || normalizedRequired.includes(normalizedPrinter) 
        || normalizedPrinter.includes(normalizedRequired);
      if (!modelsMatch) {
        return false;
      }
    }

    // Only check nozzle if printer has nozzle info configured (skip if unknown)
    if (typeof requiredNozzle === 'number' && typeof p.nozzleDiameter === 'number' && p.nozzleDiameter < requiredNozzle) {
      return false;
    }
    
    // Only check material if printer has supported materials configured
    if (typeof requiredMaterial === 'string' && requiredMaterial.length > 0 && p.supportedMaterials && p.supportedMaterials.length > 0) {
      return p.supportedMaterials.map(s => s.toLowerCase()).includes(requiredMaterial.toLowerCase());
    }
    return true;
  };

  if (!isOpen) return null;

  // Check if there are no printers available
  const noPrintersAvailable = printers.length === 0;
  const availablePrinters = printers.filter(p => p.isAvailable);
  const noAvailablePrinters = availablePrinters.length === 0;

  return (
    <Modal
      isOpen={isOpen}
      onClose={() => onClose(false)}
      title="Queue G-code for Printing"
      footer={
        <div className="flex gap-2">
          <Button variant="secondary" onClick={() => onClose(false)}>
            Cancel
          </Button>
          <Button variant="primary" onClick={handleQueue} disabled={loading || noPrintersAvailable || noAvailablePrinters}>
            {loading ? 'Queueing…' : 'Queue for Print'}
          </Button>
        </div>
      }
    >
      <div className="space-y-4">
        <div>
          <div className="text-sm text-pf-text-secondary mb-1">File</div>
          <div className="font-medium text-pf-text-primary">{file.name}</div>
        </div>

        {noPrintersAvailable && (
          <div className="text-sm text-amber-600 bg-amber-50 dark:bg-amber-900/20 p-3 rounded-lg">
            <div className="font-medium">No printers configured</div>
            <div className="text-sm mt-1">Add at least one printer before queuing jobs.</div>
          </div>
        )}

        {!noPrintersAvailable && noAvailablePrinters && (
          <div className="text-sm text-amber-600 bg-amber-50 dark:bg-amber-900/20 p-3 rounded-lg">
            <div className="font-medium">All printers are offline</div>
            <div className="text-sm mt-1">Please wait for at least one printer to come online.</div>
          </div>
        )}

        {!noPrintersAvailable && availablePrinters.length > 0 && (
          <div>
            <Checkbox 
              label="Auto-assign best available printer (recommended)" 
              checked={autoAssign} 
              onChange={(e) => setAutoAssign((e.target as HTMLInputElement).checked)} 
            />
          </div>
        )}

        {!noPrintersAvailable && !autoAssign && availablePrinters.length > 0 && (
          <div>
            <div className="text-sm text-pf-text-secondary mb-2">Select Printer</div>
            <div>
              <Select
                aria-label={`Select printer for ${file.name}`}
                value={selectedPrinter ?? ''}
                onChange={(e) => setSelectedPrinter((e.target as HTMLSelectElement).value || undefined)}
              >
                <option value="">-- Select printer --</option>
                {printers.map(p => {
                  const compatible = isPrinterCompatible(p);
                  return (
                    <option key={p.id} value={p.id} disabled={!p.isAvailable || !compatible}>
                      {p.name} — {p.model} {p.isAvailable ? '' : '(offline)'}{!compatible ? ' (incompatible)' : ''}
                    </option>
                  );
                })}
              </Select>
            </div>
          </div>
        )}

        {error && (
          <div className="text-sm text-red-600 bg-red-50 dark:bg-red-900/20 p-3 rounded-lg">
            {error}
          </div>
        )}

        {(file.extractedPrinterModel || file.extractedPrinterModelName || file.extractedNozzleDiameter || file.extractedMaterial) && (
          <div className="text-sm text-pf-text-secondary bg-pf-bg-2 p-3 rounded-lg">
            <div className="font-medium mb-1">Compatibility required for this file:</div>
            {(file.extractedPrinterModel || file.extractedPrinterModelName) && <div>- Printer Model: {file.extractedPrinterModel || file.extractedPrinterModelName}</div>}
            {file.extractedNozzleDiameter && <div>- Nozzle: {file.extractedNozzleDiameter} mm</div>}
            {file.extractedMaterial && <div>- Material: {file.extractedMaterial}</div>}
          </div>
        )}
      </div>
    </Modal>
  );
}

/**
 * React 19 wrapper with Suspense boundary for printer list loading
 */
export const QueueGcodeModal: React.FC<Props> = ({ file, isOpen, onClose }) => {
  // Memoize the printer promise to prevent re-fetching on every render
  const printerPromise = useMemo(() => fetchPrinters(), []);

  if (!isOpen) return null;

  return (
    <Suspense fallback={
      <Modal
        isOpen={isOpen}
        onClose={() => onClose(false)}
        title="Queue G-code for Printing"
      >
        <div className="flex items-center justify-center py-8">
          <div className="text-center">
            <div className="animate-spin w-8 h-8 border-2 border-pf-accent border-t-transparent rounded-full mx-auto mb-2"></div>
            <p className="text-pf-text-secondary">Loading printers...</p>
          </div>
        </div>
      </Modal>
    }>
      <QueueGcodeModalInner 
        file={file}
        printerPromise={printerPromise}
        isOpen={isOpen}
        onClose={onClose}
      />
    </Suspense>
  );
};
