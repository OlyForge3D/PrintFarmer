import { useState, useEffect, useCallback, useRef } from 'react';
import { slicerHubService, type SliceJobEvent } from '@/services/slicerHubService';
import { sliceJobService } from '@/services/sliceJobService';

export interface SliceJobProgressState {
  progressPercent: number;
  progressMessage: string | null;
  status: string | null;
  estimatedPrintTimeSeconds: number | null;
  filamentUsedGrams: number | null;
  resultFileUrl: string | null;
  error: string | null;
  isConnected: boolean;
}

const INITIAL_STATE: SliceJobProgressState = {
  progressPercent: 0,
  progressMessage: null,
  status: null,
  estimatedPrintTimeSeconds: null,
  filamentUsedGrams: null,
  resultFileUrl: null,
  error: null,
  isConnected: false,
};

/**
 * Hook that subscribes to real-time progress events for a specific slice job
 * via the SlicerHub SignalR connection.
 */
export function useSliceJobProgress(jobId: string | null): SliceJobProgressState {
  const [state, setState] = useState<SliceJobProgressState>(INITIAL_STATE);
  const [prevJobId, setPrevJobId] = useState<string | null>(null);
  const unsubRef = useRef<(() => void) | null>(null);
  // Tracks whether a live 'slicejobevent' has arrived since the most recent
  // (re)subscription attempt for the current job. Reset before every
  // subscribe/resubscribe so a live event always wins over the REST
  // reconciliation fetch for that window, while still letting reconciliation
  // run again on the next reconnect if the terminal event was missed while
  // disconnected (issue #1912) — a guard keyed off `state.status` alone
  // would stay permanently "satisfied" once any non-terminal event had ever
  // arrived, even across a reconnect that missed the terminal one.
  const hasLiveEventRef = useRef(false);
  // Monotonic token identifying the most recent (re)subscribe attempt. Each
  // call to reconcileStatus() captures the token value current at its own
  // start; if a newer (re)subscribe attempt starts (and bumps the token)
  // before an older reconcileStatus's REST call resolves, the older
  // response is discarded even though it is not the *last* one to resolve.
  // Without this, a slow response from attempt A racing a fast reconnect
  // into attempt B could land after B's correct result and clobber it back
  // to stale data.
  const reconcileAttemptRef = useRef(0);

  // React-recommended "adjust state during render" pattern for prop changes
  if (jobId !== prevJobId) {
    setPrevJobId(jobId);
    setState(INITIAL_STATE);
  }

  const handleEvent = useCallback((event: SliceJobEvent) => {
    hasLiveEventRef.current = true;
    setState(prev => ({
      ...prev,
      status: event.status,
      progressPercent: event.progressPercent ?? prev.progressPercent,
      progressMessage: event.progressMessage ?? prev.progressMessage,
      estimatedPrintTimeSeconds: event.estimatedPrintTimeSeconds ?? prev.estimatedPrintTimeSeconds,
      filamentUsedGrams: event.filamentUsedGrams ?? prev.filamentUsedGrams,
      resultFileUrl: event.resultFileUrl ?? prev.resultFileUrl,
      error: event.errorMessage ?? prev.error,
    }));
  }, []);

  useEffect(() => {
    if (!jobId) return;

    let cancelled = false;
    // Fresh race window for this job: any live event received for a
    // previous job (or before this effect ran) must not suppress
    // reconciliation for this one.
    hasLiveEventRef.current = false;

    // Reconciles local state with the job's current server-side status. A
    // fast job (e.g. one that fails almost immediately) — or a job whose
    // terminal event was broadcast while the connection was reconnecting —
    // can reach a terminal state before/while we (re)subscribe. SignalR does
    // not replay missed events to a late-joining group member, so without
    // this catch-up fetch the UI would be stuck at "Job queued" forever
    // (issue #1912). Only applied when no live event has arrived since the
    // most recent (re)subscribe attempt, so a real-time event — past or
    // still to come — always wins.
    const reconcileStatus = async () => {
      const attempt = ++reconcileAttemptRef.current;
      try {
        const current = await sliceJobService.getJobStatus(jobId);
        if (cancelled || hasLiveEventRef.current || attempt !== reconcileAttemptRef.current) return;
        setState(prev => {
          if (hasLiveEventRef.current || attempt !== reconcileAttemptRef.current) return prev;
          return {
            ...prev,
            status: current.status,
            progressPercent: current.progressPercent ?? prev.progressPercent,
            progressMessage: current.progressMessage ?? prev.progressMessage,
            estimatedPrintTimeSeconds: current.estimatedPrintTimeSeconds ?? prev.estimatedPrintTimeSeconds,
            filamentUsedGrams: current.filamentUsedGrams ?? prev.filamentUsedGrams,
            resultFileUrl: current.resultFileUrl ?? prev.resultFileUrl,
            error: current.errorMessage ?? prev.error,
          };
        });
      } catch {
        // Best effort only — live SignalR events remain the primary source
        // of truth and this reconciliation must never surface as a
        // connection error.
      }
    };

    const setup = async () => {
      try {
        await slicerHubService.ensureConnected();
        if (cancelled) return;

        if (!slicerHubService.isConnected()) {
          setState(prev => ({ ...prev, isConnected: false }));
          return;
        }

        setState(prev => ({ ...prev, isConnected: true }));
        await slicerHubService.subscribeToJob(jobId);
        if (cancelled) return;

        unsubRef.current = slicerHubService.onJobEvent(jobId, handleEvent);
        await reconcileStatus();
      } catch {
        if (!cancelled) {
          setState(prev => ({ ...prev, isConnected: false }));
        }
      }
    };

    const resubscribe = async () => {
      if (cancelled || !jobId) return;
      // A previous live event (e.g. a non-terminal "Processing" update)
      // must not suppress reconciliation forever: the terminal event for
      // *this* job may have been broadcast and missed entirely while the
      // connection was down, so every reconnect gets its own fresh race
      // window instead of inheriting "already satisfied" from before the
      // drop (this is what left jobs stuck at issue #1912).
      hasLiveEventRef.current = false;
      try {
        await slicerHubService.subscribeToJob(jobId);
        setState(prev => ({ ...prev, isConnected: true }));
        await reconcileStatus();
      } catch {
        setState(prev => ({ ...prev, isConnected: false }));
      }
    };

    const unsubReconnect = slicerHubService.onReconnected(resubscribe);
    setup();

    return () => {
      cancelled = true;
      unsubRef.current?.();
      unsubRef.current = null;
      unsubReconnect();
      slicerHubService.unsubscribeFromJob(jobId).catch(() => { /* best effort */ });
    };
  }, [jobId, handleEvent]);

  return state;
}
