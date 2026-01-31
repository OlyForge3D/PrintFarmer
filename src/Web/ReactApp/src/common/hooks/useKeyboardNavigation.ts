import { useState, useEffect, useCallback, useMemo } from 'react';

export interface UseKeyboardNavigationOptions {
  columns?: number;
  onEscapeKey?: () => void;
  onEnter?: () => void;
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
