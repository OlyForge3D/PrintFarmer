export const NAV_PREFERENCES_VERSION = 1;
export const NAV_PREFERENCES_STORAGE_KEY = 'pf_nav_preferences_v1';
export const NAV_PREFERENCES_UPDATED_EVENT = 'pf-nav-preferences-updated';

export type NavPreferenceRole = 'admin' | 'operator' | 'guest';

export interface NavPreferenceItem {
  id: string;
  name: string;
  sectionName: string;
  anchored?: boolean;
}

export interface NavPreferences {
  version: typeof NAV_PREFERENCES_VERSION;
  orderedItemIds: string[];
  hiddenItemIds: string[];
  pinnedItemIds: string[];
  /** Stable admin destination IDs explicitly pinned by this user. */
  adminPinnedItemIds?: string[];
}

export interface ResolvedNavPreferences {
  preferences: NavPreferences;
  orderedItems: NavPreferenceItem[];
  visibleItems: NavPreferenceItem[];
  hiddenItems: NavPreferenceItem[];
  favoriteItems: NavPreferenceItem[];
  regularItems: NavPreferenceItem[];
}

export interface NavPreferenceGroup<T extends NavPreferenceItem = NavPreferenceItem> {
  sectionName: string;
  items: T[];
}

export type NavMoveDirection = 'up' | 'down';

export interface NavMoveFocusTargets {
  up?: HTMLButtonElement | null;
  down?: HTMLButtonElement | null;
  row?: HTMLElement | null;
}

