import React, { useState, useEffect } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui';
// Sparkles icon - using ActivityIcon as close substitute
import { ActivityIcon } from '@/common/components/icons/MdiIcons';
import {
  GcodeHarvestStatus,
  GcodeHarvestOperation,
} from '@/types/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { usePrinters, useCancelHarvestOperation, useRestartHarvestDiscovery } from '@/common/hooks/useApi';
import { signalRService } from '@/services/harvest-signalr';
import { apiClient } from '@/services/api';
import { HarvestOperationDetails } from '@/features/gcode/components/harvest/HarvestOperationDetails';
import { HarvestWizard } from '@/features/gcode/components/harvest/HarvestWizard';
import { AccessDenied } from '@/features/auth/components/AccessDenied';

export const HarvestPage: React.FC = () => {
  // State management
  const [selectedOperation, setSelectedOperation] = useState<GcodeHarvestOperation | null>(null);
  const [wizardStep, setWizardStep] = useState<'wizard' | 'operations'>('wizard');
  const perFileProgressMapRef = React.useRef<Record<string, Record<string, import('@/services/harvest-signalr').HarvestFileProgress>>>({});
  const queryClient = useQueryClient();
  const cancelHarvestMutation = useCancelHarvestOperation();
  const restartHarvestDiscoveryMutation = useRestartHarvestDiscovery();
  const { hasPermission } = useAuth();
  const { data: printers, isLoading: printersLoading } = usePrinters();

  const { data: harvestOperations, refetch: refetchOperations } = useQuery({
    queryKey: ['harvest-operations'],
    queryFn: () => apiClient.getHarvestOperations(),
    refetchInterval: 2000,
  });
  // Printers already have merged realtime status from API
  const printersWithLive = printers || [];

  // All hooks must be called before any return (including useEffect)

  // Set up real-time updates for harvest progress and per-file progress
  useEffect(() => {
    // Async setup function
    const setupSignalR = async () => {
      await signalRService.connect();

      // Join SignalR group for each running operation
      if (harvestOperations) {
        for (const op of harvestOperations) {
          if (op.status === GcodeHarvestStatus.Running && op.id) {
            await signalRService.joinHarvestGroup(op.id);
            joinedOps.add(op.id);
          }
        }
      }
    };

    const joinedOps = new Set<string>();
    setupSignalR();

    // Subscribe to per-file progress events and store in ref
    const unsubscribeFileProgress = signalRService.onHarvestFileProgress((progress) => {
      const prev = perFileProgressMapRef.current ?? {};
      const opMap = prev[progress.operationId] ? { ...prev[progress.operationId] } : {};
      opMap[progress.fileName] = progress;
      perFileProgressMapRef.current = { ...prev, [progress.operationId]: opMap };
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

  // Redirect to wizard when all active operations are cancelled/completed
  useEffect(() => {
    if (harvestOperations) {
      const activeOps = harvestOperations.filter(op => op.status === GcodeHarvestStatus.Running);
      if (activeOps.length === 0 && wizardStep === 'operations') {
        setWizardStep('wizard');
      }

      // Invalidate gcode-files queries when any operation completes (status changed from Running)
      const completedOps = harvestOperations.filter(op =>
        op.status === GcodeHarvestStatus.Completed ||
        op.status === GcodeHarvestStatus.Cancelled ||
        op.status === GcodeHarvestStatus.Failed
      );

      if (completedOps.length > 0) {
        // Invalidate all gcode-files related queries to ensure views refresh with new files
        queryClient.invalidateQueries({ queryKey: ['gcode-files'] });
        queryClient.invalidateQueries({ queryKey: ['gcode-files-hierarchy'] });
        queryClient.invalidateQueries({ queryKey: ['gcode-files-all-folders'] });
      }
    }
  }, [harvestOperations, wizardStep, queryClient]);

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
      icon={ActivityIcon}
    >
      {/* Details panel overlay */}
      {selectedOperation && (
        <HarvestOperationDetails
          operation={selectedOperation}
          onClose={() => setSelectedOperation(null)}
        />
      )}

      {/* Tab-like switcher between wizard and active operations */}
      {activeOperations.length > 0 && (
        <div className="mb-6 flex gap-2 border-b border-pf-border">
          <Button
            type="button"
            variant={wizardStep === 'wizard' ? 'primary' : 'subtle'}
            onClick={() => setWizardStep('wizard')}
            className="px-4 py-2 !justify-start"
          >
            Start New Harvest
          </Button>
          <Button
            type="button"
            variant={wizardStep === 'operations' ? 'primary' : 'subtle'}
            onClick={() => setWizardStep('operations')}
            className="px-4 py-2 !justify-start"
          >
            Active Operations ({activeOperations.length})
          </Button>
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
              <Button
                type="button"
                variant="danger"
                size="sm"
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
                disabled={cancelHarvestMutation.isPending}
              >
                {cancelHarvestMutation.isPending ? 'Cancelling...' : 'Cancel All'}
              </Button>
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
                  <div className="flex gap-2 flex-shrink-0">
                    <Button
                      type="button"
                      variant="secondary"
                      size="sm"
                      onClick={async (e) => {
                        e.stopPropagation();
                        try {
                          await restartHarvestDiscoveryMutation.mutateAsync(op.id);
                          toast.success(`Restarting discovery for: ${op.printerName}`);
                          refetchOperations();
                        } catch (error) {
                          console.error('Failed to restart discovery:', error);
                          toast.error(`Failed to restart discovery: ${op.printerName}`);
                        }
                      }}
                      disabled={restartHarvestDiscoveryMutation.isPending}
                      title="Restart file discovery for this harvest"
                    >
                      {restartHarvestDiscoveryMutation.isPending ? 'Restarting...' : 'Restart Discovery'}
                    </Button>
                    <Button
                      type="button"
                      variant="danger"
                      size="sm"
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
                      disabled={cancelHarvestMutation.isPending}
                    >
                      {cancelHarvestMutation.isPending ? 'Cancelling...' : 'Cancel'}
                    </Button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </PageTemplate>
  );
};