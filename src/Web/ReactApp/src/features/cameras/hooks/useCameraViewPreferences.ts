import { useCallback, useMemo, useState } from 'react';

export type CameraViewMode = 'snapshot' | 'stream';

const CAMERA_MODE_PREFIX = 'printfarmer-camera-mode:';
const CAMERA_ROTATION_PREFIX = 'printfarmer-camera-rotation:';

interface UseCameraViewPreferencesOptions {
  preferenceKey: string;
  defaultMode?: CameraViewMode;
  hasStream: boolean;
  hasSnapshot: boolean;
}

function getModeStorageKey(preferenceKey: string): string {
  return `${CAMERA_MODE_PREFIX}${preferenceKey}`;
}

function getRotationStorageKey(preferenceKey: string): string {
  return `${CAMERA_ROTATION_PREFIX}${preferenceKey}`;
}

function getFallbackMode(hasStream: boolean): CameraViewMode {
  if (hasStream) {
    return 'stream';
  }

  return 'snapshot';
}

function isModeAvailable(mode: CameraViewMode, hasStream: boolean, hasSnapshot: boolean): boolean {
  return (mode === 'stream' && hasStream) || (mode === 'snapshot' && hasSnapshot);
}

function getInitialMode(
  preferenceKey: string,
  defaultMode: CameraViewMode,
  hasStream: boolean,
  hasSnapshot: boolean
): CameraViewMode {
  const fallbackMode = getFallbackMode(hasStream);
  if (typeof window === 'undefined') {
    return isModeAvailable(defaultMode, hasStream, hasSnapshot) ? defaultMode : fallbackMode;
  }

  const saved = localStorage.getItem(getModeStorageKey(preferenceKey));
  if (saved === 'stream' || saved === 'snapshot') {
    return isModeAvailable(saved, hasStream, hasSnapshot) ? saved : fallbackMode;
  }

  return isModeAvailable(defaultMode, hasStream, hasSnapshot) ? defaultMode : fallbackMode;
}

function getInitialRotation(preferenceKey: string): number {
  if (typeof window === 'undefined') {
    return 0;
  }

  const raw = Number.parseInt(localStorage.getItem(getRotationStorageKey(preferenceKey)) ?? '0', 10);
  if (Number.isNaN(raw)) {
    return 0;
  }

  const normalized = ((raw % 360) + 360) % 360;
  return normalized - (normalized % 90);
}

export function getCameraMediaTransformClassName(rotation: number): string {
  const normalized = ((rotation % 360) + 360) % 360;
  const baseClassName = 'absolute left-1/2 top-1/2 h-full w-full -translate-x-1/2 -translate-y-1/2 origin-center';

  switch (normalized) {
    case 90:
      return `${baseClassName} rotate-90 scale-[0.5625]`;
    case 180:
      return `${baseClassName} rotate-180`;
    case 270:
      return `${baseClassName} -rotate-90 scale-[0.5625]`;
    default:
      return baseClassName;
  }
}

export function useCameraViewPreferences({
  preferenceKey,
  defaultMode = 'stream',
  hasStream,
  hasSnapshot,
}: UseCameraViewPreferencesOptions) {
  const fallbackMode = useMemo(
    () => getFallbackMode(hasStream),
    [hasStream]
  );
  const [selectedMode, setSelectedMode] = useState<CameraViewMode>(() =>
    getInitialMode(preferenceKey, defaultMode, hasStream, hasSnapshot)
  );
  const [rotation, setRotation] = useState<number>(() => getInitialRotation(preferenceKey));

  const cameraMode = isModeAvailable(selectedMode, hasStream, hasSnapshot)
    ? selectedMode
    : fallbackMode;

  const setCameraMode = useCallback(
    (nextMode: CameraViewMode) => {
      setSelectedMode(nextMode);
      if (typeof window !== 'undefined') {
        localStorage.setItem(getModeStorageKey(preferenceKey), nextMode);
      }
    },
    [preferenceKey]
  );

  const rotateClockwise = useCallback(() => {
    setRotation((current) => {
      const next = (current + 90) % 360;
      if (typeof window !== 'undefined') {
        localStorage.setItem(getRotationStorageKey(preferenceKey), String(next));
      }
      return next;
    });
  }, [preferenceKey]);

  return {
    cameraMode,
    setCameraMode,
    rotation,
    rotateClockwise,
    hasModeToggle: hasStream && hasSnapshot,
    hasMedia: hasStream || hasSnapshot,
  };
}
