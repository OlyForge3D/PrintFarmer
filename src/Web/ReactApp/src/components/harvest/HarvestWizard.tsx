import React, { useState, useEffect, useRef, useCallback } from 'react';
import { Printer, GcodeHarvestOperation } from '@/types/api';
import { HarvestWizardStep1Selection } from './steps/HarvestWizardStep1Selection';
import { HarvestWizardStep2Options } from './steps/HarvestWizardStep2Options';
import { HarvestWizardStep3FileSelection } from './steps/HarvestWizardStep3FileSelection';
import { HarvestWizardStep4Progress } from './steps/HarvestWizardStep4Progress';
import { apiClient } from '@/services/api';
import { signalRService } from '@/services/harvest-signalr';

export interface HarvestDiscoveredFile {
  id: string;
  name: string;
  size: number;
  path: string;
  slicerName?: string;
  material?: string;
}

export interface HarvestOptions {
  includeSubdirectories: boolean;
  maxFileSizeBytes: number;
  fileExtensions: string[];
  minFileSizeBytes: number;
  duplicateHandling: 'skip' | 'replace' | 'keep';
}

export interface HarvestWizardState {
  selectedPrinterId: string | null;
  options: HarvestOptions;
  operationId: string | null;
  discoveredFiles: HarvestDiscoveredFile[];
  isDiscovering: boolean;
  selectedFileIds: Set<string>;
  selectedFiles: HarvestDiscoveredFile[]; // Store selected file details for Step 4
}

interface HarvestWizardProps {
  printers: Printer[];
  onClose: () => void;
  onComplete?: () => void;
}

/**
 * Multi-step harvest wizard
 * Step 1: Select printer
 * Step 2: Configure harvest options
 * Step 3: Display discovered files, allow selection
 * Step 4: Show import progress with real-time updates
 */
