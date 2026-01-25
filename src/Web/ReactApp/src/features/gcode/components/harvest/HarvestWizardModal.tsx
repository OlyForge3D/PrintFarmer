/**
 * HarvestWizardModal - A proper modal wizard for harvesting G-code files from printers
 * 
 * This modal wizard manages the harvest operation lifecycle while delegating
 * file list management to the IndexedFilesList component.
 * 
 * Flow:
 * 1. Step 1: Select a printer from online printers
 * 2. Step 2: Configure harvest options (subdirectories, file extensions, etc.)
 * 3. Step 3: Discovery & file selection - IndexedFilesList handles file display and selection
 * 4. User selects files and clicks Import
 * 5. Import progress is shown, then modal closes
 */
import React, { useState, useEffect, useRef, useCallback } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Printer, GcodeHarvestOperation } from '@/types/api';
import { HarvestWizardStep1Selection } from './steps/HarvestWizardStep1Selection';
import { HarvestWizardStep2Options, HarvestWizardStep2OptionsRef } from './steps/HarvestWizardStep2Options';
import { IndexedFilesList, IndexedFilesListRef } from './IndexedFilesList';
import { apiClient } from '@/services/api';
import { signalRService, HarvestOperationProgress, HarvestOperationCompletedEvent } from '@/services/harvest-signalr';
import { toast } from 'sonner';

export interface HarvestOptions {
  includeSubdirectories: boolean;
  maxFileSizeBytes: number;
  fileExtensions: string[];
  minFileSizeBytes: number;
  duplicateHandling: 'skip' | 'replace' | 'keep';
}

interface HarvestWizardModalProps {
  isOpen: boolean;
  onClose: () => void;
  printers: Printer[];
  onComplete?: () => void;
  activeHarvests?: GcodeHarvestOperation[];
}

type WizardStep = 'select-printer' | 'configure-options' | 'discovery-import';

