import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { ApiError, NotificationPreferencesDto, UpdateNotificationPreferencesRequest } from '@/types/api';

const KEYS = {
  all: ['notifications'] as const,
  preferences: () => [...KEYS.all, 'preferences'] as const,
};

function isApiError(error: unknown): error is ApiError {
  return (
    typeof error === 'object' &&
    error !== null &&
    'statusCode' in error &&
    typeof (error as { statusCode: unknown }).statusCode === 'number'
  );
}

export function useNotificationPreferences() {
  return useQuery<NotificationPreferencesDto | null>({
    queryKey: KEYS.preferences(),
    queryFn: async () => {
      try {
        return await apiClient.getNotificationPreferences();
      } catch (error) {
        if (isApiError(error) && error.statusCode === 404) {
          return null;
        }

        throw error;
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
