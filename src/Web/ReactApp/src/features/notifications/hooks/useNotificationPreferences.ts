import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type {
  NotificationCapabilitiesResponse,
  NotificationPreferencesDto,
  UpdateNotificationPreferencesRequest,
} from '@/types/api';

const KEYS = {
  all: ['notifications'] as const,
  preferences: () => [...KEYS.all, 'preferences'] as const,
  capabilities: () => [...KEYS.all, 'capabilities'] as const,
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

/**
 * Capability probe (#708). `null` result = legacy server (endpoint 404).
 * Isolated from `useNotificationPreferences` so the preferences query is not
 * blocked on the probe and the adapter can render immediately.
 */
export function useNotificationCapabilities() {
  return useQuery<NotificationCapabilitiesResponse | null>({
    queryKey: KEYS.capabilities(),
    queryFn: () => apiClient.getNotificationCapabilities(),
    staleTime: 5 * 60 * 1000,
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
