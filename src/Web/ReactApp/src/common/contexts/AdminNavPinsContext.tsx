import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useAuth } from '@/features/auth/hooks/useAuth';
import {
  getNavPreferencesStorageKey,
  loadNavPreferences,
  NAV_PREFERENCES_UPDATED_EVENT,
  saveNavPreferences,
  NAV_PREFERENCES_VERSION,
  type NavPreferences,
} from '@/common/utils/navPreferences';
import { AdminNavPinsContext } from '@/common/contexts/adminNavPinsContextValue';

export function AdminNavPinsProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const storageKey = useMemo(() => getNavPreferencesStorageKey(user?.id), [user?.id]);
  // `version` is bumped whenever preferences change (this tab's own writes, or
  // the storage-updated event from another consumer) to force `pinnedIds` to
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
    const refresh = () => setVersion((current) => current + 1);
    window.addEventListener(NAV_PREFERENCES_UPDATED_EVENT, refresh);
    return () => window.removeEventListener(NAV_PREFERENCES_UPDATED_EVENT, refresh);
  }, []);

  const pinnedIds = useMemo(() => {
    const preferences = loadNavPreferences(storageKey);
    return Array.isArray(preferences?.adminPinnedItemIds) ? [...new Set(preferences.adminPinnedItemIds)] : [];
    // `version` is intentionally a dependency purely to invalidate this memo
    // when preferences change; its value is never read.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storageKey, version]);

  const setPinned = useCallback((destinationId: string, pinned: boolean) => {
    const existing = loadNavPreferences(storageKey);
    const current = Array.isArray(existing?.adminPinnedItemIds) ? [...new Set(existing.adminPinnedItemIds)] : [];
    const nextIds = pinned
      ? [...new Set([...current, destinationId])]
      : current.filter((id) => id !== destinationId);
    const next: NavPreferences = {
      version: NAV_PREFERENCES_VERSION,
      orderedItemIds: existing?.orderedItemIds ?? [],
      hiddenItemIds: existing?.hiddenItemIds ?? [],
      pinnedItemIds: existing?.pinnedItemIds ?? [],
      ...existing,
      adminPinnedItemIds: nextIds,
    };
    saveNavPreferences(storageKey, next);
    setVersion((current) => current + 1);
  }, [storageKey]);

  return (
    <AdminNavPinsContext.Provider value={{ pinnedIds, setPinned }}>
      {children}
    </AdminNavPinsContext.Provider>
  );
}
