import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiClient } from '@/services/api';
import { mutationErrorMessage, mutationErrorStatus } from '@/common/utils/mutationError';
import type { QueuedPrintJobWithFileMetaDto } from '@/types/api';
import {
  canDropQueueJob,
  getQueueMoveNeighbors,
  getQueueJobOrdinal,
  moveQueuedJobInList,
  type QueueMovePlacement,
} from '@/features/queue/utils/queueReordering';

interface UseQueueReorderingOptions {
  jobs: QueuedPrintJobWithFileMetaDto[];
  queryKey: readonly unknown[];
  enabled: boolean;
  onRefresh: () => Promise<void>;
  onError: (message: string | null) => void;
}

interface OptimisticQueue {
  queryKey: string;
  serverSnapshot: string;
  jobs: QueuedPrintJobWithFileMetaDto[];
}

function getQueueSnapshot(jobs: QueuedPrintJobWithFileMetaDto[]): string {
  return JSON.stringify(
    jobs.map(({ job, assignedPrinter }) => [
      job.id,
      job.rowVersion,
      job.status,
      job.priority,
      job.queuePosition,
      job.assignedPrinterId,
      assignedPrinter?.id,
    ])
  );
}

function hasRevision(value: string | null | undefined): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function getJobLabel(job: QueuedPrintJobWithFileMetaDto): string {
  return job.gcodeFile?.name || job.gcodeFile?.fileName || job.job.fileName || job.job.name || 'Print job';
}

function getQueueScopeLabel(job: QueuedPrintJobWithFileMetaDto): string {
  return job.assignedPrinter?.name || 'Any printer';
}

export function useQueueReordering({
  jobs,
  queryKey,
  enabled,
  onRefresh,
  onError,
}: UseQueueReorderingOptions) {
  const [optimisticQueue, setOptimisticQueue] = useState<OptimisticQueue | null>(null);
  const [isMoving, setIsMoving] = useState(false);
  const [draggedJobId, setDraggedJobId] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const moveInFlight = useRef(false);
  const latestJobs = useRef(jobs);
  useEffect(() => {
    latestJobs.current = jobs;
  }, [jobs]);
  const queryKeyHash = JSON.stringify(queryKey);
  const serverSnapshot = getQueueSnapshot(jobs);
  const displayedJobs =
    optimisticQueue?.queryKey === queryKeyHash &&
    optimisticQueue.serverSnapshot === serverSnapshot
      ? optimisticQueue.jobs
      : jobs;

  const canDropOnJob = useCallback(
    (movedJobId: string, neighborJobId: string) =>
      enabled &&
      !moveInFlight.current &&
      canDropQueueJob(jobs, movedJobId, neighborJobId),
    [enabled, jobs]
  );

  const startDraggingJob = useCallback(
    (jobId: string) => {
      const job = jobs.find((entry) => entry.job.id === jobId);
      if (
        enabled &&
        !moveInFlight.current &&
        job?.job.status === 'Queued' &&
        hasRevision(job.job.rowVersion)
      ) {
        setDraggedJobId(jobId);
      }
    },
    [enabled, jobs]
  );

  const stopDraggingJob = useCallback(() => setDraggedJobId(null), []);

  const moveJob = useCallback(
    async (movedJobId: string, neighborJobId: string, placement: QueueMovePlacement) => {
      if (!enabled || moveInFlight.current || !canDropQueueJob(jobs, movedJobId, neighborJobId)) {
        return;
      }

      const movedJob = jobs.find((entry) => entry.job.id === movedJobId);
      const neighbor = jobs.find((entry) => entry.job.id === neighborJobId);
      if (!movedJob || !neighbor) return;
      if (!hasRevision(movedJob.job.rowVersion) || !hasRevision(neighbor.job.rowVersion)) {
        onError('Queue revisions are unavailable. Refresh the queue before reordering jobs.');
        return;
      }

      const optimisticJobs = moveQueuedJobInList(jobs, movedJobId, neighborJobId, placement);
      if (!optimisticJobs) return;

      const request =
        placement === 'before'
          ? { beforeJobId: neighborJobId, beforeJobETag: neighbor.job.rowVersion }
          : { afterJobId: neighborJobId, afterJobETag: neighbor.job.rowVersion };
      moveInFlight.current = true;
      setIsMoving(true);
      setOptimisticQueue({ queryKey: queryKeyHash, serverSnapshot, jobs: optimisticJobs });
      setDraggedJobId(null);
      setAnnouncement('');
      onError(null);

      let mutationError: unknown;
      try {
        await apiClient.moveQueuedJob(movedJobId, request, movedJob.job.rowVersion);
      } catch (error) {
        mutationError = error;
      }

      let refreshError: unknown;
      try {
        await onRefresh();
      } catch (error) {
        refreshError = error;
      }

      setOptimisticQueue(null);
      setIsMoving(false);
      moveInFlight.current = false;

      if (mutationError !== undefined) {
        const status = mutationErrorStatus(mutationError);
        const message =
          status === 409 || status === 412
            ? 'Queue changed — refreshed. Review the updated queue before moving a job again.'
            : status === 404
              ? 'Queue changed — refreshed. This job is no longer in the queue.'
              : mutationErrorMessage(mutationError, 'Failed to reorder the queue');
        onError(
          refreshError
            ? `${message} The queue refresh also failed: ${mutationErrorMessage(refreshError, 'unknown refresh error')}`
            : message
        );
        return;
      }

      if (refreshError) {
        onError(
          `The job moved, but the queue could not be refreshed: ${mutationErrorMessage(refreshError, 'unknown refresh error')}`
        );
        return;
      }

      const refreshedJobs = latestJobs.current;
      const refreshedSnapshot = getQueueSnapshot(refreshedJobs);
      const refreshedJob = refreshedJobs.find((entry) => entry.job.id === movedJobId);
      const ordinal =
        refreshedSnapshot === serverSnapshot
          ? getQueueJobOrdinal(optimisticJobs, movedJobId)
          : refreshedJob?.job.status === 'Queued'
            ? getQueueJobOrdinal(refreshedJobs, movedJobId)
            : undefined;
      const movedLabel = getJobLabel(movedJob);
      const scopeLabel = getQueueScopeLabel(movedJob);
      setAnnouncement(
        ordinal
          ? `Moved ${movedLabel} to position ${ordinal} in ${scopeLabel}'s queue.`
          : `Moved ${movedLabel}; the queue has been refreshed.`
      );
    },
    [enabled, jobs, onError, onRefresh, queryKeyHash, serverSnapshot]
  );

  const reorderNeighbors = useMemo(
    () => getQueueMoveNeighbors(displayedJobs),
    [displayedJobs]
  );

  return {
    jobs: displayedJobs,
    isMoving,
    draggedJobId,
    announcement,
    reorderNeighbors,
    canDropOnJob,
    startDraggingJob,
    stopDraggingJob,
    moveJob,
  };
}