export function getNavMoveFocusTarget(targets: NavMoveFocusTargets | undefined, direction: NavMoveDirection) {
  const clickedButton = direction === 'up' ? targets?.up : targets?.down;
  if (clickedButton && !clickedButton.disabled) {
    return clickedButton;
  }

  const oppositeButton = direction === 'up' ? targets?.down : targets?.up;
  if (oppositeButton && !oppositeButton.disabled) {
    return oppositeButton;
  }

  return targets?.row ?? null;
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
  // #2526 — this is the *default ordering* for the admin role's navbar, not a
  // record of intentional user pins. Entries for destinations the Admin
  // Control Center now owns by default (auto-dispatch, locations, analytics,
  // maintenance, catalog) were removed along with their navbar entries, as
  // were two ids (`system-settings`, `admin-console`) that had already outlived
  // their nav items. `uniqueKnownIds` drops unknown ids anyway, so a stale id
  // here is silent dead weight rather than a visible entry — keep this list in
  // step with `navigation` in `Layout.tsx`.
  admin: [
    'overview',
    'printers',
    'print-queue',
    'scheduling',
    'files',
    'projects',
    'slice-job',
    'filament-inventory',
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

const uniqueStringIds = (ids: readonly unknown[]) => {
  const seen = new Set<string>();
  return ids.filter((id): id is string => {
    if (typeof id !== 'string' || seen.has(id)) {
      return false;
    }

    seen.add(id);
    return true;
  });
};

const getAnchoredIds = (items: readonly NavPreferenceItem[]) => new Set(items.filter((item) => item.anchored).map((item) => item.id));

const enforceAnchoredNavPreferences = (preferences: NavPreferences, items: readonly NavPreferenceItem[]): NavPreferences => {
  const itemIds = items.map((item) => item.id);
  const knownIds = new Set(itemIds);
  const anchoredIds = getAnchoredIds(items);
  const nonAnchoredIds = uniqueKnownIds(preferences.orderedItemIds, knownIds).filter((id) => !anchoredIds.has(id));
  const missingNonAnchoredIds = itemIds.filter((id) => !anchoredIds.has(id) && !nonAnchoredIds.includes(id));
  const canonicalAnchoredIds = itemIds.filter((id) => anchoredIds.has(id));

  return {
    ...preferences,
    orderedItemIds: [...nonAnchoredIds, ...missingNonAnchoredIds, ...canonicalAnchoredIds],
    hiddenItemIds: uniqueKnownIds(preferences.hiddenItemIds, knownIds).filter((id) => !anchoredIds.has(id)),
    pinnedItemIds: uniqueKnownIds(preferences.pinnedItemIds, knownIds).filter((id) => !anchoredIds.has(id)),
  };
};

export function getNavPreferencesStorageKey(userId?: string | null) {
  return `${NAV_PREFERENCES_STORAGE_KEY}:${userId ?? 'anonymous'}`;
}

export function createDefaultNavPreferences(items: readonly NavPreferenceItem[], role: NavPreferenceRole): NavPreferences {
  const itemIds = items.map((item) => item.id);
  const knownIds = new Set(itemIds);
  const roleOrder = uniqueKnownIds(DEFAULT_ORDER_BY_ROLE[role], knownIds);

  return enforceAnchoredNavPreferences({
    version: NAV_PREFERENCES_VERSION,
    orderedItemIds: [...roleOrder, ...itemIds.filter((id) => !roleOrder.includes(id))],
    hiddenItemIds: [],
    pinnedItemIds: [],
  }, items);
}

export function normalizeNavPreferences(
  items: readonly NavPreferenceItem[],
  role: NavPreferenceRole,
  preferences?: Partial<NavPreferences> | null
): NavPreferences {
  const defaults = createDefaultNavPreferences(items, role);
  const knownIds = new Set(items.map((item) => item.id));
  const orderedItemIds = uniqueKnownIds(preferences?.orderedItemIds ?? defaults.orderedItemIds, knownIds);

  return enforceAnchoredNavPreferences({
    version: NAV_PREFERENCES_VERSION,
    orderedItemIds: [
      ...orderedItemIds,
      ...defaults.orderedItemIds.filter((id) => !orderedItemIds.includes(id)),
    ],
    hiddenItemIds: uniqueKnownIds(preferences?.hiddenItemIds ?? [], knownIds),
    pinnedItemIds: uniqueKnownIds(preferences?.pinnedItemIds ?? [], knownIds),
    ...(Array.isArray(preferences?.adminPinnedItemIds)
      ? { adminPinnedItemIds: uniqueStringIds(preferences.adminPinnedItemIds) }
      : {}),
  }, items);
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
  try {
    storage.setItem(storageKey, JSON.stringify(preferences));
    if (storage === localStorage && typeof window !== 'undefined') {
      queueMicrotask(() => {
        window.dispatchEvent(new CustomEvent(NAV_PREFERENCES_UPDATED_EVENT, { detail: { storageKey } }));
      });
    }
  } catch (error) {
    console.warn('Unable to save navigation preferences.', error);
  }
}

/** Subscribe to this user's same-tab writes and cross-tab localStorage changes. */
export function subscribeToNavPreferences(storageKey: string, onChange: () => void): () => void {
  const handleUpdated = (event: Event) => {
    const detail = (event as CustomEvent<{ storageKey?: string }>).detail;
    if (!detail?.storageKey || detail.storageKey === storageKey) {
      onChange();
    }
  };
  const handleStorage = (event: StorageEvent) => {
    // A null key means localStorage.clear(); sessionStorage is unrelated.
    if (event.storageArea === localStorage && (event.key === storageKey || event.key === null)) {
      // Consumers reload the latest stored value, without writing or rebroadcasting.
      onChange();
    }
  };

  window.addEventListener(NAV_PREFERENCES_UPDATED_EVENT, handleUpdated);
  window.addEventListener('storage', handleStorage);
  return () => {
    window.removeEventListener(NAV_PREFERENCES_UPDATED_EVENT, handleUpdated);
    window.removeEventListener('storage', handleStorage);
  };
}

export function groupNavItemsByResolvedOrder<T extends NavPreferenceItem>(items: readonly T[]): NavPreferenceGroup<T>[] {
  const groups: NavPreferenceGroup<T>[] = [];

  items.forEach((item) => {
    const currentGroup = groups.at(-1);
    if (currentGroup?.sectionName === item.sectionName) {
      currentGroup.items.push(item);
      return;
    }

    groups.push({
      sectionName: item.sectionName,
      items: [item],
    });
  });

  return groups;
}

export function moveNavItem(
  preferences: NavPreferences,
  itemId: string,
  targetIndex: number,
  items?: readonly NavPreferenceItem[]
): NavPreferences {
  const anchoredIds = items ? getAnchoredIds(items) : new Set<string>();
  if (anchoredIds.has(itemId)) {
    return preferences;
  }

  const currentIndex = preferences.orderedItemIds.indexOf(itemId);
  if (currentIndex < 0) {
    return preferences;
  }

  const nextOrder = preferences.orderedItemIds.filter((id) => id !== itemId);
  const lastAllowedIndex = items ? nextOrder.filter((id) => !anchoredIds.has(id)).length : nextOrder.length;
  const safeTargetIndex = Math.max(0, Math.min(targetIndex, lastAllowedIndex));
  nextOrder.splice(safeTargetIndex, 0, itemId);

  const nextPreferences = {
    ...preferences,
    orderedItemIds: nextOrder,
  };

  return items ? enforceAnchoredNavPreferences(nextPreferences, items) : nextPreferences;
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

export function getAdminPinnedItemIds(preferences?: Partial<NavPreferences> | null): string[] {
  return Array.isArray(preferences?.adminPinnedItemIds) ? uniqueStringIds(preferences.adminPinnedItemIds) : [];
}

export function setAdminNavItemPinned(preferences: NavPreferences, destinationId: string, pinned: boolean): NavPreferences {
  const currentPinnedIds = getAdminPinnedItemIds(preferences);
  const nextPinnedIds = pinned
    ? uniqueStringIds([...currentPinnedIds, destinationId])
    : currentPinnedIds.filter((id) => id !== destinationId);

  return {
    ...preferences,
    adminPinnedItemIds: nextPinnedIds,
  };
}

export function moveAdminNavItem(
  preferences: NavPreferences,
  destinationId: string,
  targetIndex: number,
  orderedPinnedIds: readonly string[] = getAdminPinnedItemIds(preferences),
): NavPreferences {
  const currentPinnedIds = getAdminPinnedItemIds(preferences);
  const reorderablePinnedIds = uniqueStringIds(orderedPinnedIds).filter((id) => currentPinnedIds.includes(id));
  const currentIndex = reorderablePinnedIds.indexOf(destinationId);
  if (currentIndex < 0) {
    return preferences;
  }

  const nextPinnedIds = reorderablePinnedIds.filter((id) => id !== destinationId);
  const safeTargetIndex = Math.max(0, Math.min(targetIndex, nextPinnedIds.length));
  nextPinnedIds.splice(safeTargetIndex, 0, destinationId);

  if (reorderablePinnedIds.length === currentPinnedIds.length) {
    return {
      ...preferences,
      adminPinnedItemIds: nextPinnedIds,
    };
  }

  const reorderedVisibleIds = [...nextPinnedIds];
  const reorderablePinnedIdSet = new Set(reorderablePinnedIds);

  return {
    ...preferences,
    adminPinnedItemIds: currentPinnedIds.map((id) => (
      reorderablePinnedIdSet.has(id)
        ? reorderedVisibleIds.shift() ?? id
        : id
    )),
  };
}
