import { useState, useEffect, useCallback, useMemo } from 'react';

export interface UseKeyboardNavigationOptions {
  columns?: number;
  onEscapeKey?: () => void;
  onEnter?: () => void;
}

// Elements that already manage their own Enter/Space/Arrow key activation
// (links, buttons, form controls, editable regions, and any explicitly
// focusable custom widget). This hook attaches a window-level listener with
// no container/focus scoping, so without this guard it hijacks every Enter
// keypress on the page — including activating the "Skip to main content"
// link, whose native anchor navigation gets preventDefault()'d before it can
// move focus, and instead opens the first job's details modal via the stale
// default `selectedIndex` of 0 (#2373). `[tabindex]:not([tabindex="-1"])`
// also covers custom focusable widgets that manage their own Enter handling
// (e.g. queue job rows/cards, the timeline's keyboard surface), which would
// otherwise be double-handled by this listener after their own handler runs.
const INTERACTIVE_SELECTOR =
  'a[href], button, input, textarea, select, summary, [contenteditable="true"], ' +
  '[role="button"], [role="link"], [tabindex]:not([tabindex="-1"])';

function isInteractiveTarget(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest(INTERACTIVE_SELECTOR) !== null;
}

/**
 * Hook for handling keyboard navigation in grids and lists
 * Supports arrow keys for navigation, Enter to select, Escape to close
 */
export function useKeyboardNavigation<T>(
  items: T[],
  onSelect: (item: T, index: number) => void,
  options?: UseKeyboardNavigationOptions
) {
  const [selectedIndex, setSelectedIndex] = useState(0);
  const [isNavigating, setIsNavigating] = useState(false);

  // Compute valid index - clamps to bounds without triggering effect cascade
  const validSelectedIndex = useMemo(() => {
    if (items.length === 0) return 0;
    return Math.min(selectedIndex, items.length - 1);
  }, [items.length, selectedIndex]);

  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (items.length === 0) return;
      // Don't hijack keys intended for a focused interactive element (links,
      // buttons, form fields, the skip link, etc.) — let it handle its own
      // activation instead of this page-wide navigation shortcut.
      if (isInteractiveTarget(e.target)) return;

      let handled = false;
      let newIndex = validSelectedIndex;

      switch (e.key) {
        case 'ArrowDown':
          newIndex = Math.min(validSelectedIndex + (options?.columns || 1), items.length - 1);
          handled = true;
          break;
        case 'ArrowUp':
          newIndex = Math.max(validSelectedIndex - (options?.columns || 1), 0);
          handled = true;
          break;
        case 'ArrowRight':
          if (options?.columns) {
            newIndex = Math.min(validSelectedIndex + 1, items.length - 1);
            handled = true;
          }
          break;
        case 'ArrowLeft':
          if (options?.columns) {
            newIndex = Math.max(validSelectedIndex - 1, 0);
            handled = true;
          }
          break;
        case 'Enter':
          onSelect(items[validSelectedIndex], validSelectedIndex);
          options?.onEnter?.();
          handled = true;
          break;
        case 'Escape':
          options?.onEscapeKey?.();
          handled = true;
          break;
        default:
          break;
      }

      if (handled) {
        e.preventDefault();
        setSelectedIndex(newIndex);
        setIsNavigating(true);
      }
    },
    [items, validSelectedIndex, onSelect, options]
  );

  useEffect(() => {
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [handleKeyDown]);

  return {
    selectedIndex: validSelectedIndex,
    setSelectedIndex,
    isNavigating,
  };
}
