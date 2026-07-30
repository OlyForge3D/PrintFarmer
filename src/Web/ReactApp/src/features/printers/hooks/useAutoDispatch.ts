import { useEffect } from 'react';
import { QueryClient, useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { printerSignalRService } from '@/services/printer-signalr';
import {
  mutationErrorMessage,
  mutationErrorStatus,
} from '@/common/utils/mutationError';
import type {
  AutoDispatchGlobalStatus,
  AutoDispatchReadyResult,
  AutoDispatchStatus,
  BedClearAcknowledgementResult,
} from '@/types/api';

const KEYS = {
  all: ['auto-dispatch'] as const,
  status: (printerId: string) => [...KEYS.all, 'status', printerId] as const,
  allStatuses: ['auto-dispatch', 'all-statuses'] as const,
  globalStatus: ['auto-dispatch', 'global-status'] as const,
};

const autoDispatchQueryClients = new Set<QueryClient>();
let autoDispatchSignalRUnsubscribe: (() => void) | undefined;

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
  queryClient.setQueryData<AutoDispatchStatus[]>(KEYS.allStatuses, (existing = []) =>
    upsertStatus(existing, nextStatus),
  );
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
      queryClient.refetchQueries({
        queryKey: KEYS.allStatuses,
        exact: true,
        type: 'active',
      }),
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

async function getAutoDispatchStatuses(): Promise<AutoDispatchStatus[]> {
  const { printers = [] } = await apiClient.getAutoDispatchStatus();
  return printers;
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
    queryFn: () => apiClient.getAutoDispatchStatus(),
    staleTime: 10_000,
    refetchInterval: 10_000,
  });
}

/**
 * Per-printer auto-dispatch status derived from the bulk endpoint.
 * Uses `select` so all cards share one query instead of N+1 individual calls.
 */
export function useAutoDispatchStatus(printerId: string) {
  useAutoDispatchSignalRSync();
  return useQuery({
    queryKey: KEYS.allStatuses,
    queryFn: getAutoDispatchStatuses,
    select: (data: AutoDispatchStatus[]) => data.find(s => s.printerId === printerId),
    enabled: !!printerId,
    refetchInterval: 10_000,
    staleTime: 8_000,
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
      await apiClient.setAutoDispatchEnabled(
        printerId,
        enabled,
        dispatchStateETag,
        printerETag
      );
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
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

export function useAllAutoDispatchStatuses() {
  const qc = useQueryClient();
  useAutoDispatchSignalRSync();
  const query = useQuery<AutoDispatchStatus[]>({
    queryKey: KEYS.allStatuses,
    queryFn: getAutoDispatchStatuses,
    refetchInterval: 10_000,
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
      await apiClient.setAutoDispatchGlobalEnabled(enabled, statuses);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: KEYS.all });
    },
    onError: (error) =>
      handleMutationError(qc, error, 'Failed to update farm auto-dispatch'),
  });
}

export function useConfirmBedClear() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (status: AutoDispatchStatus): Promise<ConfirmBedClearResult> => {
      const dispatchStateETag = requireStatusEtag(
        status.dispatchStateETag,
        'Dispatch-state ETag'
      );
      if (status.nextJobKind !== 'FilamentCalibration') {
        return {
          kind: 'standard',
          result: await apiClient.confirmAutoDispatchReady(
            status.printerId,
            dispatchStateETag
          ),
        };
      }
      const jobId = status.nextJobId;
      const jobETag = requireStatusEtag(status.nextJobETag, 'Job ETag');
      if (!jobId) throw new Error('The exact calibration job is unavailable.');
      const result =
        await apiClient.acknowledgeCalibrationBedClearAndStart({
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
    onSuccess: (data, status) => {
      if (data.kind === 'standard') {
        syncAutoDispatchCaches(qc, data.result.status);
      }
      qc.invalidateQueries({ queryKey: KEYS.status(status.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
      qc.invalidateQueries({ queryKey: ['job-queue'] });
    },
    onError: (error) =>
      handleMutationError(qc, error, 'Failed to acknowledge the clear bed'),
  });
}

export function useSkipNextJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (status: AutoDispatchStatus) => {
      await apiClient.skipAutoDispatchJob(
        status.printerId,
        requireStatusEtag(status.dispatchStateETag, 'Dispatch-state ETag'),
        requireStatusEtag(status.nextJobETag, 'Job ETag')
      );
    },
    onSuccess: (_data, status) => {
      qc.invalidateQueries({ queryKey: KEYS.status(status.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
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
      await apiClient.cancelAutoDispatch(
        status.printerId,
        requireStatusEtag(status.dispatchStateETag, 'Dispatch-state ETag')
      );
    },
    onSuccess: (_data, status) => {
      qc.invalidateQueries({ queryKey: KEYS.status(status.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
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
      apiClient.preClearAutoDispatchBed(
        status.printerId,
        requireStatusEtag(status.dispatchStateETag, 'Dispatch-state ETag')
      ),
    onSuccess: (_data, status) => {
      qc.invalidateQueries({ queryKey: KEYS.status(status.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
      toast.success('Bed pre-cleared — ready for immediate dispatch');
    },
    onError: (error) =>
      handleMutationError(qc, error, 'Failed to pre-clear bed'),
  });
}
