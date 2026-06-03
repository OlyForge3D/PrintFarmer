import type { ComponentType } from 'react';
import {
  BellIcon,
  DatabaseIcon,
  GearIcon,
  LayersIcon,
  NetworkIcon,
  PackageIcon,
  ServerIcon,
  UsersIcon,
  WrenchIcon,
} from '@/common/components/icons/MdiIcons';
import { SETTINGS_CATEGORIES, getDefaultSubPage } from '@/features/settings/types';

export type SettingsCategoryIcon = ComponentType<{ className?: string; ariaLabel?: string }>;

export interface SettingsCommandItem {
  id: string;
  categoryId: string;
  subPageId?: string;
  label: string;
  description: string;
  breadcrumb: string;
  keywords: string[];
}

export const SETTINGS_CATEGORY_ICONS: Record<string, SettingsCategoryIcon> = {
  general: GearIcon,
  filament: PackageIcon,
  slicing: LayersIcon,
  hardware: WrenchIcon,
  notifications: BellIcon,
  integrations: NetworkIcon,
  system: ServerIcon,
  data: DatabaseIcon,
  users: UsersIcon,
};

export function getSettingsCategoryIcon(categoryId: string): SettingsCategoryIcon {
  return SETTINGS_CATEGORY_ICONS[categoryId] ?? GearIcon;
}

export function buildSettingsCommandItems(): SettingsCommandItem[] {
  return SETTINGS_CATEGORIES.flatMap((category) => {
    const categoryItem: SettingsCommandItem = {
      id: category.id,
      categoryId: category.id,
      label: category.label,
      description: category.description,
      breadcrumb: `Settings / ${category.label}`,
      keywords: [...category.keywords, category.label.toLowerCase()],
    };

    const subPageItems = category.subPages.map<SettingsCommandItem>((subPage) => ({
      id: `${category.id}.${subPage.id}`,
      categoryId: category.id,
      subPageId: subPage.id,
      label: subPage.label,
      description: subPage.description,
      breadcrumb: `Settings / ${category.label} / ${subPage.label}`,
      keywords: [
        ...category.keywords,
        ...subPage.keywords,
        category.label.toLowerCase(),
        subPage.label.toLowerCase(),
      ],
    }));

    return category.subPages.length > 0 ? [categoryItem, ...subPageItems] : [categoryItem];
  });
}

export function resolveSettingsNavigationTarget(categoryId: string, subPageId?: string) {
  const fallbackSubPage = getDefaultSubPage(categoryId);

  return {
    categoryId,
    subPageId: subPageId ?? fallbackSubPage ?? undefined,
  };
}
