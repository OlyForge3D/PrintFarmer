import React, { use, Suspense, useState, useMemo, useEffect } from 'react';
import { Button, Checkbox, Select, Spinner } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { apiClient } from '@/services/api';
import { printJobQueueService, EnqueuePrintJobRequest } from '@/services/printJobQueueService';
import { SpoolValidationModal } from '@/features/queue/components/SpoolValidationModal';
import { validateSpoolForDispatch } from '@/features/queue/utils/spoolValidation';
import type { SpoolValidationContext } from '@/features/queue/utils/spoolValidation';
import { GcodeFile, SpoolmanFilament } from '@/types/api';
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
  const [spoolValidationCtx, setSpoolValidationCtx] = useState<SpoolValidationContext | null>(null);

  // Override state: allows user to bypass model matching and see all printers
  const [overrideModelFilter, setOverrideModelFilter] = useState(false);
  const [allPrinters, setAllPrinters] = useState<PrinterOption[] | null>(null);
  const [loadingAllPrinters, setLoadingAllPrinters] = useState(false);

  // Use overridden printer list when model filter is bypassed
  const effectivePrinters = overrideModelFilter && allPrinters ? allPrinters : printers;

  // Filament picker state
  const [filaments, setFilaments] = useState<SpoolmanFilament[]>([]);
  // undefined = user hasn't chosen yet; null = user explicitly cleared
  const [userSelectedFilamentId, setUserSelectedFilamentId] = useState<number | null | undefined>(undefined);

  // Load filaments from Spoolman (graceful fallback if not configured)
  useEffect(() => {
    let cancelled = false;
    apiClient.getFilaments()
      .then(data => { if (!cancelled) setFilaments(data); })
      .catch(() => { /* Spoolman not configured — no filaments available */ });
    return () => { cancelled = true; };
  }, []);

  // Filter filaments by the G-code file's required material type
  const filteredFilaments = useMemo(() => {
    if (!file.extractedMaterial) return filaments;
    const needle = file.extractedMaterial.toLowerCase();
    return filaments.filter(f => f.material?.toLowerCase() === needle);
  }, [filaments, file.extractedMaterial]);

  // Auto-select filament matching extracted material (only when user hasn't chosen)
  const effectiveFilamentId = useMemo(() => {
    if (userSelectedFilamentId !== undefined) return userSelectedFilamentId ?? undefined;
    // Auto-select: find first filament matching extracted material
    if (filteredFilaments.length > 0 && file.extractedMaterial) {
      return filteredFilaments[0]?.id;
    }
    return undefined;
  }, [filteredFilaments, file.extractedMaterial, userSelectedFilamentId]);

  const selectedFilament = useMemo(
    () => filaments.find(f => f.id === effectiveFilamentId),
    [filaments, effectiveFilamentId]
  );

  const handleShowAllPrinters = async () => {
    setLoadingAllPrinters(true);
    try {
      const all = await fetchPrinters(
        undefined,
        file.extractedNozzleDiameter,
        file.extractedMaterial || undefined
      );
      setAllPrinters(all);
      setOverrideModelFilter(true);
      setAutoAssign(false);
    } finally {
      setLoadingAllPrinters(false);
    }
  };

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
    // Omit model filter when user overrides — they've explicitly chosen a printer
    if (!overrideModelFilter && typeof requiredModelValue === 'string' && requiredModelValue.length > 0) req.requiredPrinterModel = requiredModelValue;

    // Include selected Spoolman filament
    if (selectedFilament) {
      req.spoolmanFilamentId = selectedFilament.id;
      req.filamentName = selectedFilament.name || undefined;
      req.filamentVendor = selectedFilament.vendor || undefined;
      req.filamentColor = selectedFilament.colorHex || undefined;
    }

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

  /** Dispatch a job, validating spool state first. */
  const dispatchWithSpoolCheck = async (jobId: string, printerName: string) => {
    const req = buildRequest();
    const printerId = autoAssign ? undefined : selectedPrinter;

    if (printerId) {
      const ctx = await validateSpoolForDispatch(
        { id: jobId, name: file.name, requiredMaterialType: req.requiredMaterialType },
        { id: printerId, name: printerName },
      );
      if (ctx) {
        setSpoolValidationCtx(ctx);
        return;
      }
    }

    await apiClient.dispatchPrintQueueJob(jobId);
    setSuccessState({ jobId, printerName, isStarting: false, isStarted: true });
  };

  const handleQueueAndStart = async () => {
    setStartNowLoading(true);
    setError(null);
    try {
      const req = buildRequest();
      const result = await printJobQueueService.enqueue(req);
      const printerName = result.assignedPrinterName || 'Unknown Printer';

      await dispatchWithSpoolCheck(result.id, printerName);
      setStartNowLoading(false);
    } catch (err) {
      setStartNowLoading(false);
      setError(err instanceof Error ? err.message : 'Failed to queue and start job');
    }
  };

  const handleStartNow = async () => {
    if (!successState) return;

    setSuccessState({ ...successState, isStarting: true, startError: undefined });
    try {
      await dispatchWithSpoolCheck(successState.jobId, successState.printerName);
      if (!spoolValidationCtx) {
        setSuccessState({ ...successState, isStarting: false, isStarted: true });
      }
    } catch (err) {
      setSuccessState({
        ...successState,
        isStarting: false,
        startError: err instanceof Error ? err.message : 'Failed to start print'
      });
    }
  };

  /** Called when spool validation modal confirms (spool selected or user proceeds anyway). */
  const handleSpoolValidated = async (jobId: string) => {
    setSpoolValidationCtx(null);
    try {
      await apiClient.dispatchPrintQueueJob(jobId);
      const printerName = successState?.printerName || 'Printer';
      setSuccessState({ jobId, printerName, isStarting: false, isStarted: true });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to start print');
    } finally {
      setStartNowLoading(false);
    }
  };

  // All printer compatibility filtering is now done server-side.
  // Printers returned from the API are already filtered by model, nozzle, and material.
  // When override is active, effectivePrinters contains all printers (no model filter).

  // Spool validation modal may need to show even when the queue modal would be hidden
  if (spoolValidationCtx) {
    return (
      <SpoolValidationModal
        isOpen
        onClose={() => {
          setSpoolValidationCtx(null);
          setStartNowLoading(false);
        }}
        onProceed={handleSpoolValidated}
        context={spoolValidationCtx}
      />
    );
  }

  if (!isOpen) return null;

  // Use effectivePrinters for availability checks (respects override mode)
  const noPrintersAvailable = effectivePrinters.length === 0;
  const availablePrinters = effectivePrinters.filter(p => p.isAvailable);
  const noAvailablePrinters = availablePrinters.length === 0;
  // Original model-filtered list is empty but we haven't overridden yet
  const noModelMatch = printers.length === 0 && !!requiredModel && !overrideModelFilter;

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
                loading={successState.isStarting}
                iconLeft={<PrinterIcon className="w-4 h-4" />}
              >
                Start Print Now
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
            <div className="mt-4 text-sm text-pf-error bg-pf-error/10 p-3 rounded-lg">
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
              disabled={loading || startNowLoading || noPrintersAvailable || noAvailablePrinters || (!autoAssign && !selectedPrinter)}
            >
              {loading ? 'Queueing…' : 'Queue for Later'}
            </Button>
            <Button 
              variant="primary" 
              onClick={handleQueueAndStart} 
              disabled={loading || startNowLoading || noPrintersAvailable || noAvailablePrinters || (!autoAssign && !selectedPrinter)}
            >
              {startNowLoading ? (
                <span className="flex items-center gap-2">
                  <Spinner size="sm" />
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

        {noModelMatch && (
          <div className="text-sm text-pf-warning bg-pf-warning/10 p-3 rounded-lg">
            <div className="font-medium">No compatible printers found</div>
            <div className="text-sm mt-1">
              No printers match the required model &ldquo;{requiredModel}&rdquo;.
              You can add a matching alias to a printer, or select any printer manually.
            </div>
            <Button
              variant="secondary"
              className="mt-2"
              onClick={handleShowAllPrinters}
              disabled={loadingAllPrinters}
            >
              {loadingAllPrinters ? (
                <span className="flex items-center gap-2">
                  <Spinner size="sm" />
                  Loading printers…
                </span>
              ) : (
                'Show all printers'
              )}
            </Button>
          </div>
        )}

        {noPrintersAvailable && !requiredModel && !overrideModelFilter && (
          <div className="text-sm text-pf-warning bg-pf-warning/10 p-3 rounded-lg">
            <div className="font-medium">No printers configured</div>
            <div className="text-sm mt-1">Add at least one printer before queuing jobs.</div>
          </div>
        )}

        {overrideModelFilter && (
          <div className="text-sm text-pf-accent bg-pf-accent-bg/15 p-3 rounded-lg">
            <div className="font-medium">Model filter bypassed</div>
            <div className="text-sm mt-1">
              Showing all printers. The file expects model &ldquo;{requiredModel}&rdquo; — select the correct printer manually.
            </div>
          </div>
        )}

        {!noPrintersAvailable && noAvailablePrinters && (
          <div className="text-sm text-pf-warning bg-pf-warning/10 p-3 rounded-lg">
            <div className="font-medium">All compatible printers are offline</div>
            <div className="text-sm mt-1">Please wait for at least one printer to come online.</div>
          </div>
        )}

        {!noPrintersAvailable && availablePrinters.length > 0 && !overrideModelFilter && (
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
                {effectivePrinters.map(p => (
                  <option key={p.id} value={p.id} disabled={!p.isAvailable}>
                    {p.name} — {p.model} {p.isAvailable ? '' : '(offline)'}
                  </option>
                ))}
              </Select>
            </div>
          </div>
        )}

        {/* Filament picker (optional, loads from Spoolman) — filtered by required material */}
        {filaments.length > 0 && (
          <div>
            <label htmlFor="filament-select" className="text-sm text-pf-text-secondary mb-2 block">
              Filament{file.extractedMaterial ? ` (${file.extractedMaterial})` : ''} {selectedFilament && (
                <span
                  className="inline-block w-3 h-3 rounded-full ml-1 align-middle border border-pf-border"
                  style={{ backgroundColor: selectedFilament.colorHex || '#888' }}
                  aria-hidden="true"
                />
              )}
            </label>
            <Select
              id="filament-select"
              aria-label={`Select filament for ${file.name}`}
              value={effectiveFilamentId?.toString() ?? ''}
              onChange={(e) => {
                const val = (e.target as HTMLSelectElement).value;
                setUserSelectedFilamentId(val ? parseInt(val, 10) : null);
              }}
            >
              <option value="">-- No filament --</option>
              {filteredFilaments.map(f => (
                <option key={f.id} value={f.id.toString()}>
                  {f.vendor ? `${f.vendor} — ` : ''}{f.name || 'Unnamed'}{f.material ? ` (${f.material})` : ''}
                </option>
              ))}
            </Select>
          </div>
        )}

        {error && (
          <div className="text-sm text-pf-error bg-pf-error/10 p-3 rounded-lg">
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

        {/* Print details from gcode metadata */}
        {(file.totalLayers || file.objectDimensionX || file.supportEnabled || file.extractedPrintTime) && (
          <div className="text-sm text-pf-text-secondary bg-pf-bg-2 p-3 rounded-lg">
            <div className="font-medium mb-1">Print details:</div>
            {file.totalLayers != null && (
              <div>- Layers: {file.totalLayers.toLocaleString()}{file.extractedLayerHeight ? ` @ ${file.extractedLayerHeight}mm` : ''}{file.firstLayerHeight ? ` (first: ${file.firstLayerHeight}mm)` : ''}</div>
            )}
            {file.objectDimensionX != null && file.objectDimensionY != null && (
              <div>- Dimensions: {file.objectDimensionX} × {file.objectDimensionY} × {file.objectDimensionZ ?? '?'} mm</div>
            )}
            {file.supportEnabled && (
              <div className="text-pf-warning">- ⚠ Support material enabled (extra filament + post-processing)</div>
            )}
            {file.toolChangesCount != null && file.toolChangesCount > 0 && (
              <div>- Tool changes: {file.toolChangesCount}</div>
            )}
            {file.objectCount != null && file.objectCount > 1 && (
              <div>- Objects on plate: {file.objectCount}</div>
            )}
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
            <Spinner size="lg" className="mx-auto mb-2" />
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
