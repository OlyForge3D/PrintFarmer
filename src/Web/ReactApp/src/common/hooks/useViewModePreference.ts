import { useState, useEffect } from 'react';

export type ViewMode = 'grid' | 'explorer';

const DEFAULT_VIEW_MODE: ViewMode = 'explorer';

/**
 * Custom hook for managing view mode preference.
 * Persists to localStorage and restores on component mount.
 * @param storageKey - localStorage key for persisting preference (e.g., 'printfarmer-models-viewmode')
 */
export function useViewModePreference(storageKey: string = 'printfarmer-models-viewmode') {
  const [viewMode, setViewModeState] = useState<ViewMode>(DEFAULT_VIEW_MODE);
  const [isLoaded, setIsLoaded] = useState(false);

  // Load preference from localStorage on mount
  useEffect(() => {
    const savedViewMode = localStorage.getItem(storageKey);
    // Convert 'list' to 'grid' for backwards compatibility
    if (savedViewMode === 'grid' || savedViewMode === 'explorer') {
      setViewModeState(savedViewMode);
    } else if (savedViewMode === 'list') {
      // Migrate old 'list' view mode to 'grid'
      setViewModeState('grid');
      localStorage.setItem(storageKey, 'grid');
    }
    setIsLoaded(true);
  }, [storageKey]);

  // Update viewMode and persist to localStorage
  const setViewMode = (newMode: ViewMode) => {
    setViewModeState(newMode);
    localStorage.setItem(storageKey, newMode);
  };

  return { viewMode, setViewMode, isLoaded };
}
