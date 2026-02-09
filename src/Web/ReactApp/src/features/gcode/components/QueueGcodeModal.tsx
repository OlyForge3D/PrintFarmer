import React, { use, Suspense, useState, useMemo } from 'react';
import { Button, Checkbox, Select, Spinner } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { apiClient } from '@/services/api';
import { printJobQueueService, EnqueuePrintJobRequest } from '@/services/printJobQueueService';
import { GcodeFile } from '@/types/api';
import { CheckCircleIcon, PrinterIcon, ClockIcon } from '@/common/components/icons/MdiIcons';

interface Props {
  file: GcodeFile;
  isOpen: boolean;
  onClose: (added?: boolean) => void;
}

interface PrinterOption {
  id: string;
  name: string;
  model: string;
  /** Slicer-specific model names that map to this printer's model (e.g., "COREONEL", "MK4IS") */
  modelAliases?: string[];
  isAvailable: boolean;
  nozzleDiameter?: number;
  supportedMaterials?: string[];
}

/**
 * React 19 async function to fetch compatible printers.
 * All filtering (model, nozzle, material) is done server-side for consistency with auto-assign.
 */
async function fetchPrinters(requiredModel?: string, requiredNozzle?: number, requiredMaterial?: string): Promise<PrinterOption[]> {
  const list = await apiClient.getQueueOverview(requiredModel, requiredNozzle, requiredMaterial);
  return list.map(p => ({
    id: p.printerId,
    name: p.printerName,
    model: p.printerModel,
    modelAliases: p.modelAliases,
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
  const requiredModel = file.extractedPrinterModel || file.extractedPrinterModelName;
  
  return (
    <QueueGcodeModalContent 
      file={file}
      printers={printers}
      requiredModel={requiredModel || undefined}
      isOpen={isOpen}
      onClose={onClose}
    />
  );
}

/**
 * Success state shown after job is queued
 */
type SuccessState = {
  jobId: string;
  printerName: string;
  isStarting: boolean;
  isStarted: boolean;
  startError?: string;
};

/**
 * Content component with modal UI
 */
function QueueGcodeModalContent({ file, printers, requiredModel, isOpen, onClose }: { 
  file: GcodeFile; 
  printers: PrinterOption[];
  /** The model filter that was used when fetching printers (for messaging) */
  requiredModel?: string;
  isOpen: boolean;
  onClose: (added?: boolean) => void;
}) {
  const [selectedPrinter, setSelectedPrinter] = useState<string | undefined>(undefined);
  const [autoAssign, setAutoAssign] = useState(true);
  const [loading, setLoading] = useState(false);
  const [startNowLoading, setStartNowLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successState, setSuccessState] = useState<SuccessState | null>(null);

  const buildRequest = (): EnqueuePrintJobRequest => {
    const req: EnqueuePrintJobRequest = {
      gcodeFileId: file.id,
      assignedPrinterId: autoAssign ? undefined : (selectedPrinter || undefined),
      priority: 'Normal'
    };

    // Support extracted fields from lightweight GcodeFile
    const requiredNozzle = file.extractedNozzleDiameter;
    const requiredMaterial = file.extractedMaterial;
    const requiredModelValue = file.extractedPrinterModel || file.extractedPrinterModelName;

    if (typeof requiredNozzle === 'number') req.requiredNozzleDiameter = requiredNozzle;
    if (typeof requiredMaterial === 'string' && requiredMaterial.length > 0) req.requiredMaterialType = requiredMaterial;
    if (typeof requiredModelValue === 'string' && requiredModelValue.length > 0) req.requiredPrinterModel = requiredModelValue;

    return req;
  };

  const handleQueue = async () => {
    setLoading(true);
    setError(null);
    try {
      const req = buildRequest();
      const result = await printJobQueueService.enqueue(req);
      setLoading(false);
      
      // Show success state instead of closing
      setSuccessState({
        jobId: result.id,
        printerName: result.assignedPrinterName || 'Unknown Printer',
        isStarting: false,
        isStarted: false
      });
    } catch (err) {
      setLoading(false);
      setError(err instanceof Error ? err.message : 'Failed to queue job');
    }
  };

  const handleQueueAndStart = async () => {
    setStartNowLoading(true);
    setError(null);
    try {
      const req = buildRequest();
      const result = await printJobQueueService.enqueue(req);
      
      // Immediately dispatch the job
      await apiClient.dispatchPrintQueueJob(result.id);
      
      setStartNowLoading(false);
      setSuccessState({
        jobId: result.id,
        printerName: result.assignedPrinterName || 'Unknown Printer',
        isStarting: false,
        isStarted: true
      });
    } catch (err) {
      setStartNowLoading(false);
      setError(err instanceof Error ? err.message : 'Failed to queue and start job');
    }
  };

  const handleStartNow = async () => {
    if (!successState) return;
    
    setSuccessState({ ...successState, isStarting: true, startError: undefined });
    try {
      await apiClient.dispatchPrintQueueJob(successState.jobId);
      setSuccessState({ ...successState, isStarting: false, isStarted: true });
    } catch (err) {
      setSuccessState({ 
        ...successState, 
        isStarting: false, 
        startError: err instanceof Error ? err.message : 'Failed to start print'
      });
    }
  };

  // All printer compatibility filtering is now done server-side.
  // Printers returned from the API are already filtered by model, nozzle, and material.

  if (!isOpen) return null;

  // Check if there are no printers available (could mean no compatible printers when model filter is applied)
  const noPrintersAvailable = printers.length === 0;
  const availablePrinters = printers.filter(p => p.isAvailable);
  const noAvailablePrinters = availablePrinters.length === 0;

  // Success state - show after job is queued
  if (successState) {
    return (
      <Modal
        isOpen={isOpen}
        onClose={() => onClose(true)}
        title={successState.isStarted ? "Print Started!" : "Job Queued Successfully!"}
        footer={
          <div className="flex gap-2 justify-end">
            {!successState.isStarted && (
              <Button 
                variant="primary" 
                onClick={handleStartNow}
                disabled={successState.isStarting}
              >
                {successState.isStarting ? (
                  <span className="flex items-center gap-2">
                    <Spinner className="h-4 w-4" />
                    Starting...
                  </span>
                ) : (
                  <>
                    <PrinterIcon className="w-4 h-4" />
                    Start Print Now
                  </>
                )}
              </Button>
            )}
            <Button variant="secondary" onClick={() => onClose(true)}>
              {successState.isStarted ? 'Done' : 'Close'}
            </Button>
          </div>
        }
      >
        <div className="text-center py-4">
          <div className={`w-16 h-16 mx-auto mb-4 rounded-full flex items-center justify-center ${
            successState.isStarted 
              ? 'bg-pf-success-bg/20' 
              : 'bg-pf-accent-bg/20'
          }`}>
            {successState.isStarted ? (
              <PrinterIcon className="w-8 h-8 text-pf-success" />
            ) : (
              <CheckCircleIcon className="w-8 h-8 text-pf-accent" />
            )}
          </div>
          
          <div className="space-y-2">
            <div className="text-lg font-medium text-pf-text-primary">
              {file.name}
            </div>
            <div className="text-pf-text-secondary flex items-center justify-center gap-2">
              <PrinterIcon className="w-4 h-4" />
              {successState.printerName}
            </div>
            {successState.isStarted ? (
              <div className="text-pf-success font-medium flex items-center justify-center gap-2 mt-4">
                <CheckCircleIcon className="w-5 h-5" />
                Print job dispatched to printer
              </div>
            ) : (
              <div className="text-pf-text-tertiary flex items-center justify-center gap-2 mt-4">
                <ClockIcon className="w-4 h-4" />
                Waiting in queue
              </div>
            )}
          </div>

          {successState.startError && (
            <div className="mt-4 text-sm text-red-600 bg-red-50 dark:bg-red-900/20 p-3 rounded-lg">
              {successState.startError}
            </div>
          )}
        </div>
      </Modal>
    );
  }

  // Normal queue form
  return (
    <Modal
      isOpen={isOpen}
      onClose={() => onClose(false)}
      title="Queue G-code for Printing"
      footer={
        <div className="flex gap-2 justify-between w-full">
          <Button variant="secondary" onClick={() => onClose(false)}>
            Cancel
          </Button>
          <div className="flex gap-2">
            <Button 
              variant="secondary" 
              onClick={handleQueue} 
              disabled={loading || startNowLoading || noPrintersAvailable || noAvailablePrinters}
            >
              {loading ? 'Queueing…' : 'Queue for Later'}
            </Button>
            <Button 
              variant="primary" 
              onClick={handleQueueAndStart} 
              disabled={loading || startNowLoading || noPrintersAvailable || noAvailablePrinters}
            >
              {startNowLoading ? (
                <span className="flex items-center gap-2">
                  <Spinner className="h-4 w-4" />
                  Starting...
                </span>
              ) : (
                'Queue & Start Now'
              )}
            </Button>
          </div>
        </div>
      }
    >
      <div className="space-y-4">
        <div>
          <div className="text-sm text-pf-text-secondary mb-1">File</div>
          <div className="font-medium text-pf-text-primary">{file.name}</div>
        </div>

        {noPrintersAvailable && requiredModel && (
          <div className="text-sm text-amber-600 bg-amber-50 dark:bg-amber-900/20 p-3 rounded-lg">
            <div className="font-medium">No compatible printers found</div>
            <div className="text-sm mt-1">
              No printers match the required model "{requiredModel}". 
              Add a printer with this model or a matching alias to your fleet.
            </div>
          </div>
        )}

        {noPrintersAvailable && !requiredModel && (
          <div className="text-sm text-amber-600 bg-amber-50 dark:bg-amber-900/20 p-3 rounded-lg">
            <div className="font-medium">No printers configured</div>
            <div className="text-sm mt-1">Add at least one printer before queuing jobs.</div>
          </div>
        )}

        {!noPrintersAvailable && noAvailablePrinters && (
          <div className="text-sm text-amber-600 bg-amber-50 dark:bg-amber-900/20 p-3 rounded-lg">
            <div className="font-medium">All compatible printers are offline</div>
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
                {printers.map(p => (
                  <option key={p.id} value={p.id} disabled={!p.isAvailable}>
                    {p.name} — {p.model} {p.isAvailable ? '' : '(offline)'}
                  </option>
                ))}
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
 * React 19 wrapper with Suspense boundary for printer list loading.
 * All filtering (model, nozzle, material) is done server-side for consistency with auto-assign.
 */
export const QueueGcodeModal: React.FC<Props> = ({ file, isOpen, onClose }) => {
  // Extract all filter criteria from the file
  const requiredModel = file.extractedPrinterModel || file.extractedPrinterModelName;
  const requiredNozzle = file.extractedNozzleDiameter;
  const requiredMaterial = file.extractedMaterial;
  
  // Memoize the printer promise - re-fetch when any filter criteria changes
  // Pass all criteria to server for filtering (consistent with auto-assign logic)
  const printerPromise = useMemo(
    () => fetchPrinters(requiredModel || undefined, requiredNozzle, requiredMaterial || undefined), 
    [requiredModel, requiredNozzle, requiredMaterial]
  );

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
            <Spinner className="w-8 h-8 text-pf-accent mx-auto mb-2" />
            <p className="text-pf-text-secondary">Loading compatible printers...</p>
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
