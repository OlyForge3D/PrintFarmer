import { beforeEach, describe, expect, it } from 'vitest';
import {
  createDefaultNavPreferences,
  getNavPreferencesStorageKey,
  loadNavPreferences,
  moveNavItem,
  resolveNavPreferences,
  saveNavPreferences,
  setNavItemHidden,
  setNavItemPinned,
} from '@/common/utils/navPreferences';
import type { NavPreferenceItem } from '@/common/utils/navPreferences';

const items: NavPreferenceItem[] = [
  { id: 'overview', name: 'Overview', sectionName: 'Dashboard' },
  { id: 'print-queue', name: 'Print Queue', sectionName: 'Dashboard' },
  { id: 'printers', name: 'Printers', sectionName: 'Printers' },
  { id: 'filament-inventory', name: 'Filament Inventory', sectionName: 'Printers' },
  { id: 'files', name: 'Files', sectionName: 'Files' },
  { id: 'scheduling', name: 'Scheduling', sectionName: 'Files' },
  { id: 'auto-dispatch', name: 'Auto-Dispatch', sectionName: 'Admin' },
];

describe('navPreferences', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('uses role-based default ordering', () => {
    const admin = createDefaultNavPreferences(items, 'admin');
    const operator = createDefaultNavPreferences(items, 'operator');

    expect(admin.orderedItemIds.slice(0, 4)).toEqual(['overview', 'printers', 'print-queue', 'auto-dispatch']);
    expect(operator.orderedItemIds.slice(0, 4)).toEqual(['overview', 'print-queue', 'printers', 'scheduling']);
  });

  it('persists reordered preferences through localStorage', () => {
    const storageKey = getNavPreferencesStorageKey('user-1');
    const defaults = createDefaultNavPreferences(items, 'operator');
    const reordered = moveNavItem(defaults, 'scheduling', 1);

    saveNavPreferences(storageKey, reordered);
    const loaded = loadNavPreferences(storageKey);
    const resolved = resolveNavPreferences(items, 'operator', loaded);

    expect(resolved.preferences.orderedItemIds.slice(0, 3)).toEqual(['overview', 'scheduling', 'print-queue']);
  });

  it('hides and reveals individual nav items', () => {
    const defaults = createDefaultNavPreferences(items, 'operator');
    const hidden = setNavItemHidden(defaults, 'files', true);
    const resolvedHidden = resolveNavPreferences(items, 'operator', hidden);
    const revealed = setNavItemHidden(hidden, 'files', false);
    const resolvedRevealed = resolveNavPreferences(items, 'operator', revealed);

    expect(resolvedHidden.visibleItems.map((item) => item.id)).not.toContain('files');
    expect(resolvedHidden.hiddenItems.map((item) => item.id)).toContain('files');
    expect(resolvedRevealed.visibleItems.map((item) => item.id)).toContain('files');
  });

  it('pins favorites into a separate top collection', () => {
    const defaults = createDefaultNavPreferences(items, 'operator');
    const pinned = setNavItemPinned(defaults, 'printers', true);
    const resolved = resolveNavPreferences(items, 'operator', pinned);

    expect(resolved.favoriteItems.map((item) => item.id)).toEqual(['printers']);
    expect(resolved.regularItems.map((item) => item.id)).not.toContain('printers');
  });

  it('resets custom preferences to role defaults', () => {
    const defaults = createDefaultNavPreferences(items, 'operator');
    const customized = setNavItemPinned(setNavItemHidden(moveNavItem(defaults, 'files', 0), 'printers', true), 'files', true);
    const reset = createDefaultNavPreferences(items, 'operator');

    expect(customized).not.toEqual(reset);
    expect(reset.hiddenItemIds).toEqual([]);
    expect(reset.pinnedItemIds).toEqual([]);
    expect(reset.orderedItemIds.slice(0, 3)).toEqual(['overview', 'print-queue', 'printers']);
  });
});
