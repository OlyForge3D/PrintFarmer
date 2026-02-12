import React, { useState, useCallback, useMemo, useEffect } from 'react';
import { SlicerState, SlicerContextValue, SlicerContext } from './SlicerTypes';
import { slicerRegistry } from '@/services/slicerRegistry';
import { apiClient } from '@/services/api';

interface SlicerSettings {
  enabled: boolean;
}

const defaultState: SlicerState = {
  settingEnabled: true, // Default to true until we fetch actual setting
  hasWorkers: false,
  isSlicerAvailable: false,
  isLoading: true,
  workerCount: 0,
};

export const SlicerProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [state, setState] = useState<SlicerState>(defaultState);

  // Fetch slicer settings and workers on mount
  useEffect(() => {
    const fetchSlicerState = async () => {
      try {
        // Fetch settings and workers in parallel
        const [settings, workers] = await Promise.all([
          apiClient.getSettings<SlicerSettings>('Slicer').catch(() => ({ enabled: true })),
          slicerRegistry.getSlicers().catch(() => []),
        ]);

        const settingEnabled = settings.enabled ?? true;
        const hasWorkers = workers.length > 0;

        setState({
          settingEnabled,
          hasWorkers,
          isSlicerAvailable: settingEnabled && hasWorkers,
          isLoading: false,
          workerCount: workers.length,
        });
      } catch (error) {
        console.error('[SlicerContext] Failed to fetch slicer state:', error);
        setState(prev => ({
          ...prev,
          isLoading: false,
          isSlicerAvailable: false,
        }));
      }
    };

    fetchSlicerState();
  }, []);

  const refreshWorkers = useCallback(async () => {
    try {
      const workers = await slicerRegistry.getSlicers();
      setState(prev => ({
        ...prev,
        hasWorkers: workers.length > 0,
        workerCount: workers.length,
        isSlicerAvailable: prev.settingEnabled && workers.length > 0,
      }));
    } catch (error) {
      console.error('[SlicerContext] Failed to refresh workers:', error);
    }
  }, []);

  // Memoize the context value to prevent unnecessary re-renders
  const value: SlicerContextValue = useMemo(() => ({
    ...state,
    refreshWorkers,
  }), [state, refreshWorkers]);

  return <SlicerContext.Provider value={value}>{children}</SlicerContext.Provider>;
};
