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
  const [pinnedIds, setPinnedIds] = useState<string[]>([]);

  useEffect(() => {
    const refresh = () => {
      const preferences = loadNavPreferences(storageKey);
      setPinnedIds(Array.isArray(preferences?.adminPinnedItemIds) ? [...new Set(preferences.adminPinnedItemIds)] : []);
    };
    refresh();
    window.addEventListener(NAV_PREFERENCES_UPDATED_EVENT, refresh);
    return () => window.removeEventListener(NAV_PREFERENCES_UPDATED_EVENT, refresh);
  }, [storageKey]);

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
    setPinnedIds(nextIds);
  }, [storageKey]);

  return (
    <AdminNavPinsContext.Provider value={{ pinnedIds, setPinned }}>
      {children}
    </AdminNavPinsContext.Provider>
  );
}
