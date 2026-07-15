import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type {
  ApiError,
  NotificationCapabilitiesResponse,
  NotificationPreferencesDto,
  UpdateNotificationPreferencesRequest,
} from '@/types/api';

/**
 * React-Query keys for the notification-preferences feature.
 *
 * The whole ['notifications'] prefix is treated as user-owned sensitive data
 * and is purged by common/auth/sensitiveQueryCache.ts on every identity
 * transition (#762/#765). We therefore do not need to fold auth identity
 * into these keys — the cache is cleared before the new identity can read
 * anything, including a previously cached capability probe.
 */
const KEYS = {
  all: ['notifications'] as const,
  preferences: () => [...KEYS.all, 'preferences'] as const,
  capabilities: () => [...KEYS.all, 'capabilities'] as const,
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

/**
 * Capability probe (#708). `null` result = legacy server (endpoint 404).
 * Isolated from `useNotificationPreferences` so the preferences query is not
 * blocked on the probe and the adapter can render immediately.
 *
 * `staleTime` is intentionally short (30s) rather than the 5-minute window
 * used elsewhere: capabilities is a safety gate for wire compatibility, so
 * detecting a server upgrade/downgrade quickly is more important than
 * reducing probe traffic. Mutations invalidate the key on success, and the
 * cache is naturally purged on auth transitions by
 * common/auth/sensitiveQueryCache.ts (['notifications'] prefix).
 */
export function useNotificationCapabilities() {
  return useQuery<NotificationCapabilitiesResponse | null>({
    queryKey: KEYS.capabilities(),
    queryFn: () => apiClient.getNotificationCapabilities(),
    staleTime: 30 * 1000,
    // Any non-404 network/server failure must surface, not be silently
    // treated as "legacy". The adapter only interprets `null` (explicit 404)
    // as legacy; a thrown error keeps the query in error state and the page
    // blocks save until the probe succeeds.
    retry: 1,
  });
}

export function useUpdateNotificationPreferences() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (prefs: UpdateNotificationPreferencesRequest) => {
      return await apiClient.updateNotificationPreferences(prefs);
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: KEYS.preferences() });
      // Reprobe capabilities so any server-side contract change (rare, but
      // possible after an upgrade during a long session) is picked up
      // before the next save.
      qc.invalidateQueries({ queryKey: KEYS.all });
    },
  });
}
