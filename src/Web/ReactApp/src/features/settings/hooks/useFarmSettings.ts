import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import type { FarmSettingsResponse, UpdateFarmSettingsRequest } from '@/features/settings/types';

const FARM_SETTINGS_KEY = ['settings', 'farm'] as const;

export function useFarmSettings() {
  return useQuery<FarmSettingsResponse>({
    queryKey: FARM_SETTINGS_KEY,
    queryFn: async () => {
      const res = await apiClient.get<FarmSettingsResponse>('/settings/farm');
      return res.data;
    },
    staleTime: 60_000,
  });
}

export function useUpdateFarmSettings() {
  const queryClient = useQueryClient();

  return useMutation<FarmSettingsResponse, Error, UpdateFarmSettingsRequest>({
    mutationFn: async (body) => {
      const res = await apiClient.put<FarmSettingsResponse>('/settings/farm', body);
      return res.data;
    },
    onSuccess: (data) => {
      queryClient.setQueryData(FARM_SETTINGS_KEY, data);
    },
    onError: (error) => {
      const statusCode = (error as { statusCode?: number }).statusCode;
      if (statusCode === 409) {
        toast.error('Settings were updated elsewhere — please refresh');
        queryClient.invalidateQueries({ queryKey: FARM_SETTINGS_KEY });
      }
    },
  });
}
