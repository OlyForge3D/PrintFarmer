import { DispatchReconciliationBanner } from '@/features/dispatch-recovery/components/DispatchReconciliationBanner';
import { RecoveryBlockedJobsPanel } from '@/features/dispatch-recovery/components/RecoveryBlockedJobsPanel';
import type { QueuedPrintJobWithFileMetaDto } from '@/types/api';

export interface DispatchRecoveryQueueSectionProps {
  jobs: QueuedPrintJobWithFileMetaDto[];
}

/**
 * Queue-dashboard entry point for dispatch recovery: one reconciliation banner
 * per printer whose latest dispatch outcome is unknown, plus the held-jobs
 * panel for jobs blocked by `OperatorRecoveryRequired`.
 */
export function DispatchRecoveryQueueSection({ jobs }: DispatchRecoveryQueueSectionProps) {
  const printers = new Map<string, string>();
  for (const entry of jobs) {
    const result = entry.job.dispatchResult;
    const printerId = entry.job.assignedPrinterId;
    if (printerId && result?.outcome === 'Unknown' && result.requiresReconciliation) {
      printers.set(printerId, entry.assignedPrinter?.name ?? 'Printer');
    }
  }

  if (printers.size === 0 && !jobs.some((entry) => entry.job.blockedReasonCode === 'OperatorRecoveryRequired')) {
    return null;
  }

  return (
    <div className="mb-4 space-y-3">
      {[...printers].map(([printerId, printerName]) => (
        <DispatchReconciliationBanner key={printerId} printerId={printerId} printerName={printerName} />
      ))}
      <RecoveryBlockedJobsPanel jobs={jobs} />
    </div>
  );
}
