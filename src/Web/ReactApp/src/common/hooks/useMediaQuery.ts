import { useCallback, useSyncExternalStore } from 'react';

/**
 * Subscribes to a CSS media query and returns whether it currently matches.
 *
 * This exists so layout decisions like "render this panel here on desktop,
 * there on mobile" can be made with a single JS branch instead of mounting
 * the same component twice and hiding one copy with a CSS class. A
 * CSS-only `lg:hidden` / `hidden lg:block` pair still mounts *both* React
 * component trees — their effects, subscriptions, and event handlers are
 * all live even for the hidden copy. For most components that's harmless,
 * but it's a real hazard for a component that fires mutating side effects
 * from user interaction (see PrintersPage's use of this hook for
 * `PrinterDetailsSidebar`/`MmuControlBox`, #1702): duplicate mounts are the
 * dual-mount race in miniature, just gated by viewport width instead of
 * route/view-mode.
 *
 * Implemented with `useSyncExternalStore` (rather than a `useState` +
 * `useEffect` pair) so the initial render, subsequent resizes, and any
 * change to `query` are all served from the same snapshot function, with no
 * separate effect-driven `setState` call needed to correct a stale initial
 * value.
 */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (onStoreChange: () => void) => {
      if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
        return () => {};
      }
      const mediaQueryList = window.matchMedia(query);
      mediaQueryList.addEventListener('change', onStoreChange);
      return () => mediaQueryList.removeEventListener('change', onStoreChange);
    },
    [query]
  );

  const getSnapshot = useCallback(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return false;
    }
    return window.matchMedia(query).matches;
  }, [query]);

  return useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
}

function getServerSnapshot(): boolean {
  return false;
}

/** Tailwind's `lg` breakpoint (1024px), as a media query string. */
export const LG_BREAKPOINT_QUERY = '(min-width: 1024px)';

/** Convenience wrapper for `useMediaQuery(LG_BREAKPOINT_QUERY)`, matching Tailwind's `lg:` prefix. */
export function useIsLgBreakpoint(): boolean {
  return useMediaQuery(LG_BREAKPOINT_QUERY);
}

/**
 * Below Tailwind's `sm` breakpoint (640px) — narrow/mobile viewports (e.g. 375px)
 * where dense toolbar-style UI should collapse into a compact/overflow form
 * instead of wrapping into multiple rows. Phrased as a `max-width` query (rather
 * than the inverse of a `min-width` query) so the global test-suite `matchMedia`
 * polyfill, which always reports `matches: false`, defaults callers to the
 * existing non-compact layout unless a test explicitly opts into the narrow
 * viewport. See issue #2406.
 */
export const MOBILE_BREAKPOINT_QUERY = '(max-width: 639.98px)';

/** Convenience wrapper for `useMediaQuery(MOBILE_BREAKPOINT_QUERY)`. */
export function useIsMobileBreakpoint(): boolean {
  return useMediaQuery(MOBILE_BREAKPOINT_QUERY);
}
