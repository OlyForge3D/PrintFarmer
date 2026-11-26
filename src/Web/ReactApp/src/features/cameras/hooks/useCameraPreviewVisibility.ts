import { useEffect, useRef, useState } from 'react';
import type { RefObject } from 'react';

export interface CameraPreviewVisibility {
  previewContainerRef: RefObject<HTMLDivElement | null>;
  isPreviewVisible: boolean;
}

function getIsDocumentVisible(): boolean {
  return typeof document === 'undefined' || document.visibilityState === 'visible';
}

/**
 * Tracks whether a camera preview container is on screen in a visible tab, so snapshot
 * polling and authenticated streams can share one demand signal. Attach
 * `previewContainerRef` to an element rendered unconditionally on mount; the observer
 * is created once.
 */
export function useCameraPreviewVisibility(): CameraPreviewVisibility {
  const previewContainerRef = useRef<HTMLDivElement | null>(null);
  const [isDocumentVisible, setIsDocumentVisible] = useState(getIsDocumentVisible);
  const [isIntersectingViewport, setIsIntersectingViewport] = useState(
    () => typeof IntersectionObserver === 'undefined'
  );

  useEffect(() => {
    if (typeof document === 'undefined') return;
    const handleVisibilityChange = () => setIsDocumentVisible(getIsDocumentVisible());
    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => document.removeEventListener('visibilitychange', handleVisibilityChange);
  }, []);

  useEffect(() => {
    if (typeof IntersectionObserver === 'undefined') return;
    const element = previewContainerRef.current;
    if (!element) return;
    const observer = new IntersectionObserver((entries) => {
      // Batched entries are chronological; the last one is the element's current state.
      setIsIntersectingViewport(entries.at(-1)?.isIntersecting ?? false);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  return { previewContainerRef, isPreviewVisible: isDocumentVisible && isIntersectingViewport };
}
