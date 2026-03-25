import { useEffect } from 'react';
import { QueryClient, useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { printerSignalRService } from '@/services/printer-signalr';
import type { AutoDispatchGlobalStatus, AutoDispatchStatus } from '@/types/api';

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
    isReady: nextStatus.isReady,
    currentJobName: nextStatus.currentJobName,
    lastActivity: nextStatus.lastActivity,
    bedPreConfirmed: nextStatus.bedPreConfirmed,
    readyGateChecks: nextStatus.readyGateChecks,
    attentionMessage: nextStatus.attentionMessage,
    attentionReason: nextStatus.attentionReason,
    operatorAction: nextStatus.operatorAction,
  } as T;
}

function upsertStatus<T extends AutoDispatchStatus>(statuses: T[], nextStatus: AutoDispatchStatus): T[] {
  const nextStatuses = [...statuses];
  const existingIndex = nextStatuses.findIndex((status) => status.printerId === nextStatus.printerId);

  if (existingIndex >= 0) {
    nextStatuses[existingIndex] = mergeStatusSnapshot(nextStatuses[existingIndex], nextStatus);
    return nextStatuses;
  }

  nextStatuses.push(mergeStatusSnapshot(undefined, nextStatus));
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
    mutationFn: async ({ printerId, enabled }: { printerId: string; enabled: boolean }) => {
      await apiClient.setAutoDispatchEnabled(printerId, enabled);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
    },
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
    mutationFn: async (enabled: boolean) => {
      await apiClient.setAutoDispatchGlobalEnabled(enabled);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: KEYS.all });
    },
  });
}

export function useConfirmBedClear() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (printerId: string) => apiClient.confirmAutoDispatchReady(printerId),
    onSuccess: (data, printerId) => {
      syncAutoDispatchCaches(qc, data.status);
      qc.invalidateQueries({ queryKey: KEYS.status(printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
    },
  });
}

export function useSkipNextJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (printerId: string) => {
      await apiClient.skipAutoDispatchJob(printerId);
    },
    onSuccess: (_data, printerId) => {
      qc.invalidateQueries({ queryKey: KEYS.status(printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
    },
  });
}

export function useCancelAutoDispatch() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (printerId: string) => {
      await apiClient.cancelAutoDispatch(printerId);
    },
    onSuccess: (_data, printerId) => {
      qc.invalidateQueries({ queryKey: KEYS.status(printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
    },
  });
}

export function usePreClearBed() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (printerId: string) => apiClient.preClearAutoDispatchBed(printerId),
    onSuccess: (_data, printerId) => {
      qc.invalidateQueries({ queryKey: KEYS.status(printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
      toast.success('Bed pre-cleared — ready for immediate dispatch');
    },
    onError: () => toast.error('Failed to pre-clear bed'),
  });
}
