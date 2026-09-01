import { useEffect, useState } from 'react';
import { QueryClient, useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import {
  getAutoDispatchStatus,
  setAutoDispatchEnabled,
  setAutoDispatchGlobalEnabled,
  confirmAutoDispatchReady,
  acknowledgeBedClearAndStart,
  skipAutoDispatchJob,
  cancelAutoDispatch,
  preClearAutoDispatchBed,
} from '@/services/api/autoDispatchApi';
import { printerSignalRService } from '@/services/printer-signalr';
import {
  mutationErrorMessage,
  mutationErrorStatus,
} from '@/common/utils/mutationError';
import { queueSummariesFleetQueryKey } from '@/features/printers/hooks/useQueueSummariesFleet';
import type {
  AutoDispatchGlobalStatus,
  AutoDispatchReadyResult,
  AutoDispatchStatus,
  BedClearAcknowledgementResult,
} from '@/types/api';

const KEYS = {
  all: ['auto-dispatch'] as const,
  status: (printerId: string) => [...KEYS.all, 'status', printerId] as const,
  // Single cache key for the full AutoDispatchGlobalStatus payload. Both the
  // per-printer/all-printers list views and the dashboard's global status view
  // derive from this one cached value via `select`, so there is only one
  // request to GET /api/auto-dispatch/status per poll interval regardless of
  // how many consumers are mounted.
  globalStatus: ['auto-dispatch', 'global-status'] as const,
};

const autoDispatchQueryClients = new Set<QueryClient>();
let autoDispatchSignalRUnsubscribe: (() => void) | undefined;

// Shared freshness window for the KEYS.globalStatus query. All consumers
// (useAutoDispatchGlobalStatus, useAutoDispatchStatus, useAllAutoDispatchStatuses)
// must agree on the same staleTime/refetchInterval so that whichever one mounts
// first and whichever one mounts second draw the identical "is this still
// fresh?" conclusion. Divergent per-hook staleTime values previously let a
// later-mounting consumer decide the shared cache entry was already stale
// and re-trigger a fetch (#1547).
const GLOBAL_STATUS_STALE_TIME_MS = 8_000;
const GLOBAL_STATUS_REFETCH_INTERVAL_MS = 10_000;

// Single-flight guard around the GET /api/auto-dispatch/status request itself.
// TanStack Query already dedupes concurrent fetches for observers that share
// one Query instance in one QueryClient, but that guarantee only holds once
// every mounted consumer has actually subscribed to the *same* in-flight
// fetch. On a cold navigation, Layout's useAllAutoDispatchStatuses and the
// lazy-loaded dashboard's useAutoDispatchGlobalStatus can each independently
// decide (via shouldFetchOnMount) to call queryFn before the other has
// subscribed, producing two real HTTP requests moments apart (#1547). This
// wrapper collapses any overlapping calls into the one underlying request
// regardless of the exact observer-mount timing that triggered them.
let inflightGlobalStatusRequest: Promise<AutoDispatchGlobalStatus> | null = null;

function fetchAutoDispatchGlobalStatus(): Promise<AutoDispatchGlobalStatus> {
  if (!inflightGlobalStatusRequest) {
    const request: Promise<AutoDispatchGlobalStatus> = getAutoDispatchStatus().finally(() => {
      // Only clear the guard if it still points at *this* request. A forced
      // refresh (see resetAutoDispatchGlobalStatusInFlight below) may have
      // already replaced it with a newer in-flight request by the time this
      // older one settles; clearing unconditionally here would wipe out
      // tracking for that newer, still-pending request.
      if (inflightGlobalStatusRequest === request) {
        inflightGlobalStatusRequest = null;
      }
    });
    inflightGlobalStatusRequest = request;
  }
  return inflightGlobalStatusRequest;
}

/**
 * Drops the single-flight guard so the next call to
 * fetchAutoDispatchGlobalStatus() issues a brand-new HTTP request instead of
 * reusing whatever request (e.g. a still-pending background poll) happened
 * to be in flight. Explicit mutation invalidation/refetch must never be
 * satisfied by a pre-mutation response, so every mutation success/error
 * handler that invalidates or refetches KEYS.globalStatus (or the broader
 * KEYS.all prefix, which includes it) calls this first.
 */
function resetAutoDispatchGlobalStatusInFlight() {
  inflightGlobalStatusRequest = null;
}

/**
 * Test-only escape hatch. Vitest does not reset this module's top-level
 * state between `it()` blocks within the same test file, so a leftover
 * in-flight promise reference from one test could otherwise leak into the
 * next and mask real dedup regressions. Call this in `beforeEach`.
 */
export function __resetAutoDispatchGlobalStatusSingleFlightForTests() {
  resetAutoDispatchGlobalStatusInFlight();
}

function mergeStatusSnapshot<T extends AutoDispatchStatus>(
  previousStatus: T | undefined,
  nextStatus: AutoDispatchStatus,
): T {
  return {
    ...previousStatus,
    ...nextStatus,
    printerName: nextStatus.printerName ?? previousStatus?.printerName,
    isReady: nextStatus.isReady ?? previousStatus?.isReady,
    currentJobName: nextStatus.currentJobName ?? previousStatus?.currentJobName,
    lastActivity: nextStatus.lastActivity ?? previousStatus?.lastActivity,
    bedPreConfirmed: nextStatus.bedPreConfirmed ?? previousStatus?.bedPreConfirmed,
    readyGateChecks: nextStatus.readyGateChecks ?? previousStatus?.readyGateChecks,
    attentionMessage: nextStatus.attentionMessage ?? previousStatus?.attentionMessage,
    attentionReason: nextStatus.attentionReason ?? previousStatus?.attentionReason,
    operatorAction: nextStatus.operatorAction ?? previousStatus?.operatorAction,
  } as T;
}

function upsertStatus<T extends AutoDispatchStatus>(statuses: T[], nextStatus: AutoDispatchStatus): T[] {
  const nextStatuses = [...statuses];
  const existingIndex = nextStatuses.findIndex((status) => status.printerId === nextStatus.printerId);

  if (existingIndex >= 0) {
    nextStatuses[existingIndex] = mergeStatusSnapshot(nextStatuses[existingIndex], nextStatus);
    return nextStatuses;
  }

  nextStatuses.push(mergeStatusSnapshot<T>(undefined, nextStatus));
  return nextStatuses;
}

function syncAutoDispatchCaches(queryClient: QueryClient, nextStatus: AutoDispatchStatus) {
  queryClient.setQueryData<AutoDispatchStatus | undefined>(KEYS.status(nextStatus.printerId), (existing) =>
    mergeStatusSnapshot(existing, nextStatus),
  );
  queryClient.setQueryData<AutoDispatchGlobalStatus | undefined>(KEYS.globalStatus, (existing) => {
    if (!existing) {
      return existing;
    }

    return {
      ...existing,
      printers: upsertStatus(existing.printers, nextStatus),
    };
  });
}

function requireStatusEtag(
  value: string | null | undefined,
  label: string
): string {
  if (!value) throw new Error(`${label} is unavailable; refresh and review again.`);
  return value;
}

function stableBedClearIdempotencyKey(status: AutoDispatchStatus): string {
  const jobId = status.nextJobId ?? 'missing';
  const jobEtag = status.nextJobETag ?? 'missing';
  const dispatchEtag = status.dispatchStateETag ?? 'missing';
  const storageKey = `printfarmer:bed-clear:${jobId}:${jobEtag}:${dispatchEtag}`;
  const existing = localStorage.getItem(storageKey);
  if (existing) return existing;
  const created =
    typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID()
      : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  localStorage.setItem(storageKey, created);
  return created;
}

export type ConfirmBedClearResult =
  | { kind: 'standard'; result: AutoDispatchReadyResult }
  | {
      kind: 'calibration';
      result: Extract<
        BedClearAcknowledgementResult,
        { kind: 'accepted' | 'replayed' }
      >;
    };

export type ConfirmBedClearVariables =
  | AutoDispatchStatus
  | {
      status: AutoDispatchStatus;
      confirmFilamentOverride: true;
      overrideJobETag: string;
      filamentCheckETag: string;
    };

function getReviewedStatus(variables: ConfirmBedClearVariables): AutoDispatchStatus {
  return 'status' in variables ? variables.status : variables;
}

function reviewedMutationError(
  statusCode: number,
  detail: string | undefined
): Error & { statusCode: number; data: { detail?: string } } {
  return Object.assign(
    new Error(detail ?? 'The reviewed mutation was not accepted.'),
    { statusCode, data: { detail } }
  );
}

async function handleMutationError(
  queryClient: QueryClient,
  error: unknown,
  fallback: string,
  printerId?: string
) {
  const status = mutationErrorStatus(error);
  if (status === 412 || status === 428) {
    // Force a genuinely fresh fetch rather than reusing whatever background
    // poll happened to be in flight when this conflict was detected.
    resetAutoDispatchGlobalStatusInFlight();
    const exactRefetch = printerId
      ? queryClient.refetchQueries({
          queryKey: KEYS.status(printerId),
          exact: true,
          type: 'active',
        })
      : Promise.resolve();
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: KEYS.all }),
      queryClient.invalidateQueries({ queryKey: ['job-queue'] }),
      queryClient.invalidateQueries({ queryKey: queueSummariesFleetQueryKey }),
      queryClient.refetchQueries({
        queryKey: KEYS.globalStatus,
        exact: true,
        type: 'active',
      }),
      exactRefetch,
    ]);
  }
  toast.error(mutationErrorMessage(error, fallback));
}

