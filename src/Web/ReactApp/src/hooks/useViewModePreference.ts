import { useState, useEffect } from 'react';

type ViewMode = 'grid' | 'list' | 'explorer';

const STORAGE_KEY = 'printfarmer-models-viewmode';
const DEFAULT_VIEW_MODE: ViewMode = 'explorer';

/**
 * Custom hook for managing 3D models view mode preference.
 * Persists to localStorage and restores on component mount.
 */
export function useViewModePreference() {
  const [viewMode, setViewModeState] = useState<ViewMode>(DEFAULT_VIEW_MODE);
  const [isLoaded, setIsLoaded] = useState(false);

  // Load preference from localStorage on mount
  useEffect(() => {
    const savedViewMode = localStorage.getItem(STORAGE_KEY);
    if (savedViewMode === 'grid' || savedViewMode === 'list' || savedViewMode === 'explorer') {
      setViewModeState(savedViewMode);
    }
    setIsLoaded(true);
  }, []);

  // Update viewMode and persist to localStorage
  const setViewMode = (newMode: ViewMode) => {
    setViewModeState(newMode);
    localStorage.setItem(STORAGE_KEY, newMode);
  };

  return { viewMode, setViewMode, isLoaded };
}
