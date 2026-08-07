import React, { useState, useCallback, useMemo, useEffect, useRef } from 'react';
import { SlicerState, SlicerContextValue, SlicerContext } from './SlicerTypes';
import { slicerRegistry } from '@/services/slicerRegistry';
import { apiClient } from '@/services/api';
import { AUTH_SESSION_ESTABLISHED_EVENT } from '@/services/authEvents';

interface SlicerSettings {
  enabled: boolean;
}

interface SlicerSnapshot {
  settings: SlicerSettings;
  workers: Awaited<ReturnType<typeof slicerRegistry.getSlicers>>;
}

const defaultState: SlicerState = {
  settingEnabled: true, // Default to true until we fetch actual setting
  hasWorkers: false,
  isSlicerAvailable: false,
  isLoading: true,
  workerCount: 0,
};

let workersRequest: Promise<SlicerSnapshot['workers']> | null = null;
let snapshotRequest: Promise<SlicerSnapshot> | null = null;

function loadWorkers(): Promise<SlicerSnapshot['workers']> {
  if (!workersRequest) {
    workersRequest = slicerRegistry.getSlicers()
      .catch(error => {
        console.warn('[SlicerContext] Failed to fetch slicer workers, using defaults:', error);
        return [];
      })
      .finally(() => {
        workersRequest = null;
      });
  }

  return workersRequest;
}

function loadAuthenticatedSnapshot(): Promise<SlicerSnapshot> {
  if (!snapshotRequest) {
    snapshotRequest = Promise.all([
      apiClient.getSettings<SlicerSettings>('Slicer').catch(error => {
        console.warn('[SlicerContext] Failed to fetch slicer settings, using defaults:', error);
        return { enabled: true };
      }),
      loadWorkers(),
    ])
      .then(([settings, workers]) => ({ settings, workers }))
      .finally(() => {
        snapshotRequest = null;
      });
  }

  return snapshotRequest;
}

export const SlicerProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [state, setState] = useState<SlicerState>(defaultState);
  const mountedRef = useRef(false);
  const settingsLoadGenerationRef = useRef(0);
  const workersLoadGenerationRef = useRef(0);

  useEffect(() => {
    mountedRef.current = true;

    const refreshAuthenticatedState = () => {
      if (!localStorage.getItem('auth-token')) {
        return;
      }

      const settingsGeneration = ++settingsLoadGenerationRef.current;
      const workersGeneration = ++workersLoadGenerationRef.current;

      void loadAuthenticatedSnapshot().then(({ settings, workers }) => {
        if (
          !mountedRef.current
          || settingsGeneration !== settingsLoadGenerationRef.current
        ) {
          return;
        }

        const settingEnabled = settings.enabled ?? true;
        setState(previousState => {
          const workersAreCurrent =
            workersGeneration === workersLoadGenerationRef.current;
          const workerCount = workersAreCurrent
            ? workers.length
            : previousState.workerCount;
          const hasWorkers = workerCount > 0;

          return {
            settingEnabled,
            hasWorkers,
            isSlicerAvailable: settingEnabled && hasWorkers,
            isLoading: false,
            workerCount,
          };
        });
      });
    };

    const refreshAnonymousWorkers = () => {
      const generation = ++workersLoadGenerationRef.current;

      void loadWorkers().then(workers => {
        if (
          !mountedRef.current
          || generation !== workersLoadGenerationRef.current
        ) {
          return;
        }

        setState(previousState => ({
          ...previousState,
          hasWorkers: workers.length > 0,
          isSlicerAvailable: previousState.settingEnabled && workers.length > 0,
          isLoading: false,
          workerCount: workers.length,
        }));
      });
    };

    window.addEventListener(
      AUTH_SESSION_ESTABLISHED_EVENT,
      refreshAuthenticatedState,
    );

    if (localStorage.getItem('auth-token')) {
      refreshAuthenticatedState();
    } else {
      refreshAnonymousWorkers();
    }

    return () => {
      mountedRef.current = false;
      window.removeEventListener(
        AUTH_SESSION_ESTABLISHED_EVENT,
        refreshAuthenticatedState,
      );
    };
  }, []);

  const refreshWorkers = useCallback(async () => {
    const generation = ++workersLoadGenerationRef.current;
    const workers = await loadWorkers();

    if (
      !mountedRef.current
      || generation !== workersLoadGenerationRef.current
    ) {
      return;
    }

    setState(previousState => {
      const hasWorkers = workers.length > 0;
      return {
        ...previousState,
        hasWorkers,
        workerCount: workers.length,
        isSlicerAvailable: previousState.settingEnabled && hasWorkers,
      };
    });
  }, []);

  const value: SlicerContextValue = useMemo(() => ({
    ...state,
    refreshWorkers,
  }), [state, refreshWorkers]);

  return <SlicerContext.Provider value={value}>{children}</SlicerContext.Provider>;
};