function ensureAutoDispatchSignalRSubscription() {
  if (autoDispatchSignalRUnsubscribe) {
    return;
  }

  void printerSignalRService.connect();
  autoDispatchSignalRUnsubscribe = printerSignalRService.onAutoDispatchStateChanged((status) => {
    autoDispatchQueryClients.forEach((queryClient) => {
      syncAutoDispatchCaches(queryClient, status);
    });
  });
}

function useAutoDispatchSignalRSync() {
  const queryClient = useQueryClient();

  useEffect(() => {
    autoDispatchQueryClients.add(queryClient);
    ensureAutoDispatchSignalRSubscription();

    return () => {
      autoDispatchQueryClients.delete(queryClient);

      if (autoDispatchQueryClients.size === 0 && autoDispatchSignalRUnsubscribe) {
        autoDispatchSignalRUnsubscribe();
        autoDispatchSignalRUnsubscribe = undefined;
      }
    };
  }, [queryClient]);
}

/** Dashboard hook — returns full global status including readyGateChecks */
export function useAutoDispatchGlobalStatus() {
  useAutoDispatchSignalRSync();
  return useQuery({
    queryKey: KEYS.globalStatus,
    queryFn: fetchAutoDispatchGlobalStatus,
    staleTime: GLOBAL_STATUS_STALE_TIME_MS,
    refetchInterval: GLOBAL_STATUS_REFETCH_INTERVAL_MS,
  });
}

