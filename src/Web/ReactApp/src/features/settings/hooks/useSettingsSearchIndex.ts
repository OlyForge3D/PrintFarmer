/**
 * Shared permission-filtered settings search index (#2505).
 *
 * Both the modal {@link GlobalCommandPaletteProvider} (#938) and the
 * persistent workspace search panel in `SettingsShell` (#2505) need the same
 * cross-group list of navigable results: admin destinations, user-scope
 * settings-nav sections, and individual metadata-driven field results. This
 * hook is the single place that assembles and permission-filters that list —
 * defining it twice would risk the two surfaces silently drifting apart (a
 * delegated user seeing a field in one but not the other).
 *
 * Deliberately does NOT include palette-only `action` items (sign out, theme
 * switch, refresh overview) — those are curated commands, not navigation
 * results, and stay local to `GlobalCommandPaletteProvider`.
 */
import { useMemo } from 'react';
import {
  buildAdminDestinationCommandItems,
  buildSettingCommandItems,
  buildSettingsCommandItems,
  type SettingsCommandItem,
} from '@/features/settings/settings-navigation';
import {
  ADMIN_DESTINATIONS,
  canAccessSettingsTab,
  filterDestinationsByAccess,
} from '@/features/admin/registry/adminDestinations';
import { useAuth } from '@/features/auth/hooks/useAuth';
import {
  useSettingsGroups,
  useSettingsMetadata,
} from '@/features/settings/queries/useSettingsMetadata';

export interface UseSettingsSearchIndexOptions {
  /**
   * Gate query execution and item assembly. Callers should pass `false` when
   * the index isn't actually being displayed (e.g. the palette is closed, or
   * the workspace search box is empty) so we don't fire a background
   * metadata fetch for every authenticated page.
   */
  enabled: boolean;
}

export interface SettingsSearchIndex {
  /** Admin destinations the current user can reach. */
  destinationItems: SettingsCommandItem[];
  /** User-scope settings-nav sections (categories/sub-pages). */
  settingsNavItems: SettingsCommandItem[];
  /** Individual metadata-driven field results, permission-filtered. */
  settingFieldItems: SettingsCommandItem[];
  /** The three lists above, concatenated — the full navigable index. */
  items: SettingsCommandItem[];
  isLoading: boolean;
  isError: boolean;
  /** Re-run both underlying queries — surfaced so a failed fetch can offer a retry action. */
  refetch: () => void;
}

/**
 * Build the shared, permission-filtered settings search index.
 *
 * Gating mirrors the pre-#2505 palette behavior exactly: the settings
 * metadata/groups queries are disabled (and therefore return no data) unless
 * both a user is signed in AND `options.enabled` is true, so a signed-out
 * visitor never fires a request that would 401, and an idle/closed search
 * surface never fetches in the background. When `user` becomes falsy (e.g.
 * on logout) every derived list collapses to `[]` immediately — no stale
 * unauthorized names can flash on screen.
 */
export function useSettingsSearchIndex(options: UseSettingsSearchIndexOptions): SettingsSearchIndex {
  const { user, hasRole, hasPermission } = useAuth();
  const queryEnabled = Boolean(user) && options.enabled;

  const metadataQuery = useSettingsMetadata({ enabled: queryEnabled });
  const groupsQuery = useSettingsGroups({ enabled: queryEnabled });

  const destinationAccess = useMemo(() => ({ hasRole, hasPermission }), [hasRole, hasPermission]);

  const accessibleDestinations = useMemo(() => {
    if (!user) {
      return [];
    }
    return filterDestinationsByAccess(ADMIN_DESTINATIONS, destinationAccess);
  }, [user, destinationAccess]);

  const destinationItems = useMemo(
    () => buildAdminDestinationCommandItems(accessibleDestinations),
    [accessibleDestinations],
  );

  // The admin-destination registry is the source of truth for admin routes
  // (see settings-navigation.ts docs) — only user-scope profile items are
  // taken from the legacy settings-nav builder, to avoid emitting duplicate
  // rows that point at the same URL.
  const settingsNavItems = useMemo(
    () => buildSettingsCommandItems().filter((item) => item.scopeId === 'user'),
    [],
  );

  // The per-field index spans every settings resource at once — no single
  // `{resource}:action}` permission can represent "any settings field", so
  // it stays gated on the `farm_admin`-adjacent `system_settings:admin`
  // permission literally (#1457), same as the palette.
  const settingFieldItems = useMemo(() => {
    if (!user || !hasPermission('system_settings', 'admin')) {
      return [] as SettingsCommandItem[];
    }
    return buildSettingCommandItems(metadataQuery.data, groupsQuery.data)
      .filter((item) => canAccessSettingsTab(item.categoryId, item.subPageId, destinationAccess));
  }, [user, hasPermission, metadataQuery.data, groupsQuery.data, destinationAccess]);

  const items = useMemo<SettingsCommandItem[]>(
    () => [...destinationItems, ...settingsNavItems, ...settingFieldItems],
    [destinationItems, settingsNavItems, settingFieldItems],
  );

  return {
    destinationItems,
    settingsNavItems,
    settingFieldItems,
    items,
    isLoading: queryEnabled && (metadataQuery.isLoading || groupsQuery.isLoading),
    isError: queryEnabled && (metadataQuery.isError || groupsQuery.isError),
    refetch: () => {
      void metadataQuery.refetch();
      void groupsQuery.refetch();
    },
  };
}
