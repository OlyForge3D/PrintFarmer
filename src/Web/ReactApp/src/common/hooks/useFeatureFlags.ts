import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/services/api';

/**
 * Feature flags for OrcaSlicer parity phased rollout.
 */
export interface FeatureFlags {
  'orca.handcraftedEditors': boolean;
  'orca.schemaEditor': boolean;
  'orca.profileComparison': boolean;
  'orca.inheritanceDiff': boolean;
  'orca.importConflictResolver': boolean;
  'orca.expandedDtos': boolean;
}

/**
 * Hook to fetch all feature flags.
 * Caches results for 5 minutes.
 */
export function useFeatureFlags() {
  return useQuery<FeatureFlags>({
    queryKey: ['feature-flags'],
    queryFn: () => apiClient.getFeatureFlags(),
    staleTime: 300_000, // 5 min cache
  });
}

/**
 * Hook to check if a specific feature flag is enabled.
 * Defaults to true if data is not yet loaded.
 * 
 * @param key - The feature flag key to check
 * @returns True if the feature is enabled, false otherwise
 */
export function useFeatureFlag(key: keyof FeatureFlags): boolean {
  const { data } = useFeatureFlags();
  return data?.[key] ?? true; // default to true
}