/**
 * Per-printer auto-dispatch status derived from the shared global-status query.
 * Uses `select` so all cards share one query instead of N+1 individual calls.
 */
export function useAutoDispatchStatus(printerId: string) {
  useAutoDispatchSignalRSync();
  return useQuery({
    queryKey: KEYS.globalStatus,
    queryFn: fetchAutoDispatchGlobalStatus,
    select: (data: AutoDispatchGlobalStatus) =>
      data.printers.find(s => s.printerId === printerId),
    enabled: !!printerId,
    refetchInterval: GLOBAL_STATUS_REFETCH_INTERVAL_MS,
    staleTime: GLOBAL_STATUS_STALE_TIME_MS,
  });
}

export function useSetAutoDispatchEnabled() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({
      printerId,
      enabled,
      dispatchStateETag,
      printerETag,
    }: {
      printerId: string;
      enabled: boolean;
      dispatchStateETag: string;
      printerETag: string;
    }) => {
      await setAutoDispatchEnabled(
        printerId,
        enabled,
        dispatchStateETag,
        printerETag
      );
    },
    onSuccess: () => {
      resetAutoDispatchGlobalStatusInFlight();
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
    },
    onError: (error, variables) =>
      handleMutationError(
        qc,
        error,
        'Failed to update auto-dispatch',
        variables.printerId
      ),
  });
}

/**
 * Derives the flat printer-status list from the shared global-status query via
 * `select`, so this hook shares one cached request with useAutoDispatchGlobalStatus
 * instead of polling GET /api/auto-dispatch/status a second time.
 */
