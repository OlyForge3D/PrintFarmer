import { useState, useCallback, useEffect } from 'react';

/**
 * Storage key prefix for catalog view preferences
 */
const STORAGE_KEY_PREFIX = 'catalog-view-';

/**
 * Valid view modes for catalog tabs
 */
export type CatalogViewMode = 'grid' | 'table';

/**
 * Catalog tab identifiers
 */
export type CatalogTab = 
  | 'filaments'
  | 'hotends'
  | 'extruders'
  | 'toolheads'
  | 'nozzles'
  | 'printer-models';

/**
 * Gets the storage key for a specific tab
 */
function getStorageKey(tab: CatalogTab): string {
  return `${STORAGE_KEY_PREFIX}${tab}`;
}

/**
 * Reads a view mode from localStorage
 */
function readViewMode(tab: CatalogTab): CatalogViewMode {
  try {
    const stored = localStorage.getItem(getStorageKey(tab));
    if (stored === 'grid' || stored === 'table') {
      return stored;
    }
  } catch {
    // localStorage may be unavailable (e.g., private browsing)
  }
  return 'grid'; // Default to grid view
}

/**
 * Writes a view mode to localStorage
 */
function writeViewMode(tab: CatalogTab, mode: CatalogViewMode): void {
  try {
    localStorage.setItem(getStorageKey(tab), mode);
  } catch {
    // localStorage may be unavailable
  }
}

/**
 * Hook for managing persisted catalog view mode
 * 
 * Stores the view preference per tab in localStorage so it persists
 * when switching between tabs and across page refreshes.
 * 
 * @param tab - The catalog tab identifier
 * @returns [currentView, setView] tuple
 * 
 * @example
 * const [view, setView] = useCatalogViewMode('filaments');
 * 
 * <ViewToggle 
 *   value={view} 
 *   onChange={setView} 
 *   options={gridTableOptions} 
 * />
 */
export function useCatalogViewMode(tab: CatalogTab): [CatalogViewMode, (mode: CatalogViewMode) => void] {
  const [view, setViewInternal] = useState<CatalogViewMode>(() => readViewMode(tab));

  // Sync with localStorage when tab changes (e.g., if component is reused)
  useEffect(() => {
    setViewInternal(readViewMode(tab));
  }, [tab]);

  const setView = useCallback((mode: CatalogViewMode) => {
    setViewInternal(mode);
    writeViewMode(tab, mode);
  }, [tab]);

  return [view, setView];
}

export default useCatalogViewMode;
