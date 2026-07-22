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

export interface SettingsCommandItem {
  id: string;
  scopeId: SettingsScopeId;
  categoryId: string;
  subPageId?: string;
  label: string;
  description: string;
  breadcrumb: string;
  keywords: string[];
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

