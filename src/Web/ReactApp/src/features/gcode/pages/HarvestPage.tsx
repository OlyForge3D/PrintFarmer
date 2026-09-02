import React, { useState, useEffect, useEffectEvent, useMemo } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Breadcrumbs } from '@/common/components/Breadcrumbs';
import { Button, ProgressBar } from '@/common/components/ui';
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

  // Bumped every time the underlying SignalR connection transitions to `Connected`
  // (initial connect AND every reconnect - see the `onConnectionStateChange` subscription
  // below). Reconnects imply the *server* has forgotten all group membership for the new
  // connection, so the delta-reconciliation effect uses a change here to force a full
  // rejoin of every currently-running operation, rather than trusting `joinedOpsRef` (which
  // still lists ops joined on the now-defunct prior connection).
  const [connectedEpoch, setConnectedEpoch] = useState(0);
  const reconciledEpochRef = React.useRef(0);

  // Stable primitive derived from the running-operation IDs. Unlike `harvestOperations`
  // (a new array reference on every poll, even when only progress fields changed), this
  // string only changes when the *set* of running operation IDs changes, so it's safe to
  // use as an effect dependency without churning on every 2s poll.
  const runningOpIdsKey = useMemo(() => {
    if (!harvestOperations) {
      return '';
    }
    return harvestOperations
      .filter(op => op.status === GcodeHarvestStatus.Running && op.id)
      .map(op => op.id)
      .sort()
      .join(',');
  }, [harvestOperations]);

  // Mount-once effect: connect, register event subscriptions, and leave any remaining
  // joined groups on unmount. The handlers are `useEffectEvent`, so they always read fresh
  // state without needing to be dependencies here. This never re-runs, so it can never
  // double-subscribe, and its cleanup runs exactly once at unmount - reading
  // `joinedOpsRef.current` at that time (refs mutate in place, so this always reflects the
  // latest set maintained by the delta-reconciliation effect below).
  useEffect(() => {
    signalRService.connect();

    const unsubscribeFileProgress = signalRService.onHarvestFileProgress(handleHarvestFileProgress);
    const unsubscribeOperationProgress = signalRService.onHarvestOperationProgress(handleHarvestOperationProgress);
    const unsubscribe = signalRService.onHarvestUpdate(handleHarvestUpdate);
    // Bump `connectedEpoch` on every transition to connected (initial connect and every
    // reconnect). The delta-reconciliation effect below is keyed on this so it never
    // attempts to join a group before the connection is actually established (`connect()`
    // above is fire-and-forget, and the underlying service silently no-ops a join attempted
    // before the connection reaches `Connected`), and so it re-joins everything after a
    // reconnect instead of trusting stale `joinedOpsRef` state from the dropped connection.
    const unsubscribeConnectionState = signalRService.onConnectionStateChange((connected) => {
      if (connected) {
        setConnectedEpoch(epoch => epoch + 1);
      }
    });

    return () => {
      unsubscribe();
      unsubscribeFileProgress();
      unsubscribeOperationProgress();
      unsubscribeConnectionState();
      // Intentionally read `joinedOpsRef.current` here rather than a value captured at
      // effect-setup time: this cleanup only ever runs once, at unmount (deps are `[]`),
      // and must leave whatever operations are joined *at that moment* - which the
      // delta-reconciliation effect below keeps up to date via ref mutation over the
      // component's lifetime, not just what was joined when this effect first ran.
      /* eslint-disable react-hooks/exhaustive-deps */
      const opsToClean = new Set(joinedOpsRef.current);
      joinedOpsRef.current.clear();
      /* eslint-enable react-hooks/exhaustive-deps */
      opsToClean.forEach(id => signalRService.leaveHarvestGroup(id));
    };
  }, []);

  // Delta-reconciliation effect: keyed on the stable running-op-id primitive and the
  // connection epoch, so it only re-runs when the set of running operations actually
  // changes, or the connection (re)connects - not on every poll. While not yet connected
  // (`connectedEpoch === 0`), it does nothing: attempting a join before the connection is
  // ready would silently no-op server-side while still marking the op as joined locally,
  // permanently losing that operation's progress events for the rest of the session (see
  // #2395 review). Once connected, an epoch change (new or reconnected connection) forces a
  // full rejoin of every currently-running operation, since the server has no memory of
  // group membership from a prior connection; otherwise it joins only newly-running ops and
  // leaves only ops that stopped running - never a clear-and-rejoin of everything - so an
  // operation that stays running across a poll never has a leave/rejoin pair fired for it,
  // eliminating the drop window. Deliberately has no cleanup function: an unmount-time
  // leave-all is handled once by the mount-once effect above, not here, so this effect
  // re-running on every set change never re-leaves operations that are still running.
  useEffect(() => {
    if (connectedEpoch === 0) {
      return;
    }

    const runningOpIds = runningOpIdsKey ? runningOpIdsKey.split(',') : [];
    const runningOpIdSet = new Set(runningOpIds);

    if (reconciledEpochRef.current !== connectedEpoch) {
      // Fresh connection (initial connect or reconnect): server-side group membership for
      // this connection is empty regardless of what `joinedOpsRef` says, so reset local
      // bookkeeping and treat every currently-running op as newly-running.
      joinedOpsRef.current.clear();
      reconciledEpochRef.current = connectedEpoch;
    }

    const newlyRunning = runningOpIds.filter(id => !joinedOpsRef.current.has(id));
    const noLongerRunning = Array.from(joinedOpsRef.current).filter(id => !runningOpIdSet.has(id));

    newlyRunning.forEach(id => {
      joinedOpsRef.current.add(id);
      signalRService.joinHarvestGroup(id);
    });
    noLongerRunning.forEach(id => {
      joinedOpsRef.current.delete(id);
      signalRService.leaveHarvestGroup(id);
    });
  }, [runningOpIdsKey, connectedEpoch]);

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
          iconLeft={<PlusIcon className="w-4 h-4" />}
        >
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
                    <ProgressBar
                      value={op.filesFound > 0 ? (op.filesProcessed / op.filesFound) * 100 : 0}
                      ariaLabel={`${op.printerName} harvest progress`}
                      showPercent={false}
                      className="mt-2"
                    />
                  </div>
                  <div className="flex gap-2 shrink-0 ml-4">
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
            iconLeft={<PlusIcon className="w-4 h-4" />}
          >
            Start Harvest
          </Button>
        </div>
      )}
    </PageTemplate>
  );
};