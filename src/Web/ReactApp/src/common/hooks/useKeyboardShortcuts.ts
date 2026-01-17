import { useEffect } from 'react';

export interface KeyboardShortcut {
  key: string; // e.g., 'u' for Ctrl+U, 'd' for Ctrl+D
  handler: () => void;
  description?: string;
}

export interface UseKeyboardShortcutsOptions {
  enabled?: boolean;
}

/**
 * Hook for managing keyboard shortcuts (Ctrl+key combinations)
 * Commonly used shortcuts:
 * - Ctrl+U: Upload
 * - Ctrl+D: Delete
 * - Ctrl+T: Tag
 * - Ctrl+F: Search/Filter
 * - Ctrl+N: New item
 * - Ctrl+S: Save
 * - Ctrl+C: Copy/Cancel
 * - Ctrl+P: Print/Pause
 */
export function useKeyboardShortcuts(
  shortcuts: KeyboardShortcut[],
  options?: UseKeyboardShortcutsOptions
) {
  const enabled = options?.enabled !== false;

  useEffect(() => {
    if (!enabled) return;

    const handleKeyDown = (e: KeyboardEvent) => {
      // Only handle Ctrl+key combinations
      if (!e.ctrlKey && !e.metaKey) return;

      const key = e.key.toLowerCase();
      const shortcut = shortcuts.find((s) => s.key.toLowerCase() === key);

      if (shortcut) {
        e.preventDefault();
        shortcut.handler();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [shortcuts, enabled]);

  // Provide shortcut reference for help text
  return {
    shortcuts: shortcuts.map((s) => ({
      display: `Ctrl+${s.key.toUpperCase()}`,
      description: s.description,
    })),
  };
}
