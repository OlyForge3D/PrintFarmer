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

const defaultFeatureFlags: FeatureFlags = {
  'orca.handcraftedEditors': true,
  'orca.schemaEditor': true,
  'orca.profileComparison': true,
  'orca.inheritanceDiff': true,
  'orca.importConflictResolver': true,
  'orca.expandedDtos': true,
};

export function normalizeFeatureFlags(
  flags: Record<string, boolean>,
): FeatureFlags {
  return {
    'orca.handcraftedEditors':
      flags['orca.handcraftedEditors'] ?? defaultFeatureFlags['orca.handcraftedEditors'],
    'orca.schemaEditor':
      flags['orca.schemaEditor'] ?? defaultFeatureFlags['orca.schemaEditor'],
    'orca.profileComparison':
      flags['orca.profileComparison'] ?? defaultFeatureFlags['orca.profileComparison'],
    'orca.inheritanceDiff':
      flags['orca.inheritanceDiff'] ?? defaultFeatureFlags['orca.inheritanceDiff'],
    'orca.importConflictResolver':
      flags['orca.importConflictResolver'] ??
      defaultFeatureFlags['orca.importConflictResolver'],
    'orca.expandedDtos':
      flags['orca.expandedDtos'] ?? defaultFeatureFlags['orca.expandedDtos'],
  };
}

/**
 * Hook to fetch all feature flags.
 * Caches results for 5 minutes.
 */
export function useFeatureFlags() {
  return useQuery<FeatureFlags>({
    queryKey: ['feature-flags'],
    queryFn: async () => {
      const flags = await apiClient.getFeatureFlags();
      return normalizeFeatureFlags(flags);
    },
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
