import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { PowerMonitor, PowerMonitorUpsert, PowerMonitorTestResult } from '@/features/power-monitors/types';

export const powerMonitorKeys = {
  all: ['power-monitors'] as const,
  detail: (id: string) => ['power-monitors', id] as const,
};

export function usePowerMonitors() {
  return useQuery<PowerMonitor[]>({
    queryKey: powerMonitorKeys.all,
    queryFn: async () => {
      const res = await apiClient.get<PowerMonitor[]>('/admin/power-monitors');
      return res.data;
    },
  });
}

export function useCreatePowerMonitor() {
  const queryClient = useQueryClient();
  return useMutation<PowerMonitor, Error, PowerMonitorUpsert>({
    mutationFn: async (dto) => {
      const res = await apiClient.post<PowerMonitor>('/admin/power-monitors', dto);
      return res.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: powerMonitorKeys.all });
    },
  });
}

export function useUpdatePowerMonitor() {
  const queryClient = useQueryClient();
  return useMutation<PowerMonitor, Error, { id: string; dto: PowerMonitorUpsert }>({
    mutationFn: async ({ id, dto }) => {
      const res = await apiClient.put<PowerMonitor>(`/admin/power-monitors/${id}`, dto);
      return res.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: powerMonitorKeys.all });
    },
  });
}

export function useDeletePowerMonitor() {
  const queryClient = useQueryClient();
  return useMutation<void, Error, string>({
    mutationFn: async (id) => {
      await apiClient.delete(`/admin/power-monitors/${id}`);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: powerMonitorKeys.all });
    },
  });
}

export function useTestPowerMonitorConnection() {
  return useMutation<PowerMonitorTestResult, Error, { provider: string; deviceAddress: string }>({
    mutationFn: async (dto) => {
      const res = await apiClient.post<PowerMonitorTestResult>('/admin/power-monitors/test', dto);
      return res.data;
    },
  });
}
