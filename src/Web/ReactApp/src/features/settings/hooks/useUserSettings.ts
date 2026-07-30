import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { getAuthEpoch } from '@/common/auth/authEpoch';
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

  return useMutation<UserSettingsResponse, Error, UpdateUserSettingsRequest, { epochAtStart: number }>({
    mutationFn: async (body) => {
      const res = await apiClient.put<UserSettingsResponse>('/settings/user', body);
      return res.data;
    },
    onMutate: () => ({ epochAtStart: getAuthEpoch() }),
    onSuccess: (data, _variables, context) => {
      // If the authenticated identity changed while this save was in
      // flight (e.g. the user logged out mid-save), the response belongs
      // to a previous identity — discard it instead of writing it back
      // into the (already-cleared) shared cache key. See #762.
      if (context.epochAtStart !== getAuthEpoch()) {
        return;
      }
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
