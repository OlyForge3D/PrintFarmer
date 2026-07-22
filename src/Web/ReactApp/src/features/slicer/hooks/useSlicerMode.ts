import { useState } from 'react';
import { useFarmSettings } from '@/features/settings/hooks/useFarmSettings';

export type SlicerMode = 'Simple' | 'Advanced';

/** localStorage key for the per-user mode preference. */
export const SLICER_MODE_STORAGE_KEY = 'pf.slicerMode';

const MODE_ORDER: readonly SlicerMode[] = ['Simple', 'Advanced'];

/**
 * SlicerMode is a UX simplification only.
 * It hides advanced controls in the browser but does NOT prevent users from
 * submitting arbitrary slicer overrides via the API directly.
 * To enforce parameter restrictions at the server level, a server-side policy
 * on the slicer submission endpoint would be required (out of scope for this feature).
 */
export interface UseSlicerModeResult {
  /** Effective mode for the current user, or null while settings are loading. */
  mode: SlicerMode | null;
  /** Admin-enabled modes (canonical order). Empty while loading. */
  enabledModes: SlicerMode[];
  /** True when more than one mode is enabled, so the user may switch. */
  canToggle: boolean;
  /** Switch the per-user mode. No-op when the target mode is not enabled. */
  setMode: (mode: SlicerMode) => void;
}

function readStoredMode(): SlicerMode | null {
  try {
    const value = localStorage.getItem(SLICER_MODE_STORAGE_KEY);
    return value === 'Simple' || value === 'Advanced' ? value : null;
  } catch {
    return null;
  }
}

/**
 * Resolves the effective slicer mode from farm settings plus a per-user override.
 *
 * Resolution rules:
 * - While loading, {@link UseSlicerModeResult.mode} is null.
 * - The admin chooses which modes are enabled and the default mode.
 * - When both modes are enabled the user may toggle; their choice is stored in
 *   localStorage and clamped to the enabled set.
 * - When exactly one mode is enabled it is forced and no toggle is offered.
 */
export function useSlicerMode(): UseSlicerModeResult {
  const { data, isLoading } = useFarmSettings();
  const [override, setOverride] = useState<SlicerMode | null>(() => readStoredMode());

  if (isLoading || data === undefined) {
    return { mode: null, enabledModes: [], canToggle: false, setMode: () => { /* loading */ } };
  }

  const defaultMode: SlicerMode = data.slicerMode ?? 'Simple';
  const enabledRaw = data.enabledModes && data.enabledModes.length > 0
    ? data.enabledModes
    : [defaultMode];
  const enabledModes = MODE_ORDER.filter((m) => enabledRaw.includes(m));
  const canToggle = enabledModes.length > 1;

  let mode: SlicerMode = defaultMode;
  if (canToggle && override && enabledModes.includes(override)) {
    mode = override;
  } else if (!enabledModes.includes(mode)) {
    mode = enabledModes[0] ?? 'Simple';
  }

  const setMode = (next: SlicerMode) => {
    if (!enabledModes.includes(next)) return;
    try {
      localStorage.setItem(SLICER_MODE_STORAGE_KEY, next);
    } catch {
      /* ignore persistence failures — override still applies for this session */
    }
    setOverride(next);
  };

  return { mode, enabledModes, canToggle, setMode };
}
