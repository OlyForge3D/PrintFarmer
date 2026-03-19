import { useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import type { AutoDispatchStatus, AutoDispatchReadyResult } from '@/types/api';

const KEYS = {
  all: ['auto-dispatch'] as const,
  status: (printerId: string) => [...KEYS.all, 'status', printerId] as const,
  allStatuses: ['auto-dispatch', 'all-statuses'] as const,
  globalStatus: ['auto-dispatch', 'global-status'] as const,
};

/** Dashboard hook — returns full global status including readyGateChecks */
export function useAutoDispatchGlobalStatus() {
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
