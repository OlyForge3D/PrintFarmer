import { useState, useEffect, useCallback, useRef } from 'react';
import { slicerHubService, type SliceJobEvent } from '@/services/slicerHubService';

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

  // React-recommended "adjust state during render" pattern for prop changes
  if (jobId !== prevJobId) {
    setPrevJobId(jobId);
    setState(INITIAL_STATE);
  }

  const handleEvent = useCallback((event: SliceJobEvent) => {
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
      } catch {
        if (!cancelled) {
          setState(prev => ({ ...prev, isConnected: false }));
        }
      }
    };

    const resubscribe = async () => {
      if (cancelled || !jobId) return;
      try {
        await slicerHubService.subscribeToJob(jobId);
        setState(prev => ({ ...prev, isConnected: true }));
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
