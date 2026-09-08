import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useAuth } from '@/features/auth/hooks/useAuth';
import {
  getAdminPinnedItemIds,
  getNavPreferencesStorageKey,
  loadNavPreferences,
  NAV_PREFERENCES_VERSION,
  moveAdminNavItem,
  subscribeToNavPreferences,
  saveNavPreferences,
  setAdminNavItemPinned,
  type NavPreferences,
} from '@/common/utils/navPreferences';
import { AdminNavPinsContext } from '@/common/contexts/adminNavPinsContextValue';

export function AdminNavPinsProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const storageKey = useMemo(() => getNavPreferencesStorageKey(user?.id), [user?.id]);
  // `version` is bumped whenever preferences change (this tab's own writes, or
  // same-tab or cross-tab storage events) to force `pinnedIds` to
  // recompute. Using useMemo keyed on storageKey (rather than useState +
  // useEffect) means a change in `storageKey` — logout or account switch —
  // is reflected in the very first render for the new principal: useEffect
  // only runs after paint, so a useState-based cache would commit one frame
  // showing the *previous* user's pins before the effect corrected it. That
  // transient frame is exactly the leak the "no pin leakage across accounts"
  // acceptance criterion forbids, so pins must be derived synchronously
  // during render instead.
  const [version, setVersion] = useState(0);

  useEffect(() => {
    return subscribeToNavPreferences(storageKey, () => setVersion((current) => current + 1));
  }, [storageKey]);

  const resolveStoredPreferences = useCallback((existing?: Partial<NavPreferences> | null): NavPreferences => ({
    version: existing?.version ?? NAV_PREFERENCES_VERSION,
    orderedItemIds: existing?.orderedItemIds ?? [],
    hiddenItemIds: existing?.hiddenItemIds ?? [],
    pinnedItemIds: existing?.pinnedItemIds ?? [],
    ...existing,
    adminPinnedItemIds: getAdminPinnedItemIds(existing),
  }), []);

  const pinnedIds = useMemo(() => {
    const preferences = loadNavPreferences(storageKey);
    return getAdminPinnedItemIds(preferences);
    // `version` is intentionally a dependency purely to invalidate this memo
    // when preferences change; its value is never read.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storageKey, version]);

  const setPinned = useCallback((destinationId: string, pinned: boolean) => {
    const existing = resolveStoredPreferences(loadNavPreferences(storageKey));
    const next = setAdminNavItemPinned(existing, destinationId, pinned);
    saveNavPreferences(storageKey, next);
    setVersion((current) => current + 1);
  }, [resolveStoredPreferences, storageKey]);

  const movePinned = useCallback((destinationId: string, targetIndex: number, orderedPinnedIds?: readonly string[]) => {
    const existing = resolveStoredPreferences(loadNavPreferences(storageKey));
    const next = moveAdminNavItem(existing, destinationId, targetIndex, orderedPinnedIds);
    saveNavPreferences(storageKey, next);
    setVersion((current) => current + 1);
  }, [resolveStoredPreferences, storageKey]);

  return (
    <AdminNavPinsContext.Provider value={{ pinnedIds, setPinned, movePinned }}>
      {children}
    </AdminNavPinsContext.Provider>
  );
}
