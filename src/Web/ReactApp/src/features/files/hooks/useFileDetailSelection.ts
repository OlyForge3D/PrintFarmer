import { useCallback, useRef, useState } from 'react';

/**
 * Tracks which file is open in the detail panel and returns focus to the element that
 * opened it when the panel closes (WCAG 2.4.3 focus order).
 */
export function useFileDetailSelection<T>() {
  const [selected, setSelected] = useState<T | null>(null);
  const invokerRef = useRef<HTMLElement | null>(null);

  const open = useCallback((item: T, invoker?: HTMLElement | null) => {
    invokerRef.current = invoker
      ?? (document.activeElement instanceof HTMLElement ? document.activeElement : null);
    setSelected(item);
  }, []);

  const close = useCallback(() => {
    const invoker = invokerRef.current;
    invokerRef.current = null;
    setSelected(null);
    if (invoker) {
      window.requestAnimationFrame(() => {
        if (invoker.isConnected) invoker.focus();
      });
    }
  }, []);

  return { selected, open, close };
}