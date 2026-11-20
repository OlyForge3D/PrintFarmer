import { useState, useCallback, useEffect } from 'react';

export type SlicerViewMode = 'simple' | 'advanced';

const STORAGE_KEY = 'printfarmer-slicer-viewmode';

function getStoredMode(): SlicerViewMode {
  if (typeof window === 'undefined') return 'simple';
  const stored = localStorage.getItem(STORAGE_KEY);
  return stored === 'advanced' ? 'advanced' : 'simple';
}

/**
 * Global, persisted Basic/Advanced view for all slicer profile editors.
 * Reads from localStorage on mount, writes on change, syncs across
 * mounted components via the 'storage' event.
 */
export function useSlicerViewMode(): [SlicerViewMode, () => void, (mode: SlicerViewMode) => void] {
  const [mode, setMode] = useState<SlicerViewMode>(getStoredMode);

  const setViewMode = useCallback((next: SlicerViewMode) => {
    setMode(next);
    localStorage.setItem(STORAGE_KEY, next);
    // Keep side effects out of the replayable React state updater.
    window.dispatchEvent(new CustomEvent('slicer-viewmode-change', { detail: next }));
  }, []);

  const toggleMode = useCallback(() => {
    setViewMode(mode === 'simple' ? 'advanced' : 'simple');
  }, [mode, setViewMode]);

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

  return [mode, toggleMode, setViewMode];
}
