import { useEffect, useCallback, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { slicerHubService, type SliceJobEvent } from '@/services/slicerHubService';
import type { SliceJobStatusResponse } from '@/services/sliceJobService';

const SLICE_JOBS_KEY = ['slice-jobs'] as const;

/**
 * Maps a SignalR SliceJobEvent to a partial SliceJobStatusResponse
 * for optimistic cache updates.
 */
function applyEventToJob(
  existing: SliceJobStatusResponse,
  event: SliceJobEvent,
): SliceJobStatusResponse {
  return {
    ...existing,
    status: event.status,
    progressPercent: event.progressPercent ?? existing.progressPercent,
    progressMessage: event.progressMessage ?? existing.progressMessage,
    startedAt: event.startedAt ?? existing.startedAt,
    completedAt: event.completedAt ?? existing.completedAt,
    resultFileUrl: event.resultFileUrl ?? existing.resultFileUrl,
    estimatedPrintTimeSeconds: event.estimatedPrintTimeSeconds ?? existing.estimatedPrintTimeSeconds,
    filamentUsedGrams: event.filamentUsedGrams ?? existing.filamentUsedGrams,
    errorMessage: event.errorMessage ?? existing.errorMessage,
    workerId: event.workerId ?? existing.workerId,
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
          const updated = [...old];
          updated[idx] = applyEventToJob(updated[idx], event);
          return updated;
        },
      );
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

        await slicerHubService.joinUserGroup(userId);
        if (cancelled) return;

        unsubUser = slicerHubService.onUserJobEvent(handleEvent);
        setIsConnected(true);
      } catch {
        setIsConnected(false);
      }
    };

    setup();

    return () => {
      cancelled = true;
      unsubUser?.();
      setIsConnected(false);
      if (userId) {
        slicerHubService.leaveUserGroup(userId).catch(() => { /* best effort */ });
      }
    };
  }, [enabled, userId, handleEvent]);

  return { isConnected };
}
