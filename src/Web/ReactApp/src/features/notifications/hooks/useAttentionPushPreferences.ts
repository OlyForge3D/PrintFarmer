import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type {
  AttentionCategoriesResponse,
  AttentionPushPreferencesDto,
  UpdateAttentionPushPreferencesRequest,
} from '@/types/api';
import { isAttentionFeatureUnavailableError } from '@/features/notifications/attentionPushCategories';

const KEYS = {
  all: ['notifications', 'attention'] as const,
  categories: () => [...KEYS.all, 'categories'] as const,
  preferences: () => [...KEYS.all, 'preferences'] as const,
};

/**
 * Fetch the server's attention-category catalog. Returns `null` when the endpoint is
 * unavailable (older server or NativePush feature disabled). Any non-404 error is
 * surfaced normally so real problems remain visible.
 */
export function useAttentionCategories() {
  return useQuery<AttentionCategoriesResponse | null>({
    queryKey: KEYS.categories(),
    queryFn: async () => {
      try {
        return await apiClient.getAttentionCategories();
      } catch (err) {
        if (isAttentionFeatureUnavailableError(err)) return null;
        throw err;
      }
    },
    staleTime: 5 * 60 * 1000,
  });
}

export interface AttentionPushPreferencesQueryResult {
  preferences: AttentionPushPreferencesDto | null;
  featureAvailable: boolean;
}

/**
 * Fetch the caller's per-category attention push preferences. Feature-unavailable
 * responses are normalized to `{ preferences: null, featureAvailable: false }` so the
 * page can render a graceful notice instead of an error.
 */
export function useAttentionPushPreferences() {
  return useQuery<AttentionPushPreferencesQueryResult>({
    queryKey: KEYS.preferences(),
    queryFn: async () => {
      try {
        const preferences = await apiClient.getAttentionPushPreferences();
        return { preferences, featureAvailable: true };
      } catch (err) {
        if (isAttentionFeatureUnavailableError(err)) {
          return { preferences: null, featureAvailable: false };
        }
        throw err;
      }
    },
  });
}

export function useUpdateAttentionPushPreferences() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (prefs: UpdateAttentionPushPreferencesRequest) => {
      await apiClient.updateAttentionPushPreferences(prefs);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: KEYS.preferences() }),
  });
}
