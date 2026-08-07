import React, { useState, useCallback, useMemo, useEffect, useRef } from 'react';
import { SlicerState, SlicerContextValue, SlicerContext } from './SlicerTypes';
import { slicerRegistry } from '@/services/slicerRegistry';
import { apiClient } from '@/services/api';
import { AUTH_SESSION_ESTABLISHED_EVENT } from '@/services/authEvents';
import { useAuth } from '@/features/auth/hooks/useAuth';

interface SlicerSettings {
  enabled: boolean;
}

interface SlicerSnapshot {
  settings: SlicerSettings;
  workers: Awaited<ReturnType<typeof slicerRegistry.getSlicers>>;
}

interface TokenScopedSlicerState {
  authToken: string | null;
  value: SlicerState;
}

const defaultState: SlicerState = {
  settingEnabled: true, // Default to true until we fetch actual setting
  hasWorkers: false,
  isSlicerAvailable: false,
  isLoading: true,
  workerCount: 0,
};

const loggedOutState: SlicerState = {
  settingEnabled: true,
  hasWorkers: false,
  isSlicerAvailable: false,
  isLoading: false,
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
  const [scopedState, setScopedState] = useState<TokenScopedSlicerState>({
    authToken: null,
    value: defaultState,
  });
  const { isAuthenticated } = useAuth();
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
        setScopedState(previousScopedState => {
          const previousState = previousScopedState.authToken === authToken
            ? previousScopedState.value
            : defaultState;
          const workersAreCurrent =
            workersGeneration === workersLoadGenerationRef.current;
          const workerCount = workersAreCurrent
            ? workers.length
            : previousState.workerCount;
          const hasWorkers = workerCount > 0;

          return {
            authToken,
            value: {
              settingEnabled,
              hasWorkers,
              isSlicerAvailable: settingEnabled && hasWorkers,
              isLoading: false,
              workerCount,
            },
          };
        });
      });
    };

    window.addEventListener(
      AUTH_SESSION_ESTABLISHED_EVENT,
      refreshAuthenticatedState,
    );

    if (localStorage.getItem('auth-token')) {
      if (isAuthenticated) {
        refreshAuthenticatedState();
      }
    } else {
      settingsLoadGenerationRef.current++;
      workersLoadGenerationRef.current++;
    }

    return () => {
      mountedRef.current = false;
      window.removeEventListener(
        AUTH_SESSION_ESTABLISHED_EVENT,
        refreshAuthenticatedState,
      );
    };
  }, [isAuthenticated]);

  const refreshWorkers = useCallback(async () => {
    const authToken = localStorage.getItem('auth-token');
    if (!authToken) {
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

    setScopedState(previousScopedState => {
      const previousState = previousScopedState.authToken === authToken
        ? previousScopedState.value
        : defaultState;
      const hasWorkers = workers.length > 0;
      return {
        authToken,
        value: {
          ...previousState,
          hasWorkers,
          workerCount: workers.length,
          isSlicerAvailable: previousState.settingEnabled && hasWorkers,
        },
      };
    });
  }, []);

  const currentAuthToken = isAuthenticated
    ? localStorage.getItem('auth-token')
    : null;
  const visibleState = !currentAuthToken
    ? loggedOutState
    : scopedState.authToken === currentAuthToken
      ? scopedState.value
      : defaultState;

  const value: SlicerContextValue = useMemo(() => ({
    ...visibleState,
    refreshWorkers,
  }), [visibleState, refreshWorkers]);

  return <SlicerContext.Provider value={value}>{children}</SlicerContext.Provider>;
};
