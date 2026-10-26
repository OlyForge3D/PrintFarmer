import { useCallback, useState } from 'react';

/**
 * Latches `true` the first time the attached element intersects the viewport.
 *
 * Used to gate printer-preview fetches so only on-screen items ever request a preview.
 * Without `IntersectionObserver` (jsdom, very old browsers) this stays `false`: nothing is
 * fetched speculatively for rows the user may never see.
 */
export function useInViewOnce<T extends Element>(): [(element: T | null) => (() => void) | undefined, boolean] {
  const [inView, setInView] = useState(false);
  // React 19 ref callbacks may return a cleanup, so the observer is torn down with the node.
  const ref = useCallback((element: T | null) => {
    if (!element || inView || typeof IntersectionObserver === 'undefined') return undefined;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        setInView(true);
        observer.disconnect();
      }
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [inView]);
  return [ref, inView];
}