export function HarvestWizard({ printers, onClose, onComplete }: HarvestWizardProps) {
  const [step, setStep] = useState(1);
  const [activeHarvests, setActiveHarvests] = useState<GcodeHarvestOperation[]>([]);
  const [state, setState] = useState<HarvestWizardState>({
    selectedPrinterId: null,
    options: {
      includeSubdirectories: true,
      maxFileSizeBytes: 100 * 1024 * 1024, // 100MB default
      fileExtensions: ['gcode', 'gco', 'g'],
      minFileSizeBytes: 0,
      duplicateHandling: 'skip',
    },
    operationId: null,
    discoveredFiles: [],
    isDiscovering: false,
    selectedFileIds: new Set(),
    selectedFiles: [],
  });

  // Refs for managing file discovery batching and subscriptions
  const subscriptionRef = useRef<(() => void) | null>(null);
  const pendingFilesRef = useRef<HarvestDiscoveredFile[]>([]);
  const batchTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const discoveryStartedRef = useRef(false);

  // Batch file updates for better performance (batches updates every 100ms)
  const flushPendingFiles = useCallback(() => {
    if (pendingFilesRef.current.length > 0) {
      const filesToAdd = pendingFilesRef.current;
      pendingFilesRef.current = [];
      
      setState(prev => ({
        ...prev,
        discoveredFiles: [...prev.discoveredFiles, ...filesToAdd],
        selectedFileIds: new Set([...prev.selectedFileIds, ...filesToAdd.map(f => f.id)]),
      }));
    }
  }, []);

  const queueFileForBatch = useCallback((file: HarvestDiscoveredFile) => {
    pendingFilesRef.current.push(file);
    
    // Clear existing timeout
    if (batchTimeoutRef.current) {
      clearTimeout(batchTimeoutRef.current);
    }
    
    // Set a new timeout to flush after 100ms of no new files
    batchTimeoutRef.current = setTimeout(flushPendingFiles, 100);
  }, [flushPendingFiles]);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      // Unsubscribe from SignalR on unmount
      if (subscriptionRef.current) {
        subscriptionRef.current();
      }
      // Clear any pending batch timeout
      if (batchTimeoutRef.current) {
        clearTimeout(batchTimeoutRef.current);
      }
      // Flush any remaining pending files
      flushPendingFiles();
    };
  }, [flushPendingFiles]);

  // Fetch active harvests to prevent selecting printers with active operations
  useEffect(() => {
    const fetchActiveHarvests = async () => {
      try {
        const active = await apiClient.getAllActiveHarvests();
        setActiveHarvests(active);
      } catch (error) {
        console.error('Failed to fetch active harvests:', error);
      }
    };
    
    fetchActiveHarvests();
    
    // Poll every 5 seconds to keep track of active harvests
    const interval = setInterval(fetchActiveHarvests, 5000);
    return () => clearInterval(interval);
  }, []);

  const handleStep1Complete = (printerId: string) => {
    setState(prev => ({ ...prev, selectedPrinterId: printerId }));
    setStep(2);
  };

  const handleStep2Complete = (options: HarvestWizardState['options']) => {
    setState(prev => ({ ...prev, options }));
    setStep(3);
  };

  const handleStartDiscovery = useCallback(async () => {
    // Prevent multiple calls to start discovery
    if (discoveryStartedRef.current || !state.selectedPrinterId) return;
    discoveryStartedRef.current = true;

    setState(prev => ({ ...prev, isDiscovering: true, discoveredFiles: [] }));

    try {
      // Start harvest discovery on backend - this returns the operation ID
      const result = await apiClient.startHarvestOperation(state.selectedPrinterId, {
        includeSubdirectories: state.options.includeSubdirectories,
        fileExtensions: state.options.fileExtensions,
        minFileSizeBytes: state.options.minFileSizeBytes,
        maxFileSizeBytes: state.options.maxFileSizeBytes,
        duplicateHandling: state.options.duplicateHandling,
      });

      const operationId = result.operationId;
      setState(prev => ({ ...prev, operationId }));

      // Join SignalR group for this discovery operation
      signalRService.connect();
      signalRService.joinHarvestGroup(operationId);

      // Unsubscribe from previous subscription if it exists
      if (subscriptionRef.current) {
        subscriptionRef.current();
      }

      // Subscribe to discovered files via SignalR with batching for performance
      subscriptionRef.current = signalRService.onHarvestFileDiscovered((evt) => {
        if (evt.operationId === operationId) {
          // Convert discovered file event to HarvestDiscoveredFile
          const discoveredFile: HarvestDiscoveredFile = {
            id: evt.fileId,
            name: evt.fileName,
            size: evt.fileSize,
            path: evt.filePath,
            slicerName: evt.extractedSlicer,
            material: evt.extractedMaterial,
          };

          // Queue file for batch update instead of updating immediately
          queueFileForBatch(discoveredFile);
        }
      });

      // Subscribe to discovery completion event
      signalRService.onHarvestDiscoveryComplete((evt) => {
        if (evt.operationId === operationId) {
          // Flush any remaining pending files before marking discovery as complete
          flushPendingFiles();
          // Mark discovery as complete
          setState(prev => ({ ...prev, isDiscovering: false }));
          if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
            console.info(`[HarvestWizard] Discovery complete for operation ${operationId}: ${evt.totalFilesDiscovered} files found`);
          }
        }
      });
    } catch (error) {
      console.error('Failed to start discovery:', error);
      setState(prev => ({ ...prev, isDiscovering: false }));
      discoveryStartedRef.current = false;
    }
  }, [state.selectedPrinterId, state.options, queueFileForBatch, flushPendingFiles]);

  const handleStep3Complete = (selectedFileIds: string[]) => {
    // Store selected file details for Step 4 display
    const selectedFiles = state.discoveredFiles.filter(f => selectedFileIds.includes(f.id));
    setState(prev => ({ ...prev, selectedFiles }));
    
    // Move to Step 4 with selected files
    setStep(4);
    // Begin import with selected files
    handleImport(selectedFileIds);
  };

  const handleImport = async (selectedFileIds: string[]) => {
    if (!state.operationId) {
      console.error('No operation ID available for import');
      return;
    }

    if ((window as unknown as { PrintFarmerDebug?: Record<string, unknown> }).PrintFarmerDebug?.harvestSignalR) {
      console.info(`[HarvestWizard] Starting import with ${selectedFileIds.length} selected files:`, selectedFileIds);
    }

    try {
      // Call API to import selected file IDs
      await apiClient.importSelectedGcodeFiles({
        harvestOperationId: state.operationId,
        fileIds: selectedFileIds,
      });

      // Subscribe to per-file import progress via SignalR
      signalRService.onHarvestFileProgress((evt) => {
        if (evt.operationId === state.operationId) {
          // Progress update - in future this can be used to track file-level progress
          console.debug(`File progress: ${evt.fileName} - ${evt.percent}%`);
        }
      });
    } catch (error) {
      console.error('Failed to start import:', error);
    }
  };

  const handleBack = () => {
    if (step > 1) {
      // Reset discovery flag when going back from step 3
      if (step === 3) {
        discoveryStartedRef.current = false;
      }
      setStep(step - 1);
    }
  };

  const handleCancel = () => {
    onClose();
  };

  const handleCompleted = () => {
    onComplete?.();
    onClose();
  };

  const stepConfig = [
    {
      title: 'Select Printer',
      subtitle: 'Choose which printer to harvest files from',
    },
    {
      title: 'Harvest Options',
      subtitle: 'Configure discovery parameters',
    },
    {
      title: 'Select Files',
      subtitle: 'Choose which files to import',
    },
    {
      title: 'Import Progress',
      subtitle: 'Importing selected files',
    },
  ];

  const current = stepConfig[step - 1];

  return (
    <div className="w-full space-y-4">
      {/* Header - Progress indicator and title */}
      <div className="border-b border-pf-border pb-4">
        <div className="mb-3">
          <h2 className="text-xl font-bold text-pf-text-primary">{current.title}</h2>
          <p className="text-sm text-pf-text-secondary mt-1">{current.subtitle}</p>
        </div>
        {/* Progress indicator */}
        <div className="flex gap-1">
          {[1, 2, 3, 4].map(s => (
            <div
              key={s}
              className={`h-1 flex-1 rounded ${
                s < step
                  ? 'bg-pf-success'
                  : s === step
                    ? 'bg-pf-accent'
                    : 'bg-pf-border'
              }`}
            />
          ))}
        </div>
      </div>

      {/* Content */}
      <div className="min-h-96">
        {step === 1 && (
          <HarvestWizardStep1Selection
            printers={printers}
            selectedPrinterId={state.selectedPrinterId}
            onSelect={handleStep1Complete}
            activeHarvests={activeHarvests}
          />
        )}
        {step === 2 && (
          <HarvestWizardStep2Options
            options={state.options}
            onComplete={handleStep2Complete}
            onStartDiscovery={handleStartDiscovery}
          />
        )}
        {step === 3 && (
          <HarvestWizardStep3FileSelection
            files={state.discoveredFiles}
            isDiscovering={state.isDiscovering}
            onComplete={handleStep3Complete}
          />
        )}
        {step === 4 && (
          <HarvestWizardStep4Progress
            totalFiles={state.selectedFileIds.size}
            selectedFiles={state.selectedFiles}
            operationId={state.operationId || undefined}
            onCompleted={handleCompleted}
            onCancel={() => setStep(3)}
          />
        )}
      </div>

      {/* Footer - Navigation buttons */}
      <div className="border-t border-pf-border pt-4 flex justify-between items-center">
        <button
          onClick={handleBack}
          disabled={step === 1 || step === 4}
          className="px-4 py-2 rounded border border-pf-border text-pf-text-primary hover:bg-pf-hover disabled:opacity-50 disabled:cursor-not-allowed"
        >
          Back
        </button>
        <div className="text-sm text-pf-text-secondary">
          Step {step} of 4
        </div>
        <button
          onClick={handleCancel}
          className="px-4 py-2 rounded border border-pf-border text-pf-text-primary hover:bg-pf-hover"
        >
          Cancel
        </button>
      </div>
    </div>
  );
}
