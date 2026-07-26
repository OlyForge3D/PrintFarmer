import type { ComponentType } from 'react';
import {
  AccountIcon,
  DatabaseIcon,
  GearIcon,
  KeyIcon,
  LayersIcon,
  NetworkIcon,
  ServerIcon,
  ShieldIcon,
  UsersIcon,
  WrenchIcon,
} from '@/common/components/icons/MdiIcons';
import type { SettingMetadata } from '@/common/components/SettingsPagelet';
import type {
  AdminDestination,
  AdminDestinationGroup,
} from '@/features/admin/registry/adminDestinations';
import { ADMIN_DESTINATION_GROUPS } from '@/features/admin/registry/adminDestinations';
import type { SettingGroupMetadata } from '@/services/settingsApi';
import {
  DEFAULT_SCOPE,
  SETTINGS_CATEGORIES,
  getDefaultCategoryForScope,
  getDefaultSubPage,
  getSettingsCategory,
  getSettingsScope,
  isSettingsScope,
  type SettingsScopeId,
} from '@/features/settings/types';

export type SettingsCategoryIcon = ComponentType<{ className?: string; ariaLabel?: string }>;

/**
 * Kind discriminator for palette items. Section headers, sort priority, and
 * execution behaviour all key off this — a `settings-nav` item navigates to a
 * settings sub-page, a `setting` navigates AND focuses a specific field, an
 * `action` invokes a callback (with optional confirmation), and a `destination`
 * points at any admin route.
 */
export type SettingsCommandItemKind = 'destination' | 'settings-nav' | 'setting' | 'action';

/**
 * Palette item shape. Backwards-compatible with the pre-#938 shape: legacy
 * consumers still emit `settings-nav` items with `scopeId` / `categoryId` /
 * `subPageId`, and the existing `CommandPalette.test.tsx` continues to pass
 * because the new fields are optional.
 */
export interface SettingsCommandItem {
  id: string;
  /** Result grouping. Defaults to `'settings-nav'` for legacy callers. */
  kind?: SettingsCommandItemKind;
  scopeId: SettingsScopeId;
  categoryId: string;
  subPageId?: string;
  label: string;
  description: string;
  breadcrumb: string;
  keywords: string[];
  /** Icon override. Falls back to the category icon when omitted. */
  icon?: SettingsCategoryIcon;
  /** Direct navigation target for `destination` and `setting` items. */
  href?: string;
  /**
   * Handler invoked when an `action` item is selected. The provider passes
   * `close` so long-running actions (e.g. sign-out) can dismiss the palette
   * before their promise resolves.
   */
  onExecute?: (helpers: { close: () => void }) => void | Promise<void>;
  /**
   * Confirmation prompt used by destructive `action` items. When present the
   * provider prompts via the in-app `ConfirmationModal` before invoking
   * `onExecute`.
   */
  confirmMessage?: string;
}

export interface ResolvedSettingsNavigationTarget {
  scopeId: SettingsScopeId;
  categoryId: string;
  subPageId?: string;
}

const LEGACY_CATEGORY_ALIASES: Record<string, ResolvedSettingsNavigationTarget> = {
  notifications: { scopeId: 'user', categoryId: 'profile', subPageId: 'notifications' },
  system: { scopeId: 'admin', categoryId: 'operations', subPageId: 'status' },
};

const LEGACY_CATEGORY_WITH_SUBPAGE_ALIASES: Record<string, Partial<Record<string, ResolvedSettingsNavigationTarget>>> = {
  system: {
    status: { scopeId: 'admin', categoryId: 'operations', subPageId: 'status' },
    workers: { scopeId: 'admin', categoryId: 'operations', subPageId: 'workers' },
    monitoring: { scopeId: 'admin', categoryId: 'operations', subPageId: 'status' },
    'file-health': { scopeId: 'admin', categoryId: 'operations', subPageId: 'status' },
  },
  users: {
    'api-keys': { scopeId: 'user', categoryId: 'profile', subPageId: 'api-keys' },
    accounts: { scopeId: 'admin', categoryId: 'users', subPageId: 'accounts' },
    audit: { scopeId: 'admin', categoryId: 'users', subPageId: 'audit' },
  },
  data: {
    quotas: { scopeId: 'system', categoryId: 'quotas' },
    tags: { scopeId: 'admin', categoryId: 'data', subPageId: 'tags' },
    management: { scopeId: 'admin', categoryId: 'data', subPageId: 'management' },
  },
  profile: {
    notifications: { scopeId: 'user', categoryId: 'profile', subPageId: 'notifications' },
    'api-keys': { scopeId: 'user', categoryId: 'profile', subPageId: 'api-keys' },
    passkeys: { scopeId: 'user', categoryId: 'profile', subPageId: 'passkeys' },
    preferences: { scopeId: 'user', categoryId: 'profile', subPageId: 'preferences' },
  },
  operations: {
    status: { scopeId: 'admin', categoryId: 'operations', subPageId: 'status' },
    workers: { scopeId: 'admin', categoryId: 'operations', subPageId: 'workers' },
  },
};

