import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { NotificationPreferencesDto, UpdateNotificationPreferencesRequest } from '@/types/api';

const KEYS = {
  all: ['notifications'] as const,
  preferences: () => [...KEYS.all, 'preferences'] as const,
};

export function useNotificationPreferences() {
  return useQuery<NotificationPreferencesDto | null>({
    queryKey: KEYS.preferences(),
    queryFn: async () => {
      try {
        return await apiClient.getNotificationPreferences();
      } catch {
        // 404 means no preferences set yet — return defaults
        return null;
      }
    },
  });
}

export function useUpdateNotificationPreferences() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (prefs: UpdateNotificationPreferencesRequest) => {
      return await apiClient.updateNotificationPreferences(prefs);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: KEYS.preferences() }),
  });
}