export function useAllAutoDispatchStatuses() {
  const qc = useQueryClient();
  useAutoDispatchSignalRSync();
  const query = useQuery({
    queryKey: KEYS.globalStatus,
    queryFn: fetchAutoDispatchGlobalStatus,
    select: (data: AutoDispatchGlobalStatus) => data.printers ?? [],
    staleTime: GLOBAL_STATUS_STALE_TIME_MS,
    refetchInterval: GLOBAL_STATUS_REFETCH_INTERVAL_MS,
  });

  const data = query.data;
  useEffect(() => {
    if (data) {
      for (const status of data) {
        qc.setQueryData(KEYS.status(status.printerId), status);
      }
    }
  }, [data, qc]);

  return query;
}

export function useSetAllAutoDispatchEnabled() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({
      enabled,
      statuses,
    }: {
      enabled: boolean;
      statuses: AutoDispatchStatus[];
    }) => {
      await setAutoDispatchGlobalEnabled(enabled, statuses);
    },
    onSuccess: () => {
      resetAutoDispatchGlobalStatusInFlight();
      qc.invalidateQueries({ queryKey: KEYS.all });
    },
    onError: (error) =>
      handleMutationError(qc, error, 'Failed to update farm auto-dispatch'),
  });
}

export function useConfirmBedClear() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (variables: ConfirmBedClearVariables): Promise<ConfirmBedClearResult> => {
      const status = getReviewedStatus(variables);
      const confirmFilamentOverride =
        'status' in variables && variables.confirmFilamentOverride;
      const dispatchStateETag = requireStatusEtag(
        status.dispatchStateETag,
        'Dispatch-state ETag'
      );
      if (status.nextJobKind !== 'FilamentCalibration') {
        return {
          kind: 'standard',
          result: confirmFilamentOverride
            ? await confirmAutoDispatchReady(
                status.printerId,
                dispatchStateETag,
                true,
                variables.overrideJobETag,
                variables.filamentCheckETag
              )
            : await confirmAutoDispatchReady(
                status.printerId,
                dispatchStateETag
              ),
        };
      }
      const jobId = status.nextJobId;
      const jobETag = requireStatusEtag(status.nextJobETag, 'Job ETag');
      if (!jobId) throw new Error('The exact calibration job is unavailable.');
      const result =
        await acknowledgeBedClearAndStart({
          jobId,
          printerId: status.printerId,
          jobETag,
          dispatchStateETag,
          expectedPrinterConfigRevision:
            status.nextJobPrinterConfigRevision,
          idempotencyKey: stableBedClearIdempotencyKey(status),
        });
      if (result.kind !== 'accepted' && result.kind !== 'replayed') {
        throw reviewedMutationError(
          result.httpStatus,
          'detail' in result ? result.detail : undefined
        );
      }
      return {
        kind: 'calibration',
        result,
      };
    },
    onSuccess: (data, variables) => {
      const status = getReviewedStatus(variables);
      if (data.kind === 'standard') {
        syncAutoDispatchCaches(qc, data.result.status);
      }
      resetAutoDispatchGlobalStatusInFlight();
      qc.invalidateQueries({ queryKey: KEYS.status(status.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
      qc.invalidateQueries({ queryKey: ['job-queue'] });
      // Confirming bed-clear dispatches the next queued job, changing that
      // printer's (and potentially others') "X of Y" queue-summary label.
      qc.invalidateQueries({ queryKey: queueSummariesFleetQueryKey });
    },
    onError: (error) =>
      handleMutationError(qc, error, 'Failed to acknowledge the clear bed'),
  });
}

interface FilamentOverrideChallenge {
  status: AutoDispatchStatus;
  printerName: string;
  result: AutoDispatchReadyResult;
}

