import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import type { SettingMetadata } from '@/common/components/SettingsPagelet';
import {
  fetchSettingsGroups,
  fetchSettingsMetadata,
  type SettingGroupMetadata,
} from '@/services/settingsApi';

/**
 * React-Query key for the shared settings-metadata cache. Exported so the
 * command palette (#938) and any future settings surface can invalidate the
 * same entry after schema-affecting operations.
 */
export const SETTINGS_METADATA_QUERY_KEY = ['settings', 'metadata'] as const;

/**
 * React-Query key for the settings-group index used to order and label the
 * sidebar in `SettingsPage`. Same rationale as {@link SETTINGS_METADATA_QUERY_KEY}.
 */
export const SETTINGS_GROUPS_QUERY_KEY = ['settings', 'groups'] as const;

/**
 * Fetch the full settings-metadata list. Shared between the command palette's
 * setting-item builder and any future consumer.
 *
 * `staleTime` is deliberately long — the metadata reflects the compiled schema,
 * not user data, and only changes when a settings class is added, removed, or
 * has its attributes edited (i.e. after a deploy). Keeping it fresh for five
 * minutes avoids re-fetching every time the palette is opened while still
 * catching new deploys during a long session.
 */
export function useSettingsMetadata(
  options?: { enabled?: boolean },
): UseQueryResult<SettingMetadata[]> {
  return useQuery<SettingMetadata[]>({
    queryKey: SETTINGS_METADATA_QUERY_KEY,
    queryFn: () => fetchSettingsMetadata(),
    staleTime: 5 * 60_000,
    refetchOnWindowFocus: false,
    enabled: options?.enabled ?? true,
  });
}

/**
 * Fetch the settings-group index (display name + sort order per group).
 * Cached alongside {@link useSettingsMetadata}.
 */
export function useSettingsGroups(
  options?: { enabled?: boolean },
): UseQueryResult<SettingGroupMetadata[]> {
  return useQuery<SettingGroupMetadata[]>({
    queryKey: SETTINGS_GROUPS_QUERY_KEY,
    queryFn: () => fetchSettingsGroups(),
    staleTime: 5 * 60_000,
    refetchOnWindowFocus: false,
    enabled: options?.enabled ?? true,
  });
}
