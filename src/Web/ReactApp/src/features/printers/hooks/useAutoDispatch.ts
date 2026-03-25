import { useEffect } from 'react';
import { QueryClient, useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { printerSignalRService } from '@/services/printer-signalr';
import type { AutoDispatchGlobalStatus, AutoDispatchStatus, AutoDispatchReadyResult } from '@/types/api';

const KEYS = {
  all: ['auto-dispatch'] as const,
  status: (printerId: string) => [...KEYS.all, 'status', printerId] as const,
  allStatuses: ['auto-dispatch', 'all-statuses'] as const,
  globalStatus: ['auto-dispatch', 'global-status'] as const,
};

const autoDispatchQueryClients = new Set<QueryClient>();
let autoDispatchSignalRUnsubscribe: (() => void) | undefined;

function upsertStatus<T extends AutoDispatchStatus>(statuses: T[], nextStatus: AutoDispatchStatus): T[] {
  const nextStatuses = [...statuses];
  const existingIndex = nextStatuses.findIndex((status) => status.printerId === nextStatus.printerId);

  if (existingIndex >= 0) {
    nextStatuses[existingIndex] = { ...nextStatuses[existingIndex], ...nextStatus } as T;
    return nextStatuses;
  }

  nextStatuses.push(nextStatus as T);
  return nextStatuses;
}

function syncAutoDispatchCaches(queryClient: QueryClient, nextStatus: AutoDispatchStatus) {
  queryClient.setQueryData<AutoDispatchStatus[]>(KEYS.allStatuses, (existing = []) =>
    upsertStatus(existing, nextStatus),
  );
  queryClient.setQueryData(KEYS.status(nextStatus.printerId), nextStatus);
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
  autoDispatchSignalRUnsubscribe = printerSignalRService.onAutoPrintStateChanged((status) => {
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
    queryFn: async (): Promise<AutoDispatchStatus[]> => {
      const res = await apiClient.get('/auto-print/status');
      const payload = res.data;
      if (payload && typeof payload === 'object' && Array.isArray(payload.printers)) {
        return payload.printers;
      }
      return Array.isArray(payload) ? payload : [];
    },
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
    queryFn: async () => {
      const res = await apiClient.get('/auto-print/status');
      const payload = res.data;
      if (payload && typeof payload === 'object' && Array.isArray(payload.printers)) {
        return payload.printers;
      }
      return Array.isArray(payload) ? payload : [];
    },
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
    mutationFn: async (printerId: string) => {
      const res = await apiClient.post(`/auto-print/${printerId}/ready`);
      return res.data as AutoDispatchReadyResult;
    },
    onSuccess: (_data, printerId) => {
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
    mutationFn: async (printerId: string) => {
      const res = await apiClient.post(`/auto-print/${printerId}/pre-clear`);
      return res.data as AutoDispatchStatus;
    },
    onSuccess: (_data, printerId) => {
      qc.invalidateQueries({ queryKey: KEYS.status(printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
      qc.invalidateQueries({ queryKey: KEYS.globalStatus });
      toast.success('Bed pre-cleared — ready for immediate dispatch');
    },
    onError: () => toast.error('Failed to pre-clear bed'),
  });
}
