import React, { useState, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/components/PageTemplate';
import { Sparkles } from 'lucide-react';
import { 
  Printer, 
  GcodeHarvestStatus,
  GcodeHarvestOperation,
} from '@/types/api';
import { useAuth } from '@/contexts/AuthHooks';
import { usePrinters, useCancelHarvestOperation } from '@/hooks/useApi';
import { usePrinterStatusUpdates } from '@/hooks/useSignalR';
import { signalRService } from '@/services/harvest-signalr';
import { apiClient } from '@/services/api';
import { HarvestOperationDetails } from '@/components/harvest/HarvestOperationDetails';
import { HarvestWizard } from '@/components/harvest/HarvestWizard';
import { AccessDenied } from '@/components/common/AccessDenied';

export const HarvestPage: React.FC = () => {
  // State management
  const [selectedOperation, setSelectedOperation] = useState<GcodeHarvestOperation | null>(null);
  const [wizardStep, setWizardStep] = useState<'wizard' | 'operations'>('wizard');
  const [perFileProgressMap, setPerFileProgressMap] = useState<
    Record<string, Record<string, import('@/services/harvest-signalr').HarvestFileProgress>>
  >({});
  const cancelHarvestMutation = useCancelHarvestOperation();
  const { hasPermission } = useAuth();
  const { data: printers, isLoading: printersLoading } = usePrinters();
  const { getPrinterStatus } = usePrinterStatusUpdates();
  
  const { data: harvestOperations, refetch: refetchOperations } = useQuery({
    queryKey: ['harvest-operations'],
    queryFn: () => apiClient.getHarvestOperations(),
    refetchInterval: 2000,
  });

  // Merge live status into base printer data
  const printersWithLive = (printers || []).map(p => {
    const status = getPrinterStatus(p.id);
    if (!status) return p;
    return {
      ...p,
      isOnline: status.isOnline,
      isReachable: status.isOnline || p.isReachable,
      progress: status.progress ?? p.progress,
      jobName: status.jobName ?? p.jobName,
      hotendTemp: status.hotendTemp ?? p.hotendTemp,
      bedTemp: status.bedTemp ?? p.bedTemp,
      hotendTarget: status.hotendTarget ?? p.hotendTarget,
      bedTarget: status.bedTarget ?? p.bedTarget,
      x: status.x ?? p.x,
      y: status.y ?? p.y,
      z: status.z ?? p.z,
    } as Printer;
  });

  // All hooks must be called before any return (including useEffect)

  // Set up real-time updates for harvest progress and per-file progress
  useEffect(() => {
    signalRService.connect();

    // Join SignalR group for each running operation
    const joinedOps = new Set<string>();
    if (harvestOperations) {
      for (const op of harvestOperations) {
        if (op.status === GcodeHarvestStatus.Running && op.id) {
          signalRService.joinHarvestGroup(op.id);
          joinedOps.add(op.id);
        }
      }
    }

    // Subscribe to per-file progress events
    const unsubscribeFileProgress = signalRService.onHarvestFileProgress((progress) => {
      setPerFileProgressMap(prev => {
        const opMap = prev[progress.operationId] ? { ...prev[progress.operationId] } : {};
        opMap[progress.fileName] = progress;
        return { ...prev, [progress.operationId]: opMap };
      });
    });

    // Subscribe to operation progress events
    const unsubscribeOperationProgress = signalRService.onHarvestOperationProgress(() => {
      refetchOperations();
    });

    // Subscribe to harvest update for total progress
    const unsubscribe = signalRService.onHarvestUpdate(() => {
      refetchOperations();
    });

    return () => {
      unsubscribe();
      unsubscribeFileProgress();
      unsubscribeOperationProgress();
      joinedOps.forEach(opId => signalRService.leaveHarvestGroup(opId));
    };
  }, [refetchOperations, harvestOperations]);

  // Update selectedOperation when harvestOperations changes
  useEffect(() => {
    if (selectedOperation && harvestOperations) {
      const updatedOp = harvestOperations.find(op => op.id === selectedOperation.id);
      if (updatedOp && JSON.stringify(updatedOp) !== JSON.stringify(selectedOperation)) {
        setSelectedOperation(updatedOp);
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [harvestOperations]);

  // Early return for permission check (must be after all hooks)
  if (!hasPermission('gcode_harvest', 'execute')) {
    return <AccessDenied />;
  }

  const activeOperations = harvestOperations?.filter(op => 
    op.status === GcodeHarvestStatus.Running
  ) || [];

  return (
    <PageTemplate
      title="G-code Harvest"
      subtitle="Start harvesting G-code files from your printers"
      icon={Sparkles}
      maxWidth="max-w-5xl"
    >
      {/* Details panel overlay */}
      {selectedOperation && (
        <HarvestOperationDetails
          operation={selectedOperation}
          onClose={() => setSelectedOperation(null)}
          perFileProgress={perFileProgressMap[selectedOperation.id] || {}}
        />
      )}

      {/* Tab-like switcher between wizard and active operations */}
      {activeOperations.length > 0 && (
        <div className="mb-6 flex gap-2 border-b border-pf-border">
          <button
            onClick={() => setWizardStep('wizard')}
            className={`px-4 py-2 font-medium transition-colors ${
              wizardStep === 'wizard'
                ? 'text-pf-accent border-b-2 border-pf-accent'
                : 'text-pf-text-secondary hover:text-pf-text-primary'
            }`}
          >
            Start New Harvest
          </button>
          <button
            onClick={() => setWizardStep('operations')}
            className={`px-4 py-2 font-medium transition-colors ${
              wizardStep === 'operations'
                ? 'text-pf-accent border-b-2 border-pf-accent'
                : 'text-pf-text-secondary hover:text-pf-text-primary'
            }`}
          >
            Active Operations ({activeOperations.length})
          </button>
        </div>
      )}

      {/* Main content area */}
      {wizardStep === 'wizard' ? (
        // Harvest Wizard - main interface
        <div>
          {printersLoading ? (
            <div className="text-center py-16">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent mx-auto mb-4" />
              <p className="text-pf-text-secondary">Loading printers...</p>
            </div>
          ) : (
            <HarvestWizard
              printers={printersWithLive}
              onClose={() => {
                // Show operations tab if there are active ones
                if (activeOperations.length > 0) {
                  setWizardStep('operations');
                }
              }}
              onComplete={() => {
                toast.success('Harvest operation started');
                refetchOperations();
                // Switch to operations view if there are active ones
                if (activeOperations.length > 0) {
                  setWizardStep('operations');
                }
              }}
            />
          )}
        </div>
      ) : (
        // Active Operations - view and manage running harvests
        <div className="space-y-4">
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-lg font-semibold text-pf-text-primary">
              Active Operations ({activeOperations.length})
            </h2>
            {activeOperations.length > 0 && (
              <button
                type="button"
                onClick={async () => {
                  let successCount = 0;
                  let errorCount = 0;
                  for (const op of activeOperations) {
                    try {
                      await cancelHarvestMutation.mutateAsync(op.id);
                      successCount++;
                    } catch (error) {
                      console.error('Failed to cancel operation:', error);
                      errorCount++;
                    }
                  }
                  if (successCount && !errorCount) {
                    toast.success('All operations cancelled');
                    refetchOperations();
                  } else if (successCount && errorCount) {
                    toast.warning(`${successCount} cancelled, ${errorCount} failed`);
                    refetchOperations();
                  } else {
                    toast.error('Failed to cancel operations');
                  }
                }}
                className="pf-btn pf-btn-danger text-sm"
                disabled={cancelHarvestMutation.isPending}
              >
                {cancelHarvestMutation.isPending ? 'Cancelling...' : 'Cancel All'}
              </button>
            )}
          </div>

          <div className="space-y-3">
            {activeOperations.map(op => (
              <div
                key={op.id}
                onClick={() => setSelectedOperation(op)}
                className="bg-pf-panel border border-pf-border rounded-lg p-4 cursor-pointer hover:border-pf-accent transition-colors"
              >
                <div className="flex items-start justify-between">
                  <div className="flex-1">
                    <h3 className="font-semibold text-pf-text-primary">{op.printerName}</h3>
                    <div className="flex gap-4 mt-2 text-sm text-pf-text-secondary">
                      <span>Found: {op.filesFound}</span>
                      <span>Processed: {op.filesProcessed}</span>
                      <span>Added: {op.filesAdded}</span>
                      <span>Skipped: {op.filesSkipped}</span>
                    </div>
                    <div className="mt-2 w-full bg-pf-background rounded-full h-2 overflow-hidden">
                      {/* CSS Variable Progress Bar: Uses --progress CSS variable for dynamic width */}
                      {/* See styles/components.css for the width rule: [style*="--progress"] { width: var(--progress); } */}
                      <div
                        className="bg-pf-success h-2 rounded-full transition-all"
                        style={{
                          '--progress': `${op.filesFound > 0 ? (op.filesProcessed / op.filesFound) * 100 : 0}%`,
                        } as React.CSSProperties & { '--progress': string }}
                      />
                    </div>
                  </div>
                  <button
                    onClick={async (e) => {
                      e.stopPropagation();
                      try {
                        await cancelHarvestMutation.mutateAsync(op.id);
                        toast.success(`Cancelled: ${op.printerName}`);
                        refetchOperations();
                      } catch (error) {
                        console.error('Failed to cancel operation:', error);
                        toast.error(`Failed to cancel: ${op.printerName}`);
                      }
                    }}
                    className="pf-btn pf-btn-danger text-xs flex-shrink-0"
                    disabled={cancelHarvestMutation.isPending}
                  >
                    {cancelHarvestMutation.isPending ? 'Cancelling...' : 'Cancel'}
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </PageTemplate>
  );
};