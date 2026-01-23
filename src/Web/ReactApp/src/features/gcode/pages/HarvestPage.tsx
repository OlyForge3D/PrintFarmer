import React, { useState, useEffect, useEffectEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Breadcrumbs } from '@/common/components/Breadcrumbs';
import { Button } from '@/common/components/ui';
// Sparkles icon - using ActivityIcon as close substitute
import { ActivityIcon, PlusIcon } from '@/common/components/icons/MdiIcons';
import {
  GcodeHarvestStatus,
  GcodeHarvestOperation,
} from '@/types/api';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { usePrinters, useCancelHarvestOperation, useRestartHarvestDiscovery } from '@/common/hooks/useApi';
import { signalRService } from '@/services/harvest-signalr';
import { apiClient } from '@/services/api';
import { HarvestOperationDetails } from '@/features/gcode/components/harvest/HarvestOperationDetails';
import { HarvestWizardModal } from '@/features/gcode/components/harvest/HarvestWizardModal';
import { AccessDenied } from '@/features/auth/components/AccessDenied';

export const HarvestPage: React.FC = () => {
  // State management
  const [selectedOperation, setSelectedOperation] = useState<GcodeHarvestOperation | null>(null);
  const [isWizardOpen, setIsWizardOpen] = useState(false);
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

  // All hooks must be called before any return (including useEffect)

  // Extract event handlers with useEffectEvent to prevent effect retriggers
  const handleHarvestFileProgress = useEffectEvent((progress: import('@/services/harvest-signalr').HarvestFileProgress) => {
    const prev = perFileProgressMapRef.current ?? {};
    const opMap = prev[progress.operationId] ? { ...prev[progress.operationId] } : {};
    opMap[progress.fileName] = progress;
    perFileProgressMapRef.current = { ...prev, [progress.operationId]: opMap };
  });

  const handleHarvestOperationProgress = useEffectEvent(() => {
    // Invalidate operations on progress updates
    queryClient.invalidateQueries({ queryKey: ['harvest-operations'] });
  });

  const handleHarvestUpdate = useEffectEvent(() => {
    // Use latest refetchOperations from closure
    queryClient.invalidateQueries({ queryKey: ['harvest-operations'] });
  });

  // Set up real-time updates for harvest progress and per-file progress
  const joinedOpsRef = React.useRef<Set<string>>(new Set());

  useEffect(() => {
    // Async setup function
    const setupSignalR = async () => {
      await signalRService.connect();

      // Join SignalR group for each running operation
      if (harvestOperations) {
        joinedOpsRef.current.clear();
        for (const op of harvestOperations) {
          if (op.status === GcodeHarvestStatus.Running && op.id) {
            await signalRService.joinHarvestGroup(op.id);
            joinedOpsRef.current.add(op.id);
          }
        }
      }
    };

    setupSignalR();

    // Subscribe to events using extracted handlers (no dependency on callbacks)
    const unsubscribeFileProgress = signalRService.onHarvestFileProgress(handleHarvestFileProgress);
    const unsubscribeOperationProgress = signalRService.onHarvestOperationProgress(handleHarvestOperationProgress);
    const unsubscribe = signalRService.onHarvestUpdate(handleHarvestUpdate);

    // Copy ref to local variable to avoid ref warning in cleanup
    const opsToClean = new Set(joinedOpsRef.current);

    return () => {
      unsubscribe();
      unsubscribeFileProgress();
      unsubscribeOperationProgress();
      // Clean up joined ops using local copy
      opsToClean.forEach(opId => signalRService.leaveHarvestGroup(opId));
    };
  }, [harvestOperations, handleHarvestFileProgress, handleHarvestOperationProgress, handleHarvestUpdate]);

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

  // Invalidate gcode-files queries when any operation completes
  useEffect(() => {
    if (harvestOperations) {
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
  }, [harvestOperations, queryClient]);

  // Early return for permission check (must be after all hooks)
  if (!hasPermission('gcode_harvest', 'execute')) {
    return <AccessDenied />;
  }

  const activeOperations = harvestOperations?.filter(op =>
    op.status === GcodeHarvestStatus.Running
  ) || [];

  const completedOperations = harvestOperations?.filter(op =>
    op.status === GcodeHarvestStatus.Completed ||
    op.status === GcodeHarvestStatus.Cancelled ||
    op.status === GcodeHarvestStatus.Failed
  ) || [];

  return (
    <PageTemplate
      title="G-code Harvest"
      subtitle="Harvest G-code files from your printers"
      icon={ActivityIcon}
    >
      {/* Breadcrumbs */}
      <Breadcrumbs
        items={[
          { label: 'Dashboard', href: '/' },
          { label: 'Files', href: '/files' },
          { label: 'Harvest', current: true }
        ]}
        className="mb-4"
      />

      {/* Harvest Wizard Modal */}
      <HarvestWizardModal
        isOpen={isWizardOpen}
        onClose={() => setIsWizardOpen(false)}
        printers={printers || []}
        activeHarvests={activeOperations}
        onComplete={() => {
          refetchOperations();
          toast.success('Harvest operation completed');
        }}
      />

      {/* Details panel overlay */}
      {selectedOperation && (
        <HarvestOperationDetails
          operation={selectedOperation}
          onClose={() => setSelectedOperation(null)}
        />
      )}

      {/* Header with Start Harvest button */}
      <div className="flex items-center justify-between mb-6">
        <div>
          <h2 className="text-lg font-semibold text-pf-text-primary">
            Harvest Operations
          </h2>
          <p className="text-sm text-pf-text-secondary">
            {activeOperations.length > 0
              ? `${activeOperations.length} active operation${activeOperations.length > 1 ? 's' : ''}`
              : 'No active harvest operations'}
          </p>
        </div>
        <Button
          type="button"
          variant="primary"
          onClick={() => setIsWizardOpen(true)}
          disabled={printersLoading}
        >
          <PlusIcon className="w-4 h-4 mr-2" />
          Start Harvest
        </Button>
      </div>

      {/* Active Operations */}
      {activeOperations.length > 0 && (
        <div className="mb-8">
          <div className="flex items-center justify-between mb-4">
            <h3 className="text-md font-semibold text-pf-text-primary">
              Active Operations ({activeOperations.length})
            </h3>
            {activeOperations.length > 1 && (
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
                    <h4 className="font-semibold text-pf-text-primary">{op.printerName}</h4>
                    <div className="flex gap-4 mt-2 text-sm text-pf-text-secondary">
                      <span>Found: {op.filesFound}</span>
                      <span>Processed: {op.filesProcessed}</span>
                      <span>Added: {op.filesAdded}</span>
                      <span>Skipped: {op.filesSkipped}</span>
                    </div>
                    <div className="mt-2 w-full bg-pf-background rounded-full h-2 overflow-hidden">
                      <div
                        className="bg-pf-success h-2 rounded-full transition-all"
                        style={{
                          '--progress': `${op.filesFound > 0 ? (op.filesProcessed / op.filesFound) * 100 : 0}%`,
                        } as React.CSSProperties & { '--progress': string }}
                      />
                    </div>
                  </div>
                  <div className="flex gap-2 flex-shrink-0 ml-4">
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
                      Restart
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
                      Cancel
                    </Button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Recent Operations */}
      {completedOperations.length > 0 && (
        <div>
          <h3 className="text-md font-semibold text-pf-text-primary mb-4">
            Recent Operations ({completedOperations.length})
          </h3>
          <div className="space-y-3">
            {completedOperations.slice(0, 5).map(op => (
              <div
                key={op.id}
                onClick={() => setSelectedOperation(op)}
                className="bg-pf-panel border border-pf-border rounded-lg p-4 cursor-pointer hover:border-pf-accent transition-colors opacity-80"
              >
                <div className="flex items-start justify-between">
                  <div className="flex-1">
                    <div className="flex items-center gap-2">
                      <h4 className="font-semibold text-pf-text-primary">{op.printerName}</h4>
                      <span className={`px-2 py-0.5 rounded text-xs font-medium ${
                        op.status === GcodeHarvestStatus.Completed
                          ? 'bg-pf-success-bg text-pf-success-text'
                          : op.status === GcodeHarvestStatus.Failed
                            ? 'bg-pf-error-bg text-pf-error-text'
                            : 'bg-pf-warning-bg text-pf-warning-text'
                      }`}>
                        {op.status}
                      </span>
                    </div>
                    <div className="flex gap-4 mt-2 text-sm text-pf-text-secondary">
                      <span>Found: {op.filesFound}</span>
                      <span>Added: {op.filesAdded}</span>
                      <span>Skipped: {op.filesSkipped}</span>
                      {op.filesErrored > 0 && <span className="text-pf-error-text">Errors: {op.filesErrored}</span>}
                    </div>
                  </div>
                  <div className="text-sm text-pf-text-secondary">
                    {op.completedAt && new Date(op.completedAt).toLocaleString()}
                  </div>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Empty state */}
      {activeOperations.length === 0 && completedOperations.length === 0 && (
        <div className="text-center py-16">
          <ActivityIcon className="w-16 h-16 text-pf-text-secondary mx-auto mb-4 opacity-50" />
          <h3 className="text-lg font-semibold text-pf-text-primary mb-2">No harvest operations</h3>
          <p className="text-pf-text-secondary mb-6">
            Start a harvest to import G-code files from your printers
          </p>
          <Button
            type="button"
            variant="primary"
            onClick={() => setIsWizardOpen(true)}
            disabled={printersLoading}
          >
            <PlusIcon className="w-4 h-4 mr-2" />
            Start Harvest
          </Button>
        </div>
      )}
    </PageTemplate>
  );
};