import { useEffect } from 'react';
import { useQuery, useMutation, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { AutoDispatchStatus, AutoDispatchReadyResult } from '@/types/api';

const KEYS = {
  all: ['auto-dispatch'] as const,
  status: (printerId: string) => [...KEYS.all, 'status', printerId] as const,
  allStatuses: ['auto-dispatch', 'all-statuses'] as const,
};

export function useAutoDispatchStatus(printerId: string): UseQueryResult<AutoDispatchStatus> {
  return useQuery({
    queryKey: KEYS.status(printerId),
    queryFn: async (): Promise<AutoDispatchStatus> => {
      const res = await apiClient.get(`/auto-print/${printerId}/status`);
      return res.data as AutoDispatchStatus;
    },
    enabled: !!printerId,
    refetchInterval: 10_000,
    staleTime: 8_000,
  });
}

export function useSetAutoDispatchEnabled() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ printerId, enabled }: { printerId: string; enabled: boolean }) => {
      const res = await apiClient.put(`/auto-print/${printerId}/enabled`, { enabled });
      return res.data as AutoDispatchStatus;
    },
    onSuccess: (_data, variables) => {
      qc.invalidateQueries({ queryKey: KEYS.status(variables.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
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
      // API returns { globalEnabled, printers: [...] } wrapper
      if (payload && typeof payload === 'object' && Array.isArray(payload.printers)) {
        return payload.printers;
      }
      // Fallback for flat array responses
      return Array.isArray(payload) ? payload : [];
    },
    refetchInterval: 10_000,
  });

  // Populate per-printer caches from bulk data to avoid redundant requests
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
      const res = await apiClient.put('/auto-print/enabled', { enabled });
      return res.data as AutoDispatchStatus[];
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
    },
  });
}

export function useSkipNextJob() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (printerId: string) => {
      const res = await apiClient.post(`/auto-print/${printerId}/skip`);
      return res.data;
    },
    onSuccess: (_data, printerId) => {
      qc.invalidateQueries({ queryKey: KEYS.status(printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
    },
  });
}

export function useCancelAutoDispatch() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (printerId: string) => {
      const res = await apiClient.post(`/auto-print/${printerId}/cancel`);
      return res.data;
    },
    onSuccess: (_data, printerId) => {
      qc.invalidateQueries({ queryKey: KEYS.status(printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
    },
  });
}
