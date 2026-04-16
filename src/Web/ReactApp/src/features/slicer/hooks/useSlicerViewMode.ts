import { useState, useCallback, useEffect } from 'react';

export type SlicerViewMode = 'simple' | 'advanced';

const STORAGE_KEY = 'printfarmer-slicer-viewmode';

function getStoredMode(): SlicerViewMode {
  if (typeof window === 'undefined') return 'simple';
  const stored = localStorage.getItem(STORAGE_KEY);
  return stored === 'advanced' ? 'advanced' : 'simple';
}

/**
 * Global, persisted Simple/Advanced toggle for all slicer profile editors.
 * Reads from localStorage on mount, writes on change, syncs across
 * mounted components via the 'storage' event.
 */
export function useSlicerViewMode(): [SlicerViewMode, () => void] {
  const [mode, setMode] = useState<SlicerViewMode>(getStoredMode);

  // Toggle and persist
  const toggleMode = useCallback(() => {
    setMode((prev) => {
      const next = prev === 'simple' ? 'advanced' : 'simple';
      localStorage.setItem(STORAGE_KEY, next);
      // Dispatch a custom event so other mounted instances of this hook sync
      window.dispatchEvent(new CustomEvent('slicer-viewmode-change', { detail: next }));
      return next;
    });
  }, []);

  // Listen for changes from OTHER components using this hook
  useEffect(() => {
    const handler = (e: Event) => {
      const detail = (e as CustomEvent<SlicerViewMode>).detail;
      setMode(detail);
    };
    window.addEventListener('slicer-viewmode-change', handler);
    // Also listen to storage events (cross-tab sync)
    const storageHandler = (e: StorageEvent) => {
      if (e.key === STORAGE_KEY && (e.newValue === 'simple' || e.newValue === 'advanced')) {
        setMode(e.newValue);
      }
    };
    window.addEventListener('storage', storageHandler);
    return () => {
      window.removeEventListener('slicer-viewmode-change', handler);
      window.removeEventListener('storage', storageHandler);
    };
  }, []);

  return [mode, toggleMode];
}
