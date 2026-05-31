import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import type { UserSettingsResponse, UpdateUserSettingsRequest } from '@/features/settings/types';

const USER_SETTINGS_KEY = ['settings', 'user'] as const;

export function useUserSettings() {
  return useQuery<UserSettingsResponse>({
    queryKey: USER_SETTINGS_KEY,
    queryFn: async () => {
      const res = await apiClient.get<UserSettingsResponse>('/settings/user');
      return res.data;
    },
    staleTime: 60_000,
  });
}

export function useUpdateUserSettings() {
  const queryClient = useQueryClient();

  return useMutation<UserSettingsResponse, Error, UpdateUserSettingsRequest>({
    mutationFn: async (body) => {
      const res = await apiClient.put<UserSettingsResponse>('/settings/user', body);
      return res.data;
    },
    onSuccess: (data) => {
      queryClient.setQueryData(USER_SETTINGS_KEY, data);
    },
    onError: (error) => {
      const statusCode = (error as { statusCode?: number }).statusCode;
      if (statusCode === 409) {
        toast.error('Settings were updated elsewhere — please refresh');
        queryClient.invalidateQueries({ queryKey: USER_SETTINGS_KEY });
      }
    },
  });
}
