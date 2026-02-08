import { useState, useCallback } from 'react';

export type ViewMode = 'grid' | 'explorer';

const DEFAULT_VIEW_MODE: ViewMode = 'explorer';

// Initialize from localStorage, with migration from 'list' to 'grid'
function getInitialViewMode(storageKey: string): ViewMode {
  if (typeof window === 'undefined') return DEFAULT_VIEW_MODE;
  const saved = localStorage.getItem(storageKey);
  if (saved === 'grid' || saved === 'explorer') return saved;
  if (saved === 'list') {
    // Migrate old 'list' view mode to 'grid'
    localStorage.setItem(storageKey, 'grid');
    return 'grid';
  }
  return DEFAULT_VIEW_MODE;
}

/**
 * Custom hook for managing view mode preference.
 * Persists to localStorage and restores on component mount.
 * @param storageKey - localStorage key for persisting preference (e.g., 'printfarmer-models-viewmode')
 */
export function useViewModePreference(storageKey: string = 'printfarmer-models-viewmode') {
  const [viewMode, setViewModeState] = useState<ViewMode>(() => getInitialViewMode(storageKey));

  // Update viewMode and persist to localStorage
  const setViewMode = useCallback((newMode: ViewMode) => {
    setViewModeState(newMode);
    localStorage.setItem(storageKey, newMode);
  }, [storageKey]);

  // isLoaded is now always true since we initialize synchronously
  return { viewMode, setViewMode, isLoaded: true };
}