export function useAutoDispatchReadyFlow(
  onDispatchInitiated?: (result: AutoDispatchReadyResult) => void
) {
  const confirmation = useConfirmBedClear();
  const [challenge, setChallenge] = useState<FilamentOverrideChallenge | null>(null);

  const handleStandardResult = (
    result: AutoDispatchReadyResult,
    status: AutoDispatchStatus,
    printerName: string
  ) => {
    if (!result.nextJob) {
      toast.success(`Bed clear confirmed for ${printerName} — no jobs queued`);
      return;
    }

    const dispatchInitiated = result.dispatchInitiated === true;
    if (result.requiresFilamentOverride && !dispatchInitiated) {
      setChallenge({ status: result.status, printerName, result });
      return;
    }

    if (result.filamentCheckChanged) {
      setChallenge(null);
      toast.warning(
        'Filament conditions changed after review. Check the current details and confirm again.',
        { duration: 8000 }
      );
      return;
    }

    if (!dispatchInitiated) {
      toast.warning(
        `Job was not dispatched: ${
          result.filamentCheck?.message ?? 'the server did not initiate dispatch'
        }`,
        { duration: 8000 }
      );
      return;
    }

    setChallenge(null);
    if (result.dispatchReconciliationPending) {
      toast.warning(
        `Dispatch submitted for "${result.nextJob.name}" to ${printerName}; awaiting printer reconciliation.`,
        { duration: 8000 }
      );
      onDispatchInitiated?.(result);
      return;
    }

    toast.success(
      result.filamentOverrideApplied
        ? `Dispatching "${result.nextJob.name}" to ${printerName} (filament override confirmed)`
        : `Dispatching "${result.nextJob.name}" to ${printerName}`
    );
    onDispatchInitiated?.(result);
  };

  const confirmReady = async (
    status: AutoDispatchStatus,
    printerName: string
  ) => {
    const response = await confirmation.mutateAsync(status);
    if (response.kind === 'calibration') {
      const jobName = status.nextJobName ?? 'Calibration job';
      toast.success(
        response.result.kind === 'accepted'
          ? `Dispatching "${jobName}" to ${printerName}`
          : `Calibration dispatch for "${jobName}" was already accepted`
      );
      return;
    }

    handleStandardResult(response.result, status, printerName);
  };

  const confirmFilamentOverride = async () => {
    if (!challenge) return;

    const response = await confirmation.mutateAsync({
      status: challenge.status,
      confirmFilamentOverride: true,
      overrideJobETag:
        challenge.result.nextJob?.jobETag ??
        challenge.status.nextJobETag ??
        '',
      filamentCheckETag: challenge.result.filamentCheckETag ?? '',
    });
    if (response.kind === 'standard') {
      handleStandardResult(
        response.result,
        challenge.status,
        challenge.printerName
      );
    }
  };

  return {
    challenge,
    confirmation,
    confirmReady,
    confirmFilamentOverride,
    cancelFilamentOverride: () => setChallenge(null),
  };
}

export function useSkipNextJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (status: AutoDispatchStatus) => {
      await skipAutoDispatchJob(
        status.printerId,
        requireStatusEtag(status.dispatchStateETag, 'Dispatch-state ETag'),
        requireStatusEtag(status.nextJobETag, 'Job ETag')
      );
    },
    onSuccess: (_data, status) => {
      resetAutoDispatchGlobalStatusInFlight();
      qc.invalidateQueries({ queryKey: KEYS.status(status.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
    },
    onError: (error) =>
      handleMutationError(qc, error, 'Failed to skip the reviewed job'),
  });
}

export function useCancelAutoDispatch() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (status: AutoDispatchStatus) => {
      await cancelAutoDispatch(
        status.printerId,
        requireStatusEtag(status.dispatchStateETag, 'Dispatch-state ETag')
      );
    },
    onSuccess: (_data, status) => {
      resetAutoDispatchGlobalStatusInFlight();
      qc.invalidateQueries({ queryKey: KEYS.status(status.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
    },
    onError: (error) =>
      handleMutationError(qc, error, 'Failed to cancel auto-dispatch'),
  });
}

export function usePreClearBed() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (status: AutoDispatchStatus) =>
      preClearAutoDispatchBed(
        status.printerId,
        requireStatusEtag(status.dispatchStateETag, 'Dispatch-state ETag')
      ),
    onSuccess: (result, status) => {
      resetAutoDispatchGlobalStatusInFlight();
      qc.invalidateQueries({ queryKey: KEYS.status(status.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
      if (result.bedPreConfirmed) {
        toast.success('Bed pre-cleared — ready for immediate dispatch');
      } else {
        toast.warning(
          result.attentionMessage ??
            'Bed pre-clear stopped because the queued job needs filament confirmation.'
        );
      }
    },
    onError: (error) =>
      handleMutationError(qc, error, 'Failed to pre-clear bed'),
  });
}
