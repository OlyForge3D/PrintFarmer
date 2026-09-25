import { useCallback, useState } from 'react';
import { DispatchReconciliationBanner } from '@/features/dispatch-recovery/components/DispatchReconciliationBanner';
import { RecoveryBlockedJobsPanel } from '@/features/dispatch-recovery/components/RecoveryBlockedJobsPanel';
import { useDispatchRecoveryCandidates } from '@/features/dispatch-recovery/hooks/useDispatchRecovery';
import { isRecoveryBlocked } from '@/features/dispatch-recovery/utils';
import type { QueuedPrintJobWithFileMetaDto } from '@/types/api';

export interface DispatchRecoveryQueueSectionProps {
  /** The dashboard's (possibly filtered/paginated) rows; merged with the unfiltered discovery read. */
  jobs: QueuedPrintJobWithFileMetaDto[];
}

function mergeJobs(
  primary: QueuedPrintJobWithFileMetaDto[],
  secondary: QueuedPrintJobWithFileMetaDto[]
): QueuedPrintJobWithFileMetaDto[] {
  const byId = new Map<string, QueuedPrintJobWithFileMetaDto>();
  for (const entry of secondary) {
    byId.set(entry.job.id, entry);
  }
  // Discovery rows win: they come from the same unfiltered read for every job.
  for (const entry of primary) {
    byId.set(entry.job.id, entry);
  }
  return [...byId.values()];
}

/**
 * Queue-dashboard entry point for dispatch recovery: one reconciliation banner
 * per printer whose latest dispatch outcome is unknown, plus the held-jobs
 * panel for jobs blocked by `OperatorRecoveryRequired`.
 *
 * Candidates come from an unfiltered, fully paged queue read (falling back to
 * the dashboard rows), so filters and pagination cannot hide a warning. Once
 * a printer is tracked its banner stays mounted until the authoritative
 * reconciliation read reports the claim closed; a queue row changing first
 * never dismisses it.
 */
export function DispatchRecoveryQueueSection({ jobs }: DispatchRecoveryQueueSectionProps) {
  const discovery = useDispatchRecoveryCandidates();
  const candidates = mergeJobs(discovery.data ?? [], jobs);
  const [tracked, setTracked] = useState<ReadonlyMap<string, string>>(() => new Map());

  const discovered = new Map<string, string>();
  for (const entry of candidates) {
    const result = entry.job.dispatchResult;
    const printerId = entry.job.assignedPrinterId;
    if (printerId && result?.outcome === 'Unknown' && result.requiresReconciliation) {
      discovered.set(printerId, entry.assignedPrinter?.name ?? 'Printer');
    }
  }
  const missing = [...discovered].filter(([printerId]) => !tracked.has(printerId));
  if (missing.length > 0) {
    setTracked((previous) => {
      const next = new Map(previous);
      for (const [printerId, printerName] of missing) {
        next.set(printerId, printerName);
      }
      return next;
    });
  }

  const handleClaimClosed = useCallback((printerId: string) => {
    setTracked((previous) => {
      if (!previous.has(printerId)) {
        return previous;
      }
      const next = new Map(previous);
      next.delete(printerId);
      return next;
    });
  }, []);

  const printers = new Map([...tracked, ...discovered]);
  const hasBlocked = candidates.some((entry) => isRecoveryBlocked(entry.job));
  if (printers.size === 0 && !hasBlocked) {
    return null;
  }

  return (
    <div className="mb-4 space-y-3">
      {[...printers].map(([printerId, printerName]) => (
        <DispatchReconciliationBanner
          key={printerId}
          printerId={printerId}
          printerName={printerName}
          onClaimClosed={discovered.has(printerId) ? undefined : handleClaimClosed}
        />
      ))}
      <RecoveryBlockedJobsPanel jobs={candidates} />
    </div>
  );
}