export const NAV_PREFERENCES_VERSION = 1;
export const NAV_PREFERENCES_STORAGE_KEY = 'pf_nav_preferences_v1';

export type NavPreferenceRole = 'admin' | 'operator' | 'guest';

export interface NavPreferenceItem {
  id: string;
  name: string;
  sectionName: string;
}

export interface NavPreferences {
  version: typeof NAV_PREFERENCES_VERSION;
  orderedItemIds: string[];
  hiddenItemIds: string[];
  pinnedItemIds: string[];
}

export interface ResolvedNavPreferences {
  preferences: NavPreferences;
  orderedItems: NavPreferenceItem[];
  visibleItems: NavPreferenceItem[];
  hiddenItems: NavPreferenceItem[];
  favoriteItems: NavPreferenceItem[];
  regularItems: NavPreferenceItem[];
}

const DEFAULT_ORDER_BY_ROLE: Record<NavPreferenceRole, string[]> = {
  guest: ['overview'],
  operator: [
    'overview',
    'print-queue',
    'printers',
    'scheduling',
    'files',
    'projects',
    'slice-job',
    'filament-inventory',
  ],
  admin: [
    'overview',
    'printers',
    'print-queue',
    'auto-dispatch',
    'scheduling',
    'files',
    'projects',
    'slice-job',
    'filament-inventory',
    'locations',
    'analytics',
    'maintenance',
    'catalog',
    'system-settings',
    'admin-console',
  ],
};

const uniqueKnownIds = (ids: readonly string[], knownIds: ReadonlySet<string>) => {
  const seen = new Set<string>();
  return ids.filter((id) => {
    if (!knownIds.has(id) || seen.has(id)) {
      return false;
    }

    seen.add(id);
    return true;
  });
};

export function getNavPreferencesStorageKey(userId?: string | null) {
  return `${NAV_PREFERENCES_STORAGE_KEY}:${userId ?? 'anonymous'}`;
}

export function createDefaultNavPreferences(items: readonly NavPreferenceItem[], role: NavPreferenceRole): NavPreferences {
  const itemIds = items.map((item) => item.id);
  const knownIds = new Set(itemIds);
  const roleOrder = uniqueKnownIds(DEFAULT_ORDER_BY_ROLE[role], knownIds);

  return {
    version: NAV_PREFERENCES_VERSION,
    orderedItemIds: [...roleOrder, ...itemIds.filter((id) => !roleOrder.includes(id))],
    hiddenItemIds: [],
    pinnedItemIds: [],
  };
}

export function normalizeNavPreferences(
  items: readonly NavPreferenceItem[],
  role: NavPreferenceRole,
  preferences?: Partial<NavPreferences> | null
): NavPreferences {
  const defaults = createDefaultNavPreferences(items, role);
  const knownIds = new Set(items.map((item) => item.id));
  const orderedItemIds = uniqueKnownIds(preferences?.orderedItemIds ?? defaults.orderedItemIds, knownIds);

  return {
    version: NAV_PREFERENCES_VERSION,
    orderedItemIds: [
      ...orderedItemIds,
      ...defaults.orderedItemIds.filter((id) => !orderedItemIds.includes(id)),
    ],
    hiddenItemIds: uniqueKnownIds(preferences?.hiddenItemIds ?? [], knownIds),
    pinnedItemIds: uniqueKnownIds(preferences?.pinnedItemIds ?? [], knownIds),
  };
}

export function resolveNavPreferences(
  items: readonly NavPreferenceItem[],
  role: NavPreferenceRole,
  preferences?: Partial<NavPreferences> | null
): ResolvedNavPreferences {
  const normalized = normalizeNavPreferences(items, role, preferences);
  const itemById = new Map(items.map((item) => [item.id, item]));
  const hiddenIds = new Set(normalized.hiddenItemIds);
  const pinnedIds = new Set(normalized.pinnedItemIds);
  const orderedItems = normalized.orderedItemIds
    .map((id) => itemById.get(id))
    .filter((item): item is NavPreferenceItem => Boolean(item));
  const visibleItems = orderedItems.filter((item) => !hiddenIds.has(item.id));

  return {
    preferences: normalized,
    orderedItems,
    visibleItems,
    hiddenItems: orderedItems.filter((item) => hiddenIds.has(item.id)),
    favoriteItems: visibleItems.filter((item) => pinnedIds.has(item.id)),
    regularItems: visibleItems.filter((item) => !pinnedIds.has(item.id)),
  };
}

export function loadNavPreferences(storageKey: string, storage: Storage = localStorage): Partial<NavPreferences> | null {
  try {
    const value = storage.getItem(storageKey);
    if (!value) {
      return null;
    }

    const parsed = JSON.parse(value) as Partial<NavPreferences>;
    if (!Array.isArray(parsed.orderedItemIds) || !Array.isArray(parsed.hiddenItemIds) || !Array.isArray(parsed.pinnedItemIds)) {
      return null;
    }

    return parsed;
  } catch {
    return null;
  }
}

export function saveNavPreferences(storageKey: string, preferences: NavPreferences, storage: Storage = localStorage) {
  storage.setItem(storageKey, JSON.stringify(preferences));
}

export function moveNavItem(preferences: NavPreferences, itemId: string, targetIndex: number): NavPreferences {
  const currentIndex = preferences.orderedItemIds.indexOf(itemId);
  if (currentIndex < 0) {
    return preferences;
  }

  const nextOrder = preferences.orderedItemIds.filter((id) => id !== itemId);
  const safeTargetIndex = Math.max(0, Math.min(targetIndex, nextOrder.length));
  nextOrder.splice(safeTargetIndex, 0, itemId);

  return {
    ...preferences,
    orderedItemIds: nextOrder,
  };
}

export function setNavItemHidden(preferences: NavPreferences, itemId: string, hidden: boolean): NavPreferences {
  const hiddenIds = new Set(preferences.hiddenItemIds);
  if (hidden) {
    hiddenIds.add(itemId);
  } else {
    hiddenIds.delete(itemId);
  }

  return {
    ...preferences,
    hiddenItemIds: preferences.orderedItemIds.filter((id) => hiddenIds.has(id)),
  };
}

export function setNavItemPinned(preferences: NavPreferences, itemId: string, pinned: boolean): NavPreferences {
  const pinnedIds = new Set(preferences.pinnedItemIds);
  if (pinned) {
    pinnedIds.add(itemId);
  } else {
    pinnedIds.delete(itemId);
  }

  return {
    ...preferences,
    pinnedItemIds: preferences.orderedItemIds.filter((id) => pinnedIds.has(id)),
  };
}
