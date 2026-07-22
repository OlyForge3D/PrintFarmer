import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  createDefaultNavPreferences,
  getNavMoveFocusTarget,
  getNavPreferencesStorageKey,
  groupNavItemsByResolvedOrder,
  loadNavPreferences,
  moveNavItem,
  NAV_PREFERENCES_VERSION,
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
  { id: 'maintenance', name: 'Maintenance', sectionName: 'Admin', anchored: true },
  { id: 'locations', name: 'Locations', sectionName: 'Admin', anchored: true },
  { id: 'auto-dispatch', name: 'Auto-Dispatch', sectionName: 'Admin', anchored: true },
];

describe('navPreferences', () => {
  beforeEach(() => {
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('uses role-based default ordering', () => {
    const admin = createDefaultNavPreferences(items, 'admin');
    const operator = createDefaultNavPreferences(items, 'operator');

    expect(admin.orderedItemIds.slice(0, 4)).toEqual(['overview', 'printers', 'print-queue', 'scheduling']);
    expect(admin.orderedItemIds.slice(-3)).toEqual(['maintenance', 'locations', 'auto-dispatch']);
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

  it('reorders visible items correctly when there are hidden items', () => {
    const defaults = createDefaultNavPreferences(items, 'operator');
    const hidden = setNavItemHidden(defaults, 'print-queue', true);
    const reordered = moveNavItem(hidden, 'overview', 2);
    const resolved = resolveNavPreferences(items, 'operator', reordered);

    expect(resolved.visibleItems.map((item) => item.id)).toEqual([
      'printers',
      'overview',
      'scheduling',
      'files',
      'filament-inventory',
      'maintenance',
      'locations',
      'auto-dispatch',
    ]);
  });

  it('groups resolved items contiguously so cross-section moves render in resolved order', () => {
    const defaults = createDefaultNavPreferences(items, 'operator');
    const movedAcrossSections = moveNavItem(defaults, 'print-queue', 2);
    const resolved = resolveNavPreferences(items, 'operator', movedAcrossSections);
    const groups = groupNavItemsByResolvedOrder(resolved.regularItems);

    expect(groups.map((group) => ({
      sectionName: group.sectionName,
      itemIds: group.items.map((item) => item.id),
    }))).toEqual([
      { sectionName: 'Dashboard', itemIds: ['overview'] },
      { sectionName: 'Printers', itemIds: ['printers'] },
      { sectionName: 'Dashboard', itemIds: ['print-queue'] },
      { sectionName: 'Files', itemIds: ['scheduling', 'files'] },
      { sectionName: 'Printers', itemIds: ['filament-inventory'] },
      { sectionName: 'Admin', itemIds: ['maintenance', 'locations', 'auto-dispatch'] },
    ]);
    expect(groups.flatMap((group) => group.items.map((item) => item.id))).toEqual(
      resolved.regularItems.map((item) => item.id)
    );
  });

  it('anchors locked items at the end in canonical order and strips hidden or pinned stored state', () => {
    const resolved = resolveNavPreferences(items, 'admin', {
      version: NAV_PREFERENCES_VERSION,
      orderedItemIds: ['auto-dispatch', 'files', 'maintenance', 'overview', 'locations'],
      hiddenItemIds: ['maintenance', 'files', 'auto-dispatch'],
      pinnedItemIds: ['locations', 'printers', 'auto-dispatch'],
    });

    expect(resolved.preferences.orderedItemIds.slice(-3)).toEqual(['maintenance', 'locations', 'auto-dispatch']);
    expect(resolved.preferences.hiddenItemIds).toEqual(['files']);
    expect(resolved.preferences.pinnedItemIds).toEqual(['printers']);
    expect(resolved.hiddenItems.map((item) => item.id)).toEqual(['files']);
    expect(resolved.favoriteItems.map((item) => item.id)).toEqual(['printers']);
    expect(resolved.visibleItems.map((item) => item.id)).toEqual([
      'overview',
      'printers',
      'print-queue',
      'scheduling',
      'filament-inventory',
      'maintenance',
      'locations',
      'auto-dispatch',
    ]);
  });

  it('does not move anchored items or allow regular items below the anchored block', () => {
    const defaults = createDefaultNavPreferences(items, 'admin');
    const anchoredMove = moveNavItem(defaults, 'locations', 0, items);
    const regularMove = moveNavItem(defaults, 'overview', defaults.orderedItemIds.length, items);

    expect(anchoredMove).toEqual(defaults);
    expect(regularMove.orderedItemIds.slice(-4)).toEqual(['overview', 'maintenance', 'locations', 'auto-dispatch']);
    expect(regularMove.orderedItemIds.at(-3)).toBe('maintenance');
  });

  it('keeps focus on the pressed move button when it remains enabled', () => {
    const up = document.createElement('button');
    const down = document.createElement('button');
    const row = document.createElement('div');

    expect(getNavMoveFocusTarget({ up, down, row }, 'up')).toBe(up);
    expect(getNavMoveFocusTarget({ up, down, row }, 'down')).toBe(down);
  });

  it('falls back from a disabled pressed move button to the opposite button or row', () => {
    const up = document.createElement('button');
    const down = document.createElement('button');
    const row = document.createElement('div');

    up.disabled = true;
    expect(getNavMoveFocusTarget({ up, down, row }, 'up')).toBe(down);

    down.disabled = true;
    expect(getNavMoveFocusTarget({ up, down, row }, 'up')).toBe(row);
  });

  it('swallows storage write failures when saving nav preferences', () => {
    const defaults = createDefaultNavPreferences(items, 'operator');
    const storage = {
      setItem: vi.fn(() => {
        throw new DOMException('Storage disabled', 'SecurityError');
      }),
    } as unknown as Storage;
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);

    expect(() => saveNavPreferences('throwing-storage', defaults, storage)).not.toThrow();
    expect(storage.setItem).toHaveBeenCalledWith('throwing-storage', JSON.stringify(defaults));
    expect(warn).toHaveBeenCalled();
  });
});
