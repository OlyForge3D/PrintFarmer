import React, { useState, useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Button } from '@/common/components/ui';
import { CheckCircleIcon, SkipForwardIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { useConfirmBedClear, useSkipNextJob, useCancelAutoDispatch } from '@/features/printers/hooks/useAutoDispatch';
import { queryKeys } from '@/common/hooks/useApi';
import { queueSummariesFleetQueryKey } from '@/features/printers/hooks/useQueueSummariesFleet';
import { requiresBedClearConfirmation } from '@/common/utils/printerStateDisplay';
import { toast } from 'sonner';
import { SpoolValidationModal } from '@/features/queue/components/SpoolValidationModal';
import { apiClient } from '@/services/api';
import type { SpoolValidationContext } from '@/features/queue/utils/spoolValidation';
import type { AutoDispatchStatus, AutoDispatchReadyResult, Printer } from '@/types/api';
import { mutationErrorStatus } from '@/common/utils/mutationError';

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
  const confirmBedClear = useConfirmBedClear();
  const skipNextJob = useSkipNextJob();
  const cancelAutoDispatch = useCancelAutoDispatch();
  const [mismatchContext, setMismatchContext] = useState<SpoolValidationContext | null>(null);

  const handleMismatchProceed = useCallback(async (jobId: string) => {
    try {
      if (!autoDispatchStatus.nextJobETag) {
        throw new Error('The reviewed job revision is unavailable.');
      }
      const dispatch = await apiClient.dispatchJobToPrinter(
        jobId,
        printerId,
        autoDispatchStatus.nextJobETag
      );
      if (dispatch.kind === 'stale') {
        throw Object.assign(
          new Error(
            'The reviewed job changed. Refresh and confirm the override again.'
          ),
          { statusCode: 412 }
        );
      }
      if (dispatch.kind === 'conflict' || dispatch.kind === 'unavailable') {
        throw new Error(
          dispatch.detail ??
            `${dispatch.errorCode}: Dispatch was not accepted.`
        );
      }
      const jobName = mismatchContext?.jobName ?? 'Job';
      toast.success(
        dispatch.kind === 'reconciliation'
          ? `Dispatching "${jobName}" to ${printerName}; reconciliation is in progress`
          : `Dispatching "${jobName}" to ${printerName} (material override)`
      );
      setMismatchContext(null);

      // Optimistic UI update
      const optimisticUpdate = (printer: Printer): Printer =>
        printer.id === printerId
          ? { ...printer, state: 'Starting...', jobName, progress: 0 }
          : printer;

      queryClient.setQueryData<Printer[]>(queryKeys.printers, (old) => old?.map(optimisticUpdate));
      queryClient.setQueryData<Printer>(queryKeys.printer(printerId), (old) =>
        old ? optimisticUpdate(old) : undefined,
      );
    } catch (err) {
      const status = mutationErrorStatus(err);
      if (status === 412 || status === 428) {
        setMismatchContext(null);
        await Promise.all([
          queryClient.invalidateQueries({ queryKey: ['job-queue'] }),
          queryClient.invalidateQueries({ queryKey: ['queue-jobs'] }),
          queryClient.invalidateQueries({ queryKey: queueSummariesFleetQueryKey }),
          queryClient.invalidateQueries({ queryKey: ['auto-dispatch'] }),
        ]);
      }
      toast.error(`Failed to dispatch: ${err instanceof Error ? err.message : 'Unknown error'}`);
    }
  }, [printerId, printerName, mismatchContext, queryClient, autoDispatchStatus.nextJobETag]);

  if (!requiresBedClearConfirmation(autoDispatchStatus, printerState)) return null;

  const isAnyPending = confirmBedClear.isPending || skipNextJob.isPending || cancelAutoDispatch.isPending;

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

  const handleConfirm = async () => {
    try {
      const confirmation = await confirmBedClear.mutateAsync(autoDispatchStatus);
      if (confirmation.kind === 'calibration') {
        const result = confirmation.result;
        const jobName = autoDispatchStatus.nextJobName ?? 'Calibration job';
        toast.success(
          result.kind === 'accepted'
            ? `Dispatching "${jobName}" to ${printerName}`
            : `Calibration dispatch for "${jobName}" was already accepted`
        );
        return;
      }

      const result = confirmation.result;

      if (!result.nextJob) {
        toast.success(`Bed clear confirmed for ${printerName} — no jobs queued`);
        return;
      }

      if (result.filamentCheck?.materialMismatch) {
        const reviewedPrinterRowVersion =
          result.status.printerETag ?? autoDispatchStatus.printerETag;
        if (!reviewedPrinterRowVersion) {
          toast.error(
            'Printer revision unavailable. Refresh before selecting a spool.'
          );
          return;
        }
        setMismatchContext({
          jobId: result.nextJob.id,
          jobName: result.nextJob.name,
          requiredMaterial: result.filamentCheck.requiredMaterial ?? result.nextJob.requiredMaterialType,
          printerId,
          printerName,
          reviewedPrinterRowVersion,
          spoolInfo: {
            hasActiveSpool: true,
            material: result.filamentCheck.loadedMaterial,
          },
        });
        return;
      }

      if (result.filamentCheck && !result.filamentCheck.sufficient) {
        toast.warning(
          result.filamentCheck.message ?? 'Insufficient filament. Job not dispatched.',
          { duration: 8000 },
        );
        return;
      }

      toast.success(`Dispatching "${result.nextJob.name}" to ${printerName}`);
      applyOptimisticUpdate(result);
    } catch {
      toast.error('Failed to confirm bed clear');
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
            loading={confirmBedClear.isPending}
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

      <SpoolValidationModal
        isOpen={mismatchContext !== null}
        onClose={() => setMismatchContext(null)}
        onProceed={handleMismatchProceed}
        context={mismatchContext}
      />
    </>
  );
}
