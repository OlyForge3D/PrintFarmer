import React from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Button } from '@/common/components/ui';
import { CheckCircleIcon, SkipForwardIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { useConfirmBedClear, useSkipNextJob, useCancelAutoDispatch } from '@/features/printers/hooks/useAutoDispatch';
import { queryKeys } from '@/common/hooks/useApi';
import { toast } from 'sonner';
import type { AutoDispatchStatus, Printer } from '@/types/api';

interface BedClearBannerProps {
  printerId: string;
  printerName: string;
  autoDispatchStatus: AutoDispatchStatus;
}

export function BedClearBanner({ printerId, printerName, autoDispatchStatus }: BedClearBannerProps) {
  const queryClient = useQueryClient();
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

      // Filament check passed — the backend's auto-dispatch background service
      // handles dispatching the job (triggered by the /ready endpoint).
      // We don't dispatch manually here to avoid a double-dispatch race condition.
      toast.success(`Dispatching "${result.nextJob.name}" to ${printerName}`);

      // Optimistic UI: immediately show "Starting..." state so the printer card
      // reflects the dispatch without waiting for the SignalR round-trip (~500ms).
      // The next SignalR update will overwrite with the real state.
      const optimisticUpdate = (printer: Printer): Printer =>
        printer.id === printerId
          ? { ...printer, state: 'Starting...', jobName: result.nextJob!.name, progress: 0 }
          : printer;

      queryClient.setQueryData<Printer[]>(queryKeys.printers, (old) => old?.map(optimisticUpdate));
      queryClient.setQueryData<Printer>(queryKeys.printer(printerId), (old) =>
        old ? optimisticUpdate(old) : undefined,
      );
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
      <p className="text-xs font-medium text-pf-warning mb-0.5">
        Print complete — confirm bed is clear
      </p>
      {autoDispatchStatus.queueDepth > 0 && (
        <p className="text-[10px] text-pf-text-secondary mb-2">
          {autoDispatchStatus.queueDepth} job{autoDispatchStatus.queueDepth !== 1 ? 's' : ''} queued
        </p>
      )}
      <div className="flex gap-2">
        <Button
          variant="success"
          size="sm"
          onClick={handleConfirm}
          loading={confirmBedClear.isPending}
          disabled={isAnyPending}
          iconCenter={<CheckCircleIcon className="h-4 w-4" />}
          aria-label={`Confirm bed clear for ${printerName}`}
          title="Confirm bed is clear"
          className="flex-1 h-9 !p-0"
        />
        <Button
          variant="primary"
          size="sm"
          onClick={handleSkip}
          loading={skipNextJob.isPending}
          disabled={isAnyPending}
          iconCenter={<SkipForwardIcon className="h-4 w-4" />}
          aria-label="Skip next queued job"
          title="Skip this job"
          className="flex-1 h-9 !p-0"
        />
        <Button
          variant="secondary"
          size="sm"
          onClick={handleCancel}
          loading={cancelAutoDispatch.isPending}
          disabled={isAnyPending}
          iconCenter={<CloseIcon className="h-4 w-4" />}
          aria-label="Cancel auto-dispatch"
          title="Cancel auto-dispatch"
          className="flex-1 h-9 !p-0"
        />
      </div>
    </div>
  );
}
