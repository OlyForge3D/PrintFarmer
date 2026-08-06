import React, { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Button } from '@/common/components/ui';
import { CheckCircleIcon, SkipForwardIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { useConfirmBedClear, useSkipNextJob, useCancelAutoDispatch } from '@/features/printers/hooks/useAutoDispatch';
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
  const confirmBedClear = useConfirmBedClear();
  const skipNextJob = useSkipNextJob();
  const cancelAutoDispatch = useCancelAutoDispatch();
  const [overrideResult, setOverrideResult] = useState<AutoDispatchReadyResult | null>(null);

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

  const handleReadyResult = (result: AutoDispatchReadyResult) => {
    if (!result.nextJob) {
      toast.success(`Bed clear confirmed for ${printerName} — no jobs queued`);
      return;
    }

    const dispatchInitiated =
      result.dispatchInitiated ?? result.status.state === 'Ready';
    if (result.requiresFilamentOverride && !dispatchInitiated) {
      setOverrideResult(result);
      return;
    }

    if (!dispatchInitiated) {
      toast.warning(
        `Job was not dispatched: ${
          result.filamentCheck?.message ?? 'the server did not initiate dispatch'
        }`,
        { duration: 8000 },
      );
      return;
    }

    setOverrideResult(null);
    toast.success(
      result.filamentOverrideApplied
        ? `Dispatching "${result.nextJob.name}" to ${printerName} (filament override confirmed)`
        : `Dispatching "${result.nextJob.name}" to ${printerName}`
    );
    applyOptimisticUpdate(result);
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

      handleReadyResult(confirmation.result);
    } catch {
      toast.error('Failed to confirm bed clear');
    }
  };

  const handleFilamentOverride = async () => {
    try {
      const confirmation = await confirmBedClear.mutateAsync({
        status: autoDispatchStatus,
        confirmFilamentOverride: true,
        overrideJobETag:
          overrideResult?.nextJob?.jobETag ??
          autoDispatchStatus.nextJobETag ??
          '',
      });
      if (confirmation.kind === 'standard') {
        handleReadyResult(confirmation.result);
      }
    } catch {
      toast.error('Failed to confirm filament override');
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

      <FilamentOverrideModal
        isOpen={overrideResult !== null}
        filamentCheck={overrideResult?.filamentCheck ?? null}
        isPending={confirmBedClear.isPending}
        onCancel={() => setOverrideResult(null)}
        onConfirm={handleFilamentOverride}
      />
    </>
  );
}
