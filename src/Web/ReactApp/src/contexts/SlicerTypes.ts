import { createContext } from 'react';

/**
 * State for slicer availability
 */
export interface SlicerState {
  /** Whether slicing is enabled in app settings */
  settingEnabled: boolean;
  /** Whether any slicer workers are registered */
  hasWorkers: boolean;
  /** Combined flag: true only if setting is enabled AND workers are registered */
  isSlicerAvailable: boolean;
  /** Loading state while fetching initial data */
  isLoading: boolean;
  /** Number of registered workers */
  workerCount: number;
}

/**
 * Context value with state and actions
 */
export interface SlicerContextValue extends SlicerState {
  /** Refresh workers list from API */
  refreshWorkers: () => Promise<void>;
}

export const SlicerContext = createContext<SlicerContextValue | undefined>(undefined);