export const SETTINGS_CATEGORY_ICONS: Record<string, SettingsCategoryIcon> = {
  profile: AccountIcon,
  general: GearIcon,
  slicing: LayersIcon,
  hardware: WrenchIcon,
  integrations: NetworkIcon,
  quotas: DatabaseIcon,
  operations: ServerIcon,
  users: UsersIcon,
  data: DatabaseIcon,
};

export const SETTINGS_SUBPAGE_ICONS: Record<string, SettingsCategoryIcon> = {
  preferences: GearIcon,
  'api-keys': KeyIcon,
  notifications: NetworkIcon,
  passkeys: ShieldIcon,
};

function createScopeBreadcrumb(scopeId: SettingsScopeId): string {
  if (scopeId === 'admin') {
    return 'Admin';
  }

  return `Settings / ${getSettingsScope(scopeId)?.label ?? 'Settings'}`;
}

export function getSettingsCategoryIcon(categoryId: string): SettingsCategoryIcon {
  return SETTINGS_CATEGORY_ICONS[categoryId] ?? GearIcon;
}

export function resolveSettingsNavigationTarget(
  categoryId?: string | null,
  subPageId?: string | null,
  scopeId?: string | null,
): ResolvedSettingsNavigationTarget {
  const scopedFallback = isSettingsScope(scopeId) ? scopeId : DEFAULT_SCOPE;

  if (categoryId && subPageId) {
    const legacyTarget = LEGACY_CATEGORY_WITH_SUBPAGE_ALIASES[categoryId]?.[subPageId];
    if (legacyTarget) {
      return legacyTarget;
    }
  }

  if (categoryId) {
    const legacyCategoryTarget = LEGACY_CATEGORY_ALIASES[categoryId];
    if (legacyCategoryTarget) {
      return legacyCategoryTarget;
    }

    const directCategory = getSettingsCategory(categoryId);
    if (directCategory) {
      const resolvedSubPageId = subPageId && directCategory.subPages.some((subPage) => subPage.id === subPageId)
        ? subPageId
        : getDefaultSubPage(directCategory.id) || undefined;

      return {
        scopeId: directCategory.scopeId,
        categoryId: directCategory.id,
        subPageId: resolvedSubPageId,
      };
    }
  }

  const fallbackCategoryId = getDefaultCategoryForScope(scopedFallback);
  const fallbackSubPageId = getDefaultSubPage(fallbackCategoryId) || undefined;

  return {
    scopeId: scopedFallback,
    categoryId: fallbackCategoryId,
    subPageId: fallbackSubPageId,
  };
}

export function buildSettingsCommandItems(): SettingsCommandItem[] {
  return SETTINGS_CATEGORIES.flatMap((category) => {
    const scopeBreadcrumb = createScopeBreadcrumb(category.scopeId);
    const categoryItem: SettingsCommandItem = {
      id: `${category.scopeId}.${category.id}`,
      kind: 'settings-nav',
      scopeId: category.scopeId,
      categoryId: category.id,
      label: category.label,
      description: category.description,
      breadcrumb: `${scopeBreadcrumb} / ${category.label}`,
      keywords: [
        ...category.keywords,
        category.label.toLowerCase(),
        category.scopeId,
        getSettingsScope(category.scopeId)?.label.toLowerCase() ?? category.scopeId,
      ],
    };

    const subPageItems = category.subPages.map<SettingsCommandItem>((subPage) => ({
      id: `${category.scopeId}.${category.id}.${subPage.id}`,
      kind: 'settings-nav',
      scopeId: category.scopeId,
      categoryId: category.id,
      subPageId: subPage.id,
      label: subPage.label,
      description: subPage.description,
      breadcrumb: `${scopeBreadcrumb} / ${category.label} / ${subPage.label}`,
      keywords: [
        ...category.keywords,
        ...subPage.keywords,
        category.label.toLowerCase(),
        subPage.label.toLowerCase(),
        category.scopeId,
      ],
    }));

    return category.subPages.length > 0 ? [categoryItem, ...subPageItems] : [categoryItem];
  });
}

/**
 * Which settings sub-page renders the properties in a given metadata `group`.
 * Kept in sync by hand with `SUB_PAGE_CONTENT` in `SettingsShell.tsx` — every
 * group referenced from an `allowedGroups` prop there must map back here, or
 * palette navigation for that group would drop the user on the wrong sub-page.
 *
 * Groups that live inside a mixed sub-page (e.g. `Networking` under
 * `general.system`) all point at the same destination; the shell renders them
 * side-by-side inside a single page.
 */
export const SETTINGS_GROUP_TO_LOCATION: Record<
  string,
  { scopeId: SettingsScopeId; categoryId: string; subPageId: string }
