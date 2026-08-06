import React from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Button } from '@/common/components/ui';
import { CheckCircleIcon, SkipForwardIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { useAutoDispatchReadyFlow, useSkipNextJob, useCancelAutoDispatch } from '@/features/printers/hooks/useAutoDispatch';
import { queryKeys } from '@/common/hooks/useApi';
import { requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import { toast } from 'sonner';
import type { AutoDispatchStatus, AutoDispatchReadyResult, Printer } from '@/types/api';
import { FilamentOverrideModal } from '@/features/printers/components/FilamentOverrideModal';

interface BedClearBannerProps {
  printerId: string;
  printerName: string;
  autoDispatchStatus: AutoDispatchStatus;
  printerState?: string | null;
}

export function BedClearBanner({
  printerId,
  printerName,
  autoDispatchStatus,
  printerState,
}: BedClearBannerProps) {
  const queryClient = useQueryClient();
  const skipNextJob = useSkipNextJob();
  const cancelAutoDispatch = useCancelAutoDispatch();

  const applyOptimisticUpdate = (result: AutoDispatchReadyResult) => {
    if (!result.nextJob) return;
    const optimisticUpdate = (printer: Printer): Printer =>
      printer.id === printerId
        ? { ...printer, state: 'Starting...', jobName: result.nextJob!.name, progress: 0 }
        : printer;

    queryClient.setQueryData<Printer[]>(queryKeys.printers, (old) => old?.map(optimisticUpdate));
    queryClient.setQueryData<Printer>(queryKeys.printer(printerId), (old) =>
      old ? optimisticUpdate(old) : undefined,
    );
  };
  const readyFlow = useAutoDispatchReadyFlow(applyOptimisticUpdate);

  if (!requiresBedClearConfirmation(autoDispatchStatus, printerState)) return null;

  const isAnyPending = readyFlow.confirmation.isPending || skipNextJob.isPending || cancelAutoDispatch.isPending;

  const handleConfirm = async () => {
    try {
      await readyFlow.confirmReady(autoDispatchStatus, printerName);
    } catch {
      // The mutation's onError handler displays the typed server failure.
    }
  };

  const handleFilamentOverride = async () => {
    try {
      await readyFlow.confirmFilamentOverride();
    } catch {
      // The mutation's onError handler displays the typed server failure.
    }
  };

  const handleSkip = async () => {
    try {
      await skipNextJob.mutateAsync(autoDispatchStatus);
      toast.info('Skipped next queued job');
    } catch {
      toast.error('Failed to skip job');
    }
  };

  const handleCancel = async () => {
    try {
      await cancelAutoDispatch.mutateAsync(autoDispatchStatus);
      toast.info('Auto-dispatch cancelled');
    } catch {
      toast.error('Failed to cancel auto-dispatch');
    }
  };

  return (
    <>
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
            loading={readyFlow.confirmation.isPending}
            disabled={isAnyPending}
            iconCenter={<CheckCircleIcon className="h-4 w-4" />}
            aria-label={`Confirm bed clear for ${printerName}`}
            title="Confirm bed is clear"
            className="flex-1 h-9 p-0!"
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
            className="flex-1 h-9 p-0!"
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
            className="flex-1 h-9 p-0!"
          />
        </div>
      </div>

      <FilamentOverrideModal
        isOpen={readyFlow.challenge !== null}
        filamentCheck={readyFlow.challenge?.result.filamentCheck ?? null}
        isPending={readyFlow.confirmation.isPending}
        onCancel={readyFlow.cancelFilamentOverride}
        onConfirm={handleFilamentOverride}
      />
    </>
  );
}
