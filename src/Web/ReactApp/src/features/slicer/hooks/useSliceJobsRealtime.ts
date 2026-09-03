import { useEffect, useCallback, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { slicerHubService, type SliceJobEvent } from '@/services/slicerHubService';
import type { SliceJobStatusResponse } from '@/services/sliceJobService';
import { SliceJobStatus } from '@/services/sliceJobService';

const SLICE_JOBS_KEY = ['slice-jobs'] as const;

/**
 * Maps a SignalR SliceJobEvent to a partial SliceJobStatusResponse
 * for optimistic cache updates.
 */
function applyEventToJob(
  existing: SliceJobStatusResponse,
  event: SliceJobEvent,
): SliceJobStatusResponse {
  // The event contract carries no failure classification, so it cannot be filled in from here.
  // What it must not do is leave a *stale* one in place: a job that has been requeued or has since
  // completed would otherwise keep rendering the previous attempt's reason until the next poll.
  // Clearing on any non-failed status matches the server, which only ever reports these for a
  // failed job (issue #1811).
  const stillFailed = event.status === SliceJobStatus.Failed;

  return {
    ...existing,
    status: event.status,
    progressPercent: event.progressPercent ?? existing.progressPercent,
    progressMessage: event.progressMessage ?? existing.progressMessage,
    startedAt: event.startedAt ?? existing.startedAt,
    completedAt: event.completedAt ?? existing.completedAt,
    artifactsRoute: event.artifactsRoute ?? existing.artifactsRoute,
    estimatedPrintTimeSeconds: event.estimatedPrintTimeSeconds ?? existing.estimatedPrintTimeSeconds,
    filamentUsedGrams: event.filamentUsedGrams ?? existing.filamentUsedGrams,
    errorMessage: event.errorMessage ?? existing.errorMessage,
    workerId: event.workerId ?? existing.workerId,
    failureReason: stillFailed ? existing.failureReason : null,
    failureHint: stillFailed ? existing.failureHint : null,
  };
}

export interface UseSliceJobsRealtimeOptions {
  enabled?: boolean;
}

/**
 * Hook that joins the user's SignalR group and applies incoming
 * SliceJobEvent payloads directly to the TanStack Query cache,
 * giving the SliceJobsPage instant updates without waiting for
 * the next polling cycle.
 *
 * Returns `isConnected` so the caller can adjust polling intervals.
 */
export function useSliceJobsRealtime(
  options: UseSliceJobsRealtimeOptions = {},
): { isConnected: boolean } {
  const { enabled = true } = options;
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const userId = user?.id ?? null;
  const [isConnected, setIsConnected] = useState(false);

  const handleEvent = useCallback(
    (event: SliceJobEvent) => {
      // The event payload has no failure classification of its own, so a job that has just failed
      // needs one refetch to pick up the server-computed reason and hint. Without this a connected
      // client shows a bare "Slicing failed." until the next poll (issue #1811).
      let needsRefetch = false;

      queryClient.setQueryData<SliceJobStatusResponse[]>(
        [...SLICE_JOBS_KEY],
        (old) => {
          if (!old) return old;
          const idx = old.findIndex((j) => j.id === event.jobId);
          if (idx === -1) {
            // Unknown job — trigger a background refetch to pick it up
            queryClient.invalidateQueries({ queryKey: [...SLICE_JOBS_KEY] });
            return old;
          }
          const previous = old[idx];
          needsRefetch =
            event.status === SliceJobStatus.Failed && previous.status !== SliceJobStatus.Failed;
          const updated = [...old];
          updated[idx] = applyEventToJob(previous, event);
          return updated;
        },
      );

      if (needsRefetch) {
        queryClient.invalidateQueries({ queryKey: [...SLICE_JOBS_KEY] });
      }
    },
    [queryClient],
  );

  useEffect(() => {
    if (!enabled || !userId) return;

    let cancelled = false;
    let unsubUser: (() => void) | null = null;

    const setup = async () => {
      try {
        await slicerHubService.ensureConnected();
        if (cancelled) return;

        if (!slicerHubService.isConnected()) {
          setIsConnected(false);
          return;
        }

        await slicerHubService.joinUserGroup(userId);
        if (cancelled) return;

        unsubUser = slicerHubService.onUserJobEvent(handleEvent);
        setIsConnected(true);
      } catch {
        setIsConnected(false);
      }
    };

    const resubscribe = async () => {
      if (cancelled || !userId) return;
      try {
        await slicerHubService.joinUserGroup(userId);
        setIsConnected(true);
      } catch {
        setIsConnected(false);
      }
    };

    const unsubReconnect = slicerHubService.onReconnected(resubscribe);
    setup();

    return () => {
      cancelled = true;
      unsubUser?.();
      unsubReconnect();
      setIsConnected(false);
      if (userId) {
        slicerHubService.leaveUserGroup(userId).catch(() => { /* best effort */ });
      }
    };
  }, [enabled, userId, handleEvent]);

  return { isConnected };
}
