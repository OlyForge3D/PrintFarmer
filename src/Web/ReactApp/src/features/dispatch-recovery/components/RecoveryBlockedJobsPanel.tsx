import { useId, useState } from 'react';
import { toast } from 'sonner';
import { ShieldAlert } from 'lucide-react';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui';
import {
  useCanReconcileQueue,
  useClearDispatchRecoveryBlock,
} from '@/features/dispatch-recovery/hooks/useDispatchRecovery';
import { isRecoveryBlocked } from '@/features/dispatch-recovery/utils';
import type { DispatchRecoveryClearResult, QueuedPrintJobWithFileMetaDto } from '@/types/api';

export interface RecoveryBlockedJobsPanelProps {
  jobs: QueuedPrintJobWithFileMetaDto[];
  className?: string;
}

function clearFailureMessage(result: Exclude<DispatchRecoveryClearResult, { kind: 'cleared' }>): string {
  switch (result.errorCode) {
    case 'job_not_recovery_blocked':
      return 'This job is no longer held for recovery. The queue has been refreshed.';
    case 'job_revision_conflict':
      return 'The job changed since you reviewed it. The queue has been refreshed — review it and try again.';
    default:
      if (result.kind === 'forbidden' || result.kind === 'not_found') {
        return 'You do not have permission to allow dispatch for this job.';
      }
      return result.detail || 'Dispatch could not be allowed. Nothing was changed.';
  }
}

/**
 * Lists jobs held by `OperatorRecoveryRequired` after an operator recovery and
 * lets a `queue:reconcile` holder deliberately allow them to dispatch again.
 * The clear is revision-fenced with the job ETag and never optimistic.
 */
export function RecoveryBlockedJobsPanel({ jobs, className }: RecoveryBlockedJobsPanelProps) {
  const headingId = useId();
  const canReconcile = useCanReconcileQueue();
  const clear = useClearDispatchRecoveryBlock();
  const [confirmJobId, setConfirmJobId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const blocked = jobs.filter((entry) => isRecoveryBlocked(entry.job));
  // Always read the latest queue row so a refetch after 409/412 supplies the fresh job ETag.
  const confirmJob = confirmJobId
    ? (blocked.find((entry) => entry.job.id === confirmJobId) ?? null)
    : null;
  if (blocked.length === 0) {
    return null;
  }

  const closeConfirm = () => {
    if (clear.isPending) {
      return;
    }
    setConfirmJobId(null);
    setError(null);
  };

  const handleConfirm = async () => {
    if (!confirmJob) {
      return;
    }
    setError(null);
    let result: DispatchRecoveryClearResult;
    try {
      result = await clear.mutateAsync({
        jobId: confirmJob.job.id,
        jobETag: confirmJob.job.rowVersion,
        printerId: confirmJob.job.assignedPrinterId ?? null,
      });
    } catch {
      setError('The request did not complete. Refresh the queue to see whether dispatch was allowed.');
      return;
    }
    if (result.kind === 'cleared') {
      toast.success(`Dispatch allowed for ${confirmJob.job.name}`);
      setConfirmJobId(null);
      return;
    }
    if (result.errorCode === 'job_not_recovery_blocked') {
      toast.info(clearFailureMessage(result));
    }
    setError(clearFailureMessage(result));
  };

  return (
    <section
      aria-labelledby={headingId}
      className={`rounded-md border border-pf-warning bg-pf-warning px-3 py-2 text-sm text-pf-warning-text ${className ?? ''}`}
    >
      <h3 id={headingId} className="flex items-center gap-2 font-semibold">
        <ShieldAlert className="h-4 w-4" aria-hidden="true" />
        Held after operator recovery ({blocked.length})
      </h3>
      <p className="mt-1">
        These jobs returned to the queue after an operator recovered an unknown dispatch. They will
        not dispatch until an operator allows it.
      </p>
      {!canReconcile && (
        <p className="mt-1 text-xs">
          An operator with queue reconcile permission must allow dispatch.
        </p>
      )}
      <ul className="mt-2 space-y-1">
        {blocked.map((entry) => {
          const printerName = entry.assignedPrinter?.name ?? 'unassigned printer';
          return (
            <li key={entry.job.id} className="flex flex-wrap items-center gap-2">
              <span className="flex-1 min-w-0 truncate">
                <span className="font-medium">{entry.job.name}</span>
                <span className="text-xs"> — {printerName}</span>
              </span>
              {canReconcile && (
                <Button
                  size="sm"
                  variant="subtle"
                  onClick={() => {
                    setError(null);
                    setConfirmJobId(entry.job.id);
                  }}
                  disabled={!entry.job.rowVersion}
                  title={entry.job.rowVersion ? undefined : 'Refresh the queue before allowing dispatch'}
                  aria-label={`Allow dispatch for ${entry.job.name} on ${printerName}`}
                >
                  Allow dispatch
                </Button>
              )}
            </li>
          );
        })}
      </ul>

      {canReconcile && (
        <Modal
          isOpen={confirmJob !== null}
          onClose={closeConfirm}
          title="Allow dispatch after recovery?"
          size="sm"
          isDisabled={clear.isPending}
          footer={
            <div className="flex justify-end gap-2">
              <Button variant="subtle" onClick={closeConfirm} disabled={clear.isPending}>
                Cancel
              </Button>
              <Button onClick={() => void handleConfirm()} disabled={clear.isPending}>
                {clear.isPending ? 'Allowing…' : 'Allow dispatch'}
              </Button>
            </div>
          }
        >
          {confirmJob && (
            <div className="space-y-3 text-sm text-pf-text-primary">
              <p>
                <strong>{confirmJob.job.name}</strong> on{' '}
                <strong>{confirmJob.assignedPrinter?.name ?? 'an unassigned printer'}</strong> will
                become eligible to dispatch again. Only continue once the printer is ready and the
                bed is clear.
              </p>
              {error && (
                <div role="alert" className="rounded-sm border border-pf-error-border bg-pf-error-bg p-3 text-pf-error-text">
                  {error}
                </div>
              )}
            </div>
          )}
        </Modal>
      )}
    </section>
  );
}
