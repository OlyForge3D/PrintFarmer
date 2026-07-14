import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type {
  NotificationCapabilitiesResponse,
  NotificationPreferencesDto,
  UpdateNotificationPreferencesRequest,
} from '@/types/api';

/**
 * React-Query keys for the notification-preferences feature.
 *
 * The capabilities key incorporates a coarse-grained server/auth identity so
 * a stale cached probe from a previous session/server cannot be reused on a
 * downgraded server (which would let the client PUT operator tokens the new
 * server rejects with 400). We deliberately hash only the presence of an
 * auth token, not its value, to avoid pinning the token into cache keys.
 */
function getAuthCacheIdentity(): string {
  try {
    return localStorage.getItem('auth-token') ? 'authed' : 'anon';
  } catch {
    return 'anon';
  }
}

const KEYS = {
  all: ['notifications'] as const,
  preferences: () => [...KEYS.all, 'preferences'] as const,
  capabilities: () => [...KEYS.all, 'capabilities', getAuthCacheIdentity()] as const,
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
 *
 * `staleTime` is intentionally short (30s) rather than the 5-minute window
 * used elsewhere: capabilities is a safety gate for wire compatibility, so
 * detecting a server upgrade/downgrade quickly is more important than
 * reducing probe traffic. Mutations invalidate the key on success, and the
 * cache is naturally re-keyed on auth transitions via `getAuthCacheIdentity`.
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
