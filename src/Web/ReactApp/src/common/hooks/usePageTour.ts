import { useEffect, useRef, useState, useCallback } from 'react';
import { driver, type DriveStep, type Driver, type Config } from 'driver.js';

export interface TourStepDefinition {
  element: string;
  popover: {
    title: string;
    description: string;
  };
}

interface UsePageTourOptions {
  tourId: string;
  steps: TourStepDefinition[];
  autoStart?: boolean;
}

interface UsePageTourReturn {
  startTour: () => void;
  hasSeenTour: boolean;
  resetTour: () => void;
}

const STORAGE_PREFIX = 'pf-tour-seen-';

function getTourSeen(tourId: string): boolean {
  try {
    return localStorage.getItem(`${STORAGE_PREFIX}${tourId}`) === 'true';
  } catch {
    return false;
  }
}

function setTourSeen(tourId: string, seen: boolean): void {
  try {
    if (seen) {
      localStorage.setItem(`${STORAGE_PREFIX}${tourId}`, 'true');
    } else {
      localStorage.removeItem(`${STORAGE_PREFIX}${tourId}`);
    }
  } catch {
    // Private browsing or storage full — silently ignore
  }
}

export function usePageTour({
  tourId,
  steps,
  autoStart = true,
}: UsePageTourOptions): UsePageTourReturn {
  const [hasSeenTour, setHasSeenTour] = useState(() => getTourSeen(tourId));
  const driverRef = useRef<Driver | null>(null);

  const buildDriverSteps = useCallback((): DriveStep[] => {
    return steps.map((step) => ({
      element: step.element,
      popover: {
        title: step.popover.title,
        description: step.popover.description,
      },
    }));
  }, [steps]);

  const startTour = useCallback(() => {
    // Destroy any existing instance
    if (driverRef.current) {
      driverRef.current.destroy();
    }

    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    const config: Config = {
      showProgress: true,
      animate: !reducedMotion,
      overlayColor: 'rgba(13, 17, 23, 0.85)',
      stagePadding: 8,
      stageRadius: 8,
      popoverClass: 'pf-tour-popover',
      steps: buildDriverSteps(),
      onDestroyed: () => {
        setHasSeenTour(true);
        setTourSeen(tourId, true);
      },
    };

    driverRef.current = driver(config);
    driverRef.current.drive();
  }, [buildDriverSteps, tourId]);

  const resetTour = useCallback(() => {
    setHasSeenTour(false);
    setTourSeen(tourId, false);
  }, [tourId]);

  // Auto-start on first visit
  useEffect(() => {
    if (!autoStart || hasSeenTour) return;

    // Delay slightly to ensure DOM elements are rendered
    const timer = setTimeout(() => {
      startTour();
    }, 500);

    return () => clearTimeout(timer);
    // Only run on mount — intentionally omitting startTour to avoid re-triggers
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Cleanup on unmount
  useEffect(() => {
    return () => {
      if (driverRef.current) {
        driverRef.current.destroy();
        driverRef.current = null;
      }
    };
  }, []);

  return { startTour, hasSeenTour, resetTour };
}
