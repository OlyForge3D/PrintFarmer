import React, { useState, useCallback, useEffect, useMemo } from 'react';
import { SpoolmanState, SpoolmanContextValue, SpoolmanContext } from './SpoolmanTypes';
import { apiClient } from '@/services/api';

function loadInitialState(): SpoolmanState {
  const savedUrl = localStorage.getItem('spoolman-base-url');
  return {
    enabled: !!savedUrl,
    baseUrl: savedUrl,
    version: null,
    lastEndpoint: null,
    lastErrorCategory: null,
    lastErrorMessage: null,
  };
}

export const SpoolmanProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [state, setState] = useState<SpoolmanState>(loadInitialState);

  // Fetch Spoolman settings from the API once auth is available
  useEffect(() => {
    let cancelled = false;
    apiClient.getSettings<{ baseUrl?: string }>('Spoolman')
      .then(settings => {
        if (cancelled) return;
        const url = settings?.baseUrl?.trim() || null;
        if (url) {
          localStorage.setItem('spoolman-base-url', url);
        }
        setState(s => ({ ...s, enabled: !!url, baseUrl: url ?? s.baseUrl }));
      })
      .catch(() => { /* Not authenticated yet or settings unavailable — keep localStorage state */ });
    return () => { cancelled = true; };
  }, []);

  const setEnabled = useCallback((v: boolean) => setState(s => ({ ...s, enabled: v })), []);
  const setBaseUrl = useCallback((url: string | null) => setState(s => ({ ...s, baseUrl: url })), []);

  const updateProbeSuccess = useCallback((info: { version?: string | null; endpoint?: string | null }) => {
    setState(s => ({
      ...s,
      version: info.version ?? s.version,
      lastEndpoint: info.endpoint ?? s.lastEndpoint,
      lastErrorCategory: null,
      lastErrorMessage: null
    }));
  }, []);

  const updateProbeFailure = useCallback((info: { errorCategory?: string | null; message?: string | null }) => {
    setState(s => ({
      ...s,
      lastErrorCategory: info.errorCategory ?? s.lastErrorCategory,
      lastErrorMessage: info.message ?? s.lastErrorMessage
    }));
  }, []);

  const clear = useCallback(() => setState(defaultState), []);

  // Memoize the context value to prevent unnecessary re-renders
  const value: SpoolmanContextValue = useMemo(() => ({
    ...state,
    setEnabled,
    setBaseUrl,
    updateProbeSuccess,
    updateProbeFailure,
    clear
  }), [state, setEnabled, setBaseUrl, updateProbeSuccess, updateProbeFailure, clear]);

  return <SpoolmanContext.Provider value={value}>{children}</SpoolmanContext.Provider>;
};
