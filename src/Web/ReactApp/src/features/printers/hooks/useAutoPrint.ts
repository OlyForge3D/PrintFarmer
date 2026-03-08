import { useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { AutoPrintStatus, AutoPrintReadyResult } from '@/types/api';

const KEYS = {
  all: ['autoprint'] as const,
  status: (printerId: string) => [...KEYS.all, 'status', printerId] as const,
  allStatuses: ['autoprint', 'all-statuses'] as const,
};

export function useAutoPrintStatus(printerId: string) {
  return useQuery<AutoPrintStatus>({
    queryKey: KEYS.status(printerId),
    queryFn: async () => {
      const res = await apiClient.get(`/autoprint/${printerId}/status`);
      return res.data;
    },
    enabled: !!printerId,
    refetchInterval: 10_000,
    staleTime: 8_000,
  });
}

export function useSetAutoPrintEnabled() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ printerId, enabled }: { printerId: string; enabled: boolean }) => {
      const res = await apiClient.put(`/autoprint/${printerId}/enabled`, { enabled });
      return res.data as AutoPrintStatus;
    },
    onSuccess: (_data, variables) => {
      qc.invalidateQueries({ queryKey: KEYS.status(variables.printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
    },
  });
}

export function useAllAutoPrintStatuses() {
  const qc = useQueryClient();
  const query = useQuery<AutoPrintStatus[]>({
    queryKey: KEYS.allStatuses,
    queryFn: async () => {
      const res = await apiClient.get('/autoprint/status');
      return res.data;
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

export function useSetAllAutoPrintEnabled() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (enabled: boolean) => {
      const res = await apiClient.put('/autoprint/enabled', { enabled });
      return res.data as AutoPrintStatus[];
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
      const res = await apiClient.post(`/autoprint/${printerId}/ready`);
      return res.data as AutoPrintReadyResult;
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
      const res = await apiClient.post(`/autoprint/${printerId}/skip`);
      return res.data;
    },
    onSuccess: (_data, printerId) => {
      qc.invalidateQueries({ queryKey: KEYS.status(printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
    },
  });
}

export function useCancelAutoPrint() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (printerId: string) => {
      const res = await apiClient.post(`/autoprint/${printerId}/cancel`);
      return res.data;
    },
    onSuccess: (_data, printerId) => {
      qc.invalidateQueries({ queryKey: KEYS.status(printerId) });
      qc.invalidateQueries({ queryKey: KEYS.allStatuses });
    },
  });
}
