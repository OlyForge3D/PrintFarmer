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

let workersRequest: {
  authToken: string;
  promise: Promise<SlicerSnapshot['workers']>;
} | null = null;
let snapshotRequest: {
  authToken: string;
  promise: Promise<SlicerSnapshot>;
} | null = null;

function loadWorkers(authToken: string): Promise<SlicerSnapshot['workers']> {
  if (workersRequest?.authToken === authToken) {
    return workersRequest.promise;
  }

  const promise = slicerRegistry.getSlicers()
    .catch(error => {
      console.warn('[SlicerContext] Failed to fetch slicer workers, using defaults:', error);
      return [];
    });

  workersRequest = { authToken, promise };
  void promise.finally(() => {
    if (workersRequest?.promise === promise) {
      workersRequest = null;
    }
  });

  return promise;
}

function clearLoggedOutState() {
  return {
    settingEnabled: true,
    hasWorkers: false,
    isSlicerAvailable: false,
    isLoading: false,
    workerCount: 0,
  } satisfies SlicerState;
}

function loadAuthenticatedSnapshot(authToken: string): Promise<SlicerSnapshot> {
  if (snapshotRequest?.authToken === authToken) {
    return snapshotRequest.promise;
  }

  const promise = Promise.all([
    apiClient.getSettings<SlicerSettings>('Slicer').catch(error => {
      console.warn('[SlicerContext] Failed to fetch slicer settings, using defaults:', error);
      return { enabled: true };
    }),
    loadWorkers(authToken),
  ]).then(([settings, workers]) => ({ settings, workers }));

  snapshotRequest = { authToken, promise };
  void promise.finally(() => {
    if (snapshotRequest?.promise === promise) {
      snapshotRequest = null;
    }
  });

  return promise;
}

export const SlicerProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [state, setState] = useState<SlicerState>(() =>
    localStorage.getItem('auth-token') ? defaultState : clearLoggedOutState());
  const mountedRef = useRef(false);
  const settingsLoadGenerationRef = useRef(0);
  const workersLoadGenerationRef = useRef(0);

  useEffect(() => {
    mountedRef.current = true;

    const refreshAuthenticatedState = () => {
      const authToken = localStorage.getItem('auth-token');
      if (!authToken) {
        return;
      }

      const settingsGeneration = ++settingsLoadGenerationRef.current;
      const workersGeneration = ++workersLoadGenerationRef.current;

      void loadAuthenticatedSnapshot(authToken).then(({ settings, workers }) => {
        if (
          !mountedRef.current
          || settingsGeneration !== settingsLoadGenerationRef.current
          || localStorage.getItem('auth-token') !== authToken
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

    window.addEventListener(
      AUTH_SESSION_ESTABLISHED_EVENT,
      refreshAuthenticatedState,
    );

    if (localStorage.getItem('auth-token')) {
      refreshAuthenticatedState();
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
    const authToken = localStorage.getItem('auth-token');
    if (!authToken) {
      if (mountedRef.current) {
        setState(clearLoggedOutState());
      }
      return;
    }

    const generation = ++workersLoadGenerationRef.current;
    const workers = await loadWorkers(authToken);

    if (
      !mountedRef.current
      || generation !== workersLoadGenerationRef.current
      || localStorage.getItem('auth-token') !== authToken
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
