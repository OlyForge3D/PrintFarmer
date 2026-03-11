import React from 'react';
import { Button } from '@/common/components/ui';
import { CheckCircleIcon, SkipForwardIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { useConfirmBedClear, useSkipNextJob, useCancelAutoDispatch } from '@/features/printers/hooks/useAutoDispatch';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import type { AutoDispatchStatus } from '@/types/api';

interface BedClearBannerProps {
  printerId: string;
  printerName: string;
  autoDispatchStatus: AutoDispatchStatus;
}

export function BedClearBanner({ printerId, printerName, autoDispatchStatus }: BedClearBannerProps) {
  const confirmBedClear = useConfirmBedClear();
  const skipNextJob = useSkipNextJob();
  const cancelAutoDispatch = useCancelAutoDispatch();

  if (autoDispatchStatus.state !== 'PendingReady') return null;

  const isAnyPending = confirmBedClear.isPending || skipNextJob.isPending || cancelAutoDispatch.isPending;

  const handleConfirm = async () => {
    try {
      const result = await confirmBedClear.mutateAsync(printerId);

      if (!result.nextJob) {
        toast.success(`Bed clear confirmed for ${printerName} — no jobs queued`);
        return;
      }

      if (result.filamentCheck?.materialMismatch) {
        toast.warning(
          `Material mismatch: loaded ${result.filamentCheck.loadedMaterial ?? 'unknown'}, ` +
          `job requires ${result.filamentCheck.requiredMaterial ?? 'unknown'}. Job not dispatched.`,
          { duration: 8000 },
        );
        return;
      }

      if (result.filamentCheck && !result.filamentCheck.sufficient) {
        toast.warning(
          result.filamentCheck.message ?? 'Insufficient filament. Job not dispatched.',
          { duration: 8000 },
        );
        return;
      }

      // Filament check passed — dispatch the job
      try {
        await apiClient.dispatchPrintQueueJob(result.nextJob.id);
        toast.success(`Dispatching "${result.nextJob.name}" to ${printerName}`);
      } catch {
        toast.error(`Bed cleared but failed to dispatch "${result.nextJob.name}"`);
      }
    } catch {
      toast.error('Failed to confirm bed clear');
    }
  };

  const handleSkip = async () => {
    try {
      await skipNextJob.mutateAsync(printerId);
      toast.info('Skipped next queued job');
    } catch {
      toast.error('Failed to skip job');
    }
  };

  const handleCancel = async () => {
    try {
      await cancelAutoDispatch.mutateAsync(printerId);
      toast.info('Auto-dispatch cancelled');
    } catch {
      toast.error('Failed to cancel auto-dispatch');
    }
  };

  return (
    <div
      className="rounded-lg border border-pf-warning/30 bg-pf-warning/10 p-2.5"
      role="alert"
      aria-label="Bed clear confirmation required"
    >
      <p className="text-xs font-medium text-pf-warning mb-2">
        Print complete — confirm bed is clear
        {autoDispatchStatus.queuedJobCount > 0 && (
          <span className="text-pf-text-secondary font-normal">
            {' '}({autoDispatchStatus.queuedJobCount} job{autoDispatchStatus.queuedJobCount !== 1 ? 's' : ''} queued)
          </span>
        )}
      </p>
      <div className="flex gap-1.5">
        <Button
          variant="success"
          size="sm"
          onClick={handleConfirm}
          loading={confirmBedClear.isPending}
          disabled={isAnyPending}
          iconLeft={<CheckCircleIcon className="h-3.5 w-3.5" />}
          aria-label={`Confirm bed clear for ${printerName}`}
        >
          Confirm
        </Button>
        <Button
          variant="secondary"
          size="sm"
          onClick={handleSkip}
          loading={skipNextJob.isPending}
          disabled={isAnyPending}
          iconLeft={<SkipForwardIcon className="h-3.5 w-3.5" />}
          aria-label="Skip next queued job"
        >
          Skip
        </Button>
        <Button
          variant="ghost"
          size="sm"
          onClick={handleCancel}
          loading={cancelAutoDispatch.isPending}
          disabled={isAnyPending}
          iconLeft={<CloseIcon className="h-3.5 w-3.5" />}
          aria-label="Cancel auto-dispatch"
        >
          Cancel
        </Button>
      </div>
    </div>
  );
}
