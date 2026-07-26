import { useCallback, useState } from 'react';

/**
 * Which subset of settings is currently visible. The choice persists across
 * sessions in `localStorage`, and applies globally across every tab in the
 * settings shell — see decision note below.
 */
export type SettingsMode = 'essential' | 'everything';

const STORAGE_KEY = 'pf.settings.mode';
const DEFAULT_MODE: SettingsMode = 'essential';

/**
 * Read once, at mount, from `localStorage`. Called from a lazy `useState`
 * initialiser so the read happens off the render path (satisfying
 * `react-hooks/purity`) and never runs on subsequent renders. Wrapped in
 * try/catch because browsers block `localStorage` in some contexts (Safari
 * private mode, disabled cookies) and throwing during the initialiser would
 * blow up the whole page.
 */
function readPersistedMode(): SettingsMode {
  if (typeof window === 'undefined') {
    return DEFAULT_MODE;
  }
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return raw === 'everything' ? 'everything' : 'essential';
  } catch {
    return DEFAULT_MODE;
  }
}

function persistMode(next: SettingsMode): void {
  if (typeof window === 'undefined') {
    return;
  }
  try {
    window.localStorage.setItem(STORAGE_KEY, next);
  } catch {
    // Storage may be unavailable (private mode, quota exceeded). Silently
    // ignore — the in-memory state still applies for this tab session.
  }
}

/**
 * State hook for the Essential / Everything toggle.
 *
 * ## Compiler-rule notes (all four traps that bit #935 avoided deliberately)
 *
 * 1. `readPersistedMode` runs via the lazy `useState` initialiser
 *    (`useState(readPersistedMode)`), not inside the render body. This keeps
 *    render pure — `react-hooks/purity` would flag a bare
 *    `localStorage.getItem` call at the top of the component.
 * 2. `persistMode` runs from `setMode`, which is only ever invoked from a
 *    user event handler (the toggle's `onClick`). No effect syncs state to
 *    `localStorage`, so `react-hooks/set-state-in-effect` has nothing to
 *    complain about.
 * 3. There is no `ref.current` write during render — no refs at all.
 * 4. There are no impure calls (`Date.now`, `Math.random`) anywhere in the
 *    render path or in `useMemo` bodies.
 *
 * ## Global vs per-tab
 *
 * The setting shell mounts `SettingsPage` once per active sub-tab (Farm,
 * System, Automation, Integrations, Slicing). A shared `localStorage` key
 * means the toggle is **global**: flipping it on one tab and switching to
 * another shows the same mode. That matches the mental model observed in
 * research ("am I doing quick tuning right now, or deep config?") better
 * than a per-tab preference would, and avoids the surprise of tab A saying
 * Essential while tab B silently shows Everything.
 */
export interface UseSettingsModeResult {
  mode: SettingsMode;
  setMode: (mode: SettingsMode) => void;
}

export function useSettingsMode(): UseSettingsModeResult {
  const [mode, setModeState] = useState<SettingsMode>(readPersistedMode);

  const setMode = useCallback((next: SettingsMode) => {
    setModeState(next);
    persistMode(next);
  }, []);

  return { mode, setMode };
}