> = {
  General: { scopeId: 'system', categoryId: 'general', subPageId: 'farm' },
  System: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Networking: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Catalog: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Files: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Printers: { scopeId: 'system', categoryId: 'general', subPageId: 'system' },
  Operations: { scopeId: 'system', categoryId: 'general', subPageId: 'automation' },
  Monitoring: { scopeId: 'system', categoryId: 'general', subPageId: 'automation' },
  Maintenance: { scopeId: 'system', categoryId: 'general', subPageId: 'automation' },
  // `HistorySeedingBackgroundService` declares Group = "Job Queue". Without an
  // entry here the palette silently skips it (`if (!location) continue`), and
  // without a matching `allowedGroups` entry in the automation sub-page it
  // renders nowhere — leaving the section unreachable by any route.
  'Job Queue': { scopeId: 'system', categoryId: 'general', subPageId: 'automation' },
  Integrations: { scopeId: 'system', categoryId: 'integrations', subPageId: 'connections' },
  Slicing: { scopeId: 'system', categoryId: 'slicing', subPageId: 'defaults' },
};

/**
 * URL path for a resolved settings-navigation target. Encodes the scope split
 * introduced in #932: `admin` scope lives at `/admin/manage`, `system` scope
 * lives at `/admin/settings`, everything else at `/settings`.
 */
export function buildSettingsPath(
  target: { scopeId: SettingsScopeId; categoryId: string; subPageId?: string; field?: string },
): string {
  const basePath = target.scopeId === 'admin'
    ? '/admin/manage'
    : target.scopeId === 'system'
      ? '/admin/settings'
      : '/settings';
  const params = new URLSearchParams();
  params.set('scope', target.scopeId);
  params.set('tab', target.categoryId);
  if (target.subPageId) {
    params.set('sub', target.subPageId);
  }
  if (target.field) {
    params.set('field', target.field);
  }
  return `${basePath}?${params.toString()}`;
}

const ADMIN_GROUP_LABEL_BY_ID = new Map<AdminDestinationGroup, string>(
  ADMIN_DESTINATION_GROUPS.map((group) => [group.id, group.label] as const),
);

/**
 * Build palette items from the admin destination registry (#934). These become
 * the "Places" section of the palette and are the single source of truth for
 * admin-surface navigation — the pre-existing `buildSettingsCommandItems`
 * output is intentionally scoped to `user` in the provider to avoid emitting
 * both a `Places` and a `Settings section` row that point at the same URL.
 */
export function buildAdminDestinationCommandItems(
  destinations: readonly AdminDestination[],
): SettingsCommandItem[] {
  return destinations.map<SettingsCommandItem>((destination) => {
    const groupLabel = ADMIN_GROUP_LABEL_BY_ID.get(destination.group) ?? destination.group;
    return {
      id: `dest.${destination.id}`,
      kind: 'destination',
      // `scopeId` is only meaningful for settings-nav items; pick a safe default
      // so downstream sorters that read the field still work.
      scopeId: 'admin',
      categoryId: destination.group,
      label: destination.label,
      description: destination.description,
      breadcrumb: `Admin / ${groupLabel}`,
      keywords: [
        ...(destination.keywords ?? []),
        destination.label.toLowerCase(),
        groupLabel.toLowerCase(),
        'admin',
      ],
      icon: destination.icon,
      href: destination.path,
    };
  });
}

/**
 * Build palette items for individual settings — one per property, keyed on
 * `sectionKey.propertyName`. Requires the section metadata list and the
 * ordered group list so labels and breadcrumbs stay in sync with the sidebar.
 *
 * Only properties whose owning section has a `group` mapped in
 * {@link SETTINGS_GROUP_TO_LOCATION} become palette items. Anything else is
 * silently skipped — those properties either render inside a page the palette
 * cannot deep-link to yet, or are model-generated leftovers we do not want to
 * surface as a "jump to" target.
 */
export function buildSettingCommandItems(
  metadata: readonly SettingMetadata[] | undefined,
  groups: readonly SettingGroupMetadata[] | undefined,
): SettingsCommandItem[] {
  if (!metadata || metadata.length === 0) {
    return [];
  }

  const groupDisplayNameByKey = new Map<string, string>(
    (groups ?? []).map((group) => [group.key, group.displayName || group.key] as const),
  );

  const items: SettingsCommandItem[] = [];

  for (const section of metadata) {
    const groupKey = section.group || 'Other';
    const location = SETTINGS_GROUP_TO_LOCATION[groupKey];
    if (!location) {
      continue;
    }

    const sectionLabel = section.displayName || section.className;
    const groupLabel = groupDisplayNameByKey.get(groupKey) ?? groupKey;

    for (const property of section.properties) {
      const displayName = property.display?.name || property.name;
      const description = property.display?.description
        || section.description
        || `Setting inside ${sectionLabel}.`;

      items.push({
        id: `setting.${section.key}.${property.name}`,
        kind: 'setting',
        scopeId: location.scopeId,
        categoryId: location.categoryId,
        subPageId: location.subPageId,
        label: displayName,
        description,
        breadcrumb: `Admin / ${groupLabel} / ${sectionLabel}`,
        keywords: [
          displayName.toLowerCase(),
          property.name.toLowerCase(),
          sectionLabel.toLowerCase(),
          groupLabel.toLowerCase(),
          'setting',
        ],
        href: buildSettingsPath({ ...location, field: `${section.key}.${property.name}` }),
      });
    }
  }

  return items;
}