export function HarvestWizardModal({
  isOpen,
  onClose,
  printers,
  onComplete,
  activeHarvests = [],
}: HarvestWizardModalProps) {
  // Wizard state
  const [step, setStep] = useState<WizardStep>('select-printer');
  const [selectedPrinterId, setSelectedPrinterId] = useState<string | null>(null);
  const [options, setOptions] = useState<HarvestOptions>({
    includeSubdirectories: true,
    maxFileSizeBytes: 100 * 1024 * 1024, // 100MB
    fileExtensions: ['gcode', 'gco', 'g'],
    minFileSizeBytes: 0,
    duplicateHandling: 'skip',
  });
  
  // Discovery state (operation-level - file-level state is managed by IndexedFilesList)
  const [operationId, setOperationId] = useState<string | null>(null);
  const [isDiscovering, setIsDiscovering] = useState(false);
  const [discoveryComplete, setDiscoveryComplete] = useState(false);
  const [filesFound, setFilesFound] = useState(0);
  const [filesAdded, setFilesAdded] = useState(0);
  const [filesSkipped, setFilesSkipped] = useState(0);
  const [filesErrored, setFilesErrored] = useState(0);
  const [selectedCount, setSelectedCount] = useState(0);
  
  // Import state
  const [isImporting, setIsImporting] = useState(false);
  const [importComplete, setImportComplete] = useState(false);
  
  // Refs for cleanup and component access
  const subscriptionsRef = useRef<(() => void)[]>([]);
  const step2OptionsRef = useRef<HarvestWizardStep2OptionsRef | null>(null);
  const indexedFilesListRef = useRef<IndexedFilesListRef | null>(null);
  // Use ref to track operationId for callbacks to avoid stale closure issues
  const operationIdRef = useRef<string | null>(null);
  // Buffer for events received before we have an operationId
  const progressBufferRef = useRef<HarvestOperationProgress[]>([]);
  const completedBufferRef = useRef<HarvestOperationCompletedEvent[]>([]);
  const discoveryCompleteBufferRef = useRef<{ operationId: string }[]>([]);
  
  // Keep operationIdRef in sync with state
  useEffect(() => {
    operationIdRef.current = operationId;
  }, [operationId]);
  
  // Cleanup subscriptions on unmount or when modal closes
  useEffect(() => {
    return () => {
      subscriptionsRef.current.forEach(unsub => unsub());
      subscriptionsRef.current = [];
    };
  }, []);
  
  // Reset state when modal opens
  useEffect(() => {
    if (isOpen) {
      setStep('select-printer');
      setSelectedPrinterId(null);
      setOperationId(null);
      setIsDiscovering(false);
      setDiscoveryComplete(false);
      setFilesFound(0);
      setFilesAdded(0);
      setFilesSkipped(0);
      setFilesErrored(0);
      setSelectedCount(0);
      setIsImporting(false);
      setImportComplete(false);
      // Clear event buffers
      progressBufferRef.current = [];
      completedBufferRef.current = [];
      discoveryCompleteBufferRef.current = [];
    }
  }, [isOpen]);
  
  // Handle operation progress events (operation-level, not file-level)
  // If we don't have operationId yet, buffer the event for later processing
  const handleOperationProgress = useCallback((progress: HarvestOperationProgress) => {
    const currentOpId = operationIdRef.current;
    if (window.PrintFarmerDebug?.harvest) {
      console.log('[HarvestWizard] OperationProgress event:', progress.filesFound, 'files found, op:', progress.operationId, 'current:', currentOpId);
    }
    
    // If we don't have operationId yet, buffer the event
    if (!currentOpId) {
      if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Buffering progress (no operationId yet)'); }
      progressBufferRef.current.push(progress);
      return;
    }
    
    if (progress.operationId !== currentOpId) return;
    
    setFilesFound(progress.filesFound);
    setFilesAdded(progress.filesAdded);
    setFilesSkipped(progress.filesSkipped);
    setFilesErrored(progress.filesErrored);
  }, []); // No dependencies - uses ref
  
  // Handle operation completed
  // If we don't have operationId yet, buffer the event for later processing
  const handleOperationCompleted = useCallback((evt: HarvestOperationCompletedEvent) => {
    const currentOpId = operationIdRef.current;
    if (window.PrintFarmerDebug?.harvest) {
      console.log('[HarvestWizard] OperationCompleted event for op:', evt.operationId, 'current:', currentOpId);
    }
    
    // If we don't have operationId yet, buffer the event
    if (!currentOpId) {
      if (window.PrintFarmerDebug?.harvest) {
        console.log('[HarvestWizard] Buffering completed event (no operationId yet)'); 
      }

      completedBufferRef.current.push(evt);
      return;
    }
    
    if (evt.operationId !== currentOpId) return;
    
    if (window.PrintFarmerDebug?.harvest) {
      console.log('[HarvestWizard] Applying completed event - discovery done!');
    }

    setFilesAdded(evt.filesAdded);
    setFilesSkipped(evt.filesSkipped);
    setFilesErrored(evt.filesErrored);
    setDiscoveryComplete(true);
    setIsDiscovering(false);
  }, []); // No dependencies - uses ref
  
  // Cleanup SignalR subscriptions when operationId changes or component unmounts
  // Note: Subscriptions are created in handleOptionsComplete BEFORE the API call
  // This useEffect only handles cleanup when operationId is cleared (modal closes)
  useEffect(() => {
    if (!operationId) {
      // When operationId becomes null (modal reset/close), clean up any existing subscriptions
      subscriptionsRef.current.forEach(unsub => unsub());
      subscriptionsRef.current = [];
      return;
    }
    
    // Subscriptions and group join are handled in handleOptionsComplete
    // This effect only needs to provide cleanup when operationId changes
    return () => {
      subscriptionsRef.current.forEach(unsub => unsub());
      subscriptionsRef.current = [];
      signalRService.leaveHarvestGroup(operationId);
    };
  }, [operationId]);
  
  // Step 1: Printer selection
  const handlePrinterSelect = (printerId: string) => {
    setSelectedPrinterId(printerId);
    setStep('configure-options');
  };
  
  // Process buffered operation-level events once we have operationId
  // Note: File-level events are handled by IndexedFilesList
  const processBufferedEvents = useCallback((opId: string) => {
    if (window.PrintFarmerDebug?.harvest) {
      console.log('[HarvestWizard] Processing buffered events for operation:', opId);
      console.log('[HarvestWizard] Buffered progress events:', progressBufferRef.current.length);
    }
    
    // Process buffered progress events - take the latest one for this operation
    const matchingProgressEvents = progressBufferRef.current.filter(p => p.operationId === opId);
    if (matchingProgressEvents.length > 0) {
      const latest = matchingProgressEvents[matchingProgressEvents.length - 1];
      if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Applying latest progress:', latest.filesFound, 'files found'); }
      setFilesFound(latest.filesFound);
      setFilesAdded(latest.filesAdded);
      setFilesSkipped(latest.filesSkipped);
      setFilesErrored(latest.filesErrored);
    }
    
    // Process buffered completed events
    const matchingCompletedEvents = completedBufferRef.current.filter(e => e.operationId === opId);
    if (matchingCompletedEvents.length > 0) {
      const latest = matchingCompletedEvents[matchingCompletedEvents.length - 1];
      if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Applying buffered completed event - discovery done!'); }
      setFilesAdded(latest.filesAdded);
      setFilesSkipped(latest.filesSkipped);
      setFilesErrored(latest.filesErrored);
      setDiscoveryComplete(true);
      setIsDiscovering(false);
    }
    
    // Process buffered discovery complete events
    const matchingDiscoveryComplete = discoveryCompleteBufferRef.current.filter(e => e.operationId === opId);
    if (matchingDiscoveryComplete.length > 0) {
      if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Applying buffered discovery complete event'); }
      setDiscoveryComplete(true);
      setIsDiscovering(false);
    }
    
    // Clear buffers
    progressBufferRef.current = [];
    completedBufferRef.current = [];
    discoveryCompleteBufferRef.current = [];
  }, []);
  
  // Step 2: Options configuration and start discovery
  const handleOptionsComplete = async (newOptions: HarvestOptions) => {
    setOptions(newOptions);
    
    if (!selectedPrinterId) return;
    
    setIsDiscovering(true);
    setStep('discovery-import');
    
    // CRITICAL: Subscribe to operation-level SignalR events BEFORE starting the API call
    // File-level events (discovered, updated) are handled by IndexedFilesList
    if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Subscribing to operation-level SignalR events BEFORE starting operation'); }
    const unsubProgress = signalRService.onHarvestOperationProgress(handleOperationProgress);
    const unsubComplete = signalRService.onHarvestOperationCompleted(handleOperationCompleted);
    const unsubDiscoveryComplete = signalRService.onHarvestDiscoveryComplete((evt) => {
      const currentOpId = operationIdRef.current;
      if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] DiscoveryComplete event for op:', evt.operationId, 'current:', currentOpId); }
      
      // If we don't have operationId yet, buffer the event
      if (!currentOpId) {
        if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Buffering discovery complete event (no operationId yet)'); }
        discoveryCompleteBufferRef.current.push(evt);
        return;
      }
      
      if (evt.operationId === currentOpId) {
        if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Applying discovery complete event'); }
        setDiscoveryComplete(true);
        setIsDiscovering(false);
      }
    });
    
    subscriptionsRef.current = [unsubProgress, unsubComplete, unsubDiscoveryComplete];
    
    // Ensure SignalR is connected
    await signalRService.connect();
    
    try {
      // Start harvest discovery
      const result = await apiClient.startHarvestOperation(selectedPrinterId, {
        includeSubdirectories: newOptions.includeSubdirectories,
        fileExtensions: newOptions.fileExtensions,
        minFileSizeBytes: newOptions.minFileSizeBytes,
        maxFileSizeBytes: newOptions.maxFileSizeBytes,
        duplicateHandling: newOptions.duplicateHandling,
      });
      
      const queueItemId = result.queueItemId;
      if (!queueItemId) {
        throw new Error('No queue item ID returned');
      }
      
      // Wait for operation to be created
      const opId = await apiClient.waitForHarvestOperationCreated(selectedPrinterId, 10000);
      if (!opId) {
        throw new Error('Operation ID could not be retrieved');
      }
      
      // CRITICAL: Set operationIdRef BEFORE processing buffered events
      // This allows any live events that arrive during processing to be handled correctly
      operationIdRef.current = opId;
      
      // Process any events that were buffered while we were waiting for operationId
      processBufferedEvents(opId);
      
      // Join the SignalR group for this operation (for targeted server pushes)
      await signalRService.joinHarvestGroup(opId);
      
      // Now set state to trigger the useEffect (which will skip subscription since already done)
      setOperationId(opId);
      
      // Fetch operation status from API to check if discovery is already complete
      // File-level data is handled by IndexedFilesList via its own API call
      try {
        if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Fetching operation status for:', opId); }
        
        // Fetch operation to check if discovery is already complete
        const operation = await apiClient.getHarvestOperation(opId);
        if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Operation status:', operation.status, 'filesFound:', operation.filesFound); }
        
        // Update progress from operation
        setFilesFound(operation.filesFound);
        setFilesAdded(operation.filesAdded);
        setFilesSkipped(operation.filesSkipped);
        setFilesErrored(operation.filesErrored);
        
        // Check if discovery is already complete
        if (operation.status !== 'Running') {
          if (window.PrintFarmerDebug?.harvest) { console.log('[HarvestWizard] Discovery already complete based on API state'); }
          setDiscoveryComplete(true);
          setIsDiscovering(false);
        }
      } catch (err) {
        console.error('[HarvestWizard] Failed to fetch operation status:', err);
      }
    } catch (error) {
      console.error('Failed to start discovery:', error);
      toast.error('Failed to start file discovery');
      setIsDiscovering(false);
      // Clean up subscriptions on error
      subscriptionsRef.current.forEach(unsub => unsub());
      subscriptionsRef.current = [];
    }
  };
  
  // Handle import via IndexedFilesList ref
  const handleImport = async () => {
    if (!indexedFilesListRef.current) return;
    
    const selectedCountNow = indexedFilesListRef.current.getSelectedCount();
    if (selectedCountNow === 0) {
      toast.warning('Please select at least one file to import');
      return;
    }
    
    setIsImporting(true);
    
    try {
      const result = await indexedFilesListRef.current.importSelected();
      
      if (result.failed > 0) {
        toast.error(`Import completed with issues: ${result.imported} imported, ${result.skipped} skipped, ${result.failed} failed`);
      } else if (result.imported > 0 || result.skipped > 0) {
        toast.success(`Successfully imported ${result.imported} files${result.skipped > 0 ? `, ${result.skipped} skipped` : ''}`);
      }
      
      setImportComplete(true);
      setFilesAdded(result.imported);
      setFilesSkipped(result.skipped);
      setFilesErrored(result.failed);
    } catch (err) {
      console.error('Import failed:', err);
      toast.error('Failed to import files');
    } finally {
      setIsImporting(false);
    }
  };
  
  // Handle wizard navigation
  const handleBack = () => {
    if (step === 'configure-options') {
      setStep('select-printer');
    }
    // Can't go back from discovery step
  };
  
  const handleClose = () => {
    if (importComplete) {
      onComplete?.();
    }
    onClose();
  };
  
  // Render step content
  const renderStepContent = () => {
    switch (step) {
      case 'select-printer':
        return (
          <HarvestWizardStep1Selection
            printers={printers}
            selectedPrinterId={selectedPrinterId}
            onSelect={handlePrinterSelect}
            activeHarvests={activeHarvests}
          />
        );
        
      case 'configure-options':
        return (
          <HarvestWizardStep2Options
            ref={step2OptionsRef}
            options={options}
            onComplete={handleOptionsComplete}
            onStartDiscovery={async () => {
              // This is called when the user clicks Start in Step 2
              // The actual start happens in handleOptionsComplete
            }}
          />
        );
        
      case 'discovery-import':
        return (
          <div className="space-y-4">
            {/* Stats row */}
            <div className="flex flex-wrap gap-2">
              <div className="h-7 rounded bg-pf-bg-2 border border-pf-border text-pf-text-0 text-xs font-semibold flex items-center justify-center px-2">
                Found <span className="font-bold ml-1">{filesFound}</span>
              </div>
              <div className="h-7 rounded bg-pf-success-bg border border-pf-success-border text-pf-success-text text-xs font-semibold flex items-center justify-center px-2">
                Added <span className="font-bold ml-1">{filesAdded}</span>
              </div>
              <div className="h-7 rounded bg-pf-bg-2 border border-pf-border text-pf-text-secondary text-xs font-semibold flex items-center justify-center px-2">
                Skipped <span className="font-bold ml-1">{filesSkipped}</span>
              </div>
              <div className="h-7 rounded bg-pf-error-bg border border-pf-error-border text-pf-error-text text-xs font-semibold flex items-center justify-center px-2">
                Failed <span className="font-bold ml-1">{filesErrored}</span>
              </div>
              {selectedCount > 0 && (
                <div className="h-7 rounded bg-pf-accent border border-pf-accent text-white text-xs font-semibold flex items-center justify-center px-2">
                  Selected <span className="font-bold ml-1">{selectedCount}</span>
                </div>
              )}
            </div>
            
            {/* Discovery status */}
            {isDiscovering && !discoveryComplete && (
              <div className="flex items-center gap-2 text-pf-text-secondary">
                <svg className="w-5 h-5 text-pf-accent animate-spin" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"></path>
                </svg>
                <span>Discovering files... {filesFound} found so far</span>
              </div>
            )}
            
            {discoveryComplete && !importComplete && (
              <div className="flex items-center gap-2 text-pf-success-text">
                <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 20 20">
                  <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
                </svg>
                <span>Discovery complete! {filesFound} files found. Select files to import.</span>
              </div>
            )}
            
            {importComplete && (
              <div className="bg-pf-success-bg border border-pf-success-border rounded-lg p-3">
                <div className="flex items-center gap-2 text-pf-success-text">
                  <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 20 20">
                    <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
                  </svg>
                  <span className="font-semibold">Import Complete!</span>
                </div>
                <p className="text-pf-success-text text-sm mt-1">
                  {filesAdded} files imported, {filesSkipped} skipped, {filesErrored} failed
                </p>
              </div>
            )}
            
            {/* File list - delegate to IndexedFilesList component */}
            {operationId && (
              <div className="border border-pf-border rounded-lg overflow-hidden max-h-96">
                <IndexedFilesList
                  ref={indexedFilesListRef}
                  operationId={operationId}
                  hideHeader={true}
                  hideFooterImport={true}
                  onSelectionChange={setSelectedCount}
                  onFilesImported={() => {
                    setImportComplete(true);
                    onComplete?.();
                  }}
                />
              </div>
            )}
            
            {!operationId && isDiscovering && (
              <div className="flex flex-col items-center justify-center py-12 text-pf-text-secondary">
                <svg className="w-12 h-12 text-pf-accent animate-spin mb-4" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v8z"></path>
                </svg>
                <p className="text-lg font-medium">Starting discovery...</p>
                <p className="text-sm">Files will appear here as they are found</p>
              </div>
            )}
          </div>
        );
    }
  };
  
  // Step titles
  const stepTitles: Record<WizardStep, { title: string; subtitle: string }> = {
    'select-printer': {
      title: 'Select Printer',
      subtitle: 'Choose which printer to harvest files from',
    },
    'configure-options': {
      title: 'Configure Options',
      subtitle: 'Set up harvest parameters',
    },
    'discovery-import': {
      title: 'Import Files',
      subtitle: 'Select and import discovered files',
    },
  };
  
  const currentStep = stepTitles[step];
  const stepNumber = step === 'select-printer' ? 1 : step === 'configure-options' ? 2 : 3;
  
  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title={currentStep.title}
      size="full"
      maxHeight="max-h-[85vh]"
      closeOnBackdrop={false}
      footer={
        <div className="flex justify-between items-center w-full">
          <Button
            variant="secondary"
            onClick={handleBack}
            disabled={step === 'select-printer' || step === 'discovery-import'}
          >
            Back
          </Button>
          
          <div className="text-sm text-pf-text-secondary">
            Step {stepNumber} of 3
          </div>
          
          <div className="flex gap-2">
            {step === 'discovery-import' ? (
              importComplete ? (
                <Button variant="primary" onClick={handleClose}>
                  Done
                </Button>
              ) : (
                <Button
                  variant="primary"
                  onClick={handleImport}
                  disabled={selectedCount === 0 || isImporting}
                >
                  {isImporting ? 'Importing...' : `Import ${selectedCount} Files`}
                </Button>
              )
            ) : step === 'configure-options' ? (
              <Button
                variant="primary"
                onClick={() => step2OptionsRef.current?.validateAndStart()}
              >
                Start Discovery
              </Button>
            ) : (
              <Button variant="secondary" onClick={handleClose}>
                Cancel
              </Button>
            )}
          </div>
        </div>
      }
    >
      <div className="space-y-4">
        {/* Progress indicator */}
        <div className="flex gap-1 mb-4">
          {[1, 2, 3].map(s => (
            <div
              key={s}
              className={`h-1 flex-1 rounded ${
                s < stepNumber
                  ? 'bg-pf-success-bg'
                  : s === stepNumber
                    ? 'bg-pf-accent'
                    : 'bg-pf-border'
              }`}
            />
          ))}
        </div>
        
        <p className="text-sm text-pf-text-secondary">{currentStep.subtitle}</p>
        
        {/* Step content */}
        <div className="min-h-[300px]">
          {renderStepContent()}
        </div>
      </div>
    </Modal>
  );
}